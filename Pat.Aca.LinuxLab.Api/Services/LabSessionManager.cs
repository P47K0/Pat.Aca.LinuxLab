using System.Collections.Concurrent;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Pat.Aca.LinuxLab.Api.Hubs;
using Pat.Aca.LinuxLab.Api.Models;

namespace Pat.Aca.LinuxLab.Api.Services;

/// <summary>
/// Owns the "fresh container per session, per user" lifecycle from the BRD's
/// §08: creates a Container App from the lab image on session start,
/// attaches a PTY via <see cref="IContainerConsoleClient"/>, and deletes it
/// on disconnect or after <see cref="LabSessionOptions.IdleTimeoutMinutes"/>.
/// </summary>
public sealed class LabSessionManager : BackgroundService, ILabSessionManager
{
    private readonly ArmClient _arm;
    private readonly IContainerConsoleClient _console;
    private readonly IHubContext<LabHub> _hub;
    private readonly LabSessionOptions _options;
    private readonly ILogger<LabSessionManager> _logger;
    private readonly ConcurrentDictionary<string, LabSession> _sessions = new();

    /// <summary>Session-start timestamps per user, for the per-hour rate guard — see EnforceStartRate.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _recentStarts = new();

    public LabSessionManager(
        ArmClient arm,
        IContainerConsoleClient console,
        IHubContext<LabHub> hub,
        IOptions<LabSessionOptions> options,
        ILogger<LabSessionManager> logger)
    {
        _arm = arm;
        _console = console;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartSessionAsync(string sessionId, string userEmail, CancellationToken ct)
    {
        EnforceStartRate(userEmail);

        var containerAppName = BuildContainerAppName(sessionId);
        _logger.LogInformation("Starting lab session {SessionId} for {User} as {ContainerApp}", sessionId, userEmail, containerAppName);
        await ReportStatusAsync(sessionId, "Creating your lab environment...");

        var resourceGroupId = ResourceGroupResource.CreateResourceIdentifier(_options.SubscriptionId, _options.ResourceGroup);
        var resourceGroup = _arm.GetResourceGroupResource(resourceGroupId);

        var envId = ContainerAppManagedEnvironmentResource.CreateResourceIdentifier(
            _options.SubscriptionId, _options.ResourceGroup, _options.ContainerAppsEnvironmentName);

        var data = new ContainerAppData(_options.Location)
        {
            EnvironmentId = envId,
            Configuration = new ContainerAppConfiguration
            {
                ActiveRevisionsMode = ContainerAppActiveRevisionsMode.Single,
                // No public ingress on purpose — this container is only ever
                // reached through this API's exec/console relay, never directly.
            },
            Template = new ContainerAppTemplate
            {
                Containers =
                {
                    new ContainerAppContainer
                    {
                        Name = "lab",
                        Image = _options.LabImage,
                        Env =
                        {
                            new ContainerAppEnvironmentVariable { Name = "LAB_API_URL", Value = _options.SelfUrl },
                            new ContainerAppEnvironmentVariable { Name = "LAB_SESSION_ID", Value = sessionId },
                        },
                    },
                },
                Scale = new ContainerAppScale { MinReplicas = 1, MaxReplicas = 1 },
            },
        };

        var apps = resourceGroup.GetContainerApps();
        await apps.CreateOrUpdateAsync(Azure.WaitUntil.Completed, containerAppName, data, ct);

        var session = new LabSession(sessionId, userEmail, containerAppName, DateTimeOffset.UtcNow);
        _sessions[sessionId] = session;

        // ContainerConsoleClient also uses this same delegate for its own
        // "[lab] ..." status lines (readiness polling, connecting) — safe
        // to share with raw PTY output since status lines only ever happen
        // before the exec connection succeeds, never mixed with real bytes.
        await _console.ConnectAsync(session, chunk => _hub.Clients.Client(sessionId).SendAsync("ReceiveOutput", chunk), ct);
    }

    /// <summary>A one-off "[lab] ..." status line, for progress before the container even exists yet — see ContainerConsoleClient for the later stages, which reuse the onOutput delegate directly instead of this (session isn't tracked here at that point).</summary>
    private Task ReportStatusAsync(string sessionId, string message) =>
        _hub.Clients.Client(sessionId).SendAsync("ReceiveOutput", $"[lab] {message}\r\n");

    public async Task EndSessionAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        _logger.LogInformation("Ending lab session {SessionId} ({ContainerApp})", sessionId, session.ContainerAppName);

        await _console.DisconnectAsync(sessionId);

        var resourceGroupId = ResourceGroupResource.CreateResourceIdentifier(_options.SubscriptionId, _options.ResourceGroup);
        var resourceGroup = _arm.GetResourceGroupResource(resourceGroupId);
        var app = await resourceGroup.GetContainerAppAsync(session.ContainerAppName);
        await app.Value.DeleteAsync(Azure.WaitUntil.Started);
    }

    public Task SendInputAsync(string sessionId, string data)
    {
        // Diagnostic: confirms whether keystrokes reach the server at all,
        // to bisect a "typing does nothing" report between the browser
        // (never sending / SendInput never invoked) and the server-to-
        // container relay (sending, but ContainerConsoleClient can't
        // deliver it) without needing DevTools access.
        _logger.LogInformation("SendInputAsync: {Length} char(s) for session {SessionId}", data.Length, sessionId);

        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivityAt = DateTimeOffset.UtcNow;
        }
        return _console.SendAsync(sessionId, data);
    }

    public Task ResizeAsync(string sessionId, int cols, int rows)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivityAt = DateTimeOffset.UtcNow;
        }
        return _console.ResizeAsync(sessionId, cols, rows);
    }

    public Task ReportProgressAsync(string sessionId, ProgressEvent evt)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivityAt = DateTimeOffset.UtcNow;
        }
        return _hub.Clients.Client(sessionId).SendAsync("ChecklistUpdate", evt);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweepInterval = TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var idleCutoff = now - TimeSpan.FromMinutes(_options.IdleTimeoutMinutes);
            var maxAgeCutoff = now - TimeSpan.FromMinutes(_options.MaxSessionMinutes);

            foreach (var session in _sessions.Values)
            {
                if (session.LastActivityAt < idleCutoff)
                {
                    _logger.LogInformation(
                        "Session {SessionId} idle past {Minutes}m — tearing down", session.SessionId, _options.IdleTimeoutMinutes);
                    await EndSessionAsync(session.SessionId);
                }
                else if (session.CreatedAt < maxAgeCutoff)
                {
                    // Deliberately matches the real CKA exam's own 2-hour limit —
                    // hitting this is itself part of the practice, not just a cost guard.
                    _logger.LogInformation(
                        "Session {SessionId} hit the {Minutes}m hard cap — tearing down", session.SessionId, _options.MaxSessionMinutes);
                    await _hub.Clients.Client(session.SessionId).SendAsync(
                        "SessionEnded", "Time's up — matches the real exam's time limit. Log back in for a fresh attempt.", stoppingToken);
                    await EndSessionAsync(session.SessionId);
                }
            }

            try
            {
                await Task.Delay(sweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutting down — expected
            }
        }
    }

    /// <summary>
    /// Container App names must be lowercase alphanumeric-or-hyphen, start
    /// alphabetic, end alphanumeric, and never contain "--". SignalR
    /// connection ids are base64url (can contain '-'/'_'), so a naive
    /// substring can land on any of those forbidden shapes — confirmed by
    /// a real ContainerAppInvalidName failure ("lab-fhlqskj-", a trailing
    /// hyphen from the raw id). Filtering to alphanumeric-only before
    /// prefixing rules out every one of those cases at once, not just the
    /// specific one that happened to be hit.
    /// </summary>
    private static string BuildContainerAppName(string sessionId)
    {
        var alphanumeric = new string(sessionId.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (alphanumeric.Length > 8) alphanumeric = alphanumeric[..8];
        if (alphanumeric.Length == 0) alphanumeric = Guid.NewGuid().ToString("N")[..8]; // pathological fallback
        return $"lab-{alphanumeric}";
    }

    /// <summary>Rejects a session start once a user has started too many in the last rolling hour — the real cost/abuse guard, since this is what actually creates a billable Container App.</summary>
    private void EnforceStartRate(string userEmail)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = now - TimeSpan.FromHours(1);
        var recent = _recentStarts.GetOrAdd(userEmail, _ => new ConcurrentQueue<DateTimeOffset>());

        while (recent.TryPeek(out var oldest) && oldest < windowStart)
        {
            recent.TryDequeue(out _);
        }

        if (recent.Count >= _options.MaxSessionStartsPerHour)
        {
            // The stale-trim loop above already dropped anything past the
            // window, so whatever's left at the front is genuinely the
            // oldest still-counted start — it ages out exactly 1 hour after
            // it was recorded, which is also exactly when this user's count
            // drops back under the limit. That's real, not an estimate, so
            // it's worth surfacing to the user instead of a vague "later".
            recent.TryPeek(out var oldestStillCounted);
            var retryAfter = (oldestStillCounted + TimeSpan.FromHours(1)) - now;
            throw new SessionRateLimitExceededException(userEmail, _options.MaxSessionStartsPerHour, retryAfter);
        }

        recent.Enqueue(now);
    }
}
