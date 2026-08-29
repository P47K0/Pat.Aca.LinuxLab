namespace Pat.Aca.LinuxLab.Api.Models;

/// <summary>
/// Bound from the "LabSession" appsettings section. Left empty in
/// appsettings.json on purpose (not human-readable placeholders) — an
/// unconfigured value should fail loudly, not silently do the wrong thing.
/// </summary>
public sealed class LabSessionOptions
{
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string ContainerAppsEnvironmentName { get; set; } = "";
    public string Location { get; set; } = "westeurope";

    /// <summary>The lab image to run per session — e.g. an ACR/Docker Hub reference to the image built from this repo's Dockerfile.</summary>
    public string LabImage { get; set; } = "";

    /// <summary>This API's own reachable URL, passed into each session container as LAB_API_URL so the simulator shims can call back to /internal/progress.</summary>
    public string SelfUrl { get; set; } = "";

    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>Hard ceiling regardless of activity — deliberately matches the real CKA exam's own time limit, so hitting it is itself part of the practice.</summary>
    public int MaxSessionMinutes { get; set; } = 120;

    /// <summary>Caps how many new sessions one user can start per rolling hour — the real cost/abuse guard, since starting a session is what actually spins up billable Azure resources.</summary>
    public int MaxSessionStartsPerHour { get; set; } = 5;

    /// <summary>The frontend's own origin (e.g. "https://lab.koorevaar.com") — needed for CORS, since the browser calls this API's SignalR hub cross-origin (different subdomain).</summary>
    public string AllowedOrigin { get; set; } = "";
}
