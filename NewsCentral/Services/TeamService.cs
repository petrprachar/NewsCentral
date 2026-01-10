using System.Text.RegularExpressions;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class TeamService
{
    private readonly JsonFileRepository<TeamsCollection> _teamRepo;
    private readonly AuthenticationService _authService;
    private readonly string _basePath;

    public TeamService(AppConfiguration config, AuthenticationService authService)
    {
        _authService = authService;
        _basePath = config.DataPath;
        _teamRepo = new JsonFileRepository<TeamsCollection>(
            Path.Combine(config.DataPath, "config"),
            ""
        );
    }

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

    public async Task<Team> CreateTeamAsync(string name, string description)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can create teams");
        }

        // Generate folder name from team name
        var folderName = GenerateFolderName(name);

        // Check if folder name already exists
        var existing = await GetTeamByFolderNameAsync(folderName);
        if (existing != null)
        {
            throw new InvalidOperationException($"Team with folder name '{folderName}' already exists");
        }

        var team = new Team
        {
            TeamID = Guid.NewGuid().ToString(),
            Name = name,
            FolderName = folderName,
            ContentPath = $"{folderName}/content",
            Description = description,
            CreatedBy = currentUser.UserID,
            DateCreated = DateTime.UtcNow,
            IsActive = true
        };

        // Create team folder structure
        await CreateTeamFolderStructureAsync(folderName);

        // Add to teams collection
        var teamsCollection = await _teamRepo.GetByIdAsync("teams");
        if (teamsCollection == null)
        {
            teamsCollection = new TeamsCollection
            {
                Teams = new List<Team>(),
                Version = "1",
                ModifiedBy = currentUser.UserID
            };
        }

        teamsCollection.Teams.Add(team);
        teamsCollection.LastModified = DateTime.UtcNow;
        teamsCollection.ModifiedBy = currentUser.UserID;

        if (await _teamRepo.ExistsAsync("teams"))
        {
            await _teamRepo.UpdateAsync(teamsCollection);
        }
        else
        {
            await _teamRepo.CreateAsync(teamsCollection);
        }

        return team;
    }

    public async Task<Team> UpdateTeamAsync(string teamId, string name, string description)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can update teams");
        }

        var teamsCollection = await _teamRepo.GetByIdAsync("teams");
        if (teamsCollection == null)
        {
            throw new InvalidOperationException("Teams collection not found");
        }

        var team = teamsCollection.Teams.FirstOrDefault(t => t.TeamID == teamId);
        if (team == null)
        {
            throw new InvalidOperationException($"Team {teamId} not found");
        }

        team.Name = name;
        team.Description = description;

        teamsCollection.LastModified = DateTime.UtcNow;
        teamsCollection.ModifiedBy = currentUser.UserID;

        await _teamRepo.UpdateAsync(teamsCollection);

        return team;
    }

    public async Task<bool> DeleteTeamAsync(string teamId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null || !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("Only SystemAdmin can delete teams");
        }

        var teamsCollection = await _teamRepo.GetByIdAsync("teams");
        if (teamsCollection == null)
        {
            return false;
        }

        var team = teamsCollection.Teams.FirstOrDefault(t => t.TeamID == teamId);
        if (team == null)
        {
            return false;
        }

        teamsCollection.Teams.Remove(team);
        teamsCollection.LastModified = DateTime.UtcNow;
        teamsCollection.ModifiedBy = currentUser.UserID;

        await _teamRepo.UpdateAsync(teamsCollection);

        return true;
    }

    private string GenerateFolderName(string teamName)
    {
        // Convert "Engineering Team" → "team-engineering"
        var sanitized = teamName.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        // Remove invalid characters for folder names
        sanitized = Regex.Replace(sanitized, @"[^a-z0-9\-]", "");

        // Remove "team" from the name if it's already there
        sanitized = sanitized.Replace("team-", "").Replace("-team", "");

        // Add "team-" prefix
        return $"team-{sanitized}";
    }

    private async Task CreateTeamFolderStructureAsync(string folderName)
    {
        var teamPath = Path.Combine(_basePath, folderName);

        var folders = new[]
        {
            Path.Combine(teamPath, "content", "presentations"),
            Path.Combine(teamPath, "content", "schedules"),
            Path.Combine(teamPath, "content", "assignments"),
            Path.Combine(teamPath, "content", "drafts"),
            Path.Combine(teamPath, "images", "original"),
            Path.Combine(teamPath, "images", "generated")
        };

        foreach (var folder in folders)
        {
            Directory.CreateDirectory(folder);
        }

        await Task.CompletedTask;
    }
}