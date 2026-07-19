namespace NewsService.Services;

/// <summary>
/// NewsService-local telemetry handling defaults, in the style of <c>PresentationDefaults</c>.
/// Deliberately NOT in NewsCentral.Shared: retention is not shared behaviour — NewsViewer always
/// writes session files and never sweeps them, and placing this beside the shared DTO would imply
/// a cross-component contract that does not exist.
/// </summary>
public static class TelemetryDefaults
{
    /// <summary>
    /// Local retention window in days for uploads\session-*.json files, applied unconditionally by
    /// <see cref="TelemetryUploader"/> from the file's LastWriteTime. Deliberately a CONSTANT, not
    /// a config key — do not make this configurable: the rationale is data hygiene (the files
    /// record what a specific user saw and when), the volume is tiny so there is no operational
    /// need to tune it, and a configurable 0 would be dangerously ambiguous between
    /// delete-everything and keep-forever.
    /// </summary>
    public const int RetentionDays = 30;
}
