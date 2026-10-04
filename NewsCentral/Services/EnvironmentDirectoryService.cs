using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>Why <see cref="EnvironmentDirectoryService.AddAsync"/> succeeded or failed.</summary>
public enum AddOutcome
{
    Added,
    AddedNotInitialized,
    RejectedNotAllowed,
    RejectedEmptyPath,
    RejectedRelativePath,
    RejectedAlreadyListed,
    RejectedNotFound,
    RejectedTimedOut,
    RejectedError
}

/// <summary>
/// <see cref="ErrorMessage"/> is set whenever <see cref="Outcome"/> is not <see cref="AddOutcome.Added"/>
/// or <see cref="AddOutcome.AddedNotInitialized"/> — <see cref="AddOutcome.AddedNotInitialized"/> is a
/// SUCCESS carrying an informational note, not a rejection.
/// </summary>
public sealed record AddResult(AddOutcome Outcome, string? ErrorMessage = null);

/// <summary>
/// Owns the per-user environment list (add/remove/hide), startup/current-environment lookup, and
/// environment switching. The single entry point the UI (EnvironmentPicker, Login) talks to — it
/// never reads <see cref="UserEnvironmentStore"/> or calls <see cref="EnvironmentListBuilder"/>
/// directly.
///
/// Never probes a listed environment (see <see cref="EnvironmentProbe"/>'s own remarks) — disk is
/// touched only by <see cref="AddAsync"/> (via the probe), by an actual switch (through the normal
/// storage/seeder path, outside this class), and by <see cref="UserEnvironmentStore"/> itself reading
/// the local state file.
/// </summary>
public sealed class EnvironmentDirectoryService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly EnvironmentCatalog _catalog;
    private readonly AppConfiguration _config;
    private readonly UserEnvironmentStore _store;
    private readonly EnvironmentContext _environment;
    private readonly AuthenticationService _authService;
    private readonly TeamContextService _teamContext;

    private UserEnvironmentState? _cachedState;

    /// <summary>Raised after any add/remove/hide/unhide/switch — the picker's cue to re-render.</summary>
    public event Action? Changed;

    public EnvironmentDirectoryService(
        EnvironmentCatalog catalog,
        AppConfiguration config,
        UserEnvironmentStore store,
        EnvironmentContext environment,
        AuthenticationService authService,
        TeamContextService teamContext)
    {
        _catalog = catalog;
        _config = config;
        _store = store;
        _environment = environment;
        _authService = authService;
        _teamContext = teamContext;
    }

    private UserEnvironmentState GetState() => _cachedState ??= _store.Load();

    private async Task SaveStateAsync(UserEnvironmentState state)
    {
        await _store.SaveAsync(state);
        _cachedState = state;
    }

    /// <summary>The environment list for the picker — built entirely from stored data, never probed.</summary>
    public IReadOnlyList<EnvironmentOption> GetOptions(bool includeHidden = false) =>
        EnvironmentListBuilder.Build(_catalog, _config.DataPath, GetState(), _environment.DataPath, includeHidden);

    /// <summary>
    /// The option matching the live <see cref="EnvironmentContext.DataPath"/> — always shown even if
    /// it is a hidden Policy entry. Synthesizes a fallback option (last path segment as display name)
    /// in the edge case where the current path matches none of Policy/Configured/User, so the picker
    /// always has something to show as "current."
    /// </summary>
    public EnvironmentOption Current
    {
        get
        {
            var match = GetOptions(includeHidden: true).FirstOrDefault(o => o.IsCurrent);
            if (match != null)
                return match;

            var path = _environment.DataPath;
            return new EnvironmentOption(
                DataPath: path,
                DisplayName: LastPathSegmentOrPath(path),
                Kind: EnvironmentKind.User,
                PolicyName: null,
                IsHidden: false,
                IsShareable: EnvironmentPaths.IsShareable(path),
                IsCurrent: true);
        }
    }

    /// <summary>
    /// True when the current user may add a new environment: authenticated, a System Administrator
    /// (of the environment they are currently logged into), and user environments are allowed — by
    /// policy (<see cref="EnvironmentCatalog.AllowUserEnvironments"/>) or because this machine has no
    /// policy catalog at all (an unmanaged machine must not be left unable to add any environment).
    /// </summary>
    public bool CanAdd =>
        _authService.IsAuthenticated() &&
        _authService.IsSystemAdmin() &&
        (_catalog.AllowUserEnvironments || _catalog.Entries.Count == 0);

    /// <summary>
    /// True when the picker should render a read-only label instead of a dropdown — at most one
    /// environment is visible and there is no way to add another.
    /// </summary>
    public bool IsLocked => GetOptions().Count <= 1 && !CanAdd;

    /// <summary>
    /// Validates and adds <paramref name="path"/> to the per-user list. Rejections never touch disk
    /// beyond the probe itself, and never modify stored state.
    /// </summary>
    public async Task<AddResult> AddAsync(string path, string? displayName)
    {
        if (!CanAdd)
            return new AddResult(AddOutcome.RejectedNotAllowed, "Adding environments is not available.");

        if (string.IsNullOrWhiteSpace(path))
            return new AddResult(AddOutcome.RejectedEmptyPath, "Path is required.");

        if (!EnvironmentPaths.IsAbsolute(path))
            return new AddResult(AddOutcome.RejectedRelativePath, "Path must be a fully qualified (absolute) path.");

        var canonical = EnvironmentPaths.Canonicalize(path);
        if (GetOptions(includeHidden: true).Any(o => EnvironmentPaths.Canonicalize(o.DataPath) == canonical))
            return new AddResult(AddOutcome.RejectedAlreadyListed, "This environment is already listed.");

        var probe = await EnvironmentProbe.ProbeAsync(path, ProbeTimeout);

        if (probe.TimedOut)
            return new AddResult(AddOutcome.RejectedTimedOut, "Timed out while checking that path. Check that it is reachable.");

        if (probe.Error != null)
            return new AddResult(AddOutcome.RejectedError, probe.Error);

        if (!probe.Exists)
            return new AddResult(AddOutcome.RejectedNotFound, "That folder does not exist or is not accessible.");

        var state = GetState();
        state.Entries.Add(new UserEnvironmentEntry
        {
            DataPath = path,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            AddedUtc = DateTime.UtcNow
        });
        await SaveStateAsync(state);
        Changed?.Invoke();

        return new AddResult(probe.Initialized ? AddOutcome.Added : AddOutcome.AddedNotInitialized);
    }

    /// <summary>
    /// Removes a USER-added entry. Policy and Configured entries are never in
    /// <see cref="UserEnvironmentState.Entries"/>, so they cannot be removed through this method
    /// regardless of caller. Fails (returns false, no state change) when the caller is not a System
    /// Administrator, or when <paramref name="dataPath"/> is the current environment.
    /// </summary>
    public async Task<bool> RemoveAsync(string dataPath)
    {
        if (!_authService.IsSystemAdmin())
            return false;

        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        if (canonical == EnvironmentPaths.Canonicalize(_environment.DataPath))
            return false;

        var state = GetState();
        var removedCount = state.Entries.RemoveAll(e => EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
        if (removedCount == 0)
            return false;

        await SaveStateAsync(state);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Hides a POLICY entry locally (any logged-in user may do this — it is a personal preference,
    /// not an admin action). Returns false without any state change when not authenticated, or when
    /// <paramref name="dataPath"/> does not match a Policy entry.
    /// </summary>
    public async Task<bool> HideAsync(string dataPath)
    {
        if (!_authService.IsAuthenticated())
            return false;

        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        var isPolicyEntry = _catalog.Entries.Any(e => EnvironmentPaths.Canonicalize(e.DataPath) == canonical);
        if (!isPolicyEntry)
            return false;

        var state = GetState();
        if (state.HiddenPaths.Any(p => string.Equals(p, canonical, StringComparison.OrdinalIgnoreCase)))
            return true; // already hidden — no-op, still a success from the caller's point of view

        state.HiddenPaths.Add(canonical);
        await SaveStateAsync(state);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Clears every locally-hidden Policy entry.</summary>
    public async Task UnhideAllAsync()
    {
        var state = GetState();
        if (state.HiddenPaths.Count == 0)
            return;

        state.HiddenPaths.Clear();
        await SaveStateAsync(state);
        Changed?.Invoke();
    }

    /// <summary>
    /// Records the current environment as "last used" — called only after a SUCCESSFUL login (Login
    /// or UPN Login), never on mere selection in the picker. Best-effort: a write failure is logged
    /// to Debug and swallowed, since failing to remember the last-used environment must never block
    /// login.
    /// </summary>
    public async Task RecordSuccessfulLoginAsync()
    {
        try
        {
            var state = GetState();
            state.LastUsedDataPath = _environment.DataPath;
            await SaveStateAsync(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnvironmentDirectoryService] Failed to record last-used environment: {ex.Message}");
        }
    }

    /// <summary>
    /// Switches the runtime environment: logs out if currently logged in, clears the team context,
    /// then calls <see cref="EnvironmentContext.SwitchTo"/>. Navigation (and the confirm-before-switch
    /// dialog, when logged in) is the caller's responsibility — this method performs the switch
    /// itself unconditionally once called.
    /// </summary>
    public void SwitchTo(string dataPath)
    {
        if (_authService.IsAuthenticated())
            _authService.Logout();

        _teamContext.ClearCurrentTeam();
        _environment.SwitchTo(dataPath);
        Changed?.Invoke();
    }

    private static string LastPathSegmentOrPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segment = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(segment) ? path : segment;
    }
}
