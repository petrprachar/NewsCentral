using System.Security.Cryptography;
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
///      or a configurable default image when no lock-screen content is active. The winning
///      image is re-verified against its signed hash and published to a protected,
///      non-user-writable folder (IImagePublisher) before PersonalizationCSP is pointed at it —
///      not at the ProgramData cache directly. Three-state, registry-driven, and stateless: the
///      live PersonalizationCSP value is the single source of truth. When there is no active
///      content and no usable default, a value NewsService previously published is CLEARED —
///      returning the machine to Windows' own default lock screen at the next lock — but a value
///      it did not publish (GPO, Intune, or a manual admin change) is always left alone. The
///      whole surface can be disabled via Delivery:LockScreenEnabled (default true) for
///      RDS/VDI/RemoteApp hosts, where one machine-wide value cannot correctly serve many
///      sessions; disabling it is NOT a revert — whatever is currently applied stays frozen in
///      place. A failed write or clear is never recorded as applied — the next cycle re-evaluates
///      and retries naturally.
///   3. Write status.json.
///   4. Upload session telemetry from the uploads folder.
///
/// Desktop wallpaper is intentionally not applied here — NewsViewer owns it today, applied
/// per-user in the user session. Delivery:WallpaperEnabled is reserved for a later phase where
/// that ownership migrates to NewsService, and has no effect yet. NewsService is a
/// lock-screen-only SYSTEM responsibility for now.
/// </summary>
public sealed class SyncService(
    IRepositoryReader repository,
    CacheManager cache,
    ILockScreenService lockScreen,
    IImagePublisher imagePublisher,
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
    // Three-state, registry-driven, and stateless: the live PersonalizationCSP value is the
    // single source of truth. Delivery:LockScreenEnabled (default true) gates the ENTIRE step —
    // when false, nothing below is read, published, swept, written, or cleared. Otherwise each
    // cycle computes the intended source image, publishes it (re-verified, protected folder —
    // see IImagePublisher), and dispatches on (intended published path, live CSP value): write
    // when they differ, clear when intended is null and the live value is one NewsService itself
    // published, leave alone when intended is null and the live value is foreign (or absent).
    // There is no servicestate.json — a failed write or clear simply fails to match next cycle
    // and retries naturally.

    // internal (not private) so LockScreenApplyTests can exercise the LockScreenEnabled=false
    // early-out directly without standing up the full RunCycleAsync dependency graph.
    internal async Task ApplyLockScreenAsync(IReadOnlyList<string> teams)
    {
        // Master opt-out (RDS/VDI/RemoteApp) — must be the very first statement: no CSP read,
        // sweep, publish, or write/clear happens below this line when disabled.
        if (!configuration.GetValue("Delivery:LockScreenEnabled", true))
        {
            logger.LogDebug(
                "Lock screen disabled (Delivery:LockScreenEnabled = false) — surface left untouched");
            return;
        }

        // Stale-file cleanup from the PREVIOUS cycle's publish — never the cycle that just
        // published a file, since Windows may still hold it open from the apply that just ran.
        var livePath = lockScreen.GetCurrentLockScreenPath();
        imagePublisher.SweepExcept(livePath is null ? null : Path.GetFileName(livePath));

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

        // Compute the intended SOURCE path, its expected hash, and a human-readable source label.
        string? sourcePath;
        string? expectedHash;
        string source;
        if (winner is not null)
        {
            sourcePath   = cache.Resolve(winner.Content.ImagePath);
            expectedHash = winner.Content.ImageHash;
            source       = $"presentation {winner.PresentationId}, team {winnerTeam}";
        }
        else
        {
            var defaultPath = configuration.GetValue<string>("Delivery:DefaultLockScreenPath");
            if (!string.IsNullOrEmpty(defaultPath) && File.Exists(defaultPath))
            {
                sourcePath = defaultPath;
                source     = "default";
                // The default image is admin-supplied and carries no index hash to check against.
                // Hashing the file itself makes Publish's verification a self-consistent no-op —
                // NOT a skipped security check — so Publish always verifies what it copies.
                expectedHash = "sha256:" +
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(defaultPath))).ToLowerInvariant();
            }
            else
            {
                if (!string.IsNullOrEmpty(defaultPath))
                    logger.LogWarning(
                        "Default lock-screen path configured but file not found: {Path}", defaultPath);
                sourcePath   = null;    // no content, no usable default → sticky
                expectedHash = null;
                source       = string.Empty;
            }
        }

        string? intended = null;
        if (sourcePath is not null)
        {
            intended = imagePublisher.Publish(sourcePath, expectedHash!, "lockscreen");
            if (intended is null)
                logger.LogWarning(
                    "Lock-screen image publish failed for {Source} — no usable image this cycle", source);
        }

        ApplyIntendedLockScreen(intended, source);
    }

    /// <summary>
    /// Registry-gated apply — the three-state model. Compares <paramref name="intended"/> against
    /// the live PersonalizationCSP value:
    ///   • intended non-null, live already matches → no-op (steady state).
    ///   • intended non-null, live differs or is absent → write the trio; log Information.
    ///   • intended null, live absent → no-op, silent.
    ///   • intended null, live present and inside Delivery:PublishedImagePath (i.e. one WE
    ///     published) → <b>clear</b> the trio; log Information. This is the teardown this milestone
    ///     adds — a machine with no active content and no usable default returns to Windows' own
    ///     default lock screen at the next lock, instead of staying frozen on stale content forever.
    ///   • intended null, live present but NOT inside Delivery:PublishedImagePath → left alone,
    ///     logged at Debug. NewsCentral never clears a CSP value it did not write — a value set by
    ///     GPO, Intune, or a manual admin change is someone else's to manage.
    /// A failed write or clear is logged and is <b>never</b> recorded as applied/cleared — the next
    /// cycle re-evaluates against the unchanged live value and retries.
    /// </summary>
    internal void ApplyIntendedLockScreen(string? intended, string source)
    {
        var current = lockScreen.GetCurrentLockScreenPath();

        if (intended is not null)
        {
            if (current is not null && PathsEqual(current, intended))
            {
                logger.LogDebug("Lock screen already current: {Path}", intended);
                return;
            }

            if (lockScreen.SetLockScreen(intended))
                logger.LogInformation("Lock screen applied: {Source} -> {Path}", source, intended);
            else
                logger.LogError("Lock screen apply failed: {Source} -> {Path}", source, intended);
            return;
        }

        // intended == null — no active content and no usable default (or publishing it failed).
        if (current is null)
        {
            logger.LogDebug("Lock screen already unset — nothing to clear");
            return;
        }

        if (!IsUnderPublishRoot(current))
        {
            logger.LogDebug(
                "Lock screen left unchanged — current value is not one NewsService published: {Path}",
                current);
            return;
        }

        lockScreen.ClearLockScreen();
        logger.LogInformation(
            "Lock screen cleared — no active content and no usable default (was: {Path})", current);
    }

    /// <summary>
    /// True when <paramref name="path"/> resolves to a location inside
    /// <c>Delivery:PublishedImagePath</c> — the ownership test that gates clearing. Compares
    /// fully-normalized absolute paths (<see cref="Path.GetFullPath(string)"/>,
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>) with a directory-prefix check, never a raw
    /// string <c>StartsWith</c> on the configured value, so a trailing separator, a relative
    /// <c>..</c> segment, or different casing on either side cannot defeat it. A misconfigured or
    /// unresolvable <c>PublishedImagePath</c> fails <b>closed</b>: every live value is then treated
    /// as foreign and is never cleared.
    /// </summary>
    private bool IsUnderPublishRoot(string path)
    {
        var publishRoot = configuration.GetValue<string>("Delivery:PublishedImagePath");
        if (string.IsNullOrWhiteSpace(publishRoot)) return false;

        try
        {
            var normalizedRoot = Path.GetFullPath(publishRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path);

            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(
                       normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // unresolvable path → fail closed, treat as foreign, never clear
        }
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
