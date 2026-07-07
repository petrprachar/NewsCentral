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
}
