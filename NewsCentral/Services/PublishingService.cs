using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Repositories;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace NewsCentral.Services;

public class PublishingService
{
    private readonly string _basePath;
    private readonly PresentationService _presentationService;
    private readonly ScheduleService _scheduleService;
    private readonly AssignmentService _assignmentService;
    private readonly AuthenticationService _authService;
    private readonly IndexGenerationService _indexGenerationService;

    public PublishingService(
        AppConfiguration config,
        AuthenticationService authService,
        AssignmentService assignmentService,
        PresentationService presentationService,
        ScheduleService scheduleService,
        IndexGenerationService indexGenerationService)
    {
        _basePath = config.DataPath;
        _authService = authService;
        _assignmentService = assignmentService;
        _presentationService = presentationService;
        _scheduleService = scheduleService;
        _indexGenerationService = indexGenerationService;
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
            System.Diagnostics.Debug.WriteLine($"=== PublishAssignment: {assignmentId} ===");

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

            System.Diagnostics.Debug.WriteLine($"Source: {assignment.SourceTeam} → Target: {assignment.TargetTeam}");

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
            System.Diagnostics.Debug.WriteLine($"Copying files to {assignment.TargetTeam}...");
            var publishedPaths = new List<string>();

            // 1. Copy presentation JSON (includes embedded base64 image)
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Copying presentation JSON...");
                var presentationPath = await CopyPresentationToTargetAsync(
                    presentation,
                    assignment.TargetTeam);
                publishedPaths.Add(presentationPath);
                System.Diagnostics.Debug.WriteLine($"  ✓ Presentation: {presentationPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR copying presentation: {ex.Message}");
                throw; // Critical - cannot proceed without presentation
            }

            // 2. Copy schedule JSON
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Copying schedule JSON...");
                var schedulePath = await CopyScheduleToTargetAsync(
                    schedule,
                    assignment.TargetTeam,
                    assignment.PresentationID);
                publishedPaths.Add(schedulePath);
                System.Diagnostics.Debug.WriteLine($"  ✓ Schedule: {schedulePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR copying schedule: {ex.Message}");
                throw; // Critical - cannot proceed without schedule
            }

            // 3. Copy assignment JSON to target team
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Copying assignment JSON...");
                var assignmentPath = await CopyAssignmentToTargetAsync(
                    assignment,
                    assignment.TargetTeam);
                publishedPaths.Add(assignmentPath);
                System.Diagnostics.Debug.WriteLine($"  ✓ Assignment: {assignmentPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR copying assignment: {ex.Message}");
                throw; // Critical - assignment needed for tracking
            }

            // 4. Update assignment status in SOURCE team
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Updating assignment status...");
                var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");

                assignment.Status = AssignmentStatus.Published;
                assignment.PublishedBy = currentUser.UserID;
                assignment.PublishedDate = DateTime.UtcNow;
                assignment.PublishedPaths = publishedPaths;

                await repo.UpdateAsync(assignment);
                System.Diagnostics.Debug.WriteLine($"  ✓ Status updated to Published");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR updating assignment: {ex.Message}");
                throw; // Re-throw to propagate the error
            }

            // 5. OPTIONAL: Copy physical image files for reference/backup
            // (The poster is already embedded in ContentImageBase64, but we keep originals for editing)
            if (!string.IsNullOrEmpty(presentation.GeneratedImagePath))
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("→ Copying poster image file (optional backup)...");
                    var posterPath = await CopyImageToTargetAsync(
                        presentation.GeneratedImagePath,
                        sourceTeamFolderName,
                        assignment.TargetTeam,
                        "generated");
                    publishedPaths.Add(posterPath);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Poster file: {posterPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"  ⚠ WARNING: Could not copy poster file - {ex.Message}");
                    // Non-critical - embedded image in JSON is sufficient
                }
            }

            if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("→ Copying original image file (optional backup)...");
                    var originalPath = await CopyImageToTargetAsync(
                        presentation.OriginalImagePath,
                        sourceTeamFolderName,
                        assignment.TargetTeam,
                        "original");
                    publishedPaths.Add(originalPath);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Original file: {originalPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"  ⚠ WARNING: Could not copy original file - {ex.Message}");
                    // Non-critical - not needed for display
                }
            }

            // 6. NEW: Regenerate index file for TARGET team
            try
            {
                System.Diagnostics.Debug.WriteLine($"→ Regenerating index for target team: {assignment.TargetTeam}");
                await _indexGenerationService.GenerateAndSaveIndexAsync(assignment.TargetTeam);
                System.Diagnostics.Debug.WriteLine($"  ✓ Index file regenerated for {assignment.TargetTeam}");
            }
            catch (Exception indexEx)
            {
                // Don't fail the publish if index generation fails
                System.Diagnostics.Debug.WriteLine($"  ⚠ WARNING: Index generation failed: {indexEx.Message}");
                System.Diagnostics.Debug.WriteLine($"  Publishing succeeded, but clients may not see update until next manual regeneration");
                // Publishing succeeded, so we still return success
            }

            result.Success = true;
            result.PublishedPaths = publishedPaths;
            result.Message = $"Successfully published to {assignment.TargetTeam}";

            System.Diagnostics.Debug.WriteLine($"=== SUCCESS: Published {publishedPaths.Count} files ===");
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== PUBLISH FAILED ===");
            System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");

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

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var json = JsonSerializer.Serialize(presentation, jsonOptions);
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

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var json = JsonSerializer.Serialize(scheduleCopy, jsonOptions);
        await File.WriteAllTextAsync(targetPath, json);

        return $"{targetTeamFolderName}/content/schedules/{fileName}";
    }

    private async Task<string> CopyImageToTargetAsync(
        string imagePathFromJson,
        string sourceTeamFolder,
        string targetTeamFolder,
        string imageType)
    {
        // Extract just the filename
        var fileName = Path.GetFileName(imagePathFromJson);

        // Build source path
        var sourcePath = Path.Combine(
            _basePath,
            sourceTeamFolder,
            "images",
            imageType,
            fileName);

        // Build target path
        var targetPath = Path.Combine(
            _basePath,
            targetTeamFolder,
            "images",
            imageType,
            fileName);

        // Check if source file exists
        if (!File.Exists(sourcePath))
        {
            throw new IOException($"Source image not found: {sourcePath}");
        }

        // Check if source and target are the same (self-assignment)
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine($"  Source and target are identical - skipping copy");
            return $"{targetTeamFolder}/images/{imageType}/{fileName}";
        }

        // Ensure target directory exists
        var targetDir = Path.GetDirectoryName(targetPath);
        if (targetDir != null && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Copy file with FileShare.ReadWrite to allow other processes to keep it open
        using (var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite))
        {
            using (var targetStream = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await sourceStream.CopyToAsync(targetStream);
            }
        }

        return $"{targetTeamFolder}/images/{imageType}/{fileName}";
    }

    private async Task<string> CopyAssignmentToTargetAsync(
        Assignment assignment,
        string targetTeamFolder)
    {
        System.Diagnostics.Debug.WriteLine($"  Copying assignment to target team...");

        var targetFolder = Path.Combine(_basePath, targetTeamFolder, "content", "assignments");
        Directory.CreateDirectory(targetFolder);

        var fileName = $"assign_{assignment.AssignmentID}.json";
        var targetPath = Path.Combine(targetFolder, fileName);

        // Serialize the assignment
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var json = JsonSerializer.Serialize(assignment, jsonOptions);
        await File.WriteAllTextAsync(targetPath, json);

        System.Diagnostics.Debug.WriteLine($"  ✓ Assignment file created at: {targetPath}");

        return $"{targetTeamFolder}/content/assignments/{fileName}";
    }
}

public class PublishResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public List<string> PublishedPaths { get; set; } = new();
}