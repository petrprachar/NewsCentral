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

    private static Assignment A(string sourceTeam, string createdBy = "someone-else") =>
        new() { AssignmentID = "a1", SourceTeam = sourceTeam, TargetTeam = "target-team", CreatedBy = createdBy };

    [Fact]
    public void CanDeleteAssignment_SystemAdmin_ReturnsTrue()
    {
        var admin = new User { IsSystemAdmin = true };

        Assert.True(TeamAuthorization.CanDeleteAssignment(A("cz-its"), admin));
    }

    [Fact]
    public void CanDeleteAssignment_Creator_ReturnsTrue()
    {
        var user = new User { UserID = "u1" };
        var assignment = A("cz-its", createdBy: "u1");

        Assert.True(TeamAuthorization.CanDeleteAssignment(assignment, user));
    }

    [Fact]
    public void CanDeleteAssignment_ContentAuthorOfSourceTeam_ReturnsTrue()
    {
        var user = new User
        {
            UserID = "u1",
            TeamRoles = new() { new TeamRole { TeamFolderName = "cz-its", Roles = new() { "ContentAuthor" } } }
        };
        var assignment = A("cz-its");

        Assert.True(TeamAuthorization.CanDeleteAssignment(assignment, user));
    }

    [Fact]
    public void CanDeleteAssignment_ContentAuthorOfTargetTeamOnly_ReturnsFalse()
    {
        var user = new User
        {
            UserID = "u1",
            TeamRoles = new() { new TeamRole { TeamFolderName = "target-team", Roles = new() { "ContentAuthor" } } }
        };
        var assignment = A("cz-its"); // TargetTeam = "target-team"

        Assert.False(TeamAuthorization.CanDeleteAssignment(assignment, user));
    }

    [Fact]
    public void CanDeleteAssignment_NoRole_ReturnsFalse()
    {
        var user = new User { UserID = "u1" };
        var assignment = A("cz-its");

        Assert.False(TeamAuthorization.CanDeleteAssignment(assignment, user));
    }

    [Fact]
    public void CanDeleteAssignment_NullUser_ReturnsFalse()
    {
        Assert.False(TeamAuthorization.CanDeleteAssignment(A("cz-its"), null));
    }
}
