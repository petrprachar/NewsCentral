using NewsCentral.Models;
using NewsCentral.Repositories;

namespace NewsCentral.Services;

public class PresentationService
{
    private readonly IStorageService _storage;
    private readonly IBlobDistributionService _blobDistribution;
    private readonly AuthenticationService _authService;
    private readonly PosterGenerationService _posterService;
    private readonly IndexGenerationService _indexGenerationService;

    public PresentationService(
        IStorageService storage,
        IBlobDistributionService blobDistribution,
        AuthenticationService authService,
        PosterGenerationService posterService,
        IndexGenerationService indexGenerationService)
    {
        _storage          = storage;
        _blobDistribution = blobDistribution;
        _authService      = authService;
        _posterService    = posterService;
        _indexGenerationService = indexGenerationService;
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public async Task<List<Presentation>> GetPresentationsForTeamAsync(string teamFolderName)
    {
        var repo = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        return await repo.GetAllAsync();
    }

    public async Task<Presentation?> GetPresentationAsync(string teamFolderName, string presentationId)
    {
        var repo = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        return await repo.GetByIdAsync(presentationId);
    }

    /// <summary>
    /// Read image bytes from the authoring tier.
    /// imagePath is the relative path stored in Presentation.OriginalImagePath
    /// or Presentation.GeneratedImagePath, e.g.
    /// "team-alpha/images/original/img_{id}.jpg"
    /// </summary>
    public async Task<byte[]> GetImageDataAsync(string teamFolderName, string imagePath)
    {
        var data = await _storage.ReadBytesAsync(imagePath);

        if (data == null)
            throw new FileNotFoundException($"Image not found: {imagePath}");

        return data;
    }

    // ── Create ───────────────────────────────────────────────────────────────

    public async Task<Presentation> CreatePresentationAsync(
        string teamId,
        string teamFolderName,
        string name,
        string description,
        string moreUrl,
        byte[] imageData,
        string originalImageName)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        if (!_authService.HasRole(teamId, "ContentAuthor") && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException(
                "You don't have permission to create content for this team");

        // Save original image to authoring tier
        // WriteBytesAsync creates the folder if it does not exist
        var imageId        = Guid.NewGuid().ToString();
        var imageExtension = Path.GetExtension(originalImageName);
        var savedImageName = $"img_{imageId}{imageExtension}";
        var imageRelPath   = $"{teamFolderName}/images/original/{savedImageName}";

        await _storage.WriteBytesAsync(imageRelPath, imageData);

        var presentation = new Presentation
        {
            PresentationID   = Guid.NewGuid().ToString(),
            Version          = 1,
            TeamID           = teamId,
            TeamFolderName   = teamFolderName,
            Name             = name,
            Description      = description,
            MoreUrl          = moreUrl,
            DateCreated      = DateTime.UtcNow,
            OriginalImagePath = imageRelPath,
            ImageOriginalName = originalImageName,
            ImageName         = savedImageName,
            CreatedBy         = currentUser.UserID,
            LastModified      = DateTime.UtcNow,
            ModifiedBy        = currentUser.UserID,
            ContentImageBase64 = Convert.ToBase64String(imageData)
        };

        var repo = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        return await repo.CreateAsync(presentation);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    public async Task<Presentation> UpdatePresentationAsync(
        string teamFolderName,
        string presentationId,
        string name,
        string description,
        string moreUrl)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("You can only edit your own presentations");

        presentation.Name         = name;
        presentation.Description  = description;
        presentation.MoreUrl      = moreUrl;
        presentation.LastModified = DateTime.UtcNow;
        presentation.ModifiedBy   = currentUser.UserID;

        var result = await repo.UpdateAsync(presentation);
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);
        return result;
    }

    /// <summary>
    /// Regenerates the poster from the stored original image and updates
    /// the presentation record with the new generated image path and base64.
    /// </summary>
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
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("You can only edit your own presentations");

        // Read original image from authoring tier using the stored relative path
        var originalImageData = await _storage.ReadBytesAsync(presentation.OriginalImagePath)
            ?? throw new FileNotFoundException(
                $"Original image not found: {presentation.OriginalImagePath}");

        // Generate poster (now writes via IStorageService internally)
        var posterPath = await _posterService.GeneratePosterAsync(
            teamFolderName,
            presentationId,
            presentation.Version.ToString(),
            originalImageData,
            headlineText,
            bodyText,
            ctaText);

        // Read back the saved poster for base64 embedding in the JSON record
        var posterImageData = await _posterService.GetPosterImageDataAsync(teamFolderName, posterPath);

        presentation.GeneratedImagePath = posterPath;
        presentation.ContentImageBase64 = Convert.ToBase64String(posterImageData);
        presentation.LastModified       = DateTime.UtcNow;
        presentation.ModifiedBy         = currentUser.UserID;
        presentation.IsNewsOfWeek       = isNewsOfWeek;
        presentation.IsWallpaper        = isWallpaper;
        presentation.IsLogonScreen      = isLogonScreen;

        var result = await repo.UpdateAsync(presentation);
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);
        return result;
    }

    /// <summary>
    /// Update all metadata fields (name, description, flags) without regenerating the poster.
    /// If no poster exists yet, copies the original image to the generated folder so
    /// ContentImageBase64 is always populated.
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
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (presentation.CreatedBy != currentUser.UserID && !currentUser.IsSystemAdmin)
            throw new UnauthorizedAccessException("You can only edit your own presentations");

        presentation.Name         = name;
        presentation.Description  = description;
        presentation.MoreUrl      = moreUrl;
        presentation.IsNewsOfWeek = isNewsOfWeek;
        presentation.IsWallpaper  = isWallpaper;
        presentation.IsLogonScreen = isLogonScreen;
        presentation.LastModified  = DateTime.UtcNow;
        presentation.ModifiedBy    = currentUser.UserID;

        // If no poster exists yet, promote the original image so base64 is always set
        if (string.IsNullOrEmpty(presentation.ContentImageBase64) &&
            !string.IsNullOrEmpty(presentation.OriginalImagePath))
        {
            var imageData = await _storage.ReadBytesAsync(presentation.OriginalImagePath);

            if (imageData != null)
            {
                presentation.ContentImageBase64 = Convert.ToBase64String(imageData);

                // Also save a copy to the generated folder for consistent image serving
                var contentImageFileName = $"content_{presentation.PresentationID}.jpg";
                var contentImageRelPath  = $"{teamFolderName}/images/generated/{contentImageFileName}";

                await _storage.WriteBytesAsync(contentImageRelPath, imageData);

                presentation.GeneratedImagePath = contentImageRelPath;
            }
        }

        var result = await repo.UpdateAsync(presentation);
        await RegenerateIndexesForPresentationAsync(teamFolderName, presentationId);
        return result;
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Soft-deletes a presentation and all its related assignments and schedules
    /// by moving them to the team's flat {team}/deleted/ folder.
    ///
    /// If any related assignments were Published, also:
    ///   - Removes their blobs from the distribution tier
    ///   - Regenerates (and re-pushes) index.json for each affected target team
    /// </summary>
    public async Task<bool> DeletePresentationAsync(string teamFolderName, string presentationId)
    {
        var currentUser = _authService.GetCurrentUser()
            ?? throw new UnauthorizedAccessException("Not authenticated");

        var repo         = new TeamAwareRepository<Presentation>(_storage, teamFolderName, "presentations");
        var presentation = await repo.GetByIdAsync(presentationId)
            ?? throw new InvalidOperationException($"Presentation {presentationId} not found");

        if (!_authService.IsSystemAdmin() && presentation.CreatedBy != currentUser.UserID)
            throw new UnauthorizedAccessException(
                "You don't have permission to delete this presentation");

        System.Diagnostics.Debug.WriteLine($"=== DeletePresentation: {presentationId} ===");

        // Collect target teams BEFORE moving files so we can regenerate indexes after
        var targetTeamsWithPublished =
            await GetTargetTeamsForPresentationAsync(teamFolderName, presentationId);

        // ── 1. Soft-delete the presentation JSON ─────────────────────────────
        await _storage.MoveFileAsync(
            $"{teamFolderName}/content/presentations/pres_{presentationId}.json",
            $"{teamFolderName}/deleted/pres_{presentationId}.json");

        System.Diagnostics.Debug.WriteLine($"✓ Moved pres_{presentationId} to deleted/");

        // ── 2. Soft-delete all related assignments ────────────────────────────
        var assignmentRepo  = new TeamAwareRepository<Assignment>(_storage, teamFolderName, "assignments");
        var allAssignments  = await assignmentRepo.GetAllAsync();
        var relatedAssignments = allAssignments
            .Where(a => a.PresentationID == presentationId)
            .ToList();

        foreach (var assignment in relatedAssignments)
        {
            var src = $"{teamFolderName}/content/assignments/assign_{assignment.AssignmentID}.json";
            var dst = $"{teamFolderName}/deleted/assign_{assignment.AssignmentID}.json";

            if (await _storage.FileExistsAsync(src))
            {
                await _storage.MoveFileAsync(src, dst);
                System.Diagnostics.Debug.WriteLine(
                    $"✓ Moved assign_{assignment.AssignmentID} to deleted/");
            }
        }

        // ── 3. Soft-delete all related schedules ──────────────────────────────
        var scheduleRepo = new TeamAwareRepository<Schedule>(_storage, teamFolderName, "schedules");
        var allSchedules = await scheduleRepo.GetAllAsync();
        var relatedSchedules = allSchedules
            .Where(s => s.PresentationID == presentationId)
            .ToList();

        foreach (var schedule in relatedSchedules)
        {
            var src = $"{teamFolderName}/content/schedules/sched_{schedule.ScheduleID}.json";
            var dst = $"{teamFolderName}/deleted/sched_{schedule.ScheduleID}.json";

            if (await _storage.FileExistsAsync(src))
            {
                await _storage.MoveFileAsync(src, dst);
                System.Diagnostics.Debug.WriteLine(
                    $"✓ Moved sched_{schedule.ScheduleID} to deleted/");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"✓ Moved {relatedAssignments.Count} assignments, " +
            $"{relatedSchedules.Count} schedules to deleted/");

        // ── 4. Blob distribution cleanup ──────────────────────────────────────
        // Only published assignments had content pushed to blob.
        // Delete blobs per assignment/target-team so other teams' content is untouched.
        var publishedAssignments = relatedAssignments
            .Where(a => a.Status == AssignmentStatus.Published)
            .ToList();

        foreach (var assignment in publishedAssignments)
        {
            try
            {
                // Presentation and schedule JSONs in the target team's blob prefix
                await _blobDistribution.DeleteAsync(
                    $"{assignment.TargetTeam}/content/presentations/pres_{presentationId}.json");

                await _blobDistribution.DeleteAsync(
                    $"{assignment.TargetTeam}/content/schedules/sched_{assignment.ScheduleID}.json");

                // Image blobs use the original filename without soft-delete prefix
                // (soft-delete prefix is authoring-tier only)
                if (!string.IsNullOrEmpty(presentation.GeneratedImagePath))
                {
                    var genFile = Path.GetFileName(presentation.GeneratedImagePath);
                    await _blobDistribution.DeleteAsync(
                        $"{assignment.TargetTeam}/images/generated/{genFile}");
                }

                if (!string.IsNullOrEmpty(presentation.OriginalImagePath))
                {
                    var origFile = Path.GetFileName(presentation.OriginalImagePath);
                    await _blobDistribution.DeleteAsync(
                        $"{assignment.TargetTeam}/images/original/{origFile}");
                }

                System.Diagnostics.Debug.WriteLine(
                    $"✓ Blob cleanup done for assignment {assignment.AssignmentID} " +
                    $"→ target {assignment.TargetTeam}");
            }
            catch (Exception ex)
            {
                // Non-fatal: authoring-tier files are already moved.
                // Blob can be cleaned up manually or via a future reconciliation job.
                System.Diagnostics.Debug.WriteLine(
                    $"⚠ WARNING: Blob cleanup failed for assignment " +
                    $"{assignment.AssignmentID}: {ex.Message}");
            }
        }

        // ── 5. Regenerate indexes for affected target teams ───────────────────
        // GenerateAndSaveIndexAsync writes the updated index to both authoring
        // tier and blob distribution in one call.
        foreach (var targetTeam in targetTeamsWithPublished)
        {
            try
            {
                await _indexGenerationService.GenerateAndSaveIndexAsync(targetTeam);
                System.Diagnostics.Debug.WriteLine(
                    $"✓ Index regenerated for {targetTeam}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"⚠ WARNING: Index regeneration failed for {targetTeam}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"=== DeletePresentation complete ===");
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Regenerates index.json for every target team that has a published
    /// assignment using this presentation. Called after any metadata update.
    /// IndexGenerationService.GenerateAndSaveIndexAsync pushes to blob automatically.
    /// </summary>
    private async Task RegenerateIndexesForPresentationAsync(
        string sourceTeamFolderName,
        string presentationId)
    {
        try
        {
            var targetTeams = await GetTargetTeamsForPresentationAsync(
                sourceTeamFolderName, presentationId);

            if (targetTeams.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"  No published assignments for {presentationId} — skipping index regen");
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"→ Regenerating indexes for {targetTeams.Count} target team(s)");

            foreach (var targetTeam in targetTeams)
            {
                try
                {
                    await _indexGenerationService.GenerateAndSaveIndexAsync(targetTeam);
                    System.Diagnostics.Debug.WriteLine(
                        $"  ✓ Index regenerated for {targetTeam}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"  ⚠ WARNING: Index regen failed for {targetTeam}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  ⚠ WARNING: Index regeneration check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the distinct target team folder names of all Published assignments
    /// linked to the given presentation in the source team.
    /// </summary>
    private async Task<List<string>> GetTargetTeamsForPresentationAsync(
        string sourceTeamFolderName,
        string presentationId)
    {
        try
        {
            var repo = new TeamAwareRepository<Assignment>(
                _storage, sourceTeamFolderName, "assignments");

            var all = await repo.GetAllAsync();

            return all
                .Where(a => a.PresentationID == presentationId &&
                            a.Status == AssignmentStatus.Published)
                .Select(a => a.TargetTeam)
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error getting target teams for {presentationId}: {ex.Message}");
            return new List<string>();
        }
    }
}
