using System.Text.RegularExpressions;

namespace NewsCentral.Configuration;

/// <summary>
/// Pure, I/O-free resolution of a single dynamic team folder name from an Entra
/// device's extensionAttributes. NewsService-only feature (Phase 1: logic + DTOs only).
///
/// Flow: extensionAttribute1 selects a rule from <paramref name="mappings"/>; the rule
/// names an ordered set of extensionAttribute2..15; their values are concatenated with
/// '-' and canonicalized into a team folder name.
///
/// No Graph / registry / Windows / Azure dependencies — keeps NewsCentral.Shared portable.
/// </summary>
public static class EntraTeamNameResolver
{
    // extensionAttribute2..15 only (attribute1 is reserved as the selector).
    private static readonly Regex TokenPattern =
        new(@"^extensionAttribute(?:[2-9]|1[0-5])$", RegexOptions.Compiled);

    public static EntraResolutionOutcome Resolve(
        IReadOnlyDictionary<string, string?> extensionAttributes,
        IReadOnlyDictionary<string, string> mappings)
    {
        // 1. Selector
        var selector = extensionAttributes.GetValueOrDefault("extensionAttribute1");
        if (string.IsNullOrWhiteSpace(selector))
            return new EntraResolutionOutcome(EntraResolutionReason.NoSelector);

        // 2. Selector must be a known mapping key (ordinal, case-sensitive).
        if (!mappings.TryGetValue(selector, out var rule))
            return new EntraResolutionOutcome(EntraResolutionReason.UnknownSelector);

        // 3. Rule → ordered tokens; each must be extensionAttribute2..15.
        var tokens = rule.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return new EntraResolutionOutcome(EntraResolutionReason.InvalidRule);

        foreach (var token in tokens)
        {
            if (!TokenPattern.IsMatch(token))
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
        var canonical = Canonicalize(string.Join('-', values));
        if (string.IsNullOrEmpty(canonical))
            return new EntraResolutionOutcome(EntraResolutionReason.InvalidRule);

        return new EntraResolutionOutcome(EntraResolutionReason.Resolved, canonical);
    }

    /// <summary>
    /// Mirrors <c>TeamService.GenerateFolderName</c> (post prefix-removal) byte-for-byte:
    /// lower-invariant, ' ' and '_' → '-', then strip anything outside [a-z0-9-]. No
    /// hyphen collapsing or trimming — a resolved name must equal an authored folder
    /// built from the same tokens. Kept in sync deliberately; do NOT refactor that method
    /// from here.
    /// </summary>
    private static string Canonicalize(string raw)
    {
        var sanitized = raw.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        return Regex.Replace(sanitized, @"[^a-z0-9\-]", "");
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
    EmptyRequiredAttribute
}
