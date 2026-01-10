namespace NewsCentral.Models;

public class Presentation : IEntity
{
    public string PresentationID { get; set; } = Guid.NewGuid().ToString();
    public string Version { get; set; } = "1";
    public string TeamID { get; set; } = string.Empty;
    public string TeamFolderName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MoreUrl { get; set; } = string.Empty;
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public string ContentImageBase64 { get; set; } = string.Empty;
    public string ImageName { get; set; } = string.Empty;
    public string ImageOriginalName { get; set; } = string.Empty;  // ADD THIS LINE
    public string OriginalImagePath { get; set; } = string.Empty;
    public string GeneratedImagePath { get; set; } = string.Empty;
    public string DisplayTimeSeconds { get; set; } = "60";
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public string ModifiedBy { get; set; } = string.Empty;

    public string GetId() => PresentationID;
    public void SetId(string id) => PresentationID = id;
}