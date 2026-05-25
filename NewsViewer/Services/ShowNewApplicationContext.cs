using System.Text.Json;
using NewsCentral.Models.IndexFile;
using NewsViewer.Forms;

namespace NewsViewer.Services;

internal sealed class ShowNewApplicationContext : ApplicationContext
{
    private readonly string[] _teams;
    private readonly PresentationSelector _selector;
    private readonly ViewerStateService _viewerState;
    private readonly TelemetryWriter _telemetry;
    private readonly string _cacheRootPath;
    private readonly bool _bypass;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Windows.Forms.Timer _pollTimer;
    private volatile bool _indexChanged;
    private ViewerForm? _activeForm;

    internal ShowNewApplicationContext(
        string[] teams,
        PresentationSelector selector,
        ViewerStateService viewerState,
        TelemetryWriter telemetry,
        string cacheRootPath,
        bool bypass)
    {
        _teams         = teams;
        _selector      = selector;
        _viewerState   = viewerState;
        _telemetry     = telemetry;
        _cacheRootPath = cacheRootPath;
        _bypass        = bypass;

        foreach (var team in teams)
        {
            var teamPath = Path.Combine(cacheRootPath, team);
            if (!Directory.Exists(teamPath)) continue;

            var watcher = new FileSystemWatcher(teamPath)
            {
                Filter            = "index.json",
                NotifyFilter      = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, _) => _indexChanged = true;
            _watchers.Add(watcher);
        }

        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    internal void TryShowViewer()
    {
        var (assignment, imagePath) = _selector.SelectActive(_teams);
        if (assignment is null || imagePath is null) return;
        ShowForm(assignment, imagePath);
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_activeForm != null) return;

        bool triggered = _indexChanged;
        _indexChanged = false;

        var (assignment, imagePath) = _selector.SelectActive(_teams);
        if (assignment is null || imagePath is null) return;

        // New content arrived — different presentation than last shown
        if (triggered && assignment.PresentationId != _viewerState.GetLastShownPresentationId())
        {
            ShowForm(assignment, imagePath);
            return;
        }

        // New day — ShowOnce part of ShowNew
        if (_bypass || !_viewerState.AlreadyShownToday(assignment.PresentationId))
            ShowForm(assignment, imagePath);
    }

    private void ShowForm(PublishedAssignmentIndex assignment, string imagePath)
    {
        if (_activeForm != null) return;
        var isOnline = ReadOnlineStatus(_cacheRootPath);
        _activeForm = new ViewerForm(assignment, imagePath, _telemetry, _viewerState, isOnline);
        _activeForm.FormClosed += (_, _) => _activeForm = null;
        _activeForm.Show();
    }

    private static bool ReadOnlineStatus(string cacheRootPath)
    {
        try
        {
            var path = Path.Combine(cacheRootPath, "status.json");
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("isOnline", out var prop) && prop.GetBoolean();
        }
        catch { return false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            foreach (var w in _watchers) w.Dispose();
        }
        base.Dispose(disposing);
    }
}
