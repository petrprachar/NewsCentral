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
///   2. Apply the lock screen for the most-recently-modified active logon-screen presentation,
///      or a configurable default image when no lock-screen content is active. The apply is
///      driven by the live PersonalizationCSP value (the single source of truth) — it writes
///      only when the intended image differs from the current registry value, so a failed write
///      is never recorded as applied and the next cycle retries naturally.
///   3. Write status.json.
///   4. Upload session telemetry from the uploads folder.
///
/// Desktop wallpaper is intentionally not applied here — wallpaper ownership moves to NewsViewer
/// in a later phase. NewsService is a lock-screen-only SYSTEM responsibility.
/// </summary>
public sealed class SyncService(
    IRepositoryReader repository,
    CacheManager cache,
    ILockScreenService lockScreen,
    TelemetryUploader telemetry,
    EntraTeamResolutionService entra,
    IConfiguration configuration,
    ILogger<SyncService> logger)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    // Per-team verification result from the previous cycle. SyncService is a singleton and Worker
    // runs cycles sequentially, so a plain Dictionary is safe and survives across cycles. Lets
    // acceptance follow the cycle-wide "log on change" rule: loud only when the index or the
    // verification result changed, so a misconfigured team (Unsigned/Disabled) is reported at least
    // once per service start instead of flooding every cycle — or being silent forever.
    private readonly Dictionary<string, VerifyResult> _lastVerifyResult =
        new(StringComparer.OrdinalIgnoreCase);

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

            await ApplyLockScreenAsync(effectiveTeams);
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

    internal async Task SyncTeamAsync(string teamFolder, bool isDynamic, CancellationToken ct)
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

        // Tracked for rejected results too, so a later recovery (e.g. Invalid → Valid after a key
        // is provisioned) counts as a change and logs at its natural level.
        var resultChanged = !_lastVerifyResult.TryGetValue(teamFolder, out var previous)
                            || previous != result;
        _lastVerifyResult[teamFolder] = result;

        if (SignatureGate.ShouldReject(result, requireSigned, out var reason))
        {
            // Security outcome — always Error, never demoted or gated on change.
            logger.LogError("Team {Team}: index rejected — {Reason}", teamFolder, reason);
            return;
        }

        var cachedIndex  = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
        var indexChanged = cachedIndex?.IndexHash != remoteIndex.IndexHash;

        // Acceptance logs at its natural level only when something changed — the index bytes or the
        // verification result — and at Debug on the steady state, matching every other cycle path.
        if (indexChanged || resultChanged)
        {
            if (result == VerifyResult.Unsigned)
                logger.LogWarning("Team {Team}: index accepted — {Reason}", teamFolder, reason);
            else
                logger.LogInformation("Team {Team}: index accepted — {Reason}", teamFolder, reason);
        }
        else
        {
            logger.LogDebug("Team {Team}: index accepted — {Reason}", teamFolder, reason);
        }

        if (!indexChanged)
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

    // ── Step 2 — lock screen ─────────────────────────────────────────────────
    //
    // Registry-driven and stateless: the live PersonalizationCSP value is the single source of
    // truth. Each cycle computes the intended image, compares it against the current value, and
    // writes only on a difference. There is no servicestate.json — a failed write simply fails
    // to match next cycle and retries naturally.

    private async Task ApplyLockScreenAsync(IReadOnlyList<string> teams)
    {
        PublishedAssignmentIndex? winner = null;
        string? winnerTeam = null;

        foreach (var teamFolder in teams)
        {
            var index = await cache.ReadJsonAsync<TeamIndexFile>($"{teamFolder}/index.json");
            if (index is null) continue;

            foreach (var a in ActiveAssignments(index))
            {
                if (a.DisplayTypes.IsLogonScreen && IsNewer(a, winner))
                {
                    winner     = a;
                    winnerTeam = teamFolder;
                }
            }
        }

        // Compute the intended path and a human-readable source for logging.
        string? intended;
        string source;
        if (winner is not null)
        {
            intended = cache.Resolve(winner.Content.ImagePath);
            source   = $"presentation {winner.PresentationId}, team {winnerTeam}";
        }
        else
        {
            var defaultPath = configuration.GetValue<string>("Delivery:DefaultLockScreenPath");
            if (!string.IsNullOrEmpty(defaultPath) && File.Exists(defaultPath))
            {
                intended = defaultPath;
                source   = "default";
            }
            else
            {
                if (!string.IsNullOrEmpty(defaultPath))
                    logger.LogWarning(
                        "Default lock-screen path configured but file not found: {Path}", defaultPath);
                intended = null;       // no content, no usable default → sticky
                source   = string.Empty;
            }
        }

        ApplyIntendedLockScreen(intended, source);
    }

    /// <summary>
    /// Registry-gated apply. Compares <paramref name="intended"/> against the live
    /// PersonalizationCSP value and writes only when they differ. A <c>null</c> intended path
    /// leaves the current lock screen untouched (sticky). A failed write is logged as an error and
    /// is <b>not</b> recorded as applied — the next cycle re-evaluates against the unchanged live
    /// value and retries.
    /// </summary>
    internal void ApplyIntendedLockScreen(string? intended, string source)
    {
        if (intended is null)
        {
            logger.LogDebug(
                "Lock screen left unchanged — no active content and no applicable default (sticky)");
            return;
        }

        var current = lockScreen.GetCurrentLockScreenPath();
        if (current is not null && PathsEqual(current, intended))
        {
            logger.LogDebug("Lock screen already current: {Path}", intended);
            return;
        }

        if (lockScreen.SetLockScreen(intended))
            logger.LogInformation("Lock screen applied: {Source} -> {Path}", source, intended);
        else
            logger.LogError("Lock screen apply failed: {Source} -> {Path}", source, intended);
    }

    /// <summary>Full-path, case-insensitive comparison of two file paths.</summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
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
