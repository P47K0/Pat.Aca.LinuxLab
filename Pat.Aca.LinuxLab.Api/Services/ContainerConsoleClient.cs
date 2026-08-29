using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Microsoft.Extensions.Options;
using Pat.Aca.LinuxLab.Api.Models;

namespace Pat.Aca.LinuxLab.Api.Services;

public sealed class ContainerConsoleClient : IContainerConsoleClient
{
    private const string ContainerName = "lab"; // must match the container name set in LabSessionManager.StartSessionAsync

    private readonly ArmClient _arm;
    private readonly LabSessionOptions _options;
    private readonly ILogger<ContainerConsoleClient> _logger;
    private readonly ConcurrentDictionary<string, ClientWebSocket> _sockets = new();

    public ContainerConsoleClient(ArmClient arm, IOptions<LabSessionOptions> options, ILogger<ContainerConsoleClient> logger)
    {
        _arm = arm;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ConnectAsync(LabSession session, Func<string, Task> onOutput, CancellationToken ct)
    {
        var appId = ContainerAppResource.CreateResourceIdentifier(
            _options.SubscriptionId, _options.ResourceGroup, session.ContainerAppName);
        var app = _arm.GetContainerAppResource(appId);

        var uri = await ResolveExecUriAsync(app, session, ct);

        // Confirmed via `az containerapp exec`'s own source: the WebSocket does
        // NOT take a normal ARM-scoped bearer token (that's what the earlier
        // version of this file used, and got a real 401) — it needs a separate,
        // short-lived token from this dedicated operation instead.
        var authToken = (await app.GetAuthTokenAsync(ct)).Value.Token;

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {authToken}");
        await socket.ConnectAsync(uri, ct);
        _sockets[session.SessionId] = socket;

        _ = PumpOutputAsync(session.SessionId, socket, onOutput, ct);
    }

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Finds the running replica's exec endpoint for this session's container app,
    /// using ExecEndpoint straight off the SDK's replica/container data — Azure
    /// itself hands back a ready-to-use URL, so this doesn't hand-construct one
    /// the way `az containerapp exec`'s own source does internally.
    ///
    /// Polls until the container reports IsReady, rather than resolving once
    /// immediately after creation: a real "ClusterExecEndpointConnectionError"
    /// (500 instead of 101) came back from *Azure's own* relay to the
    /// container, not from this connection to Azure — the most likely
    /// explanation is a startup race, since ARM reporting the Container App as
    /// created doesn't mean the process inside it has actually finished
    /// starting yet. If it never becomes ready within the timeout, that's a
    /// different, real problem worth its own investigation, so this still
    /// throws rather than silently trying an endpoint that isn't ready.
    /// </summary>
    private async Task<Uri> ResolveExecUriAsync(ContainerAppResource app, LabSession session, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;

        while (true)
        {
            var appData = (await app.GetAsync(ct)).Value.Data;
            var revisionName = appData.LatestReadyRevisionName ?? appData.LatestRevisionName;

            if (revisionName is not null)
            {
                var revision = await app.GetContainerAppRevisionAsync(revisionName, ct);

                ContainerAppReplicaData? replica = null;
                await foreach (var candidate in revision.Value.GetContainerAppReplicas().GetAllAsync(ct))
                {
                    replica = candidate.Data;
                    break; // any running replica will do — this is a single-node lab, not a scaled service
                }

                var container = replica?.Containers.FirstOrDefault(c => c.Name == ContainerName)
                    ?? replica?.Containers.FirstOrDefault();

                if (container is { IsReady: true } && !string.IsNullOrEmpty(container.ExecEndpoint))
                {
                    var execUri = new Uri(container.ExecEndpoint);
                    var scheme = execUri.Scheme is "https" or "http" ? "wss" : execUri.Scheme;
                    var query = string.IsNullOrEmpty(execUri.Query) ? "?command=%2Fbin%2Fbash" : $"{execUri.Query}&command=%2Fbin%2Fbash";

                    _logger.LogInformation("Resolved exec endpoint for {ContainerApp}: {Host}{Path}", session.ContainerAppName, execUri.Host, execUri.AbsolutePath);
                    return new UriBuilder(execUri) { Scheme = scheme, Port = -1, Query = query.TrimStart('?') }.Uri;
                }

                _logger.LogInformation(
                    "Container app {ContainerApp} not ready for exec yet (IsReady={IsReady}) — retrying",
                    session.ContainerAppName, container?.IsReady);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"Container app {session.ContainerAppName} never became ready for exec within {ReadyTimeout.TotalSeconds}s");
            }

            await Task.Delay(ReadyPollInterval, ct);
        }
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
