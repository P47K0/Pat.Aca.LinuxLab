namespace Pat.Aca.LinuxLab.Api.Models;

/// <summary>
/// An active lab session: one SignalR connection, one per-user Container App
/// (the BRD's §08 "fresh container per session, per user"). SessionId is the
/// owning SignalR connection id — see LabHub for why that doubles as the
/// session id everywhere, including the simulator's X-Lab-Session callbacks.
/// </summary>
public sealed class LabSession
{
    public LabSession(string sessionId, string userEmail, string containerAppName, DateTimeOffset createdAt)
    {
        SessionId = sessionId;
        UserEmail = userEmail;
        ContainerAppName = containerAppName;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
    }

    public string SessionId { get; }
    public string UserEmail { get; }
    public string ContainerAppName { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivityAt { get; set; }
}
