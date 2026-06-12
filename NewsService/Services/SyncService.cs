using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;
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
    EntraTeamResolutionService entra,
    IConfiguration configuration,
    ILogger<SyncService> logger)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    public async Task RunCycleAsync(string[] teams, CancellationToken ct)
    {
        bool online = false;
        string syncSource = "None";

        // Entra device team resolution — time-boxed and failure-isolated so a Graph problem
        // never stalls or fails blob sync. Runs FIRST so resolved-teams.json reflects this cycle
        // before the effective team set is computed below.
        await RefreshEntraTeamsAsync(ct);

        // Effective teams = static (registry/appsettings) ∪ dynamic (Entra-resolved). Per-team
        // verification routes through SignatureGate.VerifyWithPrecedence (registry key wins →
        // delivered key for dynamic teams → unsigned), so dynamic-only branching never leaks here.
        var (effectiveTeams, dynamicTeams) = ResolveEffectiveTeams(teams, cache.Root);

        try
        {
            if (!repository.IsAvailable)
            {
                logger.LogWarning("Repository not reachable — content served from cache only");
            }
            else
            {
                online = await SyncAllTeamsAsync(effectiveTeams, dynamicTeams, ct);
                syncSource = repository.SyncSource;
            }

            await ApplyWallpaperAndLockscreenAsync(effectiveTeams);
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

    // ── Step 0 — Entra dynamic-team resolution (time-boxed, isolated) ─────────

    private async Task RefreshEntraTeamsAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await entra.RefreshAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // service shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Entra team resolution failed — continuing with static teams.");
        }
    }

    /// <summary>
    /// Combines the static team list with the dynamic (Entra-resolved) team set read from
    /// resolved-teams.json. Returned as the de-duplicated effective list plus the dynamic set
    /// (so callers can ask isDynamic per team). Pure aside from the resolved-teams.json read.
    /// </summary>
    internal static (List<string> Effective, HashSet<string> Dynamic) ResolveEffectiveTeams(
        string[] staticTeams, string cacheRootPath)
    {
        var dynamicTeams = ResolvedTeamsReader.ReadDynamicTeamFolders(cacheRootPath);
        var effective    = EffectiveTeams.Union(staticTeams, dynamicTeams);
        return (effective, dynamicTeams);
    }

    // ── Step 1 — team sync ───────────────────────────────────────────────────

    private async Task<bool> SyncAllTeamsAsync(
        IReadOnlyList<string> teams, HashSet<string> dynamicTeams, CancellationToken ct)
    {
        bool allOk = true;
        foreach (var teamFolder in teams)
        {
            ct.ThrowIfCancellationRequested();
            try { await SyncTeamAsync(teamFolder, dynamicTeams.Contains(teamFolder), ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync team {Team}", teamFolder);
                allOk = false;
            }
        }
        return allOk;
    }

    private async Task SyncTeamAsync(string teamFolder, bool isDynamic, CancellationToken ct)
    {
        var remoteJson = await repository.ReadTextAsync($"{teamFolder}/index.json", ct);
        if (remoteJson is null)
        {
            logger.LogWarning("No index.json in repository for team {Team}", teamFolder);
            return;
        }

        var remoteIndex = JsonSerializer.Deserialize<TeamIndexFile>(remoteJson, Json);
        if (remoteIndex is null) return;

        var keys          = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolder);
        var result        = SignatureGate.VerifyWithPrecedence(remoteIndex, keys, isDynamic);
        var requireSigned = configuration.GetValue<bool>("Signing:RequireSignedIndex");
        if (SignatureGate.ShouldReject(result, requireSigned, out var reason))
        {
            logger.LogError("Team {Team}: index rejected — {Reason}", teamFolder, reason);
            return;
        }
        if (result == VerifyResult.Unsigned)
            logger.LogWarning("Team {Team}: index accepted — {Reason}", teamFolder, reason);
        else
            logger.LogInformation("Team {Team}: index accepted — {Reason}", teamFolder, reason);

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

    private async Task ApplyWallpaperAndLockscreenAsync(IReadOnlyList<string> teams)
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
            var path = cache.Resolve(wallpaperEntry.Content.ImagePath);
            wallpaper.SetWallpaper(path);
            state.LastWallpaperPresentationId = wallpaperEntry.PresentationId;
        }

        if (lockscreenEntry is not null &&
            lockscreenEntry.PresentationId != state.LastLockscreenPresentationId)
        {
            var path = cache.Resolve(lockscreenEntry.Content.ImagePath);
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
