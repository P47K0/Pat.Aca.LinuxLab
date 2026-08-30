namespace Pat.Aca.LinuxLab.Api.Services;

/// <summary>
/// Thrown by <see cref="LabSessionManager.StartSessionAsync"/> when a user
/// has started more sessions than <see cref="Models.LabSessionOptions.MaxSessionStartsPerHour"/>
/// in the last rolling hour. This is the real cost/abuse guard — starting a
/// session is what actually creates a billable Container App.
/// </summary>
public sealed class SessionRateLimitExceededException(string userEmail, int limit, TimeSpan retryAfter)
    : Exception($"{userEmail} has started more than {limit} lab sessions in the last hour")
{
    public string UserEmail { get; } = userEmail;
    public int Limit { get; } = limit;

    /// <summary>How long until this user's oldest still-counted session start ages out of the rolling window (see LabSessionManager.EnforceStartRate) — a real remaining wait, not an estimate.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;

    /// <summary>
    /// What actually reaches the browser — see LabHub.OnConnectedAsync.
    /// Deliberately doesn't include the email or the raw limit number the
    /// way <see cref="Exception.Message"/> above does; that one is for the
    /// server log only, same pattern as every other exception this hub
    /// catches.
    /// </summary>
    public string FriendlyMessage
    {
        get
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalMinutes));
            return $"You've started too many lab sessions recently. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
        }
    }
}
