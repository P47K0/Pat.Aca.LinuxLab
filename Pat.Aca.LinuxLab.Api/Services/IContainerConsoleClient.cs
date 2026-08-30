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

    /// <summary>Tells the container's actual PTY the browser terminal's real size, so line-wrapping/redraw (readline, vim, less, ...) matches what's visually rendered — critical on mobile, where the visible terminal is often far narrower than the PTY's assumed default.</summary>
    Task ResizeAsync(string sessionId, int cols, int rows);

    Task DisconnectAsync(string sessionId);
}
