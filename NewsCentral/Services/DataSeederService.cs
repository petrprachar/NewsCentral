using NewsCentral.Configuration;
using System.Collections.Concurrent;

namespace NewsCentral.Services;

/// <summary>
/// Result of <see cref="DataSeederService.EnsureInitializedAsync"/> for the environment that was
/// current at the time of the call. <see cref="NotInitialized"/> is not an error — it means this
/// environment has no <c>config/users.json</c> yet. M5b: there is no longer a seeded exception for
/// the Configured environment — EVERY environment with no users file reports
/// <see cref="NotInitialized"/>, and the only way out of it is the setup wizard
/// (<see cref="EnvironmentInitializer"/>), from the login page (for a Policy/Configured
/// environment) or from Environment Management (for any environment, while signed in elsewhere).
/// An unreachable/unwritable DataPath instead faults the Task.
/// </summary>
public enum EnvironmentInitStatus
{
    Ready,
    NotInitialized
}

/// <summary>
/// Reports whether the current environment is ready to log into. M5b: this class no longer seeds
/// anything — the admin/admin first-run seed was removed along with every
/// <c>Initialization:*</c> setting it read. Creating a brand-new environment's first administrator
/// is now exclusively the job of <see cref="EnvironmentInitializer"/>, driven by the setup wizard
/// (<c>EnvironmentSetupWizard.razor</c>) from either the login page or Environment Management. This
/// class is reduced to a memoized read-only status check, plus <see cref="Invalidate"/> so the
/// wizard can force a fresh check immediately after it writes <c>config/users.json</c> for the
/// environment this process is currently pointed at.
/// </summary>
public class DataSeederService
{
    private readonly IStorageService _storage;
    private readonly EnvironmentContext _environment;

    // Memoized per canonical DataPath — not per process — so switching between environments
    // re-evaluates readiness independently for each one, while still never re-checking an
    // environment this process has already resolved once (until Invalidate clears that one entry).
    // Every caller for the same canonical path receives the same Task and observes the same
    // completion or fault.
    private readonly ConcurrentDictionary<string, Task<EnvironmentInitStatus>> _initialization =
        new(StringComparer.OrdinalIgnoreCase);

    public DataSeederService(IStorageService storage, EnvironmentContext environment)
    {
        _storage     = storage;
        _environment = environment;
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports whether the CURRENT environment (<see cref="EnvironmentContext.DataPath"/> at the
    /// moment of the call) is ready to log into. Every caller — the fire-and-forget call from
    /// App.CreateWindow and the awaited call from Login.razor — for the same canonical DataPath
    /// receives the same Task and observes the same result or fault; I/O exceptions propagate to
    /// every caller through that Task, none are swallowed here.
    /// </summary>
    public Task<EnvironmentInitStatus> EnsureInitializedAsync()
    {
        var dataPath = _environment.DataPath;
        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        return _initialization.GetOrAdd(canonical, _ => CheckInitializedAsync());
    }

    private async Task<EnvironmentInitStatus> CheckInitializedAsync() =>
        await _storage.FileExistsAsync("config/users.json")
            ? EnvironmentInitStatus.Ready
            : EnvironmentInitStatus.NotInitialized;

    /// <summary>
    /// Clears the memoized status for <paramref name="dataPath"/> so the next
    /// <see cref="EnsureInitializedAsync"/> call for it re-checks disk instead of returning a
    /// stale, memoized <see cref="EnvironmentInitStatus.NotInitialized"/>. Called by the login-page
    /// setup wizard immediately after <see cref="EnvironmentInitializer"/> successfully writes
    /// <c>config/users.json</c> for the environment this process is currently pointed at — without
    /// this, the login form would stay disabled for the rest of the process's lifetime even though
    /// the environment is now genuinely ready.
    /// </summary>
    public void Invalidate(string dataPath)
    {
        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        _initialization.TryRemove(canonical, out _);
    }

    /// <summary>
    /// Synchronous check used during app startup before the async host is ready.
    /// Safe to block: LocalStorageService.FileExistsAsync is synchronous underneath.
    /// </summary>
    public bool IsInitialized() =>
        _storage.FileExistsAsync("config/users.json").GetAwaiter().GetResult();

    public InitializationStatus GetInitializationStatus()
    {
        // Read fresh every call so a runtime DataPath switch (M4) is reflected immediately —
        // useful for diagnostics when the app fails to start.
        var basePath = _environment.DataPath;
        var status = new InitializationStatus
        {
            IsInitialized  = IsInitialized(),
            BasePath       = basePath,
            BasePathExists = Directory.Exists(basePath)
        };

        if (status.BasePathExists)
        {
            status.UsersFileExists =
                _storage.FileExistsAsync("config/users.json").GetAwaiter().GetResult();
            status.TeamsFileExists =
                _storage.FileExistsAsync("config/teams.json").GetAwaiter().GetResult();
        }

        return status;
    }
}

public class InitializationStatus
{
    public bool   IsInitialized  { get; set; }
    public string BasePath       { get; set; } = string.Empty;
    public bool   BasePathExists { get; set; }
    public bool   UsersFileExists { get; set; }
    public bool   TeamsFileExists { get; set; }
}
