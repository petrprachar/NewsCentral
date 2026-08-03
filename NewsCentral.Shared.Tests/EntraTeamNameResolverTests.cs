using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EntraTeamNameResolverTests
{
    private const string DefaultSelector = "extensionAttribute1";

    // FAT/VDE/VDL selectors, each mapping to an ordered rule over extensionAttribute2..15.
    private static readonly IReadOnlyDictionary<string, string> Mappings =
        new Dictionary<string, string>
        {
            ["FAT"] = "extensionAttribute2-extensionAttribute5-extensionAttribute4",
            ["VDE"] = "extensionAttribute3",
            ["VDL"] = "extensionAttribute6-extensionAttribute2",
        };

    private static Dictionary<string, string?> Attrs(params (string Key, string? Value)[] pairs)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Resolve_FatRule_ConcatenatesInRuleOrder()
    {
        var attrs = Attrs(
            ("extensionAttribute1", "FAT"),
            ("extensionAttribute2", "CZ"),
            ("extensionAttribute4", "ITS"),
            ("extensionAttribute5", "Prague"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector);

        Assert.Equal(EntraResolutionReason.Resolved, outcome.Reason);
        Assert.Equal("cz-prague-its", outcome.TeamFolderName);
    }

    [Fact]
    public void Resolve_OrderFollowsRuleNotAttributeNumber()
    {
        // Rule order is 6 then 2 — output must be value(6)-value(2), not numeric order.
        var attrs = Attrs(
            ("extensionAttribute1", "VDL"),
            ("extensionAttribute2", "second"),
            ("extensionAttribute6", "first"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector);

        Assert.Equal(EntraResolutionReason.Resolved, outcome.Reason);
        Assert.Equal("first-second", outcome.TeamFolderName);
    }

    [Fact]
    public void Resolve_MultiWordValue_Canonicalizes()
    {
        var attrs = Attrs(
            ("extensionAttribute1", "VDE"),
            ("extensionAttribute3", "New York"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector);

        Assert.Equal(EntraResolutionReason.Resolved, outcome.Reason);
        Assert.Equal("new-york", outcome.TeamFolderName);
    }

    [Fact]
    public void Resolve_NoSelector_WhenAttribute1Absent()
    {
        var attrs = Attrs(("extensionAttribute2", "CZ"));

        Assert.Equal(EntraResolutionReason.NoSelector,
            EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_NoSelector_WhenAttribute1Empty()
    {
        var attrs = Attrs(("extensionAttribute1", "   "));

        Assert.Equal(EntraResolutionReason.NoSelector,
            EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_UnknownSelector_WhenSelectorNotMapped()
    {
        var attrs = Attrs(("extensionAttribute1", "XYZ"));

        Assert.Equal(EntraResolutionReason.UnknownSelector,
            EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_UnknownSelector_IsCaseSensitive()
    {
        var attrs = Attrs(
            ("extensionAttribute1", "fat"),   // lower-case — not a key
            ("extensionAttribute2", "CZ"));

        Assert.Equal(EntraResolutionReason.UnknownSelector,
            EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_InvalidRule_WhenRuleReferencesAttribute1()
    {
        var mappings = new Dictionary<string, string>
        {
            ["FAT"] = "extensionAttribute1",
        };
        var attrs = Attrs(("extensionAttribute1", "FAT"));

        Assert.Equal(EntraResolutionReason.InvalidRule,
            EntraTeamNameResolver.Resolve(attrs, mappings, DefaultSelector).Reason);
    }

    [Theory]
    [InlineData("extensionAttribute0")]
    [InlineData("extensionAttribute16")]
    [InlineData("extensionAttribute99")]
    [InlineData("garbage")]
    [InlineData("extensionAttribute")]
    public void Resolve_InvalidRule_ForOutOfRangeOrMalformedTokens(string token)
    {
        var mappings = new Dictionary<string, string> { ["FAT"] = token };
        var attrs = Attrs(
            ("extensionAttribute1", "FAT"),
            (token, "value"));

        Assert.Equal(EntraResolutionReason.InvalidRule,
            EntraTeamNameResolver.Resolve(attrs, mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_InvalidRule_WhenRuleEmpty()
    {
        var mappings = new Dictionary<string, string> { ["FAT"] = "" };
        var attrs = Attrs(("extensionAttribute1", "FAT"));

        Assert.Equal(EntraResolutionReason.InvalidRule,
            EntraTeamNameResolver.Resolve(attrs, mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_EmptyRequiredAttribute_WhenReferencedValueNull()
    {
        var attrs = Attrs(
            ("extensionAttribute1", "VDE"),
            ("extensionAttribute3", null));

        Assert.Equal(EntraResolutionReason.EmptyRequiredAttribute,
            EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_EmptyRequiredAttribute_WhenReferencedValueAbsent()
    {
        var attrs = Attrs(("extensionAttribute1", "VDE"));   // attr3 not present

        Assert.Equal(EntraResolutionReason.EmptyRequiredAttribute,
            EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_EmptyRequiredAttribute_WhenReferencedValueWhitespace()
    {
        var attrs = Attrs(
            ("extensionAttribute1", "VDE"),
            ("extensionAttribute3", "   "));

        Assert.Equal(EntraResolutionReason.EmptyRequiredAttribute,
            EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector).Reason);
    }

    [Fact]
    public void Resolve_VdeSelector_ResolvesViaOwnRule()
    {
        var attrs = Attrs(
            ("extensionAttribute1", "VDE"),
            ("extensionAttribute3", "Berlin"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector);

        Assert.Equal(EntraResolutionReason.Resolved, outcome.Reason);
        Assert.Equal("berlin", outcome.TeamFolderName);
    }

    [Fact]
    public void Resolve_VdlSelector_ResolvesViaOwnRule()
    {
        var attrs = Attrs(
            ("extensionAttribute1", "VDL"),
            ("extensionAttribute2", "ITS"),
            ("extensionAttribute6", "DE"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, Mappings, DefaultSelector);

        Assert.Equal(EntraResolutionReason.Resolved, outcome.Reason);
        Assert.Equal("de-its", outcome.TeamFolderName);
    }

    // ── Non-default selector (multi-scheme) ───────────────────────────────────

    [Fact]
    public void Resolve_NonDefaultSelector_UsesConfiguredAttribute()
    {
        var mappings = new Dictionary<string, string>
        {
            ["FAT"] = "extensionAttribute2-extensionAttribute5",
        };
        var attrs = Attrs(
            ("extensionAttribute7", "FAT"),
            ("extensionAttribute2", "CZ"),
            ("extensionAttribute5", "Prague"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, mappings, "extensionAttribute7");

        Assert.Equal(EntraResolutionReason.Resolved, outcome.Reason);
        Assert.Equal("cz-prague", outcome.TeamFolderName);
    }

    [Fact]
    public void Resolve_RuleMayReferenceAttribute1_WhenNotTheSelector()
    {
        var mappings = new Dictionary<string, string>
        {
            ["FAT"] = "extensionAttribute1-extensionAttribute2",
        };
        var attrs = Attrs(
            ("extensionAttribute7", "FAT"),
            ("extensionAttribute1", "US"),
            ("extensionAttribute2", "NY"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, mappings, "extensionAttribute7");

        Assert.Equal(EntraResolutionReason.Resolved, outcome.Reason);
        Assert.Equal("us-ny", outcome.TeamFolderName);
    }

    [Fact]
    public void Resolve_RuleReferencingOwnSelector_IsInvalidRule()
    {
        var mappings = new Dictionary<string, string>
        {
            ["FAT"] = "extensionAttribute7",
        };
        var attrs = Attrs(("extensionAttribute7", "FAT"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, mappings, "extensionAttribute7");

        Assert.Equal(EntraResolutionReason.InvalidRule, outcome.Reason);
    }

    // ── Selector scheme validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("extensionAttribute0")]
    [InlineData("extensionAttribute16")]
    [InlineData("ExtensionAttribute1")]     // wrong case — ordinal, case-sensitive
    [InlineData("attribute1")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Resolve_BlankSelector_IsInvalidScheme(string? selector)
    {
        var attrs = Attrs(("extensionAttribute1", "FAT"));

        var outcome = EntraTeamNameResolver.Resolve(attrs, Mappings, selector!);

        Assert.Equal(EntraResolutionReason.InvalidScheme, outcome.Reason);
    }
}
