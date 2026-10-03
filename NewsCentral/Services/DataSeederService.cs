using NewsCentral.Configuration;
using NewsCentral.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace NewsCentral.Services;

public class DataSeederService
{
    private readonly IStorageService _storage;
    private readonly string _basePath;          // kept only for BasePathExists check
    private readonly IConfiguration _configuration;
    private readonly Lazy<Task> _initialization;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public DataSeederService(
        IStorageService storage,
        AppConfiguration config,
        IConfiguration configuration)
    {
        _storage       = storage;
        _basePath      = config.DataPath;       // used only by GetInitializationStatus
        _configuration = configuration;
        _initialization = new Lazy<Task>(InitializeIfNeededAsync);
    }

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <see cref="InitializeIfNeededAsync"/> at most once per process; every caller — the
    /// fire-and-forget call from App.CreateWindow and the awaited call from Login.razor — receives
    /// the same Task and observes the same completion or fault. Exceptions from initialization
    /// propagate to every caller through this Task; none are swallowed here.
    /// </summary>
    public Task EnsureInitializedAsync() => _initialization.Value;

    /// <summary>
    /// Runs once on startup. If config/users.json is absent the entire
    /// data store is considered uninitialised and seeded from scratch.
    /// </summary>
    public async Task InitializeIfNeededAsync()
    {
        System.Diagnostics.Debug.WriteLine(
            "=== DataSeeder: Checking if initialization needed ===");

        if (await _storage.FileExistsAsync("config/users.json"))
        {
            System.Diagnostics.Debug.WriteLine("✓ Users file exists — initialization not needed");
            return;
        }

        System.Diagnostics.Debug.WriteLine("✗ Users file not found - starting initialization");
        await InitializeAsync();
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
        // _basePath is kept solely so BasePathExists can report whether the
        // configured data root (drive letter, UNC path, or Azure Files mount)
        // is reachable — useful for diagnostics when the app fails to start.
        var status = new InitializationStatus
        {
            IsInitialized  = IsInitialized(),
            BasePath       = _basePath,
            BasePathExists = Directory.Exists(_basePath)
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
