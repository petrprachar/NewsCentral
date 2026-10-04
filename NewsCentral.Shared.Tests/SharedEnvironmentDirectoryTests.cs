using System.Text.Json;
using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class SharedEnvironmentDirectoryTests
{
    private static SharedDirectoryEntry Entry(
        string dataPath, string? displayName = null, string? addedBy = "admin@contoso.com",
        DateTime? modifiedUtc = null, DateTime? deletedUtc = null, string? fingerprint = null) =>
        new(dataPath, displayName, addedBy, modifiedUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), deletedUtc, fingerprint);

    // ── SharedDirectoryFile JSON round-trip ─────────────────────────────────

    [Fact]
    public void SharedDirectoryFile_RoundTrips_ThroughJson()
    {
        var file = new SharedDirectoryFile
        {
            SchemaVersion = 1,
            Entries = new List<SharedDirectoryEntry>
            {
                Entry("\\\\srv\\share\\EnvA", "Env A", "admin@contoso.com",
                    new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), null)
            }
        };

        var json = JsonSerializer.Serialize(file, SharedDirectoryJson.Options);
        var roundTripped = JsonSerializer.Deserialize<SharedDirectoryFile>(json, SharedDirectoryJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.SchemaVersion);
        var entry = Assert.Single(roundTripped.Entries);
        Assert.Equal("\\\\srv\\share\\EnvA", entry.DataPath);
        Assert.Equal("Env A", entry.DisplayName);
        Assert.Equal("admin@contoso.com", entry.AddedBy);
        Assert.Null(entry.DeletedUtc);
    }

    // ── SharedDirectoryValidator ─────────────────────────────────────────────

    [Theory]
    [InlineData("C:\\Local")]          // local drive, not UNC
    [InlineData("Relative\\Path")]     // relative
    [InlineData("")]                   // empty
    [InlineData("\\\\?\\C:\\x")]        // device-namespace form
    [InlineData("\\\\.\\C:\\x")]        // device-namespace form
    public void Filter_RejectsUnsafePaths(string dataPath)
    {
        var incoming = new[] { Entry(dataPath) };

        var accepted = SharedDirectoryValidator.Filter(incoming, out var warnings);

        Assert.Empty(accepted);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Filter_AcceptsAbsoluteUncPath()
    {
        var incoming = new[] { Entry("\\\\srv\\share\\EnvA") };

        var accepted = SharedDirectoryValidator.Filter(incoming, out var warnings);

        Assert.Single(accepted);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Filter_TruncatesLongDisplayName()
    {
        var longName = new string('x', 150);
        var incoming = new[] { Entry("\\\\srv\\share\\EnvA", longName) };

        var accepted = SharedDirectoryValidator.Filter(incoming, out _);

        var entry = Assert.Single(accepted);
        Assert.Equal(SharedDirectoryValidator.MaxDisplayNameLength, entry.DisplayName!.Length);
        Assert.Equal(longName.Substring(0, 100), entry.DisplayName);
    }

    [Fact]
    public void Filter_CapsAt200_KeepingNewestByModifiedUtc()
    {
        var incoming = Enumerable.Range(0, 250)
            .Select(i => Entry($"\\\\srv\\share\\Env{i}", modifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)))
            .ToList();

        var accepted = SharedDirectoryValidator.Filter(incoming, out var warnings);

        Assert.Equal(SharedDirectoryValidator.MaxEntries, accepted.Count);
        // The newest 200 are minutes 50..249 (i.e. everything except the oldest 50).
        Assert.DoesNotContain(accepted, e => e.DataPath == "\\\\srv\\share\\Env0");
        Assert.Contains(accepted, e => e.DataPath == "\\\\srv\\share\\Env249");
        Assert.Contains(warnings, w => w.Contains("200"));
    }

    // ── SharedDirectoryMerger ────────────────────────────────────────────────

    // Close enough to the fixed 2026-01/02/03 dates used by the fixed-date tests below that their
    // tombstones (up to ~73 days old) survive the 90-day purge window; the dedicated purge test
    // below constructs its own dates relative to Now, so it is unaffected by this literal value.
    private static readonly DateTime Now = new(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Merge_RemoteOnlyEntry_IsAddedLocally()
    {
        var local = Array.Empty<SharedDirectoryEntry>();
        var remote = new[] { Entry("\\\\srv\\share\\EnvA") };

        var (merged, localChanged, remoteChanged) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.Single(merged);
        Assert.True(localChanged);
        Assert.False(remoteChanged);
    }

    [Fact]
    public void Merge_LocalOnlyEntry_IsAddedRemotely()
    {
        var local = new[] { Entry("\\\\srv\\share\\EnvA") };
        var remote = Array.Empty<SharedDirectoryEntry>();

        var (merged, localChanged, remoteChanged) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.Single(merged);
        Assert.False(localChanged);
        Assert.True(remoteChanged);
    }

    [Fact]
    public void Merge_NewerModification_Wins()
    {
        var local = new[] { Entry("\\\\srv\\share\\EnvA", "Old name", modifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var remote = new[] { Entry("\\\\srv\\share\\EnvA", "New name", modifiedUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)) };

        var (merged, _, _) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.Equal("New name", Assert.Single(merged).DisplayName);
    }

    [Fact]
    public void Merge_NewerTombstone_RemovesTheEntry()
    {
        var local = new[] { Entry("\\\\srv\\share\\EnvA", modifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var remote = new[]
        {
            Entry("\\\\srv\\share\\EnvA", modifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                deletedUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        var (merged, _, _) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.NotNull(Assert.Single(merged).DeletedUtc);
    }

    [Fact]
    public void Merge_OlderTombstone_LosesToNewerReAdd()
    {
        var local = new[]
        {
            Entry("\\\\srv\\share\\EnvA", modifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                deletedUtc: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))
        };
        var remote = new[] { Entry("\\\\srv\\share\\EnvA", "Re-added", modifiedUtc: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)) };

        var (merged, _, _) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        var winner = Assert.Single(merged);
        Assert.Null(winner.DeletedUtc);
        Assert.Equal("Re-added", winner.DisplayName);
    }

    [Fact]
    public void Merge_ExactTie_TombstoneWins()
    {
        var sameTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var local = new[] { Entry("\\\\srv\\share\\EnvA", "Live", modifiedUtc: sameTime) };
        var remote = new[] { Entry("\\\\srv\\share\\EnvA", "Dead", modifiedUtc: sameTime, deletedUtc: sameTime) };

        var (merged, _, _) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        var winner = Assert.Single(merged);
        Assert.NotNull(winner.DeletedUtc);
        Assert.Equal("Dead", winner.DisplayName);
    }

    [Fact]
    public void Merge_ExactTie_NoTombstoneEitherSide_GreaterAddedByWins()
    {
        var sameTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var local = new[] { Entry("\\\\srv\\share\\EnvA", "A", addedBy: "alice@contoso.com", modifiedUtc: sameTime) };
        var remote = new[] { Entry("\\\\srv\\share\\EnvA", "B", addedBy: "bob@contoso.com", modifiedUtc: sameTime) };

        var (merged, _, _) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.Equal("bob@contoso.com", Assert.Single(merged).AddedBy);
    }

    [Fact]
    public void Merge_TombstonesOlderThan90Days_ArePurged()
    {
        var oldTombstone = Entry("\\\\srv\\share\\Old", deletedUtc: Now - TimeSpan.FromDays(91), modifiedUtc: Now - TimeSpan.FromDays(100));
        var recentTombstone = Entry("\\\\srv\\share\\Recent", deletedUtc: Now - TimeSpan.FromDays(10), modifiedUtc: Now - TimeSpan.FromDays(20));

        var (merged, _, _) = SharedDirectoryMerger.Merge(
            new[] { oldTombstone, recentTombstone }, Array.Empty<SharedDirectoryEntry>(), Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.DoesNotContain(merged, e => e.DataPath == "\\\\srv\\share\\Old");
        Assert.Contains(merged, e => e.DataPath == "\\\\srv\\share\\Recent");
    }

    [Fact]
    public void Merge_IdenticalInputsDifferentOrder_GivesNeitherFlag()
    {
        var a = Entry("\\\\srv\\share\\EnvA");
        var b = Entry("\\\\srv\\share\\EnvB");

        var (merged, localChanged, remoteChanged) = SharedDirectoryMerger.Merge(
            new[] { a, b }, new[] { b, a }, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.Equal(2, merged.Count);
        Assert.False(localChanged);
        Assert.False(remoteChanged);
    }

    [Fact]
    public void Merge_CanonicalMatching_IsCaseAndTrailingSlashInsensitive()
    {
        var local = new[] { Entry("\\\\SRV\\Share\\EnvA\\", modifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var remote = new[] { Entry("\\\\srv\\share\\enva", "Canonical winner", modifiedUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)) };

        var (merged, _, _) = SharedDirectoryMerger.Merge(local, remote, Now, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.Single(merged);
        Assert.Equal("Canonical winner", merged[0].DisplayName);
    }

    // ── EnvironmentListBuilder — Shared entries ──────────────────────────────

    private static EnvironmentCatalog CatalogWith(params PolicyEnvironment[] entries) =>
        new(entries, AllowUserEnvironments: true, Warnings: Array.Empty<string>());

    private static PolicyEnvironment Policy(string name, string dataPath, string? displayName = null) =>
        new(name, dataPath, displayName, null, null, null, null, null, null, null, IsImplicitDefault: false);

    [Fact]
    public void Build_SharedEntry_AppearsWithSharedKind()
    {
        var catalog = EnvironmentCatalog.Empty;
        var state = new UserEnvironmentState
        {
            SharedEntries = new List<SharedDirectoryEntry> { Entry("\\\\srv\\share\\EnvA", "Env A") }
        };

        var options = EnvironmentListBuilder.Build(catalog, null, state, currentDataPath: null, includeHidden: false);

        var option = Assert.Single(options);
        Assert.Equal(EnvironmentKind.Shared, option.Kind);
        Assert.Equal("Env A", option.DisplayName);
        Assert.True(option.IsShareable);
    }

    [Fact]
    public void Build_SharedEntry_DedupedAway_WhenSamePathAsPolicyOrConfigured()
    {
        var catalog = CatalogWith(Policy("Dev", "\\\\srv\\share\\EnvA"));
        var state = new UserEnvironmentState
        {
            SharedEntries = new List<SharedDirectoryEntry>
            {
                Entry("\\\\srv\\share\\EnvA"),   // collides with Policy
                Entry("C:\\Configured")           // collides with Configured
            }
        };

        var options = EnvironmentListBuilder.Build(catalog, "C:\\Configured", state, currentDataPath: null, includeHidden: false);

        Assert.DoesNotContain(options, o => o.Kind == EnvironmentKind.Shared);
    }

    [Fact]
    public void Build_TombstonedSharedEntry_IsNeverListed()
    {
        var state = new UserEnvironmentState
        {
            SharedEntries = new List<SharedDirectoryEntry>
            {
                Entry("\\\\srv\\share\\EnvA", deletedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            }
        };

        var visible = EnvironmentListBuilder.Build(EnvironmentCatalog.Empty, null, state, currentDataPath: null, includeHidden: false);
        var full = EnvironmentListBuilder.Build(EnvironmentCatalog.Empty, null, state, currentDataPath: null, includeHidden: true);

        Assert.Empty(visible);
        Assert.Empty(full); // tombstones are never shown, even with includeHidden
    }

    [Fact]
    public void Build_SharedEntriesExcluded_WhenCatalogHasEntriesAndDisallowsThem()
    {
        var catalog = new EnvironmentCatalog(
            new[] { Policy("Dev", "C:\\Policy") }, AllowUserEnvironments: false, Warnings: Array.Empty<string>());
        var state = new UserEnvironmentState
        {
            SharedEntries = new List<SharedDirectoryEntry> { Entry("\\\\srv\\share\\EnvA") }
        };

        var options = EnvironmentListBuilder.Build(catalog, null, state, currentDataPath: null, includeHidden: false);

        Assert.DoesNotContain(options, o => o.Kind == EnvironmentKind.Shared);
    }

    [Fact]
    public void Build_HiddenSharedEntry_ExcludedUnlessCurrent()
    {
        var state = new UserEnvironmentState
        {
            SharedEntries = new List<SharedDirectoryEntry> { Entry("\\\\srv\\share\\EnvA") },
            HiddenPaths = new List<string> { EnvironmentPaths.Canonicalize("\\\\srv\\share\\EnvA") }
        };

        var hidden = EnvironmentListBuilder.Build(EnvironmentCatalog.Empty, null, state, currentDataPath: null, includeHidden: false);
        Assert.Empty(hidden);

        var currentStillShown = EnvironmentListBuilder.Build(
            EnvironmentCatalog.Empty, null, state, currentDataPath: "\\\\srv\\share\\EnvA", includeHidden: false);
        var option = Assert.Single(currentStillShown);
        Assert.True(option.IsCurrent);
        Assert.True(option.IsHidden);
    }

    // ── UserEnvironmentState — legacy hiddenPolicyPaths migration ───────────

    [Fact]
    public void Deserialize_OldHiddenPolicyPathsFile_LoadsIntoHiddenPaths()
    {
        const string oldJson = """
            { "schemaVersion": 1, "hiddenPolicyPaths": ["c:\\policy\\dev"] }
            """;

        var state = UserEnvironmentStateJson.Deserialize(oldJson);

        Assert.NotNull(state);
        Assert.Single(state!.HiddenPaths);
        Assert.Equal("c:\\policy\\dev", state.HiddenPaths[0]);
    }

    [Fact]
    public void Deserialize_NewFile_NeverWritesLegacyKeyBack()
    {
        const string oldJson = """
            { "schemaVersion": 1, "hiddenPolicyPaths": ["c:\\policy\\dev"] }
            """;

        var state = UserEnvironmentStateJson.Deserialize(oldJson)!;
        var json = JsonSerializer.Serialize(state, UserEnvironmentStateJson.Options);

        Assert.DoesNotContain("hiddenPolicyPaths", json);
        Assert.Contains("hiddenPaths", json);
    }

    // ── M5a: DistributionFingerprint ─────────────────────────────────────────

    [Fact]
    public void SharedDirectoryEntry_Fingerprint_SurvivesJsonRoundTrip()
    {
        var file = new SharedDirectoryFile
        {
            Entries = new List<SharedDirectoryEntry>
            {
                Entry("\\\\srv\\share\\EnvA", fingerprint: "local:c:\\download\\newscentraldist")
            }
        };

        var json = JsonSerializer.Serialize(file, SharedDirectoryJson.Options);
        var roundTripped = JsonSerializer.Deserialize<SharedDirectoryFile>(json, SharedDirectoryJson.Options);

        Assert.Contains("distributionFingerprint", json);
        Assert.Equal("local:c:\\download\\newscentraldist", Assert.Single(roundTripped!.Entries).DistributionFingerprint);
    }

    [Fact]
    public void Filter_RejectsFingerprintTooLong()
    {
        var incoming = new[] { Entry("\\\\srv\\share\\EnvA", fingerprint: new string('x', 401)) };

        var accepted = SharedDirectoryValidator.Filter(incoming, out var warnings);

        Assert.Empty(accepted);
        Assert.Contains(warnings, w => w.Contains("distributionFingerprint"));
    }

    [Fact]
    public void Filter_AcceptsFingerprintAtMaxLength()
    {
        var incoming = new[] { Entry("\\\\srv\\share\\EnvA", fingerprint: new string('x', 400)) };

        var accepted = SharedDirectoryValidator.Filter(incoming, out var warnings);

        Assert.Single(accepted);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Merge_EntryDifferingOnlyInFingerprint_CountsAsChange()
    {
        var sameTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var local = new[] { Entry("\\\\srv\\share\\EnvA", modifiedUtc: sameTime, fingerprint: "local:c:\\old") };
        var remote = new[] { Entry("\\\\srv\\share\\EnvA", modifiedUtc: sameTime, fingerprint: "local:c:\\new") };

        var (merged, localChanged, remoteChanged) = SharedDirectoryMerger.Merge(
            local, remote, sameTime, SharedDirectoryMerger.DefaultPurgeAfter);

        Assert.Single(merged);
        // Exact tie on effective timestamp, neither a tombstone, same AddedBy/DisplayName — the
        // merge is deterministic either way, but either side differs from the OTHER side's
        // fingerprint, so at least one of the two flags must report a change.
        Assert.True(localChanged || remoteChanged);
    }
}
