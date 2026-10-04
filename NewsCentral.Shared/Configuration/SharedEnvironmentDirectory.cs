using System.Text.Json;

namespace NewsCentral.Configuration;

/// <summary>
/// One entry in a shared <c>config/environments.json</c> directory file. A null
/// <see cref="DeletedUtc"/> is a live entry; a non-null value is a tombstone recording that it was
/// removed "for everyone" — tombstones travel with the merge so a deletion propagates instead of
/// being resurrected by a machine that hasn't seen it yet (see <see cref="SharedDirectoryMerger"/>).
/// <see cref="DistributionFingerprint"/> (M5a) is appended last, with a default, so every existing
/// positional construction of this record — in <c>EnvironmentDirectoryService</c> and in the M4b
/// tests — keeps compiling unchanged.
/// </summary>
public sealed record SharedDirectoryEntry(
    string DataPath,
    string? DisplayName,
    string? AddedBy,
    DateTime ModifiedUtc,
    DateTime? DeletedUtc,
    string? DistributionFingerprint = null);

/// <summary>The full contents of <c>{DataPath}\config\environments.json</c> for one environment.</summary>
public sealed class SharedDirectoryFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<SharedDirectoryEntry> Entries { get; set; } = new();
}

public static class SharedDirectoryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// Accepts or rejects entries read from a (potentially untrusted — written by any administrator of
/// any environment that has ever opened this one) shared directory file before they are allowed to
/// influence this machine's local cache. Pure, no I/O.
/// </summary>
public static class SharedDirectoryValidator
{
    /// <summary>Hard cap on how many entries a single sync processes.</summary>
    public const int MaxEntries = 200;

    /// <summary>Display names longer than this are truncated, never rejected outright.</summary>
    public const int MaxDisplayNameLength = 100;

    /// <summary>
    /// A distributionFingerprint longer than this is rejected outright (unlike an over-long display
    /// name) — a fingerprint is an internal identifier, not free text, so an abnormal length is
    /// treated as malformed rather than something to salvage by truncating.
    /// </summary>
    public const int MaxFingerprintLength = 400;

    /// <summary>
    /// Filters <paramref name="incoming"/> down to entries that are safe to merge: an absolute UNC
    /// <see cref="SharedDirectoryEntry.DataPath"/> (never a local drive path, a relative path, or a
    /// <c>\\?\</c> / <c>\\.\</c> device-namespace form). A display name over
    /// <see cref="MaxDisplayNameLength"/> is truncated rather than rejected. At most
    /// <see cref="MaxEntries"/> are returned, keeping the newest by <see cref="SharedDirectoryEntry.ModifiedUtc"/>
    /// — never the merge's effective-timestamp rule, which also considers <c>DeletedUtc</c>; the cap
    /// is purely a volume bound, independent of merge semantics.
    /// </summary>
    public static IReadOnlyList<SharedDirectoryEntry> Filter(
        IEnumerable<SharedDirectoryEntry> incoming, out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        var accepted = new List<SharedDirectoryEntry>();

        foreach (var entry in incoming)
        {
            if (string.IsNullOrWhiteSpace(entry.DataPath))
            {
                warningList.Add("Shared directory entry has no dataPath and is rejected.");
                continue;
            }

            if (!EnvironmentPaths.IsAbsolute(entry.DataPath) || !EnvironmentPaths.IsShareable(entry.DataPath))
            {
                warningList.Add(
                    $"Shared directory entry \"{entry.DataPath}\" is not an absolute UNC path and is rejected.");
                continue;
            }

            if (entry.DistributionFingerprint != null && entry.DistributionFingerprint.Length > MaxFingerprintLength)
            {
                warningList.Add(
                    $"Shared directory entry \"{entry.DataPath}\" has a distributionFingerprint longer than " +
                    $"{MaxFingerprintLength} characters and is rejected.");
                continue;
            }

            var displayName = entry.DisplayName;
            if (!string.IsNullOrEmpty(displayName) && displayName.Length > MaxDisplayNameLength)
                displayName = displayName.Substring(0, MaxDisplayNameLength);

            accepted.Add(ReferenceEquals(displayName, entry.DisplayName) || displayName == entry.DisplayName
                ? entry
                : entry with { DisplayName = displayName });
        }

        if (accepted.Count > MaxEntries)
        {
            warningList.Add(
                $"Shared directory has {accepted.Count} entries after validation; only the newest {MaxEntries} (by modifiedUtc) were kept.");
        }

        warnings = warningList;
        return accepted
            .OrderByDescending(e => e.ModifiedUtc)
            .Take(MaxEntries)
            .ToList();
    }
}

/// <summary>
/// Pure, deterministic merge of two shared-directory entry lists (this machine's local cache and one
/// environment's remote file) into one, keyed by <see cref="EnvironmentPaths.Canonicalize"/>. Never
/// touches disk.
/// </summary>
public static class SharedDirectoryMerger
{
    /// <summary>Default tombstone retention — see <see cref="Merge"/>'s <paramref name="purgeAfter"/>.</summary>
    public static readonly TimeSpan DefaultPurgeAfter = TimeSpan.FromDays(90);

    /// <summary>
    /// Merges <paramref name="local"/> and <paramref name="remote"/>. For each canonical path, the
    /// entry with the newer effective timestamp (<c>DeletedUtc ?? ModifiedUtc</c>) wins. An exact
    /// tie is won by whichever side is a tombstone (<c>DeletedUtc</c> set); if both or neither are
    /// tombstones, the lexicographically greater <c>AddedBy</c> wins, then the lexicographically
    /// greater <c>DisplayName</c> — so the result never depends on argument order. Tombstones whose
    /// <c>DeletedUtc</c> is older than <paramref name="purgeAfter"/> relative to
    /// <paramref name="nowUtc"/> are dropped from the result entirely (not just hidden).
    /// <see cref="LocalChanged"/> / RemoteChanged report whether the merged set differs from that
    /// input, ignoring order — the caller's cue to write back only the side(s) that actually changed.
    /// </summary>
    public static (IReadOnlyList<SharedDirectoryEntry> Merged, bool LocalChanged, bool RemoteChanged) Merge(
        IReadOnlyList<SharedDirectoryEntry> local,
        IReadOnlyList<SharedDirectoryEntry> remote,
        DateTime nowUtc,
        TimeSpan purgeAfter)
    {
        var byPath = new Dictionary<string, SharedDirectoryEntry>();

        void Consider(SharedDirectoryEntry candidate)
        {
            var canonical = EnvironmentPaths.Canonicalize(candidate.DataPath);
            byPath[canonical] = byPath.TryGetValue(canonical, out var existing)
                ? Winner(existing, candidate)
                : candidate;
        }

        foreach (var entry in local) Consider(entry);
        foreach (var entry in remote) Consider(entry);

        var merged = byPath.Values
            .Where(e => e.DeletedUtc == null || nowUtc - e.DeletedUtc.Value <= purgeAfter)
            .ToList();

        return (merged, !SameSet(local, merged), !SameSet(remote, merged));
    }

    private static SharedDirectoryEntry Winner(SharedDirectoryEntry a, SharedDirectoryEntry b)
    {
        var aTime = a.DeletedUtc ?? a.ModifiedUtc;
        var bTime = b.DeletedUtc ?? b.ModifiedUtc;

        if (aTime != bTime)
            return aTime > bTime ? a : b;

        var aTombstone = a.DeletedUtc != null;
        var bTombstone = b.DeletedUtc != null;
        if (aTombstone != bTombstone)
            return aTombstone ? a : b;

        var addedByCompare = string.CompareOrdinal(a.AddedBy ?? string.Empty, b.AddedBy ?? string.Empty);
        if (addedByCompare != 0)
            return addedByCompare > 0 ? a : b;

        var displayNameCompare = string.CompareOrdinal(a.DisplayName ?? string.Empty, b.DisplayName ?? string.Empty);
        return displayNameCompare >= 0 ? a : b;
    }

    private static bool SameSet(
        IReadOnlyList<SharedDirectoryEntry> input, IReadOnlyList<SharedDirectoryEntry> merged)
    {
        if (input.Count != merged.Count)
            return false;

        var byPath = new Dictionary<string, SharedDirectoryEntry>();
        foreach (var entry in input)
            byPath[EnvironmentPaths.Canonicalize(entry.DataPath)] = entry;

        foreach (var entry in merged)
        {
            var canonical = EnvironmentPaths.Canonicalize(entry.DataPath);
            if (!byPath.TryGetValue(canonical, out var match) || !Equals(entry, match))
                return false;
        }

        return true;
    }
}
