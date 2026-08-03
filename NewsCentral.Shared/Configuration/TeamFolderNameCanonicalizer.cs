using System.Text.RegularExpressions;

namespace NewsCentral.Configuration;

/// <summary>
/// Single source of truth for team-folder-name canonicalization, shared by the attribute resolver
/// (<see cref="EntraTeamNameResolver"/>), the group-membership decision (<see cref="GroupTeamDecision"/>),
/// and group-instance id derivation (NewsService's <c>EntraTeamResolutionService</c>). Mirrors
/// <c>TeamService.GenerateFolderName</c> (post prefix-removal) byte-for-byte: lower-invariant, ' '
/// and '_' → '-', then strip anything outside [a-z0-9-]. No hyphen collapsing or trimming, no
/// prefix — a resolved name must equal an authored folder built from the same tokens.
/// </summary>
public static class TeamFolderNameCanonicalizer
{
    private static readonly Regex InvalidCharsPattern = new(@"[^a-z0-9\-]", RegexOptions.Compiled);

    public static string Canonicalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sanitized = raw.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        return InvalidCharsPattern.Replace(sanitized, "");
    }
}
