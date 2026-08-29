namespace Pat.Aca.LinuxLab.Api.Services;

/// <summary>
/// Thrown by <see cref="LabSessionManager.StartSessionAsync"/> when a user
/// has started more sessions than <see cref="Models.LabSessionOptions.MaxSessionStartsPerHour"/>
/// in the last rolling hour. This is the real cost/abuse guard — starting a
/// session is what actually creates a billable Container App.
/// </summary>
public sealed class SessionRateLimitExceededException(string userEmail, int limit)
    : Exception($"{userEmail} has started more than {limit} lab sessions in the last hour")
{
    public string UserEmail { get; } = userEmail;
    public int Limit { get; } = limit;
}
