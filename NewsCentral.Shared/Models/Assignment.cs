namespace NewsCentral.Models;

public class Assignment : IEntity
{
    public string AssignmentID { get; set; } = Guid.NewGuid().ToString();
    public string PresentationID { get; set; } = string.Empty;
    public string PresentationVersion { get; set; } = "1";
    public string ScheduleID { get; set; } = string.Empty;
    public string SourceTeam { get; set; } = string.Empty;
    public string TargetTeam { get; set; } = string.Empty;
    public AssignmentStatus Status { get; set; } = AssignmentStatus.Draft;
    public bool RequiresApproval { get; set; } = true;

    public bool IsNewsOfWeek { get; set; } = true;
    public bool IsWallpaper { get; set; } = false;
    public bool IsLogonScreen { get; set; } = false;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public string? ApprovalNotes { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedDate { get; set; }
    public List<string> PublishedPaths { get; set; } = new();
    public string? CancelledBy { get; set; }
    public DateTime? CancelledDate { get; set; }
    public string? CancellationReason { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedDate { get; set; }
    public string? RejectionReason { get; set; }

    public string GetId() => AssignmentID;
    public void SetId(string id) => AssignmentID = id;
}

public enum AssignmentStatus
{
    Draft,
    PendingApproval,
    Approved,
    Published,
    Cancelled,
    Rejected
}
