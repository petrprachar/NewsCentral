using System.Text.RegularExpressions;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class TeamService
{
    private readonly IStorageService _storage;
    private readonly JsonFileRepository<TeamsCollection> _teamRepo;
    private readonly AuthenticationService _authService;

    public TeamService(IStorageService storage, AuthenticationService authService)
    {
        _storage     = storage;
        _authService = authService;

        // Primary JsonFileRepository constructor — no [Obsolete] warning
        _teamRepo = new JsonFileRepository<TeamsCollection>(_storage, "config", "");
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public async Task<List<Team>> GetAllTeamsAsync()
    {
        var teamsCollection = await _teamRepo.GetByIdAsync("teams");
        return teamsCollection?.Teams ?? new List<Team>();
    }

    public async Task<Team?> GetTeamByIdAsync(string teamId)
    {
        var teams = await GetAllTeamsAsync();
        return teams.FirstOrDefault(t => t.TeamID == teamId);
    }

    public async Task<Team?> GetTeamByFolderNameAsync(string folderName)
    {
        var teams = await GetAllTeamsAsync();
        return teams.FirstOrDefault(t => t.FolderName == folderName);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public async Task<Team> CreateTeamAsync(string name, string description)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Only SystemAdmin can create teams");

        var folderName = GenerateFolderName(name);

        if (string.IsNullOrWhiteSpace(folderName))
            throw new InvalidOperationException(
                "Team name must contain at least one letter or digit");

        var existing = await GetTeamByFolderNameAsync(folderName);
        if (existing != null)
            throw new InvalidOperationException(
                $"Team with folder name '{folderName}' already exists");

        var team = new Team
        {
            TeamID      = Guid.NewGuid().ToString(),
            Name        = name,
            FolderName  = folderName,
            ContentPath = $"{folderName}/content",
            Description = description,
            CreatedBy   = currentUser.UserID,
            DateCreated = DateTime.UtcNow,
            IsActive    = true
        };

        // Create folder structure on authoring tier before saving the record
        await CreateTeamFolderStructureAsync(folderName);

        var teamsCollection = await _teamRepo.GetByIdAsync("teams");
        if (teamsCollection == null)
        {
            teamsCollection = new TeamsCollection
            {
                Teams      = new List<Team>(),
                Version    = "1",
                ModifiedBy = currentUser.UserID
            };
        }

        teamsCollection.Teams.Add(team);
        teamsCollection.LastModified = DateTime.UtcNow;
        teamsCollection.ModifiedBy   = currentUser.UserID;

        if (await _teamRepo.ExistsAsync("teams"))
            await _teamRepo.UpdateAsync(teamsCollection);
        else
            await _teamRepo.CreateAsync(teamsCollection);

        return team;
    }

    public async Task<Team> UpdateTeamAsync(string teamId, string name, string description)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Only SystemAdmin can update teams");

        var teamsCollection = await _teamRepo.GetByIdAsync("teams")
            ?? throw new InvalidOperationException("Teams collection not found");

        var team = teamsCollection.Teams.FirstOrDefault(t => t.TeamID == teamId)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        team.Name        = name;
        team.Description = description;

        teamsCollection.LastModified = DateTime.UtcNow;
        teamsCollection.ModifiedBy   = currentUser.UserID;

        await _teamRepo.UpdateAsync(teamsCollection);
        return team;
    }

    public async Task<bool> DeleteTeamAsync(string teamId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("Only SystemAdmin can delete teams");

        var teamsCollection = await _teamRepo.GetByIdAsync("teams");
        if (teamsCollection == null) return false;

        var team = teamsCollection.Teams.FirstOrDefault(t => t.TeamID == teamId);
        if (team == null) return false;

        teamsCollection.Teams.Remove(team);
        teamsCollection.LastModified = DateTime.UtcNow;
        teamsCollection.ModifiedBy   = currentUser.UserID;

        await _teamRepo.UpdateAsync(teamsCollection);
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string GenerateFolderName(string teamName)
    {
        var sanitized = teamName.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        sanitized = Regex.Replace(sanitized, @"[^a-z0-9\-]", "");
        sanitized = sanitized.Replace("team-", "").Replace("-team", "");

        return sanitized;
    }

    /// <summary>
    /// Creates the standard folder structure for a new team on the authoring tier.
    /// EnsureFolderExistsAsync is a no-op for Azure Blob Storage.
    /// </summary>
    private async Task CreateTeamFolderStructureAsync(string folderName)
    {
        var folders = new[]
        {
            $"{folderName}/content/presentations",
            $"{folderName}/content/schedules",
            $"{folderName}/content/assignments",
            $"{folderName}/content/drafts",
            $"{folderName}/images/original",
            $"{folderName}/images/generated",
            $"{folderName}/deleted"
        };

        foreach (var folder in folders)
            await _storage.EnsureFolderExistsAsync(folder);
    }
}
