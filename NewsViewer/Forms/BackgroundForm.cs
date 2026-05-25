namespace NewsViewer.Forms;

internal sealed class BackgroundForm : Form
{
    internal BackgroundForm(string hexColor)
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState     = FormWindowState.Maximized;
        BackColor       = ParseColor(hexColor);
        ShowInTaskbar   = false;
    }

    private static Color ParseColor(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return Color.Black; }
    }
}
