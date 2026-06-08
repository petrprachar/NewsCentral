using System.Diagnostics;
using NewsCentral.Models.IndexFile;
using NewsViewer.Services;

namespace NewsViewer.Forms;

public sealed class ViewerForm : Form
{
    private const int ImageWidth = 1600;
    private const int ImageHeight = 900;
    private const int SidePanelWidth = 200;
    private const int PosterStripHeight = 44;
    private const int FormWidth = ImageWidth + SidePanelWidth;       // 1800
    private const int FormHeight = ImageHeight + PosterStripHeight;   // 944
    private const int DefaultDurationSeconds = 60;
    private const int PanelMargin = 16;
    private const int ContentWidth = SidePanelWidth - (PanelMargin * 2);   // 168
    private const int ProgressWidth = 144;
    private const int FrameThickness = 5;

    private static readonly Color FrameBand = Color.FromArgb(150, 150, 150);
    private static readonly Color FrameEdge = Color.FromArgb(105, 105, 105);

    private readonly PublishedAssignmentIndex _assignment;
    private readonly string _imagePath;
    private readonly TelemetryWriter _telemetry;
    private readonly ViewerStateService _viewerState;
    private readonly bool _isOnline;

    private readonly PictureBox _pictureBox;
    private readonly Label _lblPosterText;
    private readonly Label _lblOnlineStatus;
    private readonly Panel _pnlSide;
    private readonly RoundedPanel _pnlAutoClose;
    private readonly CheckBox _chkAutoClose;
    private readonly Label _lblCountdown;
    private readonly Label _lblCountdownUnit;
    private readonly Panel _progressFill;
    private readonly ToolTip _toolTip = new();

    private readonly System.Windows.Forms.Timer _timer;
    private int _secondsRemaining;
    private int _totalSeconds;
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private string _closeReason = "UserClose";

    public ViewerForm(
        PublishedAssignmentIndex assignment,
        string imagePath,
        TelemetryWriter telemetry,
        ViewerStateService viewerState,
        bool isOnline)
    {
        _assignment = assignment;
        _imagePath = imagePath;
        _telemetry = telemetry;
        _viewerState = viewerState;
        _isOnline = isOnline;

        // ── Form — solid 5px frame around the whole window ───────────────────
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(FormWidth + FrameThickness * 2, FormHeight + FrameThickness * 2);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = FrameBand;
        TopMost = true;
        Text = "NewsViewer";
        Paint += OnFramePaint;

        // ── Root content host (inset by the frame thickness) ─────────────────
        var root = new Panel
        {
            Location = new Point(FrameThickness, FrameThickness),
            Size = new Size(FormWidth, FormHeight),
            BackColor = FluentTheme.PanelBg
        };
        Controls.Add(root);

        // ── PictureBox — fixed 1600×900, gray stage backing ─────────────────
        _pictureBox = new PictureBox
        {
            Location = new Point(0, 0),
            Size = new Size(ImageWidth, ImageHeight),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = FluentTheme.Stage
        };
        root.Controls.Add(_pictureBox);

        // ── Side panel — fixed column, gray surface, left separator ─────────
        _pnlSide = new Panel
        {
            Location = new Point(ImageWidth, 0),
            Size = new Size(SidePanelWidth, FormHeight),
            BackColor = FluentTheme.PanelBg
        };
        _pnlSide.Paint += (_, e) =>
        {
            using var pen = new Pen(FluentTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, 0, _pnlSide.Height);
        };
        root.Controls.Add(_pnlSide);

        // Online / Offline indicator
        _lblOnlineStatus = new Label
        {
            Text = _isOnline ? "● Online" : "● Offline",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _isOnline ? Color.FromArgb(0, 130, 0) : Color.FromArgb(190, 0, 0),
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(PanelMargin, 16)
        };
        _pnlSide.Controls.Add(_lblOnlineStatus);

        var btnClose = new RoundedButton
        {
            Text = "Close",
            Font = new Font("Segoe UI", 11),
            Size = new Size(ContentWidth, 40),
            Location = new Point(PanelMargin, 44)
        };
        btnClose.Click += (_, _) => { _closeReason = "UserClose"; Close(); };
        _pnlSide.Controls.Add(btnClose);

        var btnMoreInfo = new RoundedButton
        {
            Text = "Click to see more information..",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(ContentWidth, 48),
            Location = new Point(PanelMargin, 96)
        };
        btnMoreInfo.Click += OnMoreInfoClicked;
        _pnlSide.Controls.Add(btnMoreInfo);

        // ── Auto-close card — TableLayoutPanel guarantees no row overlap ─────
        _pnlAutoClose = new RoundedPanel
        {
            Location = new Point(PanelMargin, 156),
            Size = new Size(ContentWidth, 126),
            FillColor = FluentTheme.Surface,
            BorderColor = FluentTheme.Border,
            Padding = new Padding(1)
        };
        _pnlSide.Controls.Add(_pnlAutoClose);

        var cardLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = FluentTheme.Surface,
            Padding = new Padding(11, 9, 11, 9)
        };
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pnlAutoClose.Controls.Add(cardLayout);

        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = FluentTheme.Surface,
            Margin = new Padding(0, 0, 0, 6)
        };

        _chkAutoClose = new CheckBox
        {
            Text = string.Empty,
            Checked = true,
            AutoSize = false,
            Size = new Size(16, 16),
            Margin = new Padding(0, 1, 7, 0),
            BackColor = FluentTheme.Surface,
            FlatStyle = FlatStyle.Standard,
            Cursor = Cursors.Hand
        };
        _chkAutoClose.CheckedChanged += OnAutoCloseChanged;

        var lblAutoCloseCaption = new Label
        {
            Text = "Form closes in",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = FluentTheme.Surface,
            Margin = new Padding(0, 2, 0, 0),
            Cursor = Cursors.Hand
        };
        lblAutoCloseCaption.Click += (_, _) => _chkAutoClose.Checked = !_chkAutoClose.Checked;

        header.Controls.Add(_chkAutoClose);
        header.Controls.Add(lblAutoCloseCaption);
        cardLayout.Controls.Add(header, 0, 0);

        _lblCountdown = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Font = new Font("Segoe UI", 11, FontStyle.Regular),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = FluentTheme.Surface,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0)
        };
        cardLayout.Controls.Add(_lblCountdown, 0, 1);

        _lblCountdownUnit = new Label
        {
            Text = "seconds",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = FluentTheme.TextSecondary,
            BackColor = FluentTheme.Surface,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 2, 0, 6)
        };
        cardLayout.Controls.Add(_lblCountdownUnit, 0, 2);

        var progressTrack = new Panel
        {
            Size = new Size(ProgressWidth, 4),
            BackColor = FluentTheme.Border,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0)
        };
        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ProgressWidth, 4),
            BackColor = FluentTheme.Accent
        };
        progressTrack.Controls.Add(_progressFill);
        cardLayout.Controls.Add(progressTrack, 0, 3);

        _toolTip.SetToolTip(_chkAutoClose,
            "Uncheck to stop the timer and keep this window open");
        _toolTip.SetToolTip(lblAutoCloseCaption,
            "Uncheck to stop the timer and keep this window open");

        // ── Caption bar — hairline divider ties it to the image above ────────
        var pnlPoster = new Panel
        {
            Location = new Point(0, ImageHeight),
            Size = new Size(ImageWidth, PosterStripHeight),
            BackColor = FluentTheme.PanelBg
        };
        pnlPoster.Paint += (_, e) =>
        {
            using var pen = new Pen(FluentTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, pnlPoster.Width, 0);
        };
        root.Controls.Add(pnlPoster);

        var posterText = !string.IsNullOrWhiteSpace(_assignment.PosterText)
            ? _assignment.PosterText
            : _assignment.PresentationName;

        _lblPosterText = new Label
        {
            Text = posterText,
            Font = new Font("Segoe UI", 12.5f),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(0, 1),
            Size = new Size(ImageWidth, PosterStripHeight - 1),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(0)
        };
        pnlPoster.Controls.Add(_lblPosterText);

        // ── Countdown timer ──────────────────────────────────────────────────
        _secondsRemaining = _assignment.DisplayDurationSeconds > 0
            ? _assignment.DisplayDurationSeconds
            : DefaultDurationSeconds;
        _totalSeconds = _secondsRemaining;

        UpdateCountdownUi();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        FormClosed += OnFormClosed;

        LoadImage();
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    private void OnFramePaint(object? sender, PaintEventArgs e)
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        using var edge = new Pen(FrameEdge);
        e.Graphics.DrawRectangle(edge, 0, 0, w - 1, h - 1);
        e.Graphics.DrawRectangle(edge, FrameThickness - 1, FrameThickness - 1,
            w - 2 * (FrameThickness - 1) - 1, h - 2 * (FrameThickness - 1) - 1);
    }

    // ── Image ──────────────────────────────────────────────────────────────────

    private void LoadImage()
    {
        if (!File.Exists(_imagePath)) return;
        try
        {
            var bytes = File.ReadAllBytes(_imagePath);
            using var ms = new MemoryStream(bytes);
            _pictureBox.Image = new Bitmap(ms);
        }
        catch { }
    }

    // ── Countdown ──────────────────────────────────────────────────────────────

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        UpdateCountdownUi();
        if (_secondsRemaining <= 0)
        {
            _closeReason = "Timeout";
            Close();
        }
    }

    private void OnAutoCloseChanged(object? sender, EventArgs e)
    {
        if (_chkAutoClose.Checked)
        {
            _lblCountdown.ForeColor = FluentTheme.TextPrimary;
            _lblCountdownUnit.ForeColor = FluentTheme.TextSecondary;
            _progressFill.BackColor = FluentTheme.Accent;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _lblCountdown.ForeColor = FluentTheme.AccentPaused;
            _lblCountdownUnit.ForeColor = FluentTheme.AccentPaused;
            _progressFill.BackColor = FluentTheme.AccentPaused;
        }
    }

    private void UpdateCountdownUi()
    {
        _lblCountdown.Text = _secondsRemaining.ToString();
        int w = _totalSeconds > 0
            ? (int)Math.Round(ProgressWidth * (double)_secondsRemaining / _totalSeconds)
            : 0;
        _progressFill.Width = Math.Clamp(w, 0, ProgressWidth);
    }

    // ── More info ────────────────────────────────────────────────────────────

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

    // ── Cleanup ────────────────────────────────────────────────────────────────

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _toolTip.Dispose();
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