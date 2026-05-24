using System;
using System.Collections.Generic;

namespace NewsCentral.Models.IndexFile
{
    /// <summary>
    /// Root structure for team index file (index.json)
    /// Contains all published assignments for efficient client consumption
    /// </summary>
    public class TeamIndexFile
    {
        /// <summary>
        /// Team folder name (e.g., "EXP_JP")
        /// </summary>
        public string TeamFolderName { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable team name (e.g., "Japan Express")
        /// </summary>
        public string TeamName { get; set; } = string.Empty;

        /// <summary>
        /// When this index file was generated (UTC)
        /// Clients can use this to determine if index has been updated
        /// </summary>
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Index file format version (for future compatibility)
        /// </summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// SHA256 hash of the index content (for change detection)
        /// Clients can compare this to avoid re-downloading unchanged indexes
        /// </summary>
        public string IndexHash { get; set; } = string.Empty;

        /// <summary>
        /// List of all published assignments for this team
        /// </summary>
        public List<PublishedAssignmentIndex> PublishedAssignments { get; set; } = new();

        /// <summary>
        /// Summary statistics about published content
        /// </summary>
        public IndexStatistics Statistics { get; set; } = new();
    }
}