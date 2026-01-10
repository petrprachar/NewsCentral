using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class UserService
{
    private readonly JsonFileRepository<UsersCollection> _userRepo;
    private readonly AuthenticationService _authService;

    public UserService(AppConfiguration config, AuthenticationService authService)
    {
        _authService = authService;
        _userRepo = new JsonFileRepository<UsersCollection>(
            Path.Combine(config.DataPath, "config"),
            ""
        );
    }

    public async Task<List<User>> GetAllUsersAsync()
    {
        var usersCollection = await _userRepo.GetByIdAsync("users");
        return usersCollection?.Users ?? new List<User>();
    }

    public async Task<User?> GetUserByIdAsync(string userId)
    {
        var users = await GetAllUsersAsync();
        return users.FirstOrDefault(u => u.UserID == userId);
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        var users = await GetAllUsersAsync();
        return users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<User> CreateUserAsync(string username, string email, string displayName, string password, bool isSystemAdmin = false)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can create users");
        }

        // Check if username already exists
        var existing = await GetUserByUsernameAsync(username);
        if (existing != null)
        {
            throw new InvalidOperationException($"Username '{username}' already exists");
        }

        var user = new User
        {
            UserID = Guid.NewGuid().ToString(),
            Username = username,
            Email = email,
            DisplayName = displayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            IsSystemAdmin = isSystemAdmin,
            IsActive = true,
            DateCreated = DateTime.UtcNow,
            TeamRoles = new List<TeamRole>()
        };

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null)
        {
            throw new InvalidOperationException("Users collection not found");
        }

        usersCollection.Users.Add(user);
        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);

        return user;
    }

    public async Task<User> UpdateUserAsync(string userId, string email, string displayName, bool isActive)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can update users");
        }

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null)
        {
            throw new InvalidOperationException("Users collection not found");
        }

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null)
        {
            throw new InvalidOperationException($"User {userId} not found");
        }

        user.Email = email;
        user.DisplayName = displayName;
        user.IsActive = isActive;

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);

        return user;
    }

    public async Task<bool> ChangePasswordAsync(string userId, string newPassword)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        // Can change own password, or SystemAdmin can change anyone's
        if (currentUser.UserID != userId && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Cannot change another user's password");
        }

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null)
        {
            return false;
        }

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null)
        {
            return false;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);

        return true;
    }

    public async Task<bool> AssignUserToTeamAsync(string userId, string teamId, string teamFolderName, List<string> roles)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can assign users to teams");
        }

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null)
        {
            return false;
        }

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null)
        {
            return false;
        }

        // Remove existing team role if present
        user.TeamRoles.RemoveAll(tr => tr.TeamID == teamId);

        // Add new team role
        user.TeamRoles.Add(new TeamRole
        {
            TeamID = teamId,
            TeamFolderName = teamFolderName,
            Roles = roles
        });

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);

        return true;
    }

    public async Task<bool> RemoveUserFromTeamAsync(string userId, string teamId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can remove users from teams");
        }

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null)
        {
            return false;
        }

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null)
        {
            return false;
        }

        var removed = user.TeamRoles.RemoveAll(tr => tr.TeamID == teamId);

        if (removed > 0)
        {
            usersCollection.LastModified = DateTime.UtcNow;
            usersCollection.ModifiedBy = currentUser.UserID;

            await _userRepo.UpdateAsync(usersCollection);
        }

        return removed > 0;
    }

    public async Task<bool> UpdateTeamRolesAsync(string userId, string teamId, List<string> roles)
    {
        var currentUser = _authService.GetCurrentUser();

        // SystemAdmin can update any user's roles
        // TeamAdmin can update roles for their team (check in calling code)
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null)
        {
            return false;
        }

        var user = usersCollection.Users.FirstOrDefault(u => u.UserID == userId);
        if (user == null)
        {
            return false;
        }

        var teamRole = user.TeamRoles.FirstOrDefault(tr => tr.TeamID == teamId);
        if (teamRole == null)
        {
            return false;
        }

        // TeamAdmin cannot grant/revoke TeamAdmin role
        if (!currentUser.IsSystemAdmin)
        {
            if (roles.Contains("TeamAdmin") && !teamRole.Roles.Contains("TeamAdmin"))
            {
                throw new UnauthorizedAccessException("Only SystemAdmin can grant TeamAdmin role");
            }

            if (!roles.Contains("TeamAdmin") && teamRole.Roles.Contains("TeamAdmin"))
            {
                throw new UnauthorizedAccessException("Only SystemAdmin can revoke TeamAdmin role");
            }
        }

        teamRole.Roles = roles;

        usersCollection.LastModified = DateTime.UtcNow;
        usersCollection.ModifiedBy = currentUser.UserID;

        await _userRepo.UpdateAsync(usersCollection);

        return true;
    }

    public async Task<bool> DeleteUserAsync(string userId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can delete users");
        }

        // Cannot delete yourself
        if (currentUser.UserID == userId)
        {
            throw new InvalidOperationException("Cannot delete your own account");
        }

        var usersCollection = await _userRepo.GetByIdAsync("users");
        if (usersCollection == null)
        {
            return false;
        }

        var removed = usersCollection.Users.RemoveAll(u => u.UserID == userId);

        if (removed > 0)
        {
            usersCollection.LastModified = DateTime.UtcNow;
            usersCollection.ModifiedBy = currentUser.UserID;

            await _userRepo.UpdateAsync(usersCollection);
        }

        return removed > 0;
    }
}