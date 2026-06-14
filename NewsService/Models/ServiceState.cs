namespace NewsService.Models;

/// <summary>
/// Persisted to %programdata%\NewsCentral\servicestate.json.
/// Tracks the last-applied lock screen so it is not reapplied on every poll
/// when nothing has changed. The value also carries the "__DEFAULT__" sentinel
/// when the configurable default lock-screen image is the last thing applied.
/// </summary>
public class ServiceState
{
    public string? LastLockscreenPresentationId { get; set; }
}
