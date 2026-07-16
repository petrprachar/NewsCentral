namespace NewsCentral.Models
{
    public class Presentation : IEntity
    {
        public string PresentationID { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public string TeamID { get; set; } = string.Empty;
        public string TeamFolderName { get; set; } = string.Empty;

        public string OriginalImagePath { get; set; } = string.Empty;
        public string? GeneratedImagePath { get; set; }

        public string ImageOriginalName { get; set; } = string.Empty;
        public string ImageName { get; set; } = string.Empty;

        public bool IsNewsOfWeek { get; set; } = false;
        public bool IsWallpaper { get; set; } = false;
        public bool IsLogonScreen { get; set; } = false;

        public int DisplayDurationSeconds { get; set; } = PresentationDefaults.DisplayDurationSeconds;

        public bool UseVirtualDesktop { get; set; }

        public string VirtualDesktopBackgroundColor { get; set; } = "#000000";

        public string ContentImageBase64 { get; set; } = string.Empty;

        public string? PosterText { get; set; }

        public string MoreUrl { get; set; } = string.Empty;

        public string CreatedBy { get; set; } = string.Empty;
        public DateTime DateCreated { get; set; } = DateTime.UtcNow;
        public string ModifiedBy { get; set; } = string.Empty;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        public int Version { get; set; } = 1;

        public string GetId() => PresentationID;
        public void SetId(string id) => PresentationID = id;
    }
}
