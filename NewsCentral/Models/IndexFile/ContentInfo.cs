using System;

namespace NewsCentral.Models.IndexFile
{
    /// <summary>
    /// Information about the content image and related files
    /// Used for downloading and caching
    /// </summary>
    public class ContentInfo
    {
        /// <summary>
        /// Relative path to image from team root
        /// Example: "images/generated/content_p5e6f7g8.jpg"
        /// </summary>
        public string ImagePath { get; set; } = string.Empty;

        /// <summary>
        /// Full URL to image (for Azure Blob Storage)
        /// Example: "https://storage.azure.com/newscentral/EXP_JP/images/generated/content_p5e6f7g8.jpg"
        /// </summary>
        public string? ImageUrl { get; set; }

        /// <summary>
        /// SHA256 hash of the image file
        /// Clients can use this to determine if they need to re-download
        /// Format: "sha256:abc123def456..."
        /// </summary>
        public string ImageHash { get; set; } = string.Empty;

        /// <summary>
        /// Image file size in bytes
        /// Helps clients decide download priority and caching strategy
        /// </summary>
        public long ImageSizeBytes { get; set; }

        /// <summary>
        /// When the image was last modified (UTC)
        /// </summary>
        public DateTime ImageLastModified { get; set; }

        /// <summary>
        /// Optional URL for "more information" link
        /// </summary>
        public string MoreInfoUrl { get; set; } = string.Empty;
    }
}