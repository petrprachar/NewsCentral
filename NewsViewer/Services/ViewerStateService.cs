using System.Text.Json;
using NewsViewer.Models;

namespace NewsViewer.Services;

public sealed class ViewerStateService
{
    private readonly string _statePath;

    public ViewerStateService(string cacheRootPath)
    {
        _statePath = Path.Combine(cacheRootPath, "viewerstate.json");
    }

    public bool AlreadyShownToday(string presentationId)
    {
        var state = Read();
        return state.LastShownDate == DateTime.Today.ToString("yyyy-MM-dd")
            && state.LastShownPresentationId == presentationId;
    }

    public string? GetLastShownPresentationId() => Read().LastShownPresentationId;

    public void RecordShown(string presentationId)
    {
        Write(new ViewerState
        {
            LastShownDate = DateTime.Today.ToString("yyyy-MM-dd"),
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
