using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace NewsViewer.Forms;

internal static class FluentTheme
{
    internal static readonly Color PanelBg = Color.FromArgb(240, 240, 240);
    internal static readonly Color Surface = Color.FromArgb(225, 225, 225);
    internal static readonly Color SurfaceHover = Color.FromArgb(229, 241, 251);
    internal static readonly Color SurfacePress = Color.FromArgb(204, 228, 247);
    internal static readonly Color Border = Color.FromArgb(173, 173, 173);
    internal static readonly Color BorderHover = Color.FromArgb(0, 120, 215);
    internal static readonly Color BorderPress = Color.FromArgb(0, 84, 153);
    internal static readonly Color Accent = Color.FromArgb(0, 120, 215);
    internal static readonly Color AccentPaused = Color.FromArgb(160, 160, 160);
    internal static readonly Color TextPrimary = Color.FromArgb(0, 0, 0);
    internal static readonly Color TextSecondary = Color.FromArgb(96, 96, 96);
    internal static readonly Color Stage = Color.FromArgb(96, 96, 96);
    internal const int Radius = 0;

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); path.CloseFigure(); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = FluentTheme.Radius;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = FluentTheme.Surface;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = FluentTheme.Border;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = FluentTheme.RoundedRect(rect, Radius);
        using (var b = new SolidBrush(FillColor)) e.Graphics.FillPath(b, path);
        using (var p = new Pen(BorderColor)) e.Graphics.DrawPath(p, path);
        base.OnPaint(e);
    }
}

internal sealed class RoundedButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = FluentTheme.Radius;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color NormalColor { get; set; } = FluentTheme.Surface;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverColor { get; set; } = FluentTheme.SurfaceHover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressColor { get; set; } = FluentTheme.SurfacePress;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = FluentTheme.Border;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverBorderColor { get; set; } = FluentTheme.BorderHover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressBorderColor { get; set; } = FluentTheme.BorderPress;

    private bool _hover;
    private bool _press;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = FluentTheme.TextPrimary;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = FluentTheme.RoundedRect(rect, Radius);
        var fill = _press ? PressColor : _hover ? HoverColor : NormalColor;
        var bord = _press ? PressBorderColor : _hover ? HoverBorderColor : BorderColor;
        using (var b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
        using (var p = new Pen(bord)) e.Graphics.DrawPath(p, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, rect, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
}