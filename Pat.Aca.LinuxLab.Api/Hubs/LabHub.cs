using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Pat.Aca.LinuxLab.Api.Services;

namespace Pat.Aca.LinuxLab.Api.Hubs;

/// <summary>
/// One SignalR connection = one lab session = one per-user container (BRD
/// §08). The connection id doubles as the session id: it's what gets passed
/// into the container as LAB_SESSION_ID for the simulator's progress
/// callbacks, and what /internal/progress targets to push checklist updates
/// back to exactly this browser tab.
///
/// [Authorize] here means the connection must carry a Cf-Access-Jwt-Assertion
/// that verifies against Cloudflare's own JWKS (wired up in Program.cs) — not
/// just present the header, which is what the earlier version of this file
/// did and was flagged as a TODO. There is deliberately no separate API key:
/// the browser talks to this hub directly (not proxied through the Worker),
/// so a Worker-held secret couldn't reach this connection anyway — Cloudflare
/// Access's own signed identity is the boundary instead.
/// </summary>
[Authorize]
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
        var email = Context.User?.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            // Shouldn't happen once [Authorize] has already accepted the
            // token — but Access's claim name is asserted, not verified from
            // here, so fail closed rather than fall back to a fake identity.
            _logger.LogWarning("Authorized connection {ConnectionId} has no 'email' claim", Context.ConnectionId);
            Context.Abort();
            return;
        }

        try
        {
            await _sessions.StartSessionAsync(Context.ConnectionId, email, Context.ConnectionAborted);
        }
        catch (SessionRateLimitExceededException ex)
        {
            _logger.LogWarning(ex, "Rejected session start for {User}", email);
            await Clients.Caller.SendAsync("SessionRejected", ex.Message);
            Context.Abort();
            return;
        }

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
