using NewsCentral.Models;

namespace NewsCentral.Publishing;

/// <summary>
/// PUB-2: pure predicate for whether an assignment belongs in a given team's index.json. A
/// team's own content/assignments folder holds both assignments TARGETED at that team (its own
/// Published assignments, and incoming cross-team copies — see PUB-1) and, for a team that
/// publishes to OTHER teams, that team's SOURCE records of those cross-team assignments
/// (TargetTeam != this team). Only the former belong in this team's index; the latter describe
/// content this team authored for someone else, not content for this team's own devices.
/// </summary>
public static class IndexInclusion
{
    public static bool Includes(Assignment assignment, string teamFolderName) =>
        assignment.Status == AssignmentStatus.Published &&
        string.Equals(assignment.TargetTeam, teamFolderName, StringComparison.OrdinalIgnoreCase);
}
