using System;
using System.Linq;
using System.Threading.Tasks;
using NewsCentral.Models;
using NewsCentral.Repositories;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;


namespace NewsCentral.Services;

public class AuthenticationService
{
    private readonly JsonFileRepository<UsersCollection> _userRepo;
    private readonly AppConfiguration _config;
    private readonly WindowsIdentityService _windowsIdentityService;

    private User? _currentUser;

    public AuthenticationService(AppConfiguration config, WindowsIdentityService windowsIdentityService)
    {
        _config = config;
        _userRepo = new JsonFileRepository<UsersCollection>(
            Path.Combine(config.DataPath, "config"),
            ""
        );
        _windowsIdentityService = windowsIdentityService;
    }

    public async Task InitializeAsync()
    {
        // Check if users.json exists
        var usersExist = await _userRepo.ExistsAsync("users");

        if (!usersExist)
        {
            // First-time setup: Create default admin user
            await CreateDefaultAdminAsync();
        }
    }

    private async Task CreateDefaultAdminAsync()
    {
        var adminUser = new User
        {
            UserID = "admin-001",
            Username = _config.DefaultAdminUsername,
            Email = "admin@newscental.local",
            DisplayName = "System Administrator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(_config.DefaultAdminPassword),
            IsSystemAdmin = true,
            IsActive = true,
            DateCreated = DateTime.UtcNow,
            TeamRoles = new List<TeamRole>()
        };

        var usersCollection = new UsersCollection
        {
            Users = new List<User> { adminUser },
            Version = "1",
            LastModified = DateTime.UtcNow,
            ModifiedBy = "system"
        };

        await _userRepo.CreateAsync(usersCollection);

        Console.WriteLine("Default admin user created:");
        Console.WriteLine($"Username: {_config.DefaultAdminUsername}");
        Console.WriteLine($"Password: {_config.DefaultAdminPassword}");
        Console.WriteLine("IMPORTANT: Change password after first login!");
    }

    public async Task<User?> LoginAsync(string username, string password)
    {
        var usersCollection = await _userRepo.GetByIdAsync("users");

        if (usersCollection == null)
        {
            throw new InvalidOperationException("No users found in system");
        }

        var user = usersCollection.Users.FirstOrDefault(u =>
            u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) &&
            u.IsActive);

        if (user == null)
        {
            return null;
        }

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return null;
        }

        // Update last login
        user.LastLogin = DateTime.UtcNow;
        usersCollection.LastModified = DateTime.UtcNow;
        await _userRepo.UpdateAsync(usersCollection);

        _currentUser = user;
        return user;
    }

    public async Task<User?> TryAutoLoginAsync()
    {
        if (!_windowsIdentityService.IsAutoLoginEnabled())
        {
            System.Diagnostics.Debug.WriteLine("Auto-login is disabled");
            return null;
        }

        var upn = _windowsIdentityService.GetCurrentUserUPN();

        if (string.IsNullOrEmpty(upn))
        {
            System.Diagnostics.Debug.WriteLine("Could not detect UPN");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"Attempting auto-login with UPN: {upn}");

        // Load users collection (same pattern as LoginAsync)
        var usersCollection = await _userRepo.GetByIdAsync("users");

        if (usersCollection == null)
        {
            System.Diagnostics.Debug.WriteLine("No users collection found in system");
            return null;
        }

        // Search for user with matching UPN
        var user = usersCollection.Users.FirstOrDefault(u =>
            !string.IsNullOrEmpty(u.UPN) &&
            u.UPN.Equals(upn, StringComparison.OrdinalIgnoreCase) &&
            u.IsActive);  // ← Also check IsActive like LoginAsync does

        if (user != null)
        {
            // Update last login (same as LoginAsync)
            user.LastLogin = DateTime.UtcNow;
            usersCollection.LastModified = DateTime.UtcNow;
            await _userRepo.UpdateAsync(usersCollection);

            _currentUser = user;
            System.Diagnostics.Debug.WriteLine($"✓ Auto-login successful: {user.Username}");
            return user;
        }

        System.Diagnostics.Debug.WriteLine($"No active user found with UPN: {upn}");
        return null;
    }

    public void Logout()
    {
        _currentUser = null;
    }

    public User? GetCurrentUser()
    {
        return _currentUser;
    }

    public bool IsAuthenticated()
    {
        return _currentUser != null;
    }

    public bool IsSystemAdmin()
    {
        return _currentUser?.IsSystemAdmin ?? false;
    }

    public bool HasRole(string teamID, string role)
    {
        if (_currentUser == null)
            return false;

        if (_currentUser.IsSystemAdmin)
            return true;

        var teamRole = _currentUser.TeamRoles
            .FirstOrDefault(tr => tr.TeamID == teamID);

        return teamRole?.Roles.Contains(role) ?? false;
    }

    public bool HasAnyRole(string teamID, params string[] roles)
    {
        if (_currentUser == null)
            return false;

        if (_currentUser.IsSystemAdmin)
            return true;

        var teamRole = _currentUser.TeamRoles
            .FirstOrDefault(tr => tr.TeamID == teamID);

        return teamRole?.Roles.Any(r => roles.Contains(r)) ?? false;
    }
}