using System.Text.Json;
using NewsCentral.Models.IndexFile;
using NewsService.Models;

namespace NewsService.Services;

/// <summary>
/// Orchestrates one complete poll cycle:
///   1. For each team: compare remote index hash vs cached hash; sync changed images.
///   2. Apply wallpaper / lock screen for the most-recently-modified active presentation.
///   3. Write status.json.
///   4. Upload session telemetry from the uploads folder.
/// </summary>
public sealed class SyncService(
    IRepositoryReader repository,
    CacheManager cache,
    WallpaperService wallpaper,
    TelemetryUploader telemetry,
    ILogger<SyncService> logger)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    public async Task RunCycleAsync(string[] teams, CancellationToken ct)
    {
        bool online = false;
        string syncSource = "None";

        try
        {
            if (!repository.IsAvailable)
            {
                logger.LogWarning("Repository not reachable — content served from cache only");
            }
            else
            {
                online = await SyncAllTeamsAsync(teams, ct);
                syncSource = repository.SyncSource;
            }

            await ApplyWallpaperAndLockscreenAsync(teams);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during sync cycle");
        }
        finally
        {
            await WriteStatusAsync(online, syncSource);
        }

        await telemetry.ProcessAsync(ct);
    }

    // ── Step 1 — team sync ───────────────────────────────────────────────────

    private async Task<bool> SyncAllTeamsAsync(string[] teams, CancellationToken ct)
    {
        bool allOk = true;
        foreach (var teamFolder in teams)
        {
            ct.ThrowIfCancellationRequested();
            try { await SyncTeamAsync(teamFolder, ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync team {Team}", teamFolder);
                allOk = false;
            }
        }
        return allOk;
    }

    private async Task SyncTeamAsync(string teamFolder, CancellationToken ct)
    {
        var remoteJson = await repository.ReadTextAsync($"{teamFolder}/index.json", ct);
        if (remoteJson is null)
        {
            logger.LogWarning("No index.json in repository for team {Team}", teamFolder);
            return;
        }

        var remoteIndex = JsonSerializer.Deserialize<TeamIndexFile>(remoteJson, Json);
        if (remoteIndex is null) return;

        var cachedIndex = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");

        if (cachedIndex?.IndexHash == remoteIndex.IndexHash)
        {
            logger.LogDebug("Team {Team}: index unchanged", teamFolder);
            return;
        }

        logger.LogInformation("Team {Team}: index changed — syncing {Count} assignment(s)",
            teamFolder, remoteIndex.PublishedAssignments.Count);

        foreach (var assignment in remoteIndex.PublishedAssignments)
        {
            ct.ThrowIfCancellationRequested();
            await SyncImageAsync(teamFolder, assignment, ct);
        }

        // Write the new index only after all images are safely cached.
        await cache.WriteTextAsync($"{teamFolder}/index.json", remoteJson);
        logger.LogInformation("Team {Team}: sync complete", teamFolder);
    }

    private async Task SyncImageAsync(
        string teamFolder, PublishedAssignmentIndex assignment, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(assignment.Content.ImagePath)) return;

        var relativePath = assignment.Content.ImagePath;
        var storedHash   = cache.ReadStoredHash(relativePath);

        if (storedHash == assignment.Content.ImageHash && cache.FileExists(relativePath))
        {
            logger.LogDebug("Image up-to-date: {Path}", relativePath);
            return;
        }

        var bytes = await repository.ReadBytesAsync(relativePath, ct);
        if (bytes is null)
        {
            logger.LogWarning("Image not found in repository: {Path}", relativePath);
            return;
        }

        await cache.WriteBytesAsync(relativePath, bytes);
        logger.LogDebug("Image synced: {Path} ({Bytes} bytes)", relativePath, bytes.Length);
    }

    // ── Step 2 — wallpaper / lock screen ─────────────────────────────────────

    private async Task ApplyWallpaperAndLockscreenAsync(string[] teams)
    {
        var state = await cache.ReadJsonAsync<ServiceState>("servicestate.json") ?? new();

        PublishedAssignmentIndex? wallpaperEntry   = null;
        PublishedAssignmentIndex? lockscreenEntry  = null;
        string? wallpaperTeam  = null;
        string? lockscreenTeam = null;

        foreach (var teamFolder in teams)
        {
            var index = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
            if (index is null) continue;

            foreach (var a in ActiveAssignments(index))
            {
                if (a.DisplayTypes.IsWallpaper &&
                    IsNewer(a, wallpaperEntry))
                {
                    wallpaperEntry = a;
                    wallpaperTeam  = teamFolder;
                }

                if (a.DisplayTypes.IsLogonScreen &&
                    IsNewer(a, lockscreenEntry))
                {
                    lockscreenEntry = a;
                    lockscreenTeam  = teamFolder;
                }
            }
        }

        if (wallpaperEntry is not null &&
            wallpaperEntry.PresentationId != state.LastWallpaperPresentationId)
        {
            var path = cache.Resolve($"{wallpaperTeam}/{wallpaperEntry.Content.ImagePath}");
            wallpaper.SetWallpaper(path);
            state.LastWallpaperPresentationId = wallpaperEntry.PresentationId;
        }

        if (lockscreenEntry is not null &&
            lockscreenEntry.PresentationId != state.LastLockscreenPresentationId)
        {
            var path = cache.Resolve($"{lockscreenTeam}/{lockscreenEntry.Content.ImagePath}");
            wallpaper.SetLockScreen(path);
            state.LastLockscreenPresentationId = lockscreenEntry.PresentationId;
        }

        await cache.WriteJsonAsync("servicestate.json", state);
    }

    /// <summary>
    /// Returns assignments whose schedule window covers the current local date/time
    /// and whose DaysOfWeek list includes today (1=Mon … 7=Sun).
    /// Schedules use client local time — no timezone conversion needed.
    /// </summary>
    private static IEnumerable<PublishedAssignmentIndex> ActiveAssignments(TeamIndexFile index)
    {
        var now = DateTime.Now;
        // DayOfWeek: Sunday=0, Monday=1 … Saturday=6 → map to 1=Mon … 7=Sun
        var todayKey = now.DayOfWeek == DayOfWeek.Sunday ? "7" : ((int)now.DayOfWeek).ToString();

        return index.PublishedAssignments.Where(a =>
            a.ScheduleStart <= now &&
            a.ScheduleEnd   >= now &&
            a.DaysOfWeek.Split(',').Contains(todayKey));
    }

    private static bool IsNewer(PublishedAssignmentIndex candidate, PublishedAssignmentIndex? current) =>
        current is null || candidate.PresentationLastModified > current.PresentationLastModified;

    // ── Step 3 — status.json ─────────────────────────────────────────────────

    private async Task WriteStatusAsync(bool isOnline, string syncSource)
    {
        await cache.WriteJsonAsync("status.json", new StatusFile
        {
            LastSyncTime = DateTime.UtcNow,
            IsOnline     = isOnline,
            SyncSource   = syncSource
        });
    }
}
