namespace NewsCentral.Models.IndexFile
{
    public class ContentInfo
    {
        public string ImagePath { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string ImageHash { get; set; } = string.Empty;
        public long ImageSizeBytes { get; set; }
        public DateTime ImageLastModified { get; set; }
        public string MoreInfoUrl { get; set; } = string.Empty;
    }
}
