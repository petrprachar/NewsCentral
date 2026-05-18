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
    }

    // ── Bootstrap ─────────────────────────────────────────────────────────────

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
            System.Diagnostics.Debug.WriteLine("✓ Users file exists - initialization not needed");
            return;
        }

        System.Diagnostics.Debug.WriteLine("✗ Users file not found - starting initialization");
        await InitializeAsync();
    }

    public async Task InitializeAsync()
    {
        System.Diagnostics.Debug.WriteLine("=== STARTING DATA INITIALIZATION ===");

        // Ensure the config folder exists before writing JSON files
        await _storage.EnsureFolderExistsAsync("config");
        System.Diagnostics.Debug.WriteLine("✓ Config folder ready");

        var teamName        = _configuration["Initialization:DefaultTeamName"]        ?? "My Team";
        var teamFolderName  = _configuration["Initialization:DefaultTeamFolderName"]  ?? "MY_TEAM";
        var teamDescription = _configuration["Initialization:DefaultTeamDescription"] ?? "Default team";

        var defaultTeam = await CreateDefaultTeamAsync(teamName, teamFolderName, teamDescription);
        System.Diagnostics.Debug.WriteLine($"✓ Created default team: {defaultTeam.Name}");

        await CreateTeamFolderStructureAsync(defaultTeam.FolderName);
        System.Diagnostics.Debug.WriteLine(
            $"✓ Created folder structure for: {defaultTeam.FolderName}");

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

    private async Task<Team> CreateDefaultTeamAsync(
        string teamName, string folderName, string description)
    {
        var defaultTeam = new Team
        {
            TeamID      = Guid.NewGuid().ToString(),
            Name        = teamName,
            FolderName  = folderName,
            ContentPath = $"{folderName}/content",
            Description = description,
            CreatedBy   = "system",
            DateCreated = DateTime.UtcNow,
            IsActive    = true
        };

        var teamsCollection = new TeamsCollection
        {
            Teams        = new List<Team> { defaultTeam },
            Version      = "1",
            LastModified = DateTime.UtcNow,
            ModifiedBy   = "system"
        };

        // Relative path matches JsonFileRepository<TeamsCollection>(_storage, "config", "")
        // → GetById("teams") → "config/teams.json"
        await _storage.WriteTextAsync(
            "config/teams.json",
            JsonSerializer.Serialize(teamsCollection, JsonOptions));

        System.Diagnostics.Debug.WriteLine("✓ Teams collection file created: config/teams.json");
        return defaultTeam;
    }

    private async Task CreateTeamFolderStructureAsync(string teamFolderName)
    {
        var folders = new[]
        {
            $"{teamFolderName}/content/presentations",
            $"{teamFolderName}/content/schedules",
            $"{teamFolderName}/content/assignments",
            $"{teamFolderName}/images/original",
            $"{teamFolderName}/images/generated",
            $"{teamFolderName}/published",
            $"{teamFolderName}/deleted"
        };

        foreach (var folder in folders)
        {
            await _storage.EnsureFolderExistsAsync(folder);
            System.Diagnostics.Debug.WriteLine($"  Created: {folder}");
        }
    }

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
        _storage.FileExistsAsync("config/users.json").GetAwaiter().GetResult() &&
        _storage.FileExistsAsync("config/teams.json").GetAwaiter().GetResult();

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
