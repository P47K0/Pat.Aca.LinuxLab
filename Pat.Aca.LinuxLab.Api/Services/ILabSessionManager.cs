using Pat.Aca.LinuxLab.Api.Models;

namespace Pat.Aca.LinuxLab.Api.Services;

public interface ILabSessionManager
{
    Task StartSessionAsync(string sessionId, string userEmail, CancellationToken ct);
    Task EndSessionAsync(string sessionId);
    Task SendInputAsync(string sessionId, string data);

    /// <summary>Relays the browser terminal's real size to the session's container PTY — see ContainerConsoleClient.ResizeAsync.</summary>
    Task ResizeAsync(string sessionId, int cols, int rows);

    /// <summary>Relays a step reported by the simulator shims to that session's browser checklist.</summary>
    Task ReportProgressAsync(string sessionId, ProgressEvent evt);
}
