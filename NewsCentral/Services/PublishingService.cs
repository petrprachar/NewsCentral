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
            Console.WriteLine($"Publishing to {assignment.TargetTeam}...");
            var publishedPaths = new List<string>();

            // 1. Copy presentation JSON (includes embedded base64 image)
            try
            {
                Console.WriteLine("Copying presentation JSON...");
                var presentationPath = await CopyPresentationToTargetAsync(
                    presentation,
                    assignment.TargetTeam);
                publishedPaths.Add(presentationPath);
                Console.WriteLine($"  ✓ Presentation: {presentationPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR copying presentation: {ex.Message}");
                throw; // Critical - cannot proceed without presentation
            }

            // 2. Copy schedule JSON
            try
            {
                Console.WriteLine("Copying schedule JSON...");
                var schedulePath = await CopyScheduleToTargetAsync(
                    schedule,
                    assignment.TargetTeam,
                    assignment.PresentationID);
                publishedPaths.Add(schedulePath);
                Console.WriteLine($"  ✓ Schedule: {schedulePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR copying schedule: {ex.Message}");
                throw; // Critical - cannot proceed without schedule
            }

            // 3. OPTIONAL: Copy physical image files for reference/backup
            // (The poster is already embedded in ContentImageBase64, but we keep originals for editing)
            if (!string.IsNullOrEmpty(presentation.GeneratedImagePath))
            {
                try
                {
                    Console.WriteLine("Copying poster image file (optional backup)...");
                    var posterPath = await CopyImageToTargetAsync(
                        presentation.GeneratedImagePath,
                        assignment.TargetTeam,
                        "generated");
                    publishedPaths.Add(posterPath);
                    Console.WriteLine($"  ✓ Poster file: {posterPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Could not copy poster file - {ex.Message}");
                    // Non-critical - embedded image in JSON is sufficient
                }
            }

            if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
            {
                try
                {
                    Console.WriteLine("Copying original image file (optional backup)...");
                    var originalPath = await CopyImageToTargetAsync(
                        presentation.OriginalImagePath,
                        assignment.TargetTeam,
                        "original");
                    publishedPaths.Add(originalPath);
                    Console.WriteLine($"  ✓ Original file: {originalPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Could not copy original file - {ex.Message}");
                    // Non-critical - not needed for display
                }
            }

            // Update assignment status
            Console.WriteLine($"Updating assignment status... ({publishedPaths.Count} files)");
            try
            {
                var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");
                assignment.Status = AssignmentStatus.Published;
                assignment.PublishedBy = currentUser.UserID;
                assignment.PublishedDate = DateTime.UtcNow;
                assignment.PublishedPaths = publishedPaths;

                Console.WriteLine($"  Status: {assignment.Status}");
                Console.WriteLine($"  PublishedBy: {assignment.PublishedBy}");
                Console.WriteLine($"  PublishedDate: {assignment.PublishedDate}");

                await repo.UpdateAsync(assignment);
                Console.WriteLine("  ✓ Assignment updated in JSON");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR updating assignment: {ex.Message}");
                throw; // Critical failure
            }

            result.Success = true;
            result.PublishedPaths = publishedPaths;
            result.Message = $"Successfully published to {assignment.TargetTeam}";

            Console.WriteLine($"=== SUCCESS: Published {publishedPaths.Count} files ===");
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
