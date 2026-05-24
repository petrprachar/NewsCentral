using System.Diagnostics;
using NewsCentral.Models.IndexFile;
using NewsViewer.Services;

namespace NewsViewer.Forms;

public sealed class ViewerForm : Form
{
    private const int SidePanelWidth = 200;
    private const int DefaultDurationSeconds = 60;

    private readonly PublishedAssignmentIndex _assignment;
    private readonly string _imagePath;
    private readonly TelemetryWriter _telemetry;
    private readonly ViewerStateService _viewerState;
    private readonly bool _isOnline;

    private readonly PictureBox _pictureBox;
    private readonly Label _lblPosterText;
    private readonly Label _lblOnlineStatus;
    private readonly Panel _pnlSide;
    private readonly Label _lblCountdown;

    private readonly System.Windows.Forms.Timer _timer;
    private int _secondsRemaining;
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private string _closeReason = "UserClose";

    public ViewerForm(
        PublishedAssignmentIndex assignment,
        string imagePath,
        TelemetryWriter telemetry,
        ViewerStateService viewerState,
        bool isOnline)
    {
        _assignment  = assignment;
        _imagePath   = imagePath;
        _telemetry   = telemetry;
        _viewerState = viewerState;
        _isOnline    = isOnline;

        // ── Form ────────────────────────────────────────────────────────────
        FormBorderStyle = FormBorderStyle.None;
        ClientSize      = new Size(1600, 900);
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.Black;
        TopMost         = true;
        Text            = "NewsViewer";

        // ── PictureBox (behind all other controls) ───────────────────────────
        _pictureBox = new PictureBox
        {
            Location = new Point(0, 0),
            Size     = new Size(1600, 900),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black
        };
        Controls.Add(_pictureBox);

        // ── Side panel ───────────────────────────────────────────────────────
        _pnlSide = new Panel
        {
            Location  = new Point(1600 - SidePanelWidth, 0),
            Size      = new Size(SidePanelWidth, 900),
            BackColor = Color.FromArgb(45, 45, 48)
        };
        Controls.Add(_pnlSide);

        var btnClose = new Button
        {
            Text      = "Close",
            Font      = new Font("Segoe UI", 11),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(63, 63, 70),
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(164, 38),
            Location  = new Point(18, 30),
            Cursor    = Cursors.Hand
        };
        btnClose.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        btnClose.Click += (_, _) => { _closeReason = "UserClose"; Close(); };
        _pnlSide.Controls.Add(btnClose);

        var btnMoreInfo = new Button
        {
            Text      = "Click to see more information..",
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(63, 63, 70),
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(164, 52),
            Location  = new Point(18, 86),
            Cursor    = Cursors.Hand
        };
        btnMoreInfo.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        btnMoreInfo.Click += OnMoreInfoClicked;
        _pnlSide.Controls.Add(btnMoreInfo);

        _lblCountdown = new Label
        {
            Text      = string.Empty,
            Font      = new Font("Segoe UI", 11),
            ForeColor = Color.FromArgb(180, 180, 180),
            BackColor = Color.Transparent,
            AutoSize  = false,
            Size      = new Size(164, 28),
            Location  = new Point(18, 158),
            TextAlign = ContentAlignment.MiddleCenter
        };
        _pnlSide.Controls.Add(_lblCountdown);

        // ── PosterText overlay (bottom-left of image) ────────────────────────
        var posterText = !string.IsNullOrWhiteSpace(_assignment.PosterText)
            ? _assignment.PosterText
            : _assignment.PresentationName;

        _lblPosterText = new Label
        {
            Text      = posterText,
            Font      = new Font("Segoe UI", 13),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(140, 0, 0, 0),
            AutoSize  = false,
            Size      = new Size(1380, 38),
            Location  = new Point(0, 862),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(12, 0, 0, 0)
        };
        Controls.Add(_lblPosterText);

        // ── Online / Offline indicator ───────────────────────────────────────
        _lblOnlineStatus = new Label
        {
            Text      = _isOnline ? "● Online" : "● Offline",
            Font      = new Font("Segoe UI", 9),
            ForeColor = _isOnline ? Color.LimeGreen : Color.OrangeRed,
            BackColor = Color.FromArgb(140, 0, 0, 0),
            AutoSize  = true,
            Location  = new Point(12, 10),
            Padding   = new Padding(6, 3, 6, 3)
        };
        Controls.Add(_lblOnlineStatus);

        // z-order: PictureBox at back, overlays in front
        _pictureBox.SendToBack();
        _pnlSide.BringToFront();
        _lblPosterText.BringToFront();
        _lblOnlineStatus.BringToFront();

        // ── Countdown timer ──────────────────────────────────────────────────
        _secondsRemaining = _assignment.DisplayDurationSeconds > 0
            ? _assignment.DisplayDurationSeconds
            : DefaultDurationSeconds;

        UpdateCountdownLabel();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        FormClosed += OnFormClosed;

        LoadImage();
    }

    private void LoadImage()
    {
        if (!File.Exists(_imagePath)) return;
        try
        {
            // Load via byte array to avoid locking the cache file.
            var bytes = File.ReadAllBytes(_imagePath);
            using var ms = new MemoryStream(bytes);
            _pictureBox.Image = new Bitmap(ms);
        }
        catch { }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        UpdateCountdownLabel();
        if (_secondsRemaining <= 0)
        {
            _closeReason = "Timeout";
            Close();
        }
    }

    private void UpdateCountdownLabel() =>
        _lblCountdown.Text = $"{_secondsRemaining} seconds";

    private void OnMoreInfoClicked(object? sender, EventArgs e)
    {
        var url = _assignment.Content.MoreInfoUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
        _closeReason = "UrlLaunch";
        Close();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _pictureBox.Image?.Dispose();

        _viewerState.RecordShown(_assignment.PresentationId);

        _telemetry.WriteSession(
            _assignment.PresentationId,
            _assignment.SourceTeamFolderName,
            _sessionStart,
            DateTime.UtcNow,
            _closeReason);
    }
}
