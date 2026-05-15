namespace NewsCentral.Models;

public class User : IEntity
{
    public string UserID { get; set; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? UPN { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsSystemAdmin { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
    public List<TeamRole> TeamRoles { get; set; } = new();

    // IEntity implementation
    public string GetId() => UserID;
    public void SetId(string id) => UserID = id;
}

public class TeamRole
{
    public string TeamID { get; set; } = string.Empty;
    public string TeamFolderName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
}

public class UsersCollection : IEntity
{
    public List<User> Users { get; set; } = new();
    public string Version { get; set; } = "1";
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public string ModifiedBy { get; set; } = string.Empty;

    // IEntity implementation (UsersCollection always has ID "users")
    public string GetId() => "users";
    public void SetId(string id) { } // No-op, ID is always "users"
}