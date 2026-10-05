namespace NewsCentral.Models;

/// <summary>
/// Identity of an assignment batch — every <see cref="Assignment"/> created by one Create
/// Assignment submission shares exactly one <see cref="ScheduleID"/> (CreateAssignment.razor
/// creates the schedule once, then one assignment per selected team against it), so this triple
/// is precisely "the set of assignments one Create Assignment click produced." An empty
/// <see cref="ScheduleID"/> is not special-cased — it groups on the other two fields exactly like
/// any other value would.
/// </summary>
public sealed record AssignmentBatchKey(string SourceTeam, string PresentationID, string ScheduleID);

/// <summary>One batch — all assignments sharing an <see cref="AssignmentBatchKey"/>.</summary>
public sealed class AssignmentBatch
{
    public AssignmentBatchKey Key { get; }

    /// <summary>Ordered by <see cref="Assignment.TargetTeam"/> (ordinal, case-insensitive).</summary>
    public IReadOnlyList<Assignment> Assignments { get; }

    public int Count => Assignments.Count;

    /// <summary>The earliest <see cref="Assignment.DateCreated"/> among the batch's assignments.</summary>
    public DateTime CreatedUtc { get; }

    public AssignmentBatch(AssignmentBatchKey key, IReadOnlyList<Assignment> assignments, DateTime createdUtc)
    {
        Key = key;
        Assignments = assignments;
        CreatedUtc = createdUtc;
    }
}

/// <summary>
/// UI-2: pure, no I/O — groups a flat assignment list into <see cref="AssignmentBatch"/>es keyed by
/// <see cref="AssignmentBatchKey"/> (SourceTeam, PresentationID, ScheduleID).
/// </summary>
public static class AssignmentBatchGrouper
{
    /// <summary>Batches ordered by <see cref="AssignmentBatch.CreatedUtc"/> descending (newest first).</summary>
    public static IReadOnlyList<AssignmentBatch> Group(IEnumerable<Assignment> assignments)
    {
        return assignments
            .GroupBy(a => new AssignmentBatchKey(a.SourceTeam, a.PresentationID, a.ScheduleID))
            .Select(g =>
            {
                var ordered = g
                    .OrderBy(a => a.TargetTeam, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var createdUtc = g.Min(a => a.DateCreated);
                return new AssignmentBatch(g.Key, ordered, createdUtc);
            })
            .OrderByDescending(b => b.CreatedUtc)
            .ToList();
    }
}
