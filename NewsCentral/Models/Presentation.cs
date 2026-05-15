using System;


namespace NewsCentral.Models
{
    public class Presentation : IEntity
    {
        public string PresentationID { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Team information
        public string TeamID { get; set; } = string.Empty;
        public string TeamFolderName { get; set; } = string.Empty;

        // Image paths
        public string OriginalImagePath { get; set; } = string.Empty;
        public string? GeneratedImagePath { get; set; }

        // Image file names
        public string ImageOriginalName { get; set; } = string.Empty;
        public string ImageName { get; set; } = string.Empty;

        // Base64 content
        public string ContentImageBase64 { get; set; } = string.Empty;

        // Poster text (for regeneration or reference)
        public string? PosterText { get; set; }

        public string MoreUrl { get; set; } = string.Empty;

        // Audit fields
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime DateCreated { get; set; } = DateTime.UtcNow;
        public string ModifiedBy { get; set; } = string.Empty;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        public int Version { get; set; } = 1;

        // IEntity implementation
        public string GetId() => PresentationID;
        public void SetId(string id) => PresentationID = id;
    }
}