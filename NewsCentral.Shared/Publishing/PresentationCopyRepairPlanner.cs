using NewsCentral.Models;

namespace NewsCentral.Publishing;

/// <summary>
/// PUB-1: pure decision logic for repairing a target team's copy of a cross-team PRESENTATION —
/// cleans up copies orphaned by deletes that predate <see cref="PresentationDeletePlanner"/>.
/// A copy keeps the source's <c>TeamFolderName</c> (publish serializes it unchanged); a presentation
/// created via Duplicate carries its own team and is never a copy.
/// </summary>
public static class PresentationCopyRepairPlanner
{
    /// <param name="presentation">The presentation found in <paramref name="containingTeam"/>'s own folder.</param>
    /// <param name="containingTeam">The team folder it was found in.</param>
    /// <param name="sourceExists">Whether <c>{TeamFolderName}/content/presentations/pres_{id}.json</c> exists.</param>
    public static RepairPlan Plan(Presentation presentation, string containingTeam, bool sourceExists)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        if (string.IsNullOrEmpty(presentation.TeamFolderName))
            return new RepairPlan(RepairAction.Leave, "no source team recorded");

        if (string.Equals(presentation.TeamFolderName, containingTeam, StringComparison.OrdinalIgnoreCase))
            return new RepairPlan(RepairAction.InSync, "Presentation belongs to this team (not a copy)");

        return sourceExists
            ? new RepairPlan(RepairAction.InSync, "Source presentation still exists")
            : new RepairPlan(RepairAction.Remove, "Source presentation is missing (deleted)");
    }
}
