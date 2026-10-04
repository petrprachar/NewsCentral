using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Single runtime source of the authoring data root (DataPath). Replaces the earlier pattern of
/// capturing AppConfiguration.DataPath once at construction in several singletons
/// (LocalStorageService, AuthenticationService, ScheduleService, DataSeederService,
/// LocalBlobDistributionService) — those now read EnvironmentContext.DataPath on every call
/// instead, so a later DataPath switch takes effect immediately for all of them.
///
/// M4a: the constructor now takes the STARTUP path directly, rather than always reading
/// AppConfiguration.DataPath — MauiProgram computes that path once (per-user last-used environment,
/// falling back through the Default policy entry, the Configured path, or the first visible
/// environment — see EnvironmentListBuilder / EnvironmentStartupSelector) before building this
/// singleton, so by construction DataPath is already the environment the app should open in.
/// </summary>
public sealed class EnvironmentContext
{
    public string DataPath { get; private set; }

    public event Action? Changed;

    public EnvironmentContext(string startupDataPath)
    {
        DataPath = startupDataPath;
    }

    /// <summary>
    /// Switches the runtime DataPath to <paramref name="dataPath"/> and raises
    /// <see cref="Changed"/> so dependents (e.g. DistributionServiceRouter) can rebuild. Does not
    /// validate that the path exists and never touches the disk — that is the caller's
    /// responsibility. Called only by EnvironmentDirectoryService.SwitchTo (M4a's environment
    /// switcher), never directly by UI code.
    /// </summary>
    public void SwitchTo(string dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
            throw new ArgumentException("DataPath must not be empty.", nameof(dataPath));

        DataPath = dataPath;
        Changed?.Invoke();
    }
}
