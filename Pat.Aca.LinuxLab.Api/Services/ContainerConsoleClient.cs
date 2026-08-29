using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Microsoft.Extensions.Options;
using Pat.Aca.LinuxLab.Api.Models;

namespace Pat.Aca.LinuxLab.Api.Services;

public sealed class ContainerConsoleClient : IContainerConsoleClient
{
    private const string ContainerName = "lab"; // must match the container name set in LabSessionManager.StartSessionAsync

    private readonly TokenCredential _credential;
    private readonly ArmClient _arm;
    private readonly LabSessionOptions _options;
    private readonly ILogger<ContainerConsoleClient> _logger;
    private readonly ConcurrentDictionary<string, ClientWebSocket> _sockets = new();

    public ContainerConsoleClient(TokenCredential credential, ArmClient arm, IOptions<LabSessionOptions> options, ILogger<ContainerConsoleClient> logger)
    {
        _credential = credential;
        _arm = arm;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ConnectAsync(LabSession session, Func<string, Task> onOutput, CancellationToken ct)
    {
        var uri = await ResolveExecUriAsync(session, ct);

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct);

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token.Token}");
        await socket.ConnectAsync(uri, ct);
        _sockets[session.SessionId] = socket;

        _ = PumpOutputAsync(session.SessionId, socket, onOutput, ct);
    }

    /// <summary>
    /// Finds the running replica's exec endpoint for this session's container app,
    /// using ExecEndpoint straight off the SDK's replica/container data — Azure
    /// itself hands back a ready-to-use URL, so this doesn't hand-construct one
    /// the way `az containerapp exec`'s own source does internally. Confirmed via
    /// reflection against the actual installed Azure.ResourceManager.AppContainers
    /// package that ContainerAppReplicaContainer.ExecEndpoint exists; the exact
    /// scheme/query-string shape it returns is still unverified against a live
    /// subscription, hence the defensive handling below and the log line if
    /// nothing is found.
    /// </summary>
    private async Task<Uri> ResolveExecUriAsync(LabSession session, CancellationToken ct)
    {
        var appId = ContainerAppResource.CreateResourceIdentifier(
            _options.SubscriptionId, _options.ResourceGroup, session.ContainerAppName);
        var app = _arm.GetContainerAppResource(appId);
        var appData = (await app.GetAsync(ct)).Value.Data;

        var revisionName = appData.LatestReadyRevisionName ?? appData.LatestRevisionName
            ?? throw new InvalidOperationException($"Container app {session.ContainerAppName} has no revision yet");

        var revision = await app.GetContainerAppRevisionAsync(revisionName, ct);

        ContainerAppReplicaData? replica = null;
        await foreach (var candidate in revision.Value.GetContainerAppReplicas().GetAllAsync(ct))
        {
            replica = candidate.Data;
            break; // any running replica will do — this is a single-node lab, not a scaled service
        }

        if (replica is null)
        {
            throw new InvalidOperationException($"No replicas found for {session.ContainerAppName} revision {revisionName}");
        }

        var container = replica.Containers.FirstOrDefault(c => c.Name == ContainerName)
            ?? replica.Containers.FirstOrDefault()
            ?? throw new InvalidOperationException($"Replica {replica.Name} has no containers");

        if (string.IsNullOrEmpty(container.ExecEndpoint))
        {
            throw new InvalidOperationException(
                $"Container {container.Name} in replica {replica.Name} has no ExecEndpoint — replica may not be running yet");
        }

        var execUri = new Uri(container.ExecEndpoint);
        var scheme = execUri.Scheme is "https" or "http" ? "wss" : execUri.Scheme;
        var query = string.IsNullOrEmpty(execUri.Query) ? "?command=%2Fbin%2Fbash" : $"{execUri.Query}&command=%2Fbin%2Fbash";

        _logger.LogInformation("Resolved exec endpoint for {ContainerApp}: {Host}{Path}", session.ContainerAppName, execUri.Host, execUri.AbsolutePath);

        return new UriBuilder(execUri) { Scheme = scheme, Port = -1, Query = query.TrimStart('?') }.Uri;
    }

    public async Task SendAsync(string sessionId, string data)
    {
        if (!_sockets.TryGetValue(sessionId, out var socket) || socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(data);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public async Task DisconnectAsync(string sessionId)
    {
        if (!_sockets.TryRemove(sessionId, out var socket)) return;
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None);
        }
        socket.Dispose();
    }

    private async Task PumpOutputAsync(string sessionId, ClientWebSocket socket, Func<string, Task> onOutput, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                await onOutput(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (OperationCanceledException)
        {
            // session ended — expected
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Console socket for session {SessionId} ended unexpectedly", sessionId);
        }
    }
}
