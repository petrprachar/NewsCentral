using System.Text.RegularExpressions;

namespace NewsCentral.Configuration;

/// <summary>
/// Pure, I/O-free resolution of a single dynamic team folder name from an Entra
/// device's extensionAttributes, for one named attribute scheme. NewsService-only feature.
///
/// Flow: the scheme's <paramref name="selectorAttribute"/> selects a rule from
/// <paramref name="mappings"/>; the rule names an ordered set of extensionAttribute1..15
/// (excluding the scheme's own selector); their values are concatenated with '-' and
/// canonicalized into a team folder name.
///
/// No Graph / registry / Windows / Azure dependencies — keeps NewsCentral.Shared portable.
/// </summary>
public static class EntraTeamNameResolver
{
    // extensionAttribute1..15 — used both to validate a scheme's selectorAttribute and to
    // validate each token referenced by a rule.
    private static readonly Regex ExtensionAttributeNamePattern =
        new(@"^extensionAttribute(?:[1-9]|1[0-5])$", RegexOptions.Compiled);

    public static EntraResolutionOutcome Resolve(
        IReadOnlyDictionary<string, string?> extensionAttributes,
        IReadOnlyDictionary<string, string> mappings,
        string selectorAttribute)
    {
        // 0. The scheme's selector attribute name must itself be well-formed. Fail closed —
        // never fall back to a default such as extensionAttribute1.
        if (string.IsNullOrWhiteSpace(selectorAttribute) ||
            !ExtensionAttributeNamePattern.IsMatch(selectorAttribute))
            return new EntraResolutionOutcome(EntraResolutionReason.InvalidScheme);

        // 1. Selector value
        var selectorValue = extensionAttributes.GetValueOrDefault(selectorAttribute);
        if (string.IsNullOrWhiteSpace(selectorValue))
            return new EntraResolutionOutcome(EntraResolutionReason.NoSelector);

        // 2. Selector value must be a known mapping key (ordinal, case-sensitive).
        if (!mappings.TryGetValue(selectorValue, out var rule))
            return new EntraResolutionOutcome(EntraResolutionReason.UnknownSelector);

        // 3. Rule → ordered tokens; each must be extensionAttribute1..15, and none may be the
        // scheme's own selector (a rule referencing its own selector is a config error, not a
        // team — the selector value picked the rule, so it cannot also feed the team name).
        var tokens = rule.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return new EntraResolutionOutcome(EntraResolutionReason.InvalidRule);

        foreach (var token in tokens)
        {
            if (!ExtensionAttributeNamePattern.IsMatch(token))
                return new EntraResolutionOutcome(EntraResolutionReason.InvalidRule);

            if (string.Equals(token, selectorAttribute, StringComparison.Ordinal))
                return new EntraResolutionOutcome(EntraResolutionReason.InvalidRule);
        }

        // 4. Read each referenced attribute value in token order; any empty → abort.
        var values = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            var value = extensionAttributes.GetValueOrDefault(token);
            if (string.IsNullOrWhiteSpace(value))
                return new EntraResolutionOutcome(EntraResolutionReason.EmptyRequiredAttribute);

            values.Add(value);
        }

        // 5. Join in token order, then canonicalize.
        var canonical = TeamFolderNameCanonicalizer.Canonicalize(string.Join('-', values));
        if (string.IsNullOrEmpty(canonical))
            return new EntraResolutionOutcome(EntraResolutionReason.InvalidRule);

        return new EntraResolutionOutcome(EntraResolutionReason.Resolved, canonical);
    }
}

public sealed record EntraResolutionOutcome(
    EntraResolutionReason Reason, string? TeamFolderName = null);

public enum EntraResolutionReason
{
    Resolved,
    NoSelector,
    UnknownSelector,
    InvalidRule,
    EmptyRequiredAttribute,
    InvalidScheme
}
