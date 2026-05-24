using System;

namespace NewsCentral.Models.IndexFile
{
    /// <summary>
    /// Represents a single published assignment in the index
    /// Contains all information clients need to display content
    /// </summary>
    public class PublishedAssignmentIndex
    {
        // Assignment Identifiers
        public string AssignmentId { get; set; } = string.Empty;
        public string PresentationId { get; set; } = string.Empty;
        public string ScheduleId { get; set; } = string.Empty;

        // Presentation Information
        public string PresentationName { get; set; } = string.Empty;
        public string PresentationDescription { get; set; } = string.Empty;
        public int PresentationVersion { get; set; }

        /// <summary>
        /// When presentation was last modified (UTC)
        /// Used for cache invalidation
        /// </summary>
        public DateTime PresentationLastModified { get; set; }

        // Schedule Information (LOCAL TIME - no timezone)
        /// <summary>
        /// Start date/time in CLIENT'S LOCAL TIME
        /// Format: "2026-05-18T09:00:00" (no Z suffix)
        /// Client interprets this as 9 AM in their timezone
        /// </summary>
        public DateTime ScheduleStart { get; set; }

        /// <summary>
        /// End date/time in CLIENT'S LOCAL TIME
        /// Format: "2026-06-15T17:00:00" (no Z suffix)
        /// Client interprets this as 5 PM in their timezone
        /// </summary>
        public DateTime ScheduleEnd { get; set; }

        /// <summary>
        /// Comma-separated days of week (1=Mon, 2=Tue, ..., 7=Sun)
        /// Example: "1,2,3,4,5" for weekdays
        /// </summary>
        public string DaysOfWeek { get; set; } = string.Empty;

        // Publication Information (UTC)
        /// <summary>
        /// When this assignment was published (UTC)
        /// </summary>
        public DateTime PublishedDate { get; set; }

        /// <summary>
        /// Who published this assignment
        /// </summary>
        public string PublishedBy { get; set; } = string.Empty;

        // Display Type Flags
        public DisplayTypeInfo DisplayTypes { get; set; } = new();

        // Content Information
        public ContentInfo Content { get; set; } = new();

        // Source Team Information (for cross-team assignments)
        public string SourceTeamFolderName { get; set; } = string.Empty;
        public string SourceTeamName { get; set; } = string.Empty;
    }
}
