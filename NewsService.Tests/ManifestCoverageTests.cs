using System.Reflection;
using NewsCentral.Configuration;
using NewsService.Configuration;
using NewsViewer.Configuration;

namespace NewsService.Tests;

/// <summary>
/// Drift guard (spec §9): one-directional POCO ⊆ manifest. Every public settable scalar property of
/// the bindable config POCOs (walking nested section objects) must have a manifest entry. The manifest
/// is deliberately a superset — it also carries registry-only / ad-hoc keys (e.g. Signing:RequireSignedIndex)
/// and structural hives that have no POCO property, so the reverse direction is intentionally not asserted.
/// NewsCentral is exempt — AppConfiguration is a get-only IConfiguration wrapper, not reflection-bindable.
/// Lives here (not in NewsCentral.Shared.Tests) because reflecting ServiceConfiguration/ViewerConfiguration
/// needs references to the Windows-targeted NewsService/NewsViewer assemblies.
/// </summary>
public sealed class ManifestCoverageTests
{
    [Fact]
    public void ServiceConfiguration_ScalarKeys_AllInManifest() =>
        AssertPocoSubsetOfManifest(typeof(ServiceConfiguration), ConfigManifests.NewsService);

    [Fact]
    public void ViewerConfiguration_ScalarKeys_AllInManifest() =>
        AssertPocoSubsetOfManifest(typeof(ViewerConfiguration), ConfigManifests.NewsViewer);

    private static void AssertPocoSubsetOfManifest(Type pocoType, ComponentManifest manifest)
    {
        var manifestKeys = manifest.Keys
            .Select(k => k.CanonicalKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pocoKeys = new List<string>();
        CollectScalarKeys(pocoType, prefix: "", pocoKeys);

        var missing = pocoKeys.Where(k => !manifestKeys.Contains(k)).ToList();

        Assert.True(missing.Count == 0,
            $"Manifest '{manifest.ComponentName}' is missing POCO scalar keys: {string.Join(", ", missing)}");
    }

    private static readonly string[] OwnRoots = { "NewsCentral", "NewsService", "NewsViewer" };

    private static void CollectScalarKeys(Type type, string prefix, List<string> keys)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;      // skip indexers
            if (p.GetMethod is not { IsPublic: true }) continue;
            if (p.SetMethod is not { IsPublic: true }) continue;  // settable scalars/sections only

            var t          = p.PropertyType;
            var underlying = Nullable.GetUnderlyingType(t) ?? t;
            var key        = string.IsNullOrEmpty(prefix) ? p.Name : $"{prefix}:{p.Name}";

            if (IsScalar(underlying)) { keys.Add(key); continue; }
            if (IsCollection(t)) continue;                        // Dictionary/List → structural hive, skip
            if (underlying.IsClass && IsOwnType(underlying))
                CollectScalarKeys(underlying, key, keys);         // nested section object
        }
    }

    private static bool IsScalar(Type t) => t == typeof(string) || t.IsPrimitive || t.IsEnum;

    private static bool IsCollection(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static bool IsOwnType(Type t) =>
        t.Namespace is not null && OwnRoots.Any(r => t.Namespace.StartsWith(r, StringComparison.Ordinal));
}
