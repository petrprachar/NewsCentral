using System.Text.Json;
using System.Text.Json.Serialization;
using NewsCentral.Models;

namespace NewsCentral.Configuration;

/// <summary>
/// Reads the dynamic team folder names from {cacheRootPath}\resolved-teams.json (written by
/// NewsService). Any failure mode — file absent, unreadable, or malformed — yields an empty set,
/// so a missing or corrupt file simply means "no dynamic teams" rather than an error.
///
/// Uses reflection-based <see cref="JsonSerializer"/> with the same camelCase + enum options used
/// across NewsService/NewsViewer, consistent with how NewsViewer reads index.json (NativeAOT path
/// preserved — no source-gen contract is introduced).
/// </summary>
public static class ResolvedTeamsReader
{
    private const string FileName = "resolved-teams.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static HashSet<string> ReadDynamicTeamFolders(string cacheRootPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var path = Path.Combine(cacheRootPath, FileName);
            if (!File.Exists(path)) return result;

            var file = JsonSerializer.Deserialize<ResolvedTeamsFile>(File.ReadAllText(path), Options);
            if (file?.Teams is null) return result;

            foreach (var entry in file.Teams)
            {
                if (!string.IsNullOrWhiteSpace(entry.TeamFolderName))
                    result.Add(entry.TeamFolderName);
            }
        }
        catch
        {
            // Absent/unreadable/malformed → empty set (no dynamic teams).
        }

        return result;
    }
}
