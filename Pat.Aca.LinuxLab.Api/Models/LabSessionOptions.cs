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
}
