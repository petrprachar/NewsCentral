using System.Text.Json;
using NewsCentral.Models;
using NewsViewer.Models;

namespace NewsViewer.Services;

public sealed class ViewerStateService
{
    private readonly string _statePath;
    private readonly int _logicalDayStartHour;

    public ViewerStateService(string userStatePath, int logicalDayStartHour)
    {
        _statePath = Path.Combine(userStatePath, "viewerstate.json");
        _logicalDayStartHour = logicalDayStartHour;
    }

    // Date-only gate: disturb the user once per logical day, regardless of which presentation is
    // active. A new presentation no longer earns an automatic same-day escape — that becomes
    // author-controlled (Priority) in a later release.
    public bool AlreadyShownToday()
    {
        var state = Read();
        return state.LastShownDate == LogicalDayCalculator.LogicalDay(DateTime.Now, _logicalDayStartHour);
    }

    public void RecordShown(string presentationId)
    {
        Write(new ViewerState
        {
            LastShownDate = LogicalDayCalculator.LogicalDay(DateTime.Now, _logicalDayStartHour),
            LastShownPresentationId = presentationId
        });
    }

    private ViewerState Read()
    {
        try
        {
            if (!File.Exists(_statePath)) return new();
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<ViewerState>(json, JsonDefaults.Options) ?? new();
        }
        catch { return new(); }
    }

    private void Write(ViewerState state)
    {
        try { File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonDefaults.Options)); }
        catch { }
    }
}
