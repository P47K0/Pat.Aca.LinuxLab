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
            // Log every claim actually present, so if "email" is wrong the
            // real name is right there instead of guessing again.
            var claims = string.Join(", ", Context.User?.Claims.Select(c => $"{c.Type}={c.Value}") ?? []);
            _logger.LogWarning(
                "Authorized connection {ConnectionId} has no 'email' claim. Actual claims: {Claims}",
                Context.ConnectionId, claims);
            Context.Abort();
            return;
        }

        try
        {
            await _sessions.StartSessionAsync(Context.ConnectionId, email, Context.ConnectionAborted);
        }
        catch (SessionRateLimitExceededException ex)
        {
            // ex.Message (logged in full here) includes the email and raw
            // limit for diagnostics — FriendlyMessage is the sanitized,
            // no-email version that's actually fine to put in front of the
            // user it's rate-limiting, same "detail to logs, generic to
            // browser" pattern as the catch-all below.
            _logger.LogWarning(ex, "Rejected session start for {User}", email);
            await Clients.Caller.SendAsync("SessionRejected", ex.FriendlyMessage);
            Context.Abort();
            return;
        }
        catch (Exception ex)
        {
            // Full detail goes to the server log only — an end user gets a
            // generic message, never the exception text itself (which could
            // be anything from an Azure resource ID to an internal stack
            // trace). Logging in full here is still exactly as valuable for
            // us as it was during initial debugging; this only changes what
            // reaches the browser.
            _logger.LogError(ex, "StartSessionAsync failed for {User} ({ConnectionId})", email, Context.ConnectionId);
            await Clients.Caller.SendAsync("SessionRejected", "Something went wrong starting your lab session. Please try again.");
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

    /// <summary>The browser terminal's real size (from xterm's FitAddon), relayed to the container's PTY so its own line-wrapping/redraw matches what's actually visible — see ContainerConsoleClient.ResizeAsync.</summary>
    public Task ResizeTerminal(int cols, int rows) => _sessions.ResizeAsync(Context.ConnectionId, cols, rows);
}
