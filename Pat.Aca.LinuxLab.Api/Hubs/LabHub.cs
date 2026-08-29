using Microsoft.AspNetCore.SignalR;
using Pat.Aca.LinuxLab.Api.Services;

namespace Pat.Aca.LinuxLab.Api.Hubs;

/// <summary>
/// One SignalR connection = one lab session = one per-user container (BRD
/// §08). The connection id doubles as the session id: it's what gets passed
/// into the container as LAB_SESSION_ID for the simulator's progress
/// callbacks, and what /internal/progress targets to push checklist updates
/// back to exactly this browser tab.
/// </summary>
public class LabHub : Hub
{
    private readonly ILabSessionManager _sessions;
    private readonly ILogger<LabHub> _logger;

    public LabHub(ILabSessionManager sessions, ILogger<LabHub> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // Cloudflare Access sets this header on requests it has authenticated —
        // trustworthy only as long as this API is reachable exclusively through
        // the Access-protected hostname (not directly). TODO: restrict inbound
        // access at the ACA ingress level (e.g. to Cloudflare's IP ranges)
        // before this goes further than local testing — see the README.
        var email = Context.GetHttpContext()?.Request.Headers["Cf-Access-Authenticated-User-Email"].ToString();
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning("SignalR connection {ConnectionId} has no Cloudflare Access identity header", Context.ConnectionId);
            email = "unknown@lab";
        }

        await _sessions.StartSessionAsync(Context.ConnectionId, email, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _sessions.EndSessionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Keystrokes from the browser's terminal, relayed to this session's container PTY.</summary>
    public Task SendInput(string data) => _sessions.SendInputAsync(Context.ConnectionId, data);
}
