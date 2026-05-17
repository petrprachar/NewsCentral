namespace NewsCentral.Models.IndexFile
{
    /// <summary>
    /// Summary statistics about published content
    /// Useful for client-side filtering and display
    /// </summary>
    public class IndexStatistics
    {
        /// <summary>
        /// Total number of published assignments in this index
        /// </summary>
        public int TotalPublishedAssignments { get; set; }

        /// <summary>
        /// Number of assignments currently active (within schedule window)
        /// </summary>
        public int ActiveAssignments { get; set; }

        /// <summary>
        /// Number of assignments scheduled for future display
        /// </summary>
        public int UpcomingAssignments { get; set; }

        /// <summary>
        /// Number of assignments that have expired (past end date)
        /// </summary>
        public int ExpiredAssignments { get; set; }
    }
}
