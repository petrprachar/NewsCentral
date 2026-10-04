using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EnvironmentDirectoryTests
{
    private static IConfiguration RegistryStyle(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // ── UserEnvironmentState JSON round-trip ────────────────────────────────

    [Fact]
    public void UserEnvironmentState_RoundTrips_ThroughJson()
    {
        var state = new UserEnvironmentState
        {
            SchemaVersion = 1,
            LastUsedDataPath = "C:\\Download\\NewsCentral",
            Entries = new List<UserEnvironmentEntry>
            {
                new() { DataPath = "C:\\Download\\Other", DisplayName = "Other box", AddedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) }
            },
            HiddenPaths = new List<string> { "c:\\download\\hidden" }
        };

        var json = JsonSerializer.Serialize(state, UserEnvironmentStateJson.Options);
        var roundTripped = JsonSerializer.Deserialize<UserEnvironmentState>(json, UserEnvironmentStateJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.SchemaVersion);
        Assert.Equal("C:\\Download\\NewsCentral", roundTripped.LastUsedDataPath);
        Assert.Single(roundTripped.Entries);
        Assert.Equal("Other box", roundTripped.Entries[0].DisplayName);
        Assert.Single(roundTripped.HiddenPaths);
    }

    [Fact]
    public void UserEnvironmentState_DeserializesDefaults_WhenJsonIsEmptyObject()
    {
        var roundTripped = JsonSerializer.Deserialize<UserEnvironmentState>("{}", UserEnvironmentStateJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.LastUsedDataPath);
        Assert.Empty(roundTripped.Entries);
        Assert.Empty(roundTripped.HiddenPaths);
    }

    // ── EnvironmentPaths.IsAbsolute / IsShareable ───────────────────────────

    [Theory]
    [InlineData("C:\\x", true)]
    [InlineData("C:\\x\\y", true)]
    [InlineData("\\\\srv\\share\\x", true)]
    [InlineData("x\\y", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAbsolute_ClassifiesPaths(string? path, bool expected)
    {
        Assert.Equal(expected, EnvironmentPaths.IsAbsolute(path));
    }

    [Theory]
    [InlineData("C:\\x", false)]
    [InlineData("\\\\srv\\share\\x", true)]
    [InlineData("\\\\?\\C:\\x", false)]
    [InlineData("\\\\.\\C:\\x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsShareable_ClassifiesPaths(string? path, bool expected)
    {
        Assert.Equal(expected, EnvironmentPaths.IsShareable(path));
    }

    // ── EnvironmentCatalogReader — relative DataPath is skipped ─────────────

    [Fact]
    public void Read_ExplicitEntryWithRelativeDataPath_SkippedWithWarning()
    {
        var registry = RegistryStyle(new()
        {
            ["Environments:Dev:DataPath"] = "Relative\\Path"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        Assert.Empty(catalog.Entries);
        Assert.Contains(catalog.Warnings, w => w.Contains("Dev") && w.Contains("relative"));
    }

    [Fact]
    public void Read_ImplicitDefaultWithRelativeDataPath_SkippedWithWarning()
    {
        var registry = RegistryStyle(new()
        {
            ["DataPath"] = "Relative\\Path"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        Assert.Empty(catalog.Entries);
        Assert.Contains(catalog.Warnings, w => w.Contains("Default") && w.Contains("relative"));
    }

    [Fact]
    public void Read_AbsoluteDataPaths_AreNotAffectedByRelativeCheck()
    {
        var registry = RegistryStyle(new()
        {
            ["Environments:Dev:DataPath"] = "C:\\Download\\NewsCentral",
            ["DataPath"] = "C:\\Download\\Default"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        Assert.Equal(2, catalog.Entries.Count);
        Assert.Empty(catalog.Warnings);
    }

    // ── EnvironmentListBuilder ───────────────────────────────────────────────

    private static EnvironmentCatalog CatalogWith(params PolicyEnvironment[] entries) =>
        new(entries, AllowUserEnvironments: true, Warnings: Array.Empty<string>());

    private static PolicyEnvironment Policy(string name, string dataPath, string? displayName = null) =>
        new(name, dataPath, displayName, null, null, null, null, null, null, null, IsImplicitDefault: false);

    [Fact]
    public void Build_OrdersPolicyThenConfiguredThenUser()
    {
        var catalog = CatalogWith(
            Policy("Zeta", "C:\\Policy\\Zeta"),
            Policy("Alpha", "C:\\Policy\\Alpha"));

        var state = new UserEnvironmentState
        {
            Entries = new List<UserEnvironmentEntry>
            {
                new() { DataPath = "C:\\User\\Zebra", DisplayName = "Zebra" },
                new() { DataPath = "C:\\User\\Ann", DisplayName = "Ann" }
            }
        };

        var options = EnvironmentListBuilder.Build(catalog, "C:\\Configured", state, currentDataPath: null, includeHidden: false);

        Assert.Equal(5, options.Count);
        Assert.Equal("Alpha", options[0].DisplayName);
        Assert.Equal("Zeta", options[1].DisplayName);
        Assert.Equal(EnvironmentKind.Configured, options[2].Kind);
        Assert.Equal("Ann", options[3].DisplayName);
        Assert.Equal("Zebra", options[4].DisplayName);
    }

    [Fact]
    public void Build_PolicyEntryShadowsConfiguredAtSameCanonicalPath()
    {
        var catalog = CatalogWith(Policy("Default", "C:\\Download\\NewsCentral"));
        var state = new UserEnvironmentState();

        var options = EnvironmentListBuilder.Build(
            catalog, "c:\\download\\newscentral\\", state, currentDataPath: null, includeHidden: false);

        var single = Assert.Single(options);
        Assert.Equal(EnvironmentKind.Policy, single.Kind);
    }

    [Fact]
    public void Build_UserEntryCollidingWithPolicyPath_IsDropped()
    {
        var catalog = CatalogWith(Policy("Dev", "C:\\Shared"));
        var state = new UserEnvironmentState
        {
            Entries = new List<UserEnvironmentEntry> { new() { DataPath = "C:\\Shared\\" } }
        };

        var options = EnvironmentListBuilder.Build(catalog, null, state, currentDataPath: null, includeHidden: false);

        Assert.Single(options);
    }

    [Fact]
    public void Build_UserEntriesExcluded_WhenCatalogHasEntriesAndDisallowsThem()
    {
        var catalog = new EnvironmentCatalog(
            new[] { Policy("Dev", "C:\\Policy") }, AllowUserEnvironments: false, Warnings: Array.Empty<string>());
        var state = new UserEnvironmentState
        {
            Entries = new List<UserEnvironmentEntry> { new() { DataPath = "C:\\User" } }
        };

        var options = EnvironmentListBuilder.Build(catalog, null, state, currentDataPath: null, includeHidden: false);

        Assert.DoesNotContain(options, o => o.Kind == EnvironmentKind.User);
    }

    [Fact]
    public void Build_UserEntriesIncluded_WhenCatalogIsEmpty_EvenIfAllowUserEnvironmentsFalse()
    {
        // An unmanaged machine (no policy entries at all) must not be left with zero usable
        // environments just because AllowUserEnvironments defaulted/was-set to false.
        var catalog = new EnvironmentCatalog(
            Array.Empty<PolicyEnvironment>(), AllowUserEnvironments: false, Warnings: Array.Empty<string>());
        var state = new UserEnvironmentState
        {
            Entries = new List<UserEnvironmentEntry> { new() { DataPath = "C:\\User" } }
        };

        var options = EnvironmentListBuilder.Build(catalog, null, state, currentDataPath: null, includeHidden: false);

        Assert.Contains(options, o => o.Kind == EnvironmentKind.User);
    }

    [Fact]
    public void Build_HiddenPolicyEntry_ExcludedUnlessIncludeHiddenOrCurrent()
    {
        var catalog = CatalogWith(Policy("Dev", "C:\\Policy\\Dev"));
        var state = new UserEnvironmentState
        {
            HiddenPaths = new List<string> { EnvironmentPaths.Canonicalize("C:\\Policy\\Dev") }
        };

        var hidden = EnvironmentListBuilder.Build(catalog, null, state, currentDataPath: null, includeHidden: false);
        Assert.Empty(hidden);

        var shown = EnvironmentListBuilder.Build(catalog, null, state, currentDataPath: null, includeHidden: true);
        var single = Assert.Single(shown);
        Assert.True(single.IsHidden);

        var currentStillShown = EnvironmentListBuilder.Build(
            catalog, null, state, currentDataPath: "C:\\Policy\\Dev", includeHidden: false);
        var currentOption = Assert.Single(currentStillShown);
        Assert.True(currentOption.IsCurrent);
        Assert.True(currentOption.IsHidden);
    }

    [Fact]
    public void Build_DisplayNameFallsBackToPolicyNameThenLastPathSegment()
    {
        var catalog = CatalogWith(Policy("Dev", "C:\\Policy\\Dev"));
        var state = new UserEnvironmentState
        {
            Entries = new List<UserEnvironmentEntry> { new() { DataPath = "C:\\User\\MyBox" } }
        };

        var options = EnvironmentListBuilder.Build(catalog, "C:\\Configured\\Root", state, currentDataPath: null, includeHidden: false);

        Assert.Equal("Dev", options.Single(o => o.Kind == EnvironmentKind.Policy).DisplayName);
        Assert.Equal("Root", options.Single(o => o.Kind == EnvironmentKind.Configured).DisplayName);
        Assert.Equal("MyBox", options.Single(o => o.Kind == EnvironmentKind.User).DisplayName);
    }

    [Fact]
    public void Build_IsShareable_ReflectsUncVsLocalPath()
    {
        var catalog = CatalogWith(Policy("Dev", "\\\\srv\\share\\dev"));
        var state = new UserEnvironmentState();

        var options = EnvironmentListBuilder.Build(catalog, "C:\\Local", state, currentDataPath: null, includeHidden: false);

        Assert.True(options.Single(o => o.Kind == EnvironmentKind.Policy).IsShareable);
        Assert.False(options.Single(o => o.Kind == EnvironmentKind.Configured).IsShareable);
    }

    // ── EnvironmentStartupSelector ───────────────────────────────────────────

    [Fact]
    public void Select_ReturnsNull_WhenVisibleIsEmpty()
    {
        var result = EnvironmentStartupSelector.Select(
            Array.Empty<EnvironmentOption>(), lastUsed: "C:\\x", EnvironmentCatalog.Empty, configuredDataPath: "C:\\x");

        Assert.Null(result);
    }

    [Fact]
    public void Select_PrefersLastUsed_WhenVisible()
    {
        var options = new[]
        {
            new EnvironmentOption("C:\\A", "A", EnvironmentKind.User, null, false, false, false),
            new EnvironmentOption("C:\\B", "B", EnvironmentKind.User, null, false, false, false)
        };

        var result = EnvironmentStartupSelector.Select(options, "c:\\b\\", EnvironmentCatalog.Empty, "C:\\A");

        Assert.Equal("C:\\B", result);
    }

    [Fact]
    public void Select_FallsBackToDefaultPolicy_WhenLastUsedNotVisible()
    {
        var options = new[]
        {
            new EnvironmentOption("C:\\Policy\\Default", "Default", EnvironmentKind.Policy, "Default", false, false, false),
            new EnvironmentOption("C:\\Other", "Other", EnvironmentKind.User, null, false, false, false)
        };

        var result = EnvironmentStartupSelector.Select(options, "C:\\Gone", EnvironmentCatalog.Empty, "C:\\Other");

        Assert.Equal("C:\\Policy\\Default", result);
    }

    [Fact]
    public void Select_FallsBackToConfigured_WhenNoLastUsedAndNoDefaultPolicy()
    {
        var options = new[]
        {
            new EnvironmentOption("C:\\Configured", "Configured", EnvironmentKind.Configured, null, false, false, false),
            new EnvironmentOption("C:\\Other", "Other", EnvironmentKind.User, null, false, false, false)
        };

        var result = EnvironmentStartupSelector.Select(options, lastUsed: null, EnvironmentCatalog.Empty, "C:\\Configured");

        Assert.Equal("C:\\Configured", result);
    }

    [Fact]
    public void Select_FallsBackToFirstVisible_WhenNothingElseMatches()
    {
        var options = new[]
        {
            new EnvironmentOption("C:\\First", "First", EnvironmentKind.User, null, false, false, false),
            new EnvironmentOption("C:\\Second", "Second", EnvironmentKind.User, null, false, false, false)
        };

        var result = EnvironmentStartupSelector.Select(options, lastUsed: null, EnvironmentCatalog.Empty, configuredDataPath: null);

        Assert.Equal("C:\\First", result);
    }
}
