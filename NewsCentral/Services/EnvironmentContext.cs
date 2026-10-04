using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Single runtime source of the authoring data root (DataPath). Replaces the earlier pattern of
/// capturing AppConfiguration.DataPath once at construction in several singletons
/// (LocalStorageService, AuthenticationService, ScheduleService, DataSeederService,
/// LocalBlobDistributionService) — those now read EnvironmentContext.DataPath on every call
/// instead, so a later DataPath switch takes effect immediately for all of them.
/// </summary>
public sealed class EnvironmentContext
{
    public string DataPath { get; private set; }

    public event Action? Changed;

    public EnvironmentContext(AppConfiguration config)
    {
        DataPath = config.DataPath;
    }

    /// <summary>
    /// Switches the runtime DataPath to <paramref name="dataPath"/> and raises
    /// <see cref="Changed"/> so dependents (e.g. DistributionServiceRouter) can rebuild. Does not
    /// validate that the path exists and never touches the disk — that is the caller's
    /// responsibility. No UI calls this in M2; it exists for M4's environment switcher.
    /// </summary>
    public void SwitchTo(string dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
            throw new ArgumentException("DataPath must not be empty.", nameof(dataPath));

        DataPath = dataPath;
        Changed?.Invoke();
    }
}
