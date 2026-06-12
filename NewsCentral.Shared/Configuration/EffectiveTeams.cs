namespace NewsCentral.Configuration;

/// <summary>
/// Combines the static (registry/appsettings) team list with the dynamic (Entra-resolved) team
/// list into a single de-duplicated set of folder names to process. De-duplication and equality
/// are ordinal-case-insensitive, matching folder-name semantics on Windows. Static teams are
/// listed first, then any dynamic teams not already present; order within each is preserved.
/// </summary>
public static class EffectiveTeams
{
    public static List<string> Union(IEnumerable<string> staticTeams, IEnumerable<string> dynamicTeams)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var team in (staticTeams ?? []).Concat(dynamicTeams ?? []))
        {
            if (string.IsNullOrWhiteSpace(team)) continue;
            if (seen.Add(team)) result.Add(team);
        }

        return result;
    }
}
