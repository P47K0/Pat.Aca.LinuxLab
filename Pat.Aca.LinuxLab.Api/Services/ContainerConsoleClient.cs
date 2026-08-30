using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

        var uri = await ResolveExecUriAsync(app, session, onOutput, ct);

        // Confirmed via `az containerapp exec`'s own source: the WebSocket does
        // NOT take a normal ARM-scoped bearer token (that's what the earlier
        // version of this file used, and got a real 401) — it needs a separate,
        // short-lived token from this dedicated operation instead.
        var authToken = (await app.GetAuthTokenAsync(ct)).Value.Token;

        await onOutput("[lab] Connecting to your terminal...\r\n");
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
    private async Task<Uri> ResolveExecUriAsync(ContainerAppResource app, LabSession session, Func<string, Task> onOutput, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        var announcedWaiting = false;

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

            if (!announcedWaiting)
            {
                await onOutput("[lab] Waiting for your container to start...\r\n");
                announcedWaiting = true; // once only — this can poll for up to a minute, no need to repeat the line
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"Container app {session.ContainerAppName} never became ready for exec within {ReadyTimeout.TotalSeconds}s");
            }

            await Task.Delay(ReadyPollInterval, ct);
        }
    }

    // Confirmed via az containerapp exec's own source (_ssh_utils.py,
    // _send_stdin): every stdin message needs this exact 2-byte prefix
    // before the actual character bytes — a real, undocumented framing
    // protocol, not just a Text-vs-Binary question. Explains both earlier
    // symptoms precisely: unprefixed Text frames were silently dropped;
    // unprefixed Binary frames got far enough to reach Azure's internal
    // relay/dispatch logic, which then failed trying to parse them
    // ("failed when sending message to cluster", a real error from a real
    // live test). The sibling resize prefix (below) is now wired up too.
    private static readonly byte[] StdinPrefix = [0x00, 0x00];

    // Same source as StdinPrefix, the sibling frame for terminal resize:
    // \x00\x04 followed by JSON {"Width":cols,"Height":rows}. Without this,
    // the container's PTY stays at whatever size it was when exec first
    // attached (effectively a fixed default), while the browser's terminal
    // visually renders at the real container size — on a phone, dramatically
    // narrower. The PTY's own line-wrapping/redraw (readline's prompt
    // repaint, vim, less, ...) then computes cursor positions for the wrong
    // width, which is what actually produces "text on top of other text":
    // not a CSS bug, a real terminal-size mismatch.
    private static readonly byte[] ResizePrefix = [0x00, 0x04];

    public async Task SendAsync(string sessionId, string data)
    {
        var socket = GetOpenSocketOrLog(sessionId, nameof(SendAsync));
        if (socket is null) return;

        var payload = StdinPrefix.Concat(Encoding.UTF8.GetBytes(data)).ToArray();
        await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    public async Task ResizeAsync(string sessionId, int cols, int rows)
    {
        var socket = GetOpenSocketOrLog(sessionId, nameof(ResizeAsync));
        if (socket is null) return;

        var json = JsonSerializer.Serialize(new { Width = cols, Height = rows });
        var payload = ResizePrefix.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    private ClientWebSocket? GetOpenSocketOrLog(string sessionId, string caller)
    {
        var found = _sockets.TryGetValue(sessionId, out var socket);
        if (found && socket!.State == WebSocketState.Open) return socket;

        // Was a fully silent no-op before — meaning input (or a resize)
        // could be lost here with zero trace anywhere. Log it: either the
        // socket was never stored for this session, or it's in some state
        // other than Open by the time this got called.
        _logger.LogWarning(
            "{Caller}: no open socket for session {SessionId} (found={Found}, state={State}, closeStatus={CloseStatus}, closeDescription={CloseDescription})",
            caller, sessionId, found, socket?.State, socket?.CloseStatus, socket?.CloseStatusDescription);
        return null;
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
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // The actual "why" for a real, fast close after the prompt
                    // renders but before any typing gets a response — captured
                    // at the earliest possible point (the receive result
                    // itself), rather than read back off socket state later.
                    _logger.LogWarning(
                        "Exec socket for session {SessionId} received Close: status={CloseStatus}, description={CloseDescription}",
                        sessionId, result.CloseStatus, result.CloseStatusDescription);
                    await onOutput("[lab] Your session disconnected. Refresh to start a new one.\r\n");
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

                // Azure's own exec relay can send a JSON error payload as
                // regular message content over an otherwise-successfully-
                // connected socket — confirmed for real, a
                // ClusterExecEndpointConnectionError came through exactly
                // this way rather than failing the WebSocket handshake
                // itself. Useful to see raw while debugging, but not
                // something to show a real user: log it, relay a generic
                // message instead of Azure's internal error shape.
                if (text.TrimStart().StartsWith("{\"Error\":", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Container relay reported an error for session {SessionId}: {Payload}", sessionId, text);
                    await onOutput("[lab] There was a problem connecting to your lab environment. Please try again.\r\n");
                    continue;
                }

                await onOutput(text);
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
