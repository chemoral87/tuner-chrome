using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Tuner;

/// <summary>
/// Manages the system tray icon, context menu, and tooltip for the tuner.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Action _onShow;
    private readonly Action _onExit;

    public TrayIconManager(Action onShow, Action onExit)
    {
        _onShow = onShow;
        _onExit = onExit;

        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.Add("Show Tuner", null, (_, _) => _onShow());
        _contextMenu.Items.Add("-");
        _contextMenu.Items.Add("Exit", null, (_, _) => _onExit());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon("♪"),
            Text = "Chromatic Tuner",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => _onShow();
    }

    /// <summary>
    /// Updates the tray tooltip with the current detected note.
    /// </summary>
    public void UpdateTooltip(string noteText, double frequency, double cents)
    {
        string centsStr = cents >= 0 ? $"+{cents:F0}" : $"{cents:F0}";
        _trayIcon.Text = string.IsNullOrEmpty(noteText)
            ? "Chromatic Tuner - Listening..."
            : $"Chromatic Tuner: {noteText} ({(int)frequency} Hz, {centsStr}¢)";

        // Update the icon with the current note initial
        if (!string.IsNullOrEmpty(noteText))
        {
            string initial = noteText.Length >= 1 ? noteText[..1] : "♪";
            _trayIcon.Icon = CreateTrayIcon(initial);
        }
    }

    /// <summary>
    /// Creates a simple icon with a text character for the system tray.
    /// </summary>
    private static Icon CreateTrayIcon(string text)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.FromArgb(187, 238, 255));
        using var font = new Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);

        var rect = new Rectangle(0, 0, 16, 16);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        g.DrawString(text, font, brush, rect, format);

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _contextMenu.Dispose();
    }
}
