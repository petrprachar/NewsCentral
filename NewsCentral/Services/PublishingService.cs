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
            var distributionWarnings = new List<string>();

            // ── 1. Presentation JSON → authoring tier + blob ─────────────────
            try
            {
                System.Diagnostics.Debug.WriteLine("→ Copying presentation JSON...");
                var path = await CopyPresentationToTargetAsync(presentation, assignment.TargetTeam, distributionWarnings);
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
                    schedule, assignment.TargetTeam, assignment.PresentationID, distributionWarnings);
                publishedPaths.Add(path);
                System.Diagnostics.Debug.WriteLine($"  ✓ Schedule: {path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ ERROR copying schedule: {ex.Message}");
                throw;
            }

            // ── 3a. Generated image → authoring tier + blob ───────────────────
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
                        "generated",
                        distributionWarnings);
                    publishedPaths.Add(path);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Generated image: {path}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ⚠ WARNING: Could not copy generated image - {ex.Message}");
                }
            }
            // ── 3b. Safety net: no GeneratedImagePath — promote original ──────
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
                        try
                        {
                            using (stream)
                                await _blobDistribution.UploadStreamAsync(destRelative, stream);
                        }
                        catch (Exception blobEx)
                        {
                            distributionWarnings.Add($"{destRelative}: {blobEx.Message}");
                            throw; // keeps this branch's own catch/log below unchanged.
                        }
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

            // ── 3c. Original image → authoring tier + blob ────────────────────
            if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("→ Copying original image file...");
                    var path = await CopyImageToTargetAsync(
                        presentation.OriginalImagePath,
                        sourceTeamFolderName,
                        assignment.TargetTeam,
                        "original",
                        distributionWarnings);
                    publishedPaths.Add(path);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Original file: {path}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ⚠ WARNING: Could not copy original file - {ex.Message}");
                }
            }

            // ── 4. Write the assignment in its FINAL Published state ─────────
            // PUB-1: build the complete Published state — including the assignment copy's own
            // path, deterministic and known without writing it first — BEFORE any assignment
            // JSON is written. This closes the bug where a cross-team copy was written while
            // still Status=Approved (step 3, pre-fix) and never updated afterward, because the
            // status flip (step 4, pre-fix) only ever touched the SOURCE team's record.
            var targetTeamFolder = assignment.TargetTeam;
            var isCrossTeam = !string.Equals(
                sourceTeamFolderName, targetTeamFolder, StringComparison.OrdinalIgnoreCase);
            var assignmentRelativePath =
                $"{targetTeamFolder}/content/assignments/assign_{assignment.AssignmentID}.json";

            publishedPaths.Add(assignmentRelativePath);

            assignment.Status         = AssignmentStatus.Published;
            assignment.PublishedBy    = currentUser.UserID;
            assignment.PublishedDate  = DateTime.UtcNow;
            assignment.PublishedPaths = publishedPaths;

            if (isCrossTeam)
            {
                // Target copy first, already in its final state — there is no window where a
                // reader could observe a half-published copy.
                System.Diagnostics.Debug.WriteLine("→ Writing target team's copy (final state)...");
                await WriteAssignmentJsonAsync(assignment, assignmentRelativePath);
                System.Diagnostics.Debug.WriteLine($"  ✓ Target copy: {assignmentRelativePath}");

                try
                {
                    System.Diagnostics.Debug.WriteLine("→ Updating source team's record...");
                    var sourceRepo = new TeamAwareRepository<Assignment>(
                        _storage, sourceTeamFolderName, "assignments");
                    await sourceRepo.UpdateAsync(assignment);
                    System.Diagnostics.Debug.WriteLine("  ✓ Source record updated to Published");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ✗ ERROR updating source record: {ex.Message} — rolling back target copy");

                    // Best effort — a failed rollback is logged but never masks the original
                    // failure below; the orphaned copy is also within reach of the Index
                    // Management repair tool (PublishedCopyRepairPlanner) if this cleanup fails.
                    try
                    {
                        var targetDeletedPath =
                            $"{targetTeamFolder}/deleted/assign_{assignment.AssignmentID}.json";
                        await _storage.MoveFileAsync(assignmentRelativePath, targetDeletedPath);
                    }
                    catch (Exception moveEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"  ⚠ WARNING: Could not roll back target copy: {moveEx.Message}");
                    }

                    return Fail(result, $"Publishing error: {ex.Message}");
                }
            }
            else
            {
                // Same team — exactly one write of the final state; no separate copy step.
                System.Diagnostics.Debug.WriteLine("→ Updating assignment status...");
                var repo = new TeamAwareRepository<Assignment>(
                    _storage, sourceTeamFolderName, "assignments");
                await repo.UpdateAsync(assignment);
                System.Diagnostics.Debug.WriteLine("  ✓ Status updated to Published");
            }

            // ── 5. Regenerate index → authoring tier + blob ───────────────────
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"→ Regenerating index for target team: {assignment.TargetTeam}");
                var indexResult = await _indexGenerationService.GenerateAndSaveIndexAsync(assignment.TargetTeam);
                if (!indexResult.DistributionSucceeded)
                    distributionWarnings.Add($"{assignment.TargetTeam}/index.json: {indexResult.DistributionError}");
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
            result.DistributionWarnings = distributionWarnings;
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
        string targetTeamFolderName,
        List<string> distributionWarnings)
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
            distributionWarnings.Add($"{relativePath}: {ex.Message}");
        }

        return relativePath;
    }

    private async Task<string> CopyScheduleToTargetAsync(
        Schedule schedule,
        string targetTeamFolderName,
        string presentationId,
        List<string> distributionWarnings)
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
            distributionWarnings.Add($"{relativePath}: {ex.Message}");
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
        string imageType,
        List<string> distributionWarnings)
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
            distributionWarnings.Add($"{destRelative}: {ex.Message}");
        }

        return destRelative;
    }

    /// <summary>
    /// PUB-1: writes the assignment JSON — already in its final state — to the target team's
    /// authoring-tier folder only (never blob; the assignment copy has never been distributed
    /// via blob, by design — see CopyPresentationToTargetAsync/CopyScheduleToTargetAsync for the
    /// artifacts that are).
    /// </summary>
    private async Task WriteAssignmentJsonAsync(Assignment assignment, string relativePath)
    {
        var json = JsonSerializer.Serialize(assignment, JsonOptions);
        await _storage.WriteTextAsync(relativePath, json);
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

    /// <summary>
    /// One "{relativePath}: {message}" entry per blob-upload failure that was swallowed during this
    /// publish (M5a) — the authoring-tier write still succeeded for each, so <see cref="Success"/>
    /// is unaffected, but clients will not see the content until it is republished or the team's
    /// index is regenerated.
    /// </summary>
    public List<string> DistributionWarnings { get; set; } = new();
}
