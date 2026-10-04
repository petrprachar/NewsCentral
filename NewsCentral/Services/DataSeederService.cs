using NewsCentral.Models;
using NewsCentral.Configuration;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;

namespace NewsCentral.Services;

/// <summary>
/// Result of <see cref="DataSeederService.EnsureInitializedAsync"/> for the environment that was
/// current at the time of the call. <see cref="NotInitialized"/> is not an error — it means this is
/// a non-Configured environment with no <c>config/users.json</c> yet, which M4a deliberately never
/// seeds (see the type's own remarks). An unreachable/unwritable DataPath instead faults the Task.
/// </summary>
public enum EnvironmentInitStatus
{
    Ready,
    NotInitialized
}

/// <summary>
/// Seeds the default admin user into a fresh data store. M4a: seeding is now per-environment and
/// gated to the Configured environment only — switching to any OTHER environment (a Policy or
/// User-added one) that has no <c>config/users.json</c> yet reports
/// <see cref="EnvironmentInitStatus.NotInitialized"/> and seeds nothing; an administrator will
/// initialize it from Environment Management (M5). Seeding only ever happens for the one
/// environment this machine's own appsettings/registry DataPath names — never for an environment
/// reached by switching, even as a SystemAdmin — because seeding silently creates a brand-new admin
/// account, which is the wrong thing to do to someone else's existing (if momentarily unreachable)
/// environment.
/// </summary>
public class DataSeederService
{
    private readonly IStorageService _storage;
    private readonly EnvironmentContext _environment;
    private readonly IConfiguration _configuration;
    private readonly AppConfiguration _appConfig;

    // Memoized per canonical DataPath — not per process — so switching between environments
    // re-evaluates seeding/readiness independently for each one, while still never re-checking an
    // environment this process has already resolved once. Every caller for the same canonical path
    // receives the same Task and observes the same completion or fault.
    private readonly ConcurrentDictionary<string, Task<EnvironmentInitStatus>> _initialization =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public DataSeederService(
        IStorageService storage,
        EnvironmentContext environment,
        IConfiguration configuration,
        AppConfiguration appConfig)
    {
        _storage       = storage;
        _environment   = environment;
        _configuration = configuration;
        _appConfig     = appConfig;
    }

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the CURRENT environment (<see cref="EnvironmentContext.DataPath"/> at the moment of
    /// the call) is seeded, if it is allowed to be, and reports whether it is ready to log into.
    /// Every caller — the fire-and-forget call from App.CreateWindow and the awaited call from
    /// Login.razor — for the same canonical DataPath receives the same Task and observes the same
    /// result or fault; I/O exceptions propagate to every caller through that Task, none are
    /// swallowed here.
    /// </summary>
    public Task<EnvironmentInitStatus> EnsureInitializedAsync()
    {
        var dataPath = _environment.DataPath;
        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        return _initialization.GetOrAdd(canonical, _ => InitializeIfNeededAsync(dataPath));
    }

    /// <summary>
    /// If <c>config/users.json</c> already exists, the environment is <see cref="EnvironmentInitStatus.Ready"/>
    /// regardless of which environment it is. Otherwise: the Configured environment (this machine's
    /// own appsettings/registry DataPath) is seeded from scratch and becomes Ready; any other
    /// environment is reported <see cref="EnvironmentInitStatus.NotInitialized"/> and is never seeded.
    /// </summary>
    private async Task<EnvironmentInitStatus> InitializeIfNeededAsync(string dataPath)
    {
        System.Diagnostics.Debug.WriteLine(
            $"=== DataSeeder: Checking if initialization needed for '{dataPath}' ===");

        if (await _storage.FileExistsAsync("config/users.json"))
        {
            System.Diagnostics.Debug.WriteLine("✓ Users file exists — initialization not needed");
            return EnvironmentInitStatus.Ready;
        }

        var isConfiguredEnvironment =
            EnvironmentPaths.Canonicalize(dataPath) == EnvironmentPaths.Canonicalize(_appConfig.DataPath);

        if (!isConfiguredEnvironment)
        {
            System.Diagnostics.Debug.WriteLine(
                "✗ Users file not found and this is not the Configured environment — reporting NotInitialized, seeding nothing");
            return EnvironmentInitStatus.NotInitialized;
        }

        System.Diagnostics.Debug.WriteLine("✗ Users file not found - starting initialization");
        await InitializeAsync();
        return EnvironmentInitStatus.Ready;
    }

    public async Task InitializeAsync()
    {
        System.Diagnostics.Debug.WriteLine("=== STARTING DATA INITIALIZATION ===");

        await _storage.EnsureFolderExistsAsync("config");
        System.Diagnostics.Debug.WriteLine("✓ Config folder ready");

        var adminUser = await CreateDefaultAdminUserAsync();
        System.Diagnostics.Debug.WriteLine($"✓ Created admin user: {adminUser.Username}");

        System.Diagnostics.Debug.WriteLine("=== DATA INITIALIZATION COMPLETE ===");
        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("DEFAULT CREDENTIALS:");
        System.Diagnostics.Debug.WriteLine($"  Username: {adminUser.Username}");
        System.Diagnostics.Debug.WriteLine(
            $"  Password: {_configuration["Initialization:DefaultAdminPassword"] ?? "admin"}");
        System.Diagnostics.Debug.WriteLine("");
    }

    // ── Private initialisation helpers ────────────────────────────────────────

    private async Task<User> CreateDefaultAdminUserAsync()
    {
        var username = _configuration["Initialization:DefaultAdminUsername"] ?? "admin";
        var password = _configuration["Initialization:DefaultAdminPassword"] ?? "admin";

        System.Diagnostics.Debug.WriteLine($"Creating admin user: {username}");
        System.Diagnostics.Debug.WriteLine("Hashing password...");

        var passwordHash = await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(password));

        System.Diagnostics.Debug.WriteLine("Password hashed successfully");

        var adminUser = new User
        {
            UserID        = Guid.NewGuid().ToString(),
            Username      = username,
            DisplayName   = "System Administrator",
            Email         = $"{username}@newscentral.local",
            UPN           = $"{username}@newscentral.local",
            PasswordHash  = passwordHash,
            IsSystemAdmin = true,
            IsActive      = true,
            DateCreated   = DateTime.UtcNow,
            TeamRoles     = new List<TeamRole>()
        };

        var usersCollection = new UsersCollection
        {
            Users        = new List<User> { adminUser },
            Version      = "1",
            LastModified = DateTime.UtcNow,
            ModifiedBy   = "system"
        };

        // Relative path matches JsonFileRepository<UsersCollection>(_storage, "config", "")
        // → GetById("users") → "config/users.json"
        await _storage.WriteTextAsync(
            "config/users.json",
            JsonSerializer.Serialize(usersCollection, JsonOptions));

        System.Diagnostics.Debug.WriteLine("✓ Users collection file created: config/users.json");
        return adminUser;
    }

    // ── Status queries ────────────────────────────────────────────────────────

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
            // Fixed: original code checked root users.json / teams.json;
            // correct paths are config/users.json and config/teams.json
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
