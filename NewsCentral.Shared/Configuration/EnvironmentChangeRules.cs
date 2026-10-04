namespace NewsCentral.Configuration;

/// <summary>
/// Pure rules (M5a) for two Environment Management page concerns that have nothing to do with each
/// other except that both compare distribution fingerprints: whether a settings change needs the
/// operator to confirm a retarget, and which other shared environments already publish to the same
/// place. Neither touches disk or the shared directory itself — both take already-loaded data.
/// </summary>
public static class EnvironmentChangeRules
{
    /// <summary>
    /// True when switching from (<paramref name="oldEnabled"/>, <paramref name="oldFingerprint"/>)
    /// to <paramref name="newFingerprint"/> would stop existing clients from receiving updates and
    /// so needs operator confirmation first. False whenever distribution was not previously enabled
    /// with a known target — there is nothing to lose by changing it — including when
    /// <paramref name="oldFingerprint"/> is null despite <paramref name="oldEnabled"/> being true
    /// (an inconsistent combination that should never arise in practice, but is treated the same as
    /// "nothing was being published," never as a reason to block or crash). True whenever the
    /// effective target actually changes, including turning distribution off entirely
    /// (<paramref name="newFingerprint"/> null) — ordinal string comparison, since a fingerprint is
    /// an opaque identifier, not display text.
    /// </summary>
    public static bool RequiresRetargetConfirmation(string? oldFingerprint, bool oldEnabled, string? newFingerprint)
    {
        if (!oldEnabled || oldFingerprint == null)
            return false;

        return !string.Equals(oldFingerprint, newFingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The non-tombstoned <paramref name="sharedEntries"/>, excluding
    /// <paramref name="currentDataPath"/> itself (by canonical path), whose own
    /// <see cref="SharedDirectoryEntry.DistributionFingerprint"/> equals
    /// <paramref name="currentFingerprint"/> (ordinal). Returns empty immediately when
    /// <paramref name="currentFingerprint"/> is null or empty — an environment with no target
    /// cannot collide with anything. An entry with no recorded fingerprint is never a match either,
    /// since it has never actually told us where it publishes.
    /// </summary>
    public static IReadOnlyList<SharedDirectoryEntry> FindCollisions(
        string currentDataPath, string? currentFingerprint, IEnumerable<SharedDirectoryEntry> sharedEntries)
    {
        if (string.IsNullOrEmpty(currentFingerprint))
            return Array.Empty<SharedDirectoryEntry>();

        var currentCanonical = EnvironmentPaths.Canonicalize(currentDataPath);

        return sharedEntries
            .Where(e => e.DeletedUtc == null)
            .Where(e => EnvironmentPaths.Canonicalize(e.DataPath) != currentCanonical)
            .Where(e => e.DistributionFingerprint != null &&
                        string.Equals(e.DistributionFingerprint, currentFingerprint, StringComparison.Ordinal))
            .ToList();
    }
}
