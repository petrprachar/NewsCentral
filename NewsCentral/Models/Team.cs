namespace NewsCentral.Models;

public class Team : IEntity
{
    public string TeamID { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string ContentPath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    public string GetId() => TeamID;
    public void SetId(string id) => TeamID = id;
}

public class TeamsCollection : IEntity
{
    public List<Team> Teams { get; set; } = new();
    public string Version { get; set; } = "1";
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public string ModifiedBy { get; set; } = string.Empty;

    public string GetId() => "teams";
    public void SetId(string id) { } // No-op, ID is always "teams"
}