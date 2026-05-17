using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NewsCentral.Services;

public class PresentationService
{
    private readonly string _basePath;
    private readonly AuthenticationService _authService;
    private readonly PosterGenerationService _posterService;
    private readonly IndexGenerationService _indexGenerationService;

    public PresentationService(
        AppConfiguration config,
        AuthenticationService authService,
        PosterGenerationService posterService,
        IndexGenerationService indexGenerationService)
    {
        _basePath = config.DataPath;
        _authService = authService;
        _posterService = posterService;
        _indexGenerationService = indexGenerationService;
    }

    public async Task<Presentation> UpdatePresentationWithPosterAsync(
        string teamFolderName,
        string presentationId,
        string headlineText,
        string bodyText,
        string ctaText,
        bool isNewsOfWeek,
        bool isWallpaper,
        bool isLogonScreen)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId);

        if (presentation == null)
        {
            throw new InvalidOperationException($"Presentation {presentationId} not found");
        }

        // Only creator or SystemAdmin can edit
        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("You can only edit your own presentations");
        }

        // Load original image
        var originalImagePath = Path.Combine(_basePath, presentation.OriginalImagePath);
        var originalImageData = await File.ReadAllBytesAsync(originalImagePath);

        // Generate poster
        var posterPath = await _posterService.GeneratePosterAsync(
            teamFolderName,
            presentationId,
            presentation.Version.ToString(),
            originalImageData,
            headlineText,
            bodyText,
            ctaText
        );

        // Get poster as base64 for JSON storage
        var posterImageData = await _posterService.GetPosterImageDataAsync(teamFolderName, posterPath);
        var posterBase64 = Convert.ToBase64String(posterImageData);

        // Update presentation
        presentation.GeneratedImagePath = posterPath;
        presentation.ContentImageBase64 = posterBase64;
        presentation.LastModified = DateTime.UtcNow;
        presentation.ModifiedBy = currentUser.UserID;

        // Update display flags
        presentation.IsNewsOfWeek = isNewsOfWeek;
        presentation.IsWallpaper = isWallpaper;
        presentation.IsLogonScreen = isLogonScreen;

        var result = await repo.UpdateAsync(presentation);

        // NEW: Regenerate indexes if presentation has published assignments
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);

        return result;
    }

    /// <summary>
    /// Update presentation with all fields including display flags (no poster generation)
    /// </summary>
    public async Task<Presentation> UpdatePresentationFullAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl,
        bool isNewsOfWeek,
        bool isWallpaper,
        bool isLogonScreen)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId);

        if (presentation == null)
        {
            throw new InvalidOperationException($"Presentation {presentationId} not found");
        }

        // Only creator or SystemAdmin can edit
        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("You can only edit your own presentations");
        }

        // Update basic fields
        presentation.Name = name;
        presentation.Description = description;
        presentation.MoreUrl = moreUrl;

        // Update display flags
        presentation.IsNewsOfWeek = isNewsOfWeek;
        presentation.IsWallpaper = isWallpaper;
        presentation.IsLogonScreen = isLogonScreen;

        // Update audit fields
        presentation.LastModified = DateTime.UtcNow;
        presentation.ModifiedBy = currentUser.UserID;

        // If no poster exists, copy original image to ContentImageBase64
        if (string.IsNullOrEmpty(presentation.ContentImageBase64) &&
            !string.IsNullOrEmpty(presentation.OriginalImagePath))
        {
            var originalImagePath = Path.Combine(_basePath, presentation.OriginalImagePath);
            if (File.Exists(originalImagePath))
            {
                var imageData = await File.ReadAllBytesAsync(originalImagePath);
                presentation.ContentImageBase64 = Convert.ToBase64String(imageData);

                // Also save to generated folder
                var generatedFolderPath = Path.Combine(_basePath, teamFolderName, "images", "generated");
                Directory.CreateDirectory(generatedFolderPath);

                var contentImageFileName = $"content_{presentation.PresentationID}.jpg";
                var contentImagePath = Path.Combine(generatedFolderPath, contentImageFileName);
                await File.WriteAllBytesAsync(contentImagePath, imageData);

                presentation.GeneratedImagePath = $"{teamFolderName}/images/generated/{contentImageFileName}";
            }
        }

        var result = await repo.UpdateAsync(presentation);

        // NEW: Regenerate indexes if presentation has published assignments
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);

        return result;
    }

    public async Task<List<Presentation>> GetPresentationsForTeamAsync(string teamFolderName)
    {
        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        return await repo.GetAllAsync();
    }

    public async Task<Presentation?> GetPresentationAsync(string teamFolderName, string presentationId)
    {
        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        return await repo.GetByIdAsync(presentationId);
    }

    public async Task<Presentation> CreatePresentationAsync(
        string teamId,
        string teamFolderName,
        string name,
        string description,
        string moreUrl,
        byte[] imageData,
        string originalImageName)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        // Check if user has ContentAuthor role for this team
        if (!_authService.HasRole(teamId, "ContentAuthor") && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("You don't have permission to create content for this team");
        }

        // Save original image
        var imageId = Guid.NewGuid().ToString();
        var imageExtension = Path.GetExtension(originalImageName);
        var savedImageName = $"img_{imageId}{imageExtension}";
        var imagePath = Path.Combine(_basePath, teamFolderName, "images", "original", savedImageName);

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, imageData);

        // Create presentation
        var presentation = new Presentation
        {
            PresentationID = Guid.NewGuid().ToString(),
            Version = 1,
            TeamID = teamId,
            TeamFolderName = teamFolderName,
            Name = name,
            Description = description,
            MoreUrl = moreUrl,
            DateCreated = DateTime.UtcNow,
            OriginalImagePath = $"{teamFolderName}/images/original/{savedImageName}",
            ImageOriginalName = originalImageName,
            ImageName = savedImageName,
            CreatedBy = currentUser.UserID,
            LastModified = DateTime.UtcNow,
            ModifiedBy = currentUser.UserID,

            ContentImageBase64 = Convert.ToBase64String(imageData)
        };

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        return await repo.CreateAsync(presentation);
    }

    public async Task<Presentation> UpdatePresentationAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId);

        if (presentation == null)
        {
            throw new InvalidOperationException($"Presentation {presentationId} not found");
        }

        // Only creator or SystemAdmin can edit
        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
        {
            throw new UnauthorizedAccessException("You can only edit your own presentations");
        }

        presentation.Name = name;
        presentation.Description = description;
        presentation.MoreUrl = moreUrl;
        presentation.LastModified = DateTime.UtcNow;
        presentation.ModifiedBy = currentUser.UserID;

        var result = await repo.UpdateAsync(presentation);

        // NEW: Regenerate indexes if presentation has published assignments
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);

        return result;
    }

    /// <summary>
    /// Moves a presentation and all its related assignments and schedules to the deleted folder
    /// </summary>
    public async Task<bool> DeletePresentationAsync(string teamFolderName, string presentationId)
    {
        var currentUser = _authService.GetCurrentUser();
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }

        var repo = new TeamAwareRepository<Presentation>(_basePath, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId);

        if (presentation == null)
        {
            throw new InvalidOperationException($"Presentation {presentationId} not found");
        }

        // Check permissions
        if (!_authService.IsSystemAdmin() && presentation.CreatedBy != currentUser.UserID)
        {
            throw new UnauthorizedAccessException("You don't have permission to delete this presentation");
        }

        System.Diagnostics.Debug.WriteLine($"=== DeletePresentation: {presentationId} ===");

        // Store information for index regeneration BEFORE deletion
        var targetTeamsWithPublishedAssignments = await GetTargetTeamsForPresentationAsync(teamFolderName, presentationId);

        // Create deleted folder at team root level (NOT under content)
        var teamRootPath = Path.Combine(_basePath, teamFolderName);
        var deletedPath = Path.Combine(teamRootPath, "deleted");
        Directory.CreateDirectory(deletedPath);

        var teamContentPath = Path.Combine(teamRootPath, "content");

        // Move presentation file to deleted folder
        var presentationSourcePath = Path.Combine(teamContentPath, "presentations", $"pres_{presentationId}.json");
        var presentationDestPath = Path.Combine(deletedPath, $"pres_{presentationId}.json");

        if (File.Exists(presentationSourcePath))
        {
            File.Move(presentationSourcePath, presentationDestPath, overwrite: true);
            System.Diagnostics.Debug.WriteLine($"✓ Moved presentation pres_{presentationId} to deleted folder");
        }

        // Find and move all related assignments
        var assignmentRepo = new TeamAwareRepository<Assignment>(_basePath, teamFolderName, "assignments");
        var allAssignments = await assignmentRepo.GetAllAsync();
        var relatedAssignments = allAssignments.Where(a => a.PresentationID == presentationId).ToList();

        foreach (var assignment in relatedAssignments)
        {
            var assignmentSourcePath = Path.Combine(teamContentPath, "assignments", $"assign_{assignment.AssignmentID}.json");
            var assignmentDestPath = Path.Combine(deletedPath, $"assign_{assignment.AssignmentID}.json");

            if (File.Exists(assignmentSourcePath))
            {
                File.Move(assignmentSourcePath, assignmentDestPath, overwrite: true);
                System.Diagnostics.Debug.WriteLine($"✓ Moved assignment assign_{assignment.AssignmentID} to deleted folder");
            }
        }

        // Find and move all related schedules
        var scheduleRepo = new TeamAwareRepository<Schedule>(_basePath, teamFolderName, "schedules");
        var allSchedules = await scheduleRepo.GetAllAsync();
        var relatedSchedules = allSchedules.Where(s => s.PresentationID == presentationId).ToList();

        foreach (var schedule in relatedSchedules)
        {
            var scheduleSourcePath = Path.Combine(teamContentPath, "schedules", $"sched_{schedule.ScheduleID}.json");
            var scheduleDestPath = Path.Combine(deletedPath, $"sched_{schedule.ScheduleID}.json");

            if (File.Exists(scheduleSourcePath))
            {
                File.Move(scheduleSourcePath, scheduleDestPath, overwrite: true);
                System.Diagnostics.Debug.WriteLine($"✓ Moved schedule sched_{schedule.ScheduleID} to deleted folder");
            }
        }

        System.Diagnostics.Debug.WriteLine($"✓ Presentation {presentationId} and {relatedAssignments.Count} assignments, {relatedSchedules.Count} schedules moved to deleted folder");

        // NEW: Regenerate indexes for all target teams that had published assignments
        if (targetTeamsWithPublishedAssignments.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"→ Regenerating indexes for {targetTeamsWithPublishedAssignments.Count} target team(s)");

            foreach (var targetTeam in targetTeamsWithPublishedAssignments)
            {
                try
                {
                    await _indexGenerationService.GenerateAndSaveIndexAsync(targetTeam);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Index regenerated for {targetTeam}");
                }
                catch (Exception indexEx)
                {
                    System.Diagnostics.Debug.WriteLine($"  ⚠ WARNING: Index generation failed for {targetTeam}: {indexEx.Message}");
                }
            }
        }

        return true;
    }

    public async Task<byte[]> GetImageDataAsync(string teamFolderName, string imagePath)
    {
        var fullPath = Path.Combine(_basePath, imagePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Image not found: {imagePath}");
        }

        return await File.ReadAllBytesAsync(fullPath);
    }

    /// <summary>
    /// Regenerate indexes for all teams that have published assignments using this presentation
    /// </summary>
    private async Task RegenerateIndexesForPresentationAsync(string sourceTeamFolderName, string presentationId)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"→ Checking for published assignments of presentation {presentationId}");

            // Get all target teams that have published assignments for this presentation
            var targetTeams = await GetTargetTeamsForPresentationAsync(sourceTeamFolderName, presentationId);

            if (targetTeams.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"  Found {targetTeams.Count} target team(s) with published assignments");

                foreach (var targetTeam in targetTeams)
                {
                    try
                    {
                        await _indexGenerationService.GenerateAndSaveIndexAsync(targetTeam);
                        System.Diagnostics.Debug.WriteLine($"  ✓ Index regenerated for {targetTeam}");
                    }
                    catch (Exception indexEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ⚠ WARNING: Index generation failed for {targetTeam}: {indexEx.Message}");
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"  No published assignments found - no index regeneration needed");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"  ⚠ WARNING: Index regeneration check failed: {ex.Message}");
            // Don't fail the update operation if index regeneration fails
        }
    }

    /// <summary>
    /// Get list of target teams that have published assignments for this presentation
    /// </summary>
    private async Task<List<string>> GetTargetTeamsForPresentationAsync(string sourceTeamFolderName, string presentationId)
    {
        var targetTeams = new List<string>();

        try
        {
            // Get all assignments for this presentation
            var assignmentRepo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
            var allAssignments = await assignmentRepo.GetAllAsync();

            // Find published assignments for this presentation
            var publishedAssignments = allAssignments
                .Where(a => a.PresentationID == presentationId && a.Status == AssignmentStatus.Published)
                .ToList();

            // Extract unique target teams
            targetTeams = publishedAssignments
                .Select(a => a.TargetTeam)
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting target teams: {ex.Message}");
        }

        return targetTeams;
    }
}