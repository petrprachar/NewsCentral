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

            // 3. NEW: Copy assignment JSON to target team
            /*
            try
            {
                Console.WriteLine("Copying assignment metadata...");
                var assignmentPath = await CopyAssignmentToTargetAsync(
                    assignment,
                    assignment.TargetTeam);
                publishedPaths.Add(assignmentPath);
                Console.WriteLine($"  ✓ Assignment: {assignmentPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARNING: Could not copy assignment metadata - {ex.Message}");
                // Non-critical - target team can still use the content
            }
            */
            // Update assignment status
            // System.Diagnostics.Debug.WriteLine($"Updating assignment status... ({publishedPaths.Count} files)");
            try
            {
                var repo = new TeamAwareRepository<Assignment>(_basePath, sourceTeamFolderName, "assignments");

                // System.Diagnostics.Debug.WriteLine($"  Current status: {assignment.Status}");
                // System.Diagnostics.Debug.WriteLine($"  Setting status to: Published");

                assignment.Status = AssignmentStatus.Published;
                assignment.PublishedBy = currentUser.UserID;
                assignment.PublishedDate = DateTime.UtcNow;
                assignment.PublishedPaths = publishedPaths;

                // System.Diagnostics.Debug.WriteLine($"  PublishedBy: {assignment.PublishedBy}");
                // System.Diagnostics.Debug.WriteLine($"  PublishedDate: {assignment.PublishedDate}");
                // System.Diagnostics.Debug.WriteLine($"  PublishedPaths count: {publishedPaths.Count}");

                // System.Diagnostics.Debug.WriteLine($"  Calling repo.UpdateAsync...");
                await repo.UpdateAsync(assignment);
                // System.Diagnostics.Debug.WriteLine($"  ✓ Assignment updated successfully");
            }
            catch (Exception)
            {
                // System.Diagnostics.Debug.WriteLine($"  ✗ ERROR updating assignment!");
                // System.Diagnostics.Debug.WriteLine($"  Exception type: {ex.GetType().Name}");
                // System.Diagnostics.Debug.WriteLine($"  Exception message: {ex.Message}");
                // System.Diagnostics.Debug.WriteLine($"  Stack trace: {ex.StackTrace}");
                throw; // Re-throw to propagate the error
            }


            // 4. OPTIONAL: Copy physical image files for reference/backup
            // (The poster is already embedded in ContentImageBase64, but we keep originals for editing)
            if (!string.IsNullOrEmpty(presentation.GeneratedImagePath))
            {
                try
                {
                    Console.WriteLine("Copying poster image file (optional backup)...");
                    var posterPath = await CopyImageToTargetAsync(
                        presentation.GeneratedImagePath,
                        sourceTeamFolderName,    // ← FIXED: Added source team parameter
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
            else
            {
                Console.WriteLine("  No GeneratedImagePath - skipping poster copy");
            }

            if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
            {
                try
                {
                    Console.WriteLine("Copying original image file (optional backup)...");
                    var originalPath = await CopyImageToTargetAsync(
                        presentation.OriginalImagePath,
                        sourceTeamFolderName,    // ← FIXED: Added source team parameter
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
            else
            {
                Console.WriteLine("  No OriginalImagePath - skipping original copy");
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
        string imagePathFromJson,  // Path stored in presentation JSON (might be wrong team)
        string sourceTeamFolder,   // ← NEW: The actual source team we're copying FROM
        string targetTeamFolder,   // The target team we're copying TO
        string imageType)          // "generated" or "original"
    {
        Console.WriteLine($"  CopyImage - JSON path: {imagePathFromJson}");
        Console.WriteLine($"  CopyImage - Source team: {sourceTeamFolder}");
        Console.WriteLine($"  CopyImage - Target team: {targetTeamFolder}");

        // Extract just the filename
        var fileName = Path.GetFileName(imagePathFromJson);
        Console.WriteLine($"  CopyImage - Filename: {fileName}");

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

        Console.WriteLine($"  Source: {sourcePath}");
        Console.WriteLine($"  Target: {targetPath}");

        // Check if source file exists
        if (!File.Exists(sourcePath))
        {
            var error = $"Source image not found: {sourcePath}";
            Console.WriteLine($"  ERROR: {error}");
            throw new IOException(error);
        }

        // Check if source and target are the same (self-assignment)
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Source and target are the same - skipping copy");
            return $"{targetTeamFolder}/images/{imageType}/{fileName}";
        }

        // Ensure target directory exists
        var targetDir = Path.GetDirectoryName(targetPath);
        if (targetDir != null && !Directory.Exists(targetDir))
        {
            Console.WriteLine($"  Creating directory: {targetDir}");
            Directory.CreateDirectory(targetDir);
        }

        // Copy file with FileShare.ReadWrite to allow other processes to keep it open
        try
        {
            Console.WriteLine($"  Copying file (with shared read access)...");

            // Open source with FileShare.ReadWrite (allows other processes to read)
            using (var sourceStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))  // ← KEY FIX: Allow sharing
            {
                // Create/overwrite target
                using (var targetStream = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await sourceStream.CopyToAsync(targetStream);
                }
            }

            Console.WriteLine($"  ✓ File copied successfully");
        }
        catch (IOException ioEx)
        {
            Console.WriteLine($"  ERROR: {ioEx.Message}");
            throw;
        }

        return $"{targetTeamFolder}/images/{imageType}/{fileName}";
    }

    private async Task<string> CopyAssignmentToTargetAsync(
    Assignment assignment,
    string targetTeamFolder)
    {
        Console.WriteLine($"  Copying assignment to target team...");

        // Build target path
        var targetPath = Path.Combine(
            _basePath,
            targetTeamFolder,
            "content",
            "assignments",
            $"assign_{assignment.AssignmentID}.json");

        Console.WriteLine($"  Target: {targetPath}");

        // Ensure target directory exists
        var targetDir = Path.GetDirectoryName(targetPath);
        if (targetDir != null && !Directory.Exists(targetDir))
        {
            Console.WriteLine($"  Creating directory: {targetDir}");
            Directory.CreateDirectory(targetDir);
        }

        // Create a repository for the target team
        var targetRepo = new TeamAwareRepository<Assignment>(
            _basePath,
            targetTeamFolder,
            "assignments");

        // Save a copy of the assignment to target team
        // The assignment already has Published status and all metadata
        await targetRepo.CreateAsync(assignment);

        Console.WriteLine($"  ✓ Assignment copied to target");

        return $"{targetTeamFolder}/content/assignments/assign_{assignment.AssignmentID}.json";
    }
}

public class PublishResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public List<string> PublishedPaths { get; set; } = new();
}
