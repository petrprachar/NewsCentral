using NewsCentral.Configuration;
using NewsCentral.Models;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace NewsCentral.Services
{
    public class DataSeederService
    {
        private readonly string _basePath;
        private readonly IConfiguration _configuration;

        public DataSeederService(AppConfiguration config, IConfiguration configuration)
        {
            _basePath = config.DataPath;
            _configuration = configuration;
        }

        /// <summary>
        /// Initialize the application if no data exists
        /// </summary>
        public async Task InitializeIfNeededAsync()
        {
            System.Diagnostics.Debug.WriteLine("=== DataSeeder: Checking if initialization needed ===");

            // Check if users.json exists in config folder
            var usersFile = Path.Combine(_basePath, "config", "users.json");

            if (File.Exists(usersFile))
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

            Directory.CreateDirectory(_basePath);
            System.Diagnostics.Debug.WriteLine($"✓ Created base directory: {_basePath}");

            // Get initialization settings from configuration
            var teamName = _configuration["Initialization:DefaultTeamName"] ?? "My Team";
            var teamFolderName = _configuration["Initialization:DefaultTeamFolderName"] ?? "MY_TEAM";
            var teamDescription = _configuration["Initialization:DefaultTeamDescription"] ?? "Default team";

            var defaultTeam = await CreateDefaultTeamAsync(teamName, teamFolderName, teamDescription);
            System.Diagnostics.Debug.WriteLine($"✓ Created default team: {defaultTeam.Name}");

            CreateTeamFolderStructure(defaultTeam.FolderName);
            System.Diagnostics.Debug.WriteLine($"✓ Created folder structure for: {defaultTeam.FolderName}");

            var adminUser = await CreateDefaultAdminUserAsync();
            System.Diagnostics.Debug.WriteLine($"✓ Created admin user: {adminUser.Username}");

            System.Diagnostics.Debug.WriteLine("=== DATA INITIALIZATION COMPLETE ===");
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"DEFAULT CREDENTIALS:");
            System.Diagnostics.Debug.WriteLine($"  Username: {adminUser.Username}");
            System.Diagnostics.Debug.WriteLine($"  Password: {_configuration["Initialization:DefaultAdminPassword"] ?? "admin"}");
            System.Diagnostics.Debug.WriteLine($"");
        }

        /// <summary>
        /// Create the default "My Team" team
        /// </summary>
        private async Task<Team> CreateDefaultTeamAsync(string teamName, string folderName, string description)
        {
            var defaultTeam = new Team
            {
                TeamID = Guid.NewGuid().ToString(),
                Name = teamName,
                FolderName = folderName,
                ContentPath = $"{folderName}/content",
                Description = description,
                CreatedBy = "system",
                DateCreated = DateTime.UtcNow,
                IsActive = true
            };

            // Create TeamsCollection wrapper (matches TeamService structure)
            var teamsCollection = new TeamsCollection
            {
                Teams = new List<Team> { defaultTeam },
                Version = "1",
                LastModified = DateTime.UtcNow,
                ModifiedBy = "system"
            };

            // Save to config folder (matches TeamService path: config/teams.json)
            var configFolder = Path.Combine(_basePath, "config");
            Directory.CreateDirectory(configFolder);

            var teamsFile = Path.Combine(configFolder, "teams.json");

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(teamsCollection, jsonOptions);
            await File.WriteAllTextAsync(teamsFile, json);

            System.Diagnostics.Debug.WriteLine($"✓ Teams collection file created: {teamsFile}");

            return defaultTeam;
        }

        private void CreateTeamFolderStructure(string teamFolderName)
        {
            var teamPath = Path.Combine(_basePath, teamFolderName);

            Directory.CreateDirectory(teamPath);
            Directory.CreateDirectory(Path.Combine(teamPath, "content", "presentations"));
            Directory.CreateDirectory(Path.Combine(teamPath, "content", "schedules"));
            Directory.CreateDirectory(Path.Combine(teamPath, "content", "assignments"));
            Directory.CreateDirectory(Path.Combine(teamPath, "images", "original"));
            Directory.CreateDirectory(Path.Combine(teamPath, "images", "generated"));
            Directory.CreateDirectory(Path.Combine(teamPath, "published"));
            Directory.CreateDirectory(Path.Combine(teamPath, "deleted"));

            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}");
            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}/content/presentations");
            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}/content/schedules");
            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}/content/assignments");
            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}/images/original");
            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}/images/generated");
            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}/published");
            System.Diagnostics.Debug.WriteLine($"  Created: {teamPath}/deleted");
        }

        /// <summary>
        /// Create the default admin user (admin/admin)
        /// </summary>
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
                UserID = Guid.NewGuid().ToString(),
                Username = username,
                DisplayName = "System Administrator",
                Email = $"{username}@newscentral.local",
                UPN = $"{username}@newscentral.local",
                PasswordHash = passwordHash,
                IsSystemAdmin = true,
                IsActive = true,
                DateCreated = DateTime.UtcNow,
                TeamRoles = new List<TeamRole>()
            };

            System.Diagnostics.Debug.WriteLine("Creating users collection...");

            // Create UsersCollection wrapper (matches AuthenticationService structure)
            var usersCollection = new UsersCollection
            {
                Users = new List<User> { adminUser },
                Version = "1",
                LastModified = DateTime.UtcNow,
                ModifiedBy = "system"
            };

            // Save to config folder (matches AuthenticationService path: config/users.json)
            var configFolder = Path.Combine(_basePath, "config");
            Directory.CreateDirectory(configFolder);

            var usersFile = Path.Combine(configFolder, "users.json");

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };

            var json = JsonSerializer.Serialize(usersCollection, jsonOptions);
            await File.WriteAllTextAsync(usersFile, json);

            System.Diagnostics.Debug.WriteLine($"✓ Users collection file created: {usersFile}");

            return adminUser;
        }

        /// <summary>
        /// Check if the application has been initialized
        /// </summary>
        public bool IsInitialized()
        {
            // Check for users in config folder
            var usersFile = Path.Combine(_basePath, "config", "users.json");
            var teamsFile = Path.Combine(_basePath, "config", "teams.json");

            return File.Exists(usersFile) && File.Exists(teamsFile);
        }

        public InitializationStatus GetInitializationStatus()
        {
            var status = new InitializationStatus
            {
                IsInitialized = IsInitialized(),
                BasePath = _basePath,
                BasePathExists = Directory.Exists(_basePath)
            };

            if (status.BasePathExists)
            {
                status.UsersFileExists = File.Exists(Path.Combine(_basePath, "users.json"));
                status.TeamsFileExists = File.Exists(Path.Combine(_basePath, "teams.json"));
            }

            return status;
        }
    }

    public class InitializationStatus
    {
        public bool IsInitialized { get; set; }
        public string BasePath { get; set; } = string.Empty;
        public bool BasePathExists { get; set; }
        public bool UsersFileExists { get; set; }
        public bool TeamsFileExists { get; set; }
    }
}