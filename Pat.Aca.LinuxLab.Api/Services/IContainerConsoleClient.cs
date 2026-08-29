using Pat.Aca.LinuxLab.Api.Models;

namespace Pat.Aca.LinuxLab.Api.Services;

/// <summary>
/// Wraps Azure Container Apps' exec/console API — the same one `az
/// containerapp exec` uses (POST .../exec, then a WebSocket upgrade) — to
/// attach a real PTY to a session's container and relay it to/from the
/// browser's terminal over SignalR. Deliberately not ACA's "dynamic
/// sessions" feature — see the BRD's §08 for why.
/// </summary>
public interface IContainerConsoleClient
{
    Task ConnectAsync(LabSession session, Func<string, Task> onOutput, CancellationToken ct);
    Task SendAsync(string sessionId, string data);
    Task DisconnectAsync(string sessionId);
}
