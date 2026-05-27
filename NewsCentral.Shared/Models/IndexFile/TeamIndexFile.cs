using NewsCentral.Security;

namespace NewsCentral.Models.IndexFile
{
    public class TeamIndexFile : ISignable
    {
        public string TeamFolderName { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string Version { get; set; } = "1.0.0";
        public string IndexHash { get; set; } = string.Empty;
        public List<PublishedAssignmentIndex> PublishedAssignments { get; set; } = new();
        public IndexStatistics Statistics { get; set; } = new();
        public string? Signature { get; set; }
    }
}
