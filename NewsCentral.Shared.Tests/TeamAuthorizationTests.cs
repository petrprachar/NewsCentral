using NewsCentral.Authorization;
using NewsCentral.Models;

namespace NewsCentral.Shared.Tests;

public sealed class TeamAuthorizationTests
{
    private static Team T(string id, bool active = true) =>
        new() { TeamID = id, Name = id.ToUpperInvariant(), FolderName = id, IsActive = active };

    [Fact]
    public void SystemAdmin_GetsAllActiveTeams()
    {
        var teams = new[] { T("a"), T("b"), T("c", active: false) };
        var admin = new User { IsSystemAdmin = true };

        var result = TeamAuthorization.AuthorableTeams(teams, admin);

        // All active teams, inactive excluded — regardless of TeamRoles.
        Assert.Equal(new[] { "a", "b" }, result.Select(t => t.TeamID));
    }

    [Fact]
    public void NonAdmin_GetsOnlyContentAuthorActiveTeams()
    {
        var teams = new[] { T("a"), T("b"), T("c"), T("d", active: false) };
        var user = new User
        {
            IsSystemAdmin = false,
            TeamRoles = new()
            {
                new TeamRole { TeamID = "a", Roles = new() { "ContentAuthor" } },
                new TeamRole { TeamID = "b", Roles = new() { "Approver" } },      // role present, not author
                new TeamRole { TeamID = "d", Roles = new() { "ContentAuthor" } }  // author but team inactive
            }
        };

        var result = TeamAuthorization.AuthorableTeams(teams, user);

        Assert.Equal(new[] { "a" }, result.Select(t => t.TeamID));
    }

    [Fact]
    public void NullUser_GetsNothing()
    {
        Assert.Empty(TeamAuthorization.AuthorableTeams(new[] { T("a") }, null));
    }
}
