namespace NewsService.Models;

/// <summary>
/// Persisted to %programdata%\NewsCentral\servicestate.json.
/// Tracks the last-applied wallpaper and lockscreen so they are not
/// reapplied on every poll when nothing has changed.
/// </summary>
public class ServiceState
{
    public string? LastWallpaperPresentationId { get; set; }
    public string? LastLockscreenPresentationId { get; set; }
}
