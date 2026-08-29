namespace Pat.Aca.LinuxLab.Api.Models;

/// <summary>
/// Bound from the "CloudflareAccess" appsettings section. Used to verify
/// the Cf-Access-Jwt-Assertion header cryptographically (JwtBearer, pointed
/// at Cloudflare's own JWKS) instead of trusting it as plain text — see
/// Program.cs. Left empty in appsettings.json on purpose.
/// </summary>
public sealed class CloudflareAccessOptions
{
    /// <summary>e.g. "https://your-team.cloudflareaccess.com" — find it in Zero Trust &gt; Settings &gt; Custom Pages.</summary>
    public string TeamDomain { get; set; } = "";

    /// <summary>The Access application's AUD tag (Zero Trust &gt; Access &gt; Applications &gt; this app &gt; Overview).</summary>
    public string Audience { get; set; } = "";
}
