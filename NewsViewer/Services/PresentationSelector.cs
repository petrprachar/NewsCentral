using System.Text.Json;
using NewsCentral.Models.IndexFile;
using NewsCentral.Security;

namespace NewsViewer.Services;

public sealed class PresentationSelector
{
    private readonly string _cacheRootPath;
    private readonly HmacService _hmac;

    public PresentationSelector(string cacheRootPath, HmacService hmac)
    {
        _cacheRootPath = cacheRootPath;
        _hmac          = hmac;
    }

    /// <summary>
    /// Reads index.json for each configured team, filters to assignments that are
    /// active right now, and returns the one with the most recent PresentationLastModified.
    /// Returns null if no qualifying assignment is found or all indexes fail verification.
    /// </summary>
    public (PublishedAssignmentIndex? Assignment, string? ImagePath) SelectActive(string[] teams)
    {
        var now = DateTime.Now;
        var todayKey = now.DayOfWeek == DayOfWeek.Sunday
            ? "7"
            : ((int)now.DayOfWeek).ToString();

        PublishedAssignmentIndex? best = null;

        foreach (var team in teams)
        {
            var indexPath = Path.Combine(_cacheRootPath, team, "index.json");
            if (!File.Exists(indexPath)) continue;

            TeamIndexFile? index;
            try
            {
                var json = File.ReadAllText(indexPath);
                index = JsonSerializer.Deserialize<TeamIndexFile>(json, JsonDefaults.Options);
            }
            catch { continue; }

            if (index?.PublishedAssignments is null) continue;

            var sigResult = _hmac.Verify(index);
            if (sigResult == VerifyResult.Invalid)
            {
                // Tampered index: refuse to display anything from this team.
                System.Diagnostics.Debug.WriteLine(
                    $"[HMAC] Team {team}: index.json signature invalid — skipping team");
                continue;
            }
            if (sigResult == VerifyResult.Unsigned)
                System.Diagnostics.Debug.WriteLine(
                    $"[HMAC] Team {team}: index.json carries no signature");

            foreach (var a in index.PublishedAssignments)
            {
                if (a.ScheduleStart > now || a.ScheduleEnd < now) continue;
                if (!a.DaysOfWeek.Split(',').Contains(todayKey)) continue;

                if (best is null || a.PresentationLastModified > best.PresentationLastModified)
                    best = a;
            }
        }

        if (best is null) return (null, null);

        var imagePath = Path.Combine(
            _cacheRootPath,
            best.Content.ImagePath.Replace('/', Path.DirectorySeparatorChar));

        return (best, imagePath);
    }
}
