namespace NewsCentral.Models.IndexFile
{
    public class PublishedAssignmentIndex
    {
        public string AssignmentId { get; set; } = string.Empty;
        public string PresentationId { get; set; } = string.Empty;
        public string ScheduleId { get; set; } = string.Empty;

        public string PresentationName { get; set; } = string.Empty;
        public string PresentationDescription { get; set; } = string.Empty;
        public int PresentationVersion { get; set; }
        public DateTime PresentationLastModified { get; set; }

        public DateTime ScheduleStart { get; set; }
        public DateTime ScheduleEnd { get; set; }
        public string DaysOfWeek { get; set; } = string.Empty;

        public DateTime PublishedDate { get; set; }
        public string PublishedBy { get; set; } = string.Empty;

        public DisplayTypeInfo DisplayTypes { get; set; } = new();
        public ContentInfo Content { get; set; } = new();

        public string SourceTeamFolderName { get; set; } = string.Empty;
        public string SourceTeamName { get; set; } = string.Empty;

        public string? PosterText { get; set; }
        public int DisplayDurationSeconds { get; set; }
        public Schedule.DisplayMode ShowMode { get; set; } = Schedule.DisplayMode.ShowOnce;
        public bool UseVirtualDesktop { get; set; }
        public string VirtualDesktopBackgroundColor { get; set; } = "#000000";
    }
}
