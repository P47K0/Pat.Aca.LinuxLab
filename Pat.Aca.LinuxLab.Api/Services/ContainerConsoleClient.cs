using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Options;
using Pat.Aca.LinuxLab.Api.Models;

namespace Pat.Aca.LinuxLab.Api.Services;

public sealed class ContainerConsoleClient : IContainerConsoleClient
{
    private readonly TokenCredential _credential;
    private readonly LabSessionOptions _options;
    private readonly ILogger<ContainerConsoleClient> _logger;
    private readonly ConcurrentDictionary<string, ClientWebSocket> _sockets = new();

    public ContainerConsoleClient(TokenCredential credential, IOptions<LabSessionOptions> options, ILogger<ContainerConsoleClient> logger)
    {
        _credential = credential;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ConnectAsync(LabSession session, Func<string, Task> onOutput, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct);

        // TODO: this path/query shape follows the general ARM resource-path
        // pattern and the documented existence of a POST .../exec endpoint,
        // but hasn't been confirmed against a real environment from this
        // sandbox — verify against Microsoft's current REST reference for
        // Microsoft.App/containerApps before relying on it in production.
        var uri = new Uri(
            $"wss://management.azure.com/subscriptions/{_options.SubscriptionId}" +
            $"/resourceGroups/{_options.ResourceGroup}" +
            $"/providers/Microsoft.App/containerApps/{session.ContainerAppName}/exec" +
            "?api-version=2024-03-01&command=/bin/bash&stdin=true&stdout=true&terminal=true");

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token.Token}");
        await socket.ConnectAsync(uri, ct);
        _sockets[session.SessionId] = socket;

        _ = PumpOutputAsync(session.SessionId, socket, onOutput, ct);
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
