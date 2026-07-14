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

    // Reserved for a future priority-display feature; read by nothing today. 0 = normal,
    // ascending = more urgent. Deliberately int, not an enum: JsonStringEnumConverter throws
    // on an unknown enum string, so adding a member later would make older clients reject the
    // entire index. Emitted into the signed index so the wire shape is stable from now on.
    public int Priority { get; set; } = 0;

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

    public string? Signature { get; set; }

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
