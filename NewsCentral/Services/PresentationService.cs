using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;
using System.Text.Json;

namespace NewsCentral.Services;

public class PresentationService
{
    private readonly string _basePath;
    private readonly AuthenticationService _authService;

    private readonly PosterGenerationService _posterService;

    public PresentationService(
        AppConfiguration config,
        AuthenticationService authService,
        PosterGenerationService posterService)
    {
        _basePath = config.DataPath;
        _authService = authService;
        _posterService = posterService;
    }

    public async Task<Presentation> UpdatePresentationWithPosterAsync(
    string teamFolderName,
    string presentationId,
    string headlineText,
    string bodyText,
    string ctaText)
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

        return await repo.UpdateAsync(presentation);
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

        return await repo.UpdateAsync(presentation);
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
}