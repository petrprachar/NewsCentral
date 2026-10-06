using NewsCentral.Models;

namespace NewsCentral.Authorization;

/// <summary>
/// Pure authorization helpers over the team/role model — headless-testable, no I/O.
/// </summary>
public static class TeamAuthorization
{
    public const string ContentAuthorRole = "ContentAuthor";

    /// <summary>
    /// The active teams the given user may author content for: <b>all</b> active teams for a
    /// SystemAdmin, otherwise the active teams where the user holds the
    /// <see cref="ContentAuthorRole"/>. A null user (not signed in) authors for nothing.
    /// Mirrors the service-side check in <c>PresentationService.CreatePresentationAsync</c>
    /// (<c>HasRole(team,"ContentAuthor") || IsSystemAdmin</c>).
    /// </summary>
    public static List<Team> AuthorableTeams(IEnumerable<Team> teams, User? user)
    {
        if (user is null) return new List<Team>();

        var active = teams.Where(t => t.IsActive);

        if (user.IsSystemAdmin) return active.ToList();

        return active
            .Where(t => user.TeamRoles.Any(tr =>
                tr.TeamID == t.TeamID && tr.Roles.Contains(ContentAuthorRole)))
            .ToList();
    }

    /// <summary>
    /// UI-2.3: whether the given user may delete the given assignment — mirrors
    /// <c>AssignmentService.DeleteAssignmentAsync</c>'s own check exactly: SystemAdmin, OR the
    /// creator, OR a <see cref="ContentAuthorRole"/> of the assignment's SOURCE team (never the
    /// team a viewer happens to be looking at — a target-team ContentAuthor with no role on the
    /// source team is not covered). Same rule regardless of the assignment's status; the service
    /// call is still the authority, this only has to agree with it so the UI never shows a
    /// Delete control that the service would then reject.
    /// </summary>
    public static bool CanDeleteAssignment(Assignment assignment, User? user)
    {
        if (user is null) return false;
        if (user.IsSystemAdmin) return true;
        if (assignment.CreatedBy == user.UserID) return true;

        return user.TeamRoles.Any(tr =>
            tr.TeamFolderName == assignment.SourceTeam &&
            tr.Roles.Contains(ContentAuthorRole));
    }
}
