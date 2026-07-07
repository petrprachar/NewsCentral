using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NewsCentral.Models;
using NewsCentral.Repositories;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

public class AuthenticationService
{
    private readonly JsonFileRepository<UsersCollection> _userRepo;
    private readonly AppConfiguration _config;
    private readonly WindowsIdentityService _windowsIdentityService;
    private User? _currentUser;

    public event Action? OnAuthenticationStateChanged;

    public AuthenticationService(AppConfiguration config, WindowsIdentityService windowsIdentityService)
    {
        _config = config;
        _userRepo = new JsonFileRepository<UsersCollection>(
            Path.Combine(config.DataPath, "config"),
            ""
        );
        _windowsIdentityService = windowsIdentityService;
    }

    public async Task<User?> LoginAsync(string username, string password)
    {
        System.Diagnostics.Debug.WriteLine($"=== LoginAsync: {username} ===");

        try
        {
            System.Diagnostics.Debug.WriteLine("Loading users from repository...");
            var usersCollection = await _userRepo.GetByIdAsync("users");

            if (usersCollection == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: No users collection found");
                throw new InvalidOperationException("No users found in system");
            }

            System.Diagnostics.Debug.WriteLine($"Users collection loaded: {usersCollection.Users.Count} users found");

            var user = usersCollection.Users.FirstOrDefault(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) &&
                u.IsActive);

            if (user == null)
            {
                System.Diagnostics.Debug.WriteLine($"Login failed: User '{username}' not found or inactive");
                var availableUsers = string.Join(", ", usersCollection.Users.Select(u => $"{u.Username} (Active: {u.IsActive})"));
                System.Diagnostics.Debug.WriteLine($"Available users: {availableUsers}");
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"User found: {user.Username}, verifying password...");

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                System.Diagnostics.Debug.WriteLine($"Login failed: Invalid password for user '{username}'");
                return null;
            }

            System.Diagnostics.Debug.WriteLine("Password verified successfully");

            // Update last login
            user.LastLogin = DateTime.UtcNow;
            usersCollection.LastModified = DateTime.UtcNow;
            await _userRepo.UpdateAsync(usersCollection);

            _currentUser = user;
            System.Diagnostics.Debug.WriteLine($"Login successful: {user.Username} (IsSystemAdmin: {user.IsSystemAdmin})");

            OnAuthenticationStateChanged?.Invoke();

            return user;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Login error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
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

        var usersCollection = await _userRepo.GetByIdAsync("users");

        if (usersCollection == null)
        {
            System.Diagnostics.Debug.WriteLine("No users collection found in system");
            return null;
        }

        var user = usersCollection.Users.FirstOrDefault(u =>
            !string.IsNullOrEmpty(u.UPN) &&
            u.UPN.Equals(upn, StringComparison.OrdinalIgnoreCase) &&
            u.IsActive);

        if (user != null)
        {
            user.LastLogin = DateTime.UtcNow;
            usersCollection.LastModified = DateTime.UtcNow;
            await _userRepo.UpdateAsync(usersCollection);

            _currentUser = user;

            OnAuthenticationStateChanged?.Invoke();

            System.Diagnostics.Debug.WriteLine($"✓ Auto-login successful: {user.Username}");
            return user;
        }

        System.Diagnostics.Debug.WriteLine($"No active user found with UPN: {upn}");
        return null;
    }

    public void Logout()
    {
        _currentUser = null;
        OnAuthenticationStateChanged?.Invoke();
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

    /// <summary>
    /// True if the current user is a SystemAdmin, or holds the TeamAdmin role
    /// in at least one team. Used to gate access to the Users page, where a
    /// TeamAdmin may manage users and assign roles within the team(s) they admin.
    /// </summary>
    public bool IsTeamAdminOfAnyTeam()
    {
        if (_currentUser == null)
            return false;

        if (_currentUser.IsSystemAdmin)
            return true;

        return _currentUser.TeamRoles.Any(tr => tr.Roles.Contains("TeamAdmin"));
    }
}