using NewsCentral.Models;
using NewsCentral.Repositories;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsCentral.Services;

public class PublishingService
{
    private readonly IStorageService _storage;
    private readonly IBlobDistributionService _blobDistribution;
    private readonly PresentationService _presentationService;
    private readonly ScheduleService _scheduleService;
    private readonly AssignmentService _assignmentService;
    private readonly AuthenticationService _authService;
    private readonly IndexGenerationService _indexGenerationService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PublishingService(
        IStorageService storage,
        IBlobDistributionService blobDistribution,
        AuthenticationService authService,
        AssignmentService assignmentService,
        PresentationService presentationService,
        ScheduleService scheduleService,
        IndexGenerationService indexGenerationService)
    {
        _storage                = storage;
        _blobDistribution       = blobDistribution;
        _authService            = authService;
        _assignmentService      = assignmentService;
        _presentationService    = presentationService;
        _scheduleService        = scheduleService;
        _indexGenerationService = indexGenerationService;
    }

    // ── Publish ──────────────────────────────────────────────────────────────

    public async Task<PublishResult> PublishAssignmentAsync(
        string sourceTeamFolderName,
        string assignmentId)
    {
        var result      = new PublishResult();
        var currentUser = _authService.GetCurrentUser();

        if (currentUser == null)
            return Fail(result, "Not authenticated");

        try
        {
            System.Diagnostics.Debug.WriteLine($"=== PublishAssignment: {assignmentId} ===");

            var assignment = await _assignmentService.GetAssignmentAsync(
                sourceTeamFolderName, assignmentId);

            if (assignment == null)
                return Fail(result, "Assignment not found");

            System.Diagnostics.Debug.WriteLine(
                $"Source: {assignment.SourceTeam} → Target: {assignment.TargetTeam}");

            if (assignment.Status != AssignmentStatus.Approved)
                return Fail(result,
                    $"Assignment must be approved before publishing " +
                    $"(current status: {assignment.Status})");

            if (!currentUser.IsSystemAdmin && assignment.CreatedBy != currentUser.UserID)
            {
                var hasContentAuthorRole = currentUser.TeamRoles.Any(tr =>
                    tr.TeamFolderName == sourceTeamFolderName &&
                    tr.Roles.Contains("ContentAuthor"));

                if (!hasContentAuthorRole)
                    return Fail(result,
                        "Only the assignment creator, a ContentAuthor of this team, " +
                        "or a SystemAdmin can publish");
            }

            var presentation = await _presentationService.GetPresentationAsync(
                sourceTeamFolderName, assignment.PresentationID);

            if (presentation == null)
                return Fail(result, "Presentation not found");

            var schedule = await _scheduleService.GetScheduleAsync(
                sourceTeamFolderName, assignment.ScheduleID);

            if (schedule == null)
                return Fail(result, "Schedule not found");

            System.Diagnostics.Debug.WriteLine(
                $"Copying files to {assignment.TargetTeam}...");

            var publishedPaths = new List<string>();

            // ── 1. Presentation JSON → authoring tier + blob ─────────────────
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Copying presentation JSON...");
                var path = await CopyPresentationToTargetAsync(presentation, assignment.TargetTeam);
                publishedPaths.Add(path);
                System.Diagnostics.Debug.WriteLine($"  ✓ Presentation: {path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR copying presentation: {ex.Message}");
                throw;
            }

            // ── 2. Schedule JSON → authoring tier + blob ─────────────────────
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Copying schedule JSON...");
                var path = await CopyScheduleToTargetAsync(
                    schedule, assignment.TargetTeam, assignment.PresentationID);
                publishedPaths.Add(path);
                System.Diagnostics.Debug.WriteLine($"  ✓ Schedule: {path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR copying schedule: {ex.Message}");
                throw;
            }

            // ── 3. Assignment JSON → authoring tier only ──────────────────────
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Copying assignment JSON...");
                var path = await CopyAssignmentToTargetAsync(assignment, assignment.TargetTeam);
                publishedPaths.Add(path);
                System.Diagnostics.Debug.WriteLine($"  ✓ Assignment: {path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR copying assignment: {ex.Message}");
                throw;
            }

            // ── 4. Update assignment status ───────────────────────────────────
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Updating assignment status...");
                var repo = new TeamAwareRepository<Assignment>(
                    _storage, sourceTeamFolderName, "assignments");

                assignment.Status         = AssignmentStatus.Published;
                assignment.PublishedBy    = currentUser.UserID;
                assignment.PublishedDate  = DateTime.UtcNow;
                assignment.PublishedPaths = publishedPaths;

                await repo.UpdateAsync(assignment);
                System.Diagnostics.Debug.WriteLine("  ✓ Status updated to Published");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR updating assignment: {ex.Message}");
                throw;
            }

            // ── 5a. Generated image → authoring tier + blob ───────────────────
            // GeneratedImagePath is always set at create time (new presentations)
            // or promoted during UpdatePresentationFullAsync (legacy presentations).
            if (!string.IsNullOrEmpty(presentation.GeneratedImagePath))
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("→ Copying generated image file...");
                    var path = await CopyImageToTargetAsync(
                        presentation.GeneratedImagePath,
                        sourceTeamFolderName,
                        assignment.TargetTeam,
                        "generated");
                    publishedPaths.Add(path);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Generated image: {path}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ⚠ WARNING: Could not copy generated image - {ex.Message}");
                }
            }
            // ── 5b. Safety net: no GeneratedImagePath — promote original ──────
            // Handles presentations created before the always-populate fix.
            else if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine(
                        "→ No generated image — promoting original to generated/...");

                    var fileName       = Path.GetFileName(presentation.OriginalImagePath);
                    var sourceRelative = $"{sourceTeamFolderName}/images/original/{fileName}";
                    var destRelative   = $"{assignment.TargetTeam}/images/generated/{fileName}";

                    await _storage.CopyFileAsync(sourceRelative, destRelative);

                    var stream = await _storage.OpenReadAsync(sourceRelative);
                    if (stream != null)
                    {
                        using (stream)
                            await _blobDistribution.UploadStreamAsync(destRelative, stream);
                    }

                    publishedPaths.Add(destRelative);
                    System.Diagnostics.Debug.WriteLine(
                        $"  ✓ Promoted original to generated: {destRelative}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ⚠ WARNING: Could not promote original to generated: {ex.Message}");
                }
            }

            // ── 5c. Original image → authoring tier + blob ────────────────────
            if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("→ Copying original image file...");
                    var path = await CopyImageToTargetAsync(
                        presentation.OriginalImagePath,
                        sourceTeamFolderName,
                        assignment.TargetTeam,
                        "original");
                    publishedPaths.Add(path);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Original file: {path}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ⚠ WARNING: Could not copy original file - {ex.Message}");
                }
            }

            // ── 6. Regenerate index → authoring tier + blob ───────────────────
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"→ Regenerating index for target team: {assignment.TargetTeam}");
                await _indexGenerationService.GenerateAndSaveIndexAsync(assignment.TargetTeam);
                System.Diagnostics.Debug.WriteLine(
                    $"  ✓ Index file regenerated for {assignment.TargetTeam}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"  ⚠ WARNING: Index generation failed: {ex.Message}");
            }

            result.Success        = true;
            result.PublishedPaths = publishedPaths;
            result.Message        = $"Successfully published to {assignment.TargetTeam}";

            System.Diagnostics.Debug.WriteLine(
                $"=== SUCCESS: Published {publishedPaths.Count} files ===");
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("=== PUBLISH FAILED ===");
            System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
            return Fail(result, $"Publishing error: {ex.Message}");
        }
    }

    // ── Private copy methods ─────────────────────────────────────────────────

    private async Task<string> CopyPresentationToTargetAsync(
        Presentation presentation,
        string targetTeamFolderName)
    {
        var fileName     = $"pres_{presentation.PresentationID}.json";
        var relativePath = $"{targetTeamFolderName}/content/presentations/{fileName}";
        var json         = JsonSerializer.Serialize(presentation, JsonOptions);

        await _storage.WriteTextAsync(relativePath, json);

        try
        {
            await _blobDistribution.UploadTextAsync(relativePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  ⚠ WARNING: Blob upload failed for {relativePath}: {ex.Message}");
        }

        return relativePath;
    }

    private async Task<string> CopyScheduleToTargetAsync(
        Schedule schedule,
        string targetTeamFolderName,
        string presentationId)
    {
        var scheduleCopy = new Schedule
        {
            ScheduleID      = schedule.ScheduleID,
            PresentationID  = presentationId,
            DateCreated     = schedule.DateCreated,
            ScheduleCreated = schedule.ScheduleCreated,
            ScheduleStart   = schedule.ScheduleStart,
            ScheduleEnd     = schedule.ScheduleEnd,
            DaysOfWeek      = schedule.DaysOfWeek,
            IsActive        = schedule.IsActive,
            CreatedBy       = schedule.CreatedBy,
            LastModified    = DateTime.UtcNow
        };

        var fileName     = $"sched_{schedule.ScheduleID}.json";
        var relativePath = $"{targetTeamFolderName}/content/schedules/{fileName}";
        var json         = JsonSerializer.Serialize(scheduleCopy, JsonOptions);

        await _storage.WriteTextAsync(relativePath, json);

        try
        {
            await _blobDistribution.UploadTextAsync(relativePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  ⚠ WARNING: Blob upload failed for {relativePath}: {ex.Message}");
        }

        return relativePath;
    }

    /// <summary>
    /// Copies an image on the authoring tier (source == dest is silently skipped),
    /// then streams it to blob distribution.
    /// imageType: "generated" or "original"
    /// </summary>
    private async Task<string> CopyImageToTargetAsync(
        string imagePathFromJson,
        string sourceTeamFolder,
        string targetTeamFolder,
        string imageType)
    {
        var fileName       = Path.GetFileName(imagePathFromJson);
        var sourceRelative = $"{sourceTeamFolder}/images/{imageType}/{fileName}";
        var destRelative   = $"{targetTeamFolder}/images/{imageType}/{fileName}";

        if (!await _storage.FileExistsAsync(sourceRelative))
            throw new IOException($"Source image not found: {sourceRelative}");

        await _storage.CopyFileAsync(sourceRelative, destRelative);

        try
        {
            var stream = await _storage.OpenReadAsync(sourceRelative);
            if (stream != null)
            {
                using (stream)
                    await _blobDistribution.UploadStreamAsync(destRelative, stream);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  ⚠ WARNING: Blob upload failed for {destRelative}: {ex.Message}");
        }

        return destRelative;
    }

    private async Task<string> CopyAssignmentToTargetAsync(
        Assignment assignment,
        string targetTeamFolder)
    {
        System.Diagnostics.Debug.WriteLine("  Copying assignment to target team...");

        var fileName     = $"assign_{assignment.AssignmentID}.json";
        var relativePath = $"{targetTeamFolder}/content/assignments/{fileName}";
        var json         = JsonSerializer.Serialize(assignment, JsonOptions);

        await _storage.WriteTextAsync(relativePath, json);

        System.Diagnostics.Debug.WriteLine($"  ✓ Assignment file created at: {relativePath}");

        return relativePath;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PublishResult Fail(PublishResult result, string message)
    {
        result.Success      = false;
        result.ErrorMessage = message;
        return result;
    }
}

public class PublishResult
{
    public bool         Success        { get; set; }
    public string       Message        { get; set; } = string.Empty;
    public string       ErrorMessage   { get; set; } = string.Empty;
    public List<string> PublishedPaths { get; set; } = new();
}
