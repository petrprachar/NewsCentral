using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class AssignmentService
{
    private readonly string _basePath;
    private readonly AuthenticationService _authService;
    private readonly TeamService _teamService;

    public AssignmentService(
        AppConfiguration config,
        AuthenticationService authService,
        TeamService teamService)
    {
        _basePath = config.DataPath;
        _authService = authService;
        _teamService = teamService;
    }

    public async Task<List<Assignment>> GetAssignmentsForTeamAsync(string teamFolderName)
    {
        var repo = new TeamAwareRepository<Assignment>(_basePath, teamFolderName, "assignments");
        return await repo.GetAllAsync();
    }

    public async Task<List<Assignment>> GetAllAssignmentsAsync()
    {
        // Get assignments from all teams
        var allTeams = await _teamService.GetAllTeamsAsync();
        var allAssignments = new List<Assignment>();

        foreach (var team in allTeams)
        {
            var teamAssignments = await GetAssignmentsForTeamAsync(team.FolderName);
            allAssignments.AddRange(teamAssignments);
        }

        return allAssignments;
    }

    public async Task<List<Assignment>> GetPendingApprovalsForUserAsync()
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            Console.WriteLine("DEBUG: User not authenticated");
            throw new UnauthorizedAccessException("Not authenticated");
        }

        Console.WriteLine($"DEBUG: Current user: {currentUser.Username}, IsSystemAdmin: {currentUser.IsSystemAdmin}");

        var allAssignments = await GetAllAssignmentsAsync();
        Console.WriteLine($"DEBUG: Total assignments loaded: {allAssignments.Count}");

        // Filter for PendingApproval status first
        var pendingAssignments = allAssignments
            .Where(a => a.Status == AssignmentStatus.PendingApproval)
            .ToList();

        Console.WriteLine($"DEBUG: Pending assignments found: {pendingAssignments.Count}");

        foreach (var a in pendingAssignments)
        {
            Console.WriteLine($"DEBUG: Assignment {a.AssignmentID} - Status: {a.Status}, TargetTeam: {a.TargetTeam}");
        }

        // SystemAdmin sees ALL pending approvals
        if (currentUser.IsSystemAdmin)
        {
            Console.WriteLine("DEBUG: User is SystemAdmin - returning all pending");
            return pendingAssignments;
        }

        // Regular users see approvals for teams where they have ContentApprover role
        var userApproverTeams = currentUser.TeamRoles
            .Where(tr => tr.Roles.Contains("ContentApprover") || tr.Roles.Contains("TeamAdmin"))
            .Select(tr => tr.TeamFolderName)
            .ToList();

        Console.WriteLine($"DEBUG: User approver teams: {string.Join(", ", userApproverTeams)}");

        return pendingAssignments
            .Where(a => userApproverTeams.Contains(a.TargetTeam))
            .ToList();
    }

    public async Task<Assignment?> GetAssignmentAsync(string teamFolderName, string assignmentId)
    {
        var repo = new TeamAwareRepository<Assignment>(_basePath, teamFolderName, "assignments");
        return await repo.GetByIdAsync(assignmentId);
    }

    public async Task<Assignment> CreateAssignmentAsync(
        string sourceTeamFolderName,
        string presentationId,
        string presentationVersion,
        string scheduleId,
        string targetTeamFolderName,
        bool requiresApproval)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var assignment = new Assignment
        {
            AssignmentID = Guid.NewGuid().ToString(),
            PresentationID = presentationId,
            PresentationVersion = presentationVersion,
            ScheduleID = scheduleId,
            SourceTeam = sourceTeamFolderName,
            TargetTeam = targetTeamFolderName,
            Status = requiresApproval ? AssignmentStatus.PendingApproval : AssignmentStatus.Approved,
            RequiresApproval = requiresApproval,
            CreatedBy = currentUser.UserID,
            DateCreated = DateTime.UtcNow
        };

        // Save in source team folder
        var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
        return await repo.CreateAsync(assignment);
    }

    public async Task<Assignment> ApproveAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId,
        string? approvalNotes = null)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId);

        if (assignment == null)
        {
            throw new InvalidOperationException($"Assignment {assignmentId} not found");
        }

        if (assignment.Status != AssignmentStatus.PendingApproval)
        {
            throw new InvalidOperationException($"Assignment is not pending approval (status: {assignment.Status})");
        }

        // Check if user is approver for target team
        if (!currentUser.IsSystemAdmin)
        {
            var hasApproverRole = currentUser.TeamRoles.Any(tr =>
                tr.TeamFolderName == assignment.TargetTeam &&
                tr.Roles.Contains("ContentApprover"));

            if (!hasApproverRole)
            {
                throw new UnauthorizedAccessException("You don't have permission to approve for this team");
            }
        }

        assignment.Status = AssignmentStatus.Approved;
        assignment.ApprovedBy = currentUser.UserID;
        assignment.ApprovedDate = DateTime.UtcNow;
        assignment.ApprovalNotes = approvalNotes;

        return await repo.UpdateAsync(assignment);
    }

    public async Task<Assignment> RejectAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId,
        string rejectionReason)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId);

        if (assignment == null)
        {
            throw new InvalidOperationException($"Assignment {assignmentId} not found");
        }

        if (assignment.Status != AssignmentStatus.PendingApproval)
        {
            throw new InvalidOperationException($"Assignment is not pending approval (status: {assignment.Status})");
        }

        // Check if user is approver for target team
        if (!currentUser.IsSystemAdmin)
        {
            var hasApproverRole = currentUser.TeamRoles.Any(tr =>
                tr.TeamFolderName == assignment.TargetTeam &&
                tr.Roles.Contains("ContentApprover"));

            if (!hasApproverRole)
            {
                throw new UnauthorizedAccessException("You don't have permission to reject for this team");
            }
        }

        assignment.Status = AssignmentStatus.Rejected;
        assignment.RejectedBy = currentUser.UserID;
        assignment.RejectedDate = DateTime.UtcNow;
        assignment.RejectionReason = rejectionReason;

        return await repo.UpdateAsync(assignment);
    }

    public async Task<Assignment> CancelAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId,
        string? cancellationReason = null)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId);

        if (assignment == null)
        {
            throw new InvalidOperationException($"Assignment {assignmentId} not found");
        }

        // Can only cancel if Draft or PendingApproval
        if (assignment.Status != AssignmentStatus.Draft &&
            assignment.Status != AssignmentStatus.PendingApproval)
        {
            throw new InvalidOperationException($"Cannot cancel assignment with status: {assignment.Status}");
        }

        // Only creator or SystemAdmin can cancel
        if (assignment.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only the creator can cancel this assignment");
        }

        assignment.Status = AssignmentStatus.Cancelled;
        assignment.CancelledBy = currentUser.UserID;
        assignment.CancelledDate = DateTime.UtcNow;
        assignment.CancellationReason = cancellationReason;

        return await repo.UpdateAsync(assignment);
    }

    /// <summary>
    /// Moves an assignment to the deleted folder instead of permanently deleting it
    /// </summary>
    public async Task DeleteAssignmentAsync(string sourceTeamFolderName, string assignmentId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
        var assignment = await repo.GetByIdAsync(assignmentId);

        if (assignment == null)
        {
            throw new InvalidOperationException($"Assignment {assignmentId} not found");
        }

        // Only creator or SystemAdmin can delete
        if (assignment.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only the creator or system admin can delete this assignment");
        }

        // Create deleted folder (single folder)
        var teamContentPath = Path.Combine(_basePath, sourceTeamFolderName, "content");
        var deletedPath = Path.Combine(teamContentPath, "deleted");
        Directory.CreateDirectory(deletedPath);

        // Move assignment file to deleted folder
        var assignmentSourcePath = Path.Combine(teamContentPath, "assignments", $"assign_{assignmentId}.json");
        var assignmentDestPath = Path.Combine(deletedPath, $"assign_{assignmentId}.json");

        if (File.Exists(assignmentSourcePath))
        {
            File.Move(assignmentSourcePath, assignmentDestPath, overwrite: true);
            System.Diagnostics.Debug.WriteLine($"✓ Moved assignment assign_{assignmentId} to deleted folder by {currentUser.Username}");
        }
        else
        {
            throw new InvalidOperationException($"Assignment file not found: {assignmentSourcePath}");
        }
    }
}