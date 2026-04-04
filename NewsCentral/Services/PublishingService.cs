using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class PublishingService
{
    private readonly string _basePath;
    private readonly PresentationService _presentationService;
    private readonly ScheduleService _scheduleService;
    private readonly AssignmentService _assignmentService;
    private readonly AuthenticationService _authService;

    public PublishingService(
        AppConfiguration config,
        PresentationService presentationService,
        ScheduleService scheduleService,
        AssignmentService assignmentService,
        AuthenticationService authService)
    {
        _basePath = config.DataPath;
        _presentationService = presentationService;
        _scheduleService = scheduleService;
        _assignmentService = assignmentService;
        _authService = authService;
    }

    public async Task<PublishResult> PublishAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId)
    {
        var result = new PublishResult();
        var currentUser = _authService.GetCurrentUser();

        if (currentUser == null)
        {
            result.Success = false;
            result.ErrorMessage = "Not authenticated";
            return result;
        }

        try
        {
            // Get assignment
            var assignment = await _assignmentService.GetAssignmentAsync(
                sourceTeamFolderName,
                assignmentId);

            if (assignment == null)
            {
                result.Success = false;
                result.ErrorMessage = "Assignment not found";
                return result;
            }

            // Verify assignment is approved
            if (assignment.Status != AssignmentStatus.Approved)
            {
                result.Success = false;
                result.ErrorMessage = $"Assignment must be approved before publishing (current status: {assignment.Status})";
                return result;
            }

            // Verify user has permission (creator or SystemAdmin)
            if (assignment.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            {
                result.Success = false;
                result.ErrorMessage = "Only the creator or SystemAdmin can publish";
                return result;
            }

            // Get presentation
            var presentation = await _presentationService.GetPresentationAsync(
                sourceTeamFolderName,
                assignment.PresentationID);

            if (presentation == null)
            {
                result.Success = false;
                result.ErrorMessage = "Presentation not found";
                return result;
            }

            // Get schedule
            var schedule = await _scheduleService.GetScheduleAsync(
                sourceTeamFolderName,
                assignment.ScheduleID);

            if (schedule == null)
            {
                result.Success = false;
                result.ErrorMessage = "Schedule not found";
                return result;
            }

            // Copy files to target team
            var publishedPaths = new List<string>();

            // 1. Copy presentation JSON
            var presentationPath = await CopyPresentationToTargetAsync(
                presentation,
                assignment.TargetTeam);
            publishedPaths.Add(presentationPath);

            // 2. Copy schedule JSON
            var schedulePath = await CopyScheduleToTargetAsync(
                schedule,
                assignment.TargetTeam,
                assignment.PresentationID);
            publishedPaths.Add(schedulePath);

            // 3. Copy poster image
            if (!string.IsNullOrEmpty(presentation.GeneratedImagePath))
            {
                var posterPath = await CopyImageToTargetAsync(
                    presentation.GeneratedImagePath,
                    assignment.TargetTeam,
                    "generated");
                publishedPaths.Add(posterPath);
            }

            // 4. Copy original image
            if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
            {
                var originalPath = await CopyImageToTargetAsync(
                    presentation.OriginalImagePath,
                    assignment.TargetTeam,
                    "original");
                publishedPaths.Add(originalPath);
            }

            // Update assignment status
            var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
            assignment.Status = AssignmentStatus.Published;
            assignment.PublishedBy = currentUser.UserID;
            assignment.PublishedDate = DateTime.UtcNow;
            assignment.PublishedPaths = publishedPaths;
            await repo.UpdateAsync(assignment);

            result.Success = true;
            result.PublishedPaths = publishedPaths;
            result.Message = $"Successfully published to {assignment.TargetTeam}";

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Publishing error: {ex.Message}";
            return result;
        }
    }

    private async Task<string> CopyPresentationToTargetAsync(
        Presentation presentation,
        string targetTeamFolderName)
    {
        var targetFolder = Path.Combine(_basePath, targetTeamFolderName, "content", "presentations");
        Directory.CreateDirectory(targetFolder);

        var fileName = $"pres_{presentation.PresentationID}.json";
        var targetPath = Path.Combine(targetFolder, fileName);

        var json = System.Text.Json.JsonSerializer.Serialize(presentation, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        await File.WriteAllTextAsync(targetPath, json);

        return $"{targetTeamFolderName}/content/presentations/{fileName}";
    }

    private async Task<string> CopyScheduleToTargetAsync(
        Schedule schedule,
        string targetTeamFolderName,
        string presentationId)
    {
        var targetFolder = Path.Combine(_basePath, targetTeamFolderName, "content", "schedules");
        Directory.CreateDirectory(targetFolder);

        // Create a copy with updated presentation reference
        var scheduleCopy = new Schedule
        {
            ScheduleID = schedule.ScheduleID,
            PresentationID = presentationId,
            Version = schedule.Version,
            DateCreated = schedule.DateCreated,
            ScheduleCreated = schedule.ScheduleCreated,
            ScheduleStart = schedule.ScheduleStart,
            ScheduleEnd = schedule.ScheduleEnd,
            DaysOfWeek = schedule.DaysOfWeek,
            IsActive = schedule.IsActive,
            CreatedBy = schedule.CreatedBy,
            LastModified = DateTime.UtcNow
        };

        var fileName = $"sched_{schedule.ScheduleID}.json";
        var targetPath = Path.Combine(targetFolder, fileName);

        var json = System.Text.Json.JsonSerializer.Serialize(scheduleCopy, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        await File.WriteAllTextAsync(targetPath, json);

        return $"{targetTeamFolderName}/content/schedules/{fileName}";
    }

    private async Task<string> CopyImageToTargetAsync(
        string sourceImagePath,
        string targetTeamFolderName,
        string imageType) // "original" or "generated"
    {
        var sourcePath = Path.Combine(_basePath, sourceImagePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source image not found: {sourceImagePath}");
        }

        var fileName = Path.GetFileName(sourcePath);
        var targetFolder = Path.Combine(_basePath, targetTeamFolderName, "images", imageType);
        Directory.CreateDirectory(targetFolder);

        var targetPath = Path.Combine(targetFolder, fileName);

        // Copy the file
        File.Copy(sourcePath, targetPath, overwrite: true);

        return $"{targetTeamFolderName}/images/{imageType}/{fileName}";
    }
}

public class PublishResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public List<string> PublishedPaths { get; set; } = new();
}
