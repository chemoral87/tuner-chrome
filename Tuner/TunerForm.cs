using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.Wave;

namespace Tuner;

/// <summary>
/// Main tuner form that replicates the Chrome extension tuner UI.
/// Renders a 480×320 canvas with chromatic grid, note display, and pitch history.
/// </summary>
public sealed class TunerForm : Form
{
    // --- Canvas constants (matching tuner.js exactly) ---
    private const int CanvasWidth = 480;
    private const int CanvasHeight = 320;
    private const int SplitX = 280;           // Left side = history, right side = note display
    private const int MaxHistory = 560 / 3;   // ~186 samples (~3 seconds)
    private const int HistorySpacing = 3;      // Pixels per history sample
    private const double PixelsPerSemitone = 45.0 / 2.0; // 22.5 px/semitone
    private const double YOffset = 300;        // Bottom of canvas in note coordinates
    private const double ClarityThreshold = 0.9;
    private const double SmoothingFactor = 1.0 / 5.0;
    private const double SmoothingSnapThreshold = 1.0;

    // --- Color palette (matching tuner.js) ---
    private static readonly Color BgColor = Color.Black;
    private static readonly Color GridColor = Color.FromArgb(32, 255, 255, 255); // #fff2 ≈ 12.5% white
    private static readonly Color GridLabelColor = Color.FromArgb(32, 255, 255, 255);
    private static readonly Color HighlightColor = Color.FromArgb(255, 215, 252, 112); // #d7fc70
    private static readonly Color NoteDisplayColor = Color.FromArgb(255, 187, 238, 255); // #bef
    private static readonly Color HistoryLineColor = Color.FromArgb(255, 187, 238, 255); // #bef

    // --- Pitch classes (matching tuner.js) ---
    private static readonly string[] PitchClasses =
        ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    // --- Notation modes ---
    public enum NotationMode { Roland, Yamaha, Cakewalk }

    // --- Audio state ---
    private WaveInEvent? _waveIn;
    private PitchDetector? _detector;
    private readonly object _audioLock = new();
    private double _latestSampleRate;
    private double _latestClarity;
    private double _latestPitch;
    private readonly List<float> _sampleBuffer = new();
    private const int FftSize = 2048;

    // --- Pitch state ---
    private double _note;
    private readonly List<double?> _historyData = new();

    // --- Notation ---
    private NotationMode _notationMode = NotationMode.Roland;

    // --- UI ---
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly Panel _canvas;
    private Label? _statusLabel;

    // --- Tray icon ---
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private ToolStripMenuItem _startupMenuItem = null!;
    private ToolStripMenuItem _moveMenuItem = null!;

    // --- Move mode (temporary drag via tray menu) ---
    private bool _moveMode;
    private Point _moveStart;

    // --- Win32 click-through ---
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // --- Settings persistence ---
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Tuner", "settings.json");

    public TunerForm()
    {
        Text = "Tuner";
        Size = new Size(CanvasWidth + 16, CanvasHeight + 60); // Extra for controls
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        TopMost = true;
        Opacity = 0.7;

        // Make window click-through by default (WS_EX_TRANSPARENT)
        // WS_EX_LAYERED is auto-set by Opacity property, required for transparent input
        Load += (_, _) => SetClickThrough(true);

        // Canvas panel for custom rendering
        _canvas = new Panel
        {
            Size = new Size(CanvasWidth, CanvasHeight),
            Location = new Point(8, 8),
            BackColor = BgColor,
        };
        // Enable double-buffering via reflection (not exposed in Panel's public API)
        typeof(Panel).InvokeMember("DoubleBuffered",
            System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, _canvas, new object[] { true });
        _canvas.Paint += Canvas_Paint;
        Controls.Add(_canvas);



        // Status label
        _statusLabel = new Label
        {
            Text = "Initializing microphone...",
            Location = new Point(380, CanvasHeight + 16),
            Size = new Size(CanvasWidth - 370, 20),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 9f),
        };
        Controls.Add(_statusLabel);

        // Render timer at ~60 FPS
        _renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _renderTimer.Tick += RenderTimer_Tick;

        // --- System tray icon (visible from startup in notification area) ---
        _trayMenu = new ContextMenuStrip();

        // Opacity submenu
        var opacityMenu = new ToolStripMenuItem("Opacity");
        foreach (int pct in new[] { 25, 50, 70, 90, 100 })
        {
            double alpha = pct / 100.0;
            var item = new ToolStripMenuItem($"{pct}%")
            {
                Checked = pct == 70, // default is 0.7
            };
            item.Click += (_, _) =>
            {
                Opacity = alpha;
                foreach (ToolStripMenuItem m in opacityMenu.DropDownItems)
                    m.Checked = false;
                item.Checked = true;
                SaveSettings();
            };
            opacityMenu.DropDownItems.Add(item);
        }
        // Pitch notation submenu
        var notationMenu = new ToolStripMenuItem("Pitch Notation");
        var notationOptions = new (string label, NotationMode mode)[]
        {
            ("Roland (C4 = middle C)", NotationMode.Roland),
            ("Yamaha (C3 = middle C)", NotationMode.Yamaha),
            ("Cakewalk (C5 = middle C)", NotationMode.Cakewalk),
        };
        foreach (var (label, mode) in notationOptions)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = mode == NotationMode.Roland,
            };
            item.Click += (_, _) =>
            {
                _notationMode = mode;
                foreach (ToolStripMenuItem m in notationMenu.DropDownItems)
                    m.Checked = false;
                item.Checked = true;
            };
            notationMenu.DropDownItems.Add(item);
        }

        // Start on Windows startup
        _startupMenuItem = new ToolStripMenuItem("Start on Windows startup")
        {
            Checked = IsStartupEnabled(),
        };
        _startupMenuItem.Click += (_, _) =>
        {
            _startupMenuItem.Checked = !_startupMenuItem.Checked;
            SetStartup(_startupMenuItem.Checked);
        };

        _trayMenu.Items.Add(opacityMenu);
        _trayMenu.Items.Add(notationMenu);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_startupMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _moveMenuItem = new ToolStripMenuItem("Move Window (hold & drag)");
        _moveMenuItem.Click += (_, _) =>
        {
            _moveMode = !_moveMode;
            _moveMenuItem.Checked = _moveMode;
            Cursor = _moveMode ? Cursors.SizeAll : Cursors.Default;
            SetClickThrough(!_moveMode);
        };

        _trayMenu.Items.Add("Show Window", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add(_moveMenuItem);
        _trayMenu.Items.Add("Center on Screen", null, (_, _) => CenterOnScreen());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Close", null, (_, _) => { StopAudio(); SaveSettings(); Application.Exit(); });

        _trayIcon = new NotifyIcon
        {
            Text = "Tuner",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        Load += (_, _) =>
        {
            LoadSettings();
            StartAudio();
        };
        FormClosing += (_, _) =>
        {
            SaveSettings();
            StopAudio();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        };

        // --- Move support (only when move mode is active) ---
        _canvas.MouseDown += (_, e) =>
        {
            if (_moveMode && e.Button == MouseButtons.Left)
            {
                _moveStart = e.Location;
            }
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (_moveMode && e.Button == MouseButtons.Left)
            {
                Location = new Point(Location.X + e.X - _moveStart.X, Location.Y + e.Y - _moveStart.Y);
            }
        };
        _canvas.MouseUp += (_, e) =>
        {
            if (_moveMode && e.Button == MouseButtons.Left)
            {
                _moveMode = false;
                _moveMenuItem.Checked = false;
                Cursor = Cursors.Default;
            }
        };
    }

    /// <summary>
    /// Enables or disables click-through on the window.
    /// When enabled, all mouse input passes to the window behind.
    /// </summary>
    private void SetClickThrough(bool enable)
    {
        int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
        if (enable)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
    }

    private void MinimizeToTray()
    {
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void CenterOnScreen()
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var area = screen.WorkingArea;
        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
            MinimizeToTray();
    }

    // --- Startup on Windows boot ---
    private static string GetExePath() => Environment.ProcessPath ??
        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue("Tuner") != null;
    }

    private static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;
        if (enable)
            key.SetValue("Tuner", $"\"{GetExePath()}\"");
        else
            key.DeleteValue("Tuner", false);
    }

    // --- Settings persistence (position, opacity, notation) ---
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<SettingsData>(json);
            if (s == null) return;

            if (s.X != 0 || s.Y != 0)
                Location = new Point(s.X, s.Y);
            if (s.Opacity > 0)
                Opacity = s.Opacity;
            if (Enum.IsDefined<NotationMode>(s.NotationMode))
                _notationMode = s.NotationMode;

            // Sync tray menu checkmarks
            foreach (ToolStripMenuItem m in _trayMenu.Items.OfType<ToolStripMenuItem>())
            {
                if (m.HasDropDown)
                {
                    foreach (ToolStripMenuItem sub in m.DropDownItems)
                    {
                        if (m.Text == "Opacity" && sub.Text == $"{(int)(Opacity * 100)}%")
                        {
                            foreach (ToolStripMenuItem x in m.DropDownItems) x.Checked = false;
                            sub.Checked = true;
                        }
                        if (m.Text == "Pitch Notation")
                        {
                            sub.Checked = sub.Text.Contains(_notationMode.ToString());
                        }
                    }
                }
            }
        }
        catch { /* ignore corrupt settings */ }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var s = new SettingsData
            {
                X = Location.X,
                Y = Location.Y,
                Opacity = Opacity,
                NotationMode = _notationMode,
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore write errors */ }
    }

    private class SettingsData
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Opacity { get; set; }
        public NotationMode NotationMode { get; set; }
    }

    private void StartAudio()
    {
        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(44100, 1),
                BufferMilliseconds = 50,
            };

            _detector = new PitchDetector(FftSize);
            _latestSampleRate = _waveIn.WaveFormat.SampleRate;

            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.StartRecording();

            _renderTimer.Start();

            _statusLabel!.Text = "Listening...";
            _statusLabel.ForeColor = Color.FromArgb(100, 200, 100);
        }
        catch (Exception ex)
        {
            _statusLabel!.Text = $"Microphone error: {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
        }
    }

    private void StopAudio()
    {
        _renderTimer.Stop();
        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
        }
        catch { /* ignore cleanup errors */ }
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_detector == null) return;

        int sampleCount = e.BytesRecorded / 2; // 16-bit mono samples

        lock (_audioLock)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(e.Buffer, i * 2);
                _sampleBuffer.Add(s / 32768f);
            }
            // Keep buffer from growing unbounded
            if (_sampleBuffer.Count > FftSize * 4)
                _sampleBuffer.RemoveRange(0, _sampleBuffer.Count - FftSize * 2);
        }
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (_detector == null) return;

        float[] input;
        double sampleRate;
        lock (_audioLock)
        {
            if (_sampleBuffer.Count < FftSize) return;
            // Take the most recent FftSize samples
            input = _sampleBuffer.GetRange(_sampleBuffer.Count - FftSize, FftSize).ToArray();
            sampleRate = _latestSampleRate;
        }

        // Run pitch detection
        var (pitch, clarity) = _detector.FindPitch(input, sampleRate);
        _latestPitch = pitch;
        _latestClarity = clarity;

        // Convert to MIDI note number
        double fnote = FrequencyToMidi(pitch);

        // Apply EMA smoothing
        if (double.IsFinite(fnote) && !double.IsNaN(fnote))
        {
            if (Math.Abs(fnote - _note) < SmoothingSnapThreshold)
                _note += (fnote - _note) * SmoothingFactor;
            else
                _note = fnote;
        }

        // Update history
        if (clarity >= ClarityThreshold)
            _historyData.Add(_note);
        else
            _historyData.Add(null);

        if (_historyData.Count > MaxHistory)
            _historyData.RemoveAt(0);

        // Trigger repaint
        _canvas.Invalidate();
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;

        // Clear canvas
        g.Clear(BgColor);

        // Use cached pitch data from the last render tick
        double note = _note;
        double pitch = _latestPitch;
        double clarity = _latestClarity;

        // --- Draw chromatic grid (13 horizontal lines) ---
        using var gridPen = new Pen(GridColor, 2f);
        using var labelFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(GridLabelColor);

        for (int i = 0; i <= 12; i++)
        {
            float y = (float)GetY(i);
            g.DrawLine(gridPen, 0, y, CanvasWidth, y);
            g.DrawString(PitchClasses[i % 12], labelFont, labelBrush, 32, y - 16);
        }

        // --- Draw note display (right side) when clarity >= 0.9 ---
        if (clarity >= ClarityThreshold)
        {
            double o = (clarity - ClarityThreshold) / 0.1; // opacity factor [0, 1]
            double p = note % 12;
            int closestNote = (int)Math.Round(note);
            int octave = GetOctave(closestNote);
            int pitchClass = closestNote % 12;
            if (pitchClass >= 12) pitchClass -= 12;
            if (pitchClass < 0) pitchClass += 12;
            string name = PitchClasses[pitchClass];

            // --- Green highlight on chromatic grid ---
            using var highlightPen = new Pen(Color.FromArgb(255, HighlightColor), 2f);
            for (int i = 0; i < 12; i++)
            {
                double dist = p - i;
                if (dist > 6) dist -= 12;
                if (dist < -6) dist += 12;
                dist = Math.Abs(dist);
                if (dist < 0.5)
                {
                    double alpha = (1 - dist / 0.5) * o;
                    int a = (int)(alpha * 255);
                    highlightPen.Color = Color.FromArgb(Math.Clamp(a, 0, 255), HighlightColor);
                    float y = (float)GetY(i);
                    g.DrawLine(highlightPen, 0, y, CanvasWidth, y);
                }
            }

            // --- Note display (right side) for octaves -1, 0, +1 ---
            using var noteBrush = new SolidBrush(NoteDisplayColor);
            using var smearBrush = new SolidBrush(NoteDisplayColor);
            using var infoBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 0)); // yellow for cents+Hz
            using var noteFont = new Font("Segoe UI", 32f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var infoFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

            for (int i = -1; i <= 1; i++)
            {
                double y = GetY(p + 12 * i);
                double yR = GetY(Math.Round(p) + 12 * i);

                // Smear rectangle (gap between actual and rounded pitch)
                double smearAlpha = o * (1 - Math.Abs(p - Math.Round(p)) / 0.5);
                int smearA = (int)(smearAlpha * 255);
                smearBrush.Color = Color.FromArgb(Math.Clamp(smearA, 0, 255), NoteDisplayColor);
                g.FillRectangle(smearBrush, SplitX, (float)Math.Min(y, yR) - 1, CanvasWidth - SplitX, (float)Math.Abs(y - yR) + 2);

                // Pitch line
                noteBrush.Color = Color.FromArgb((int)(o * 255), NoteDisplayColor);
                g.FillRectangle(noteBrush, SplitX, (float)y - 1, CanvasWidth - SplitX, 2);

                // Cents + Hz (below the line)
                int deviation = (int)Math.Round((note - Math.Round(note)) * 100);
                string cents = deviation < 0 ? $"{deviation}" : $"+{deviation}";
                string info = $"{cents} ({Math.Round(pitch)} Hz)";
                g.DrawString(info, infoFont, infoBrush, 290, (float)y + 6);

                // Note name (above the line)
                string noteName = $"{name}{octave}";
                g.DrawString(noteName, noteFont, noteBrush, 290, (float)y - 43);
            }
        }

        // --- Draw pitch history line (left side) ---
        // Each continuous detection run produces one stroke per octave (-1, 0, +1).
        // Null entries break the strokes, creating disconnected segments.
        // Uses anti-aliasing and a subtle glow effect for a polished look.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;

        using var historyPen = new Pen(HistoryLineColor, 2f);
        using var glowPen = new Pen(Color.FromArgb(40, 187, 238, 255), 6f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
        historyPen.EndCap = LineCap.Round;
        historyPen.StartCap = LineCap.Round;
        historyPen.LineJoin = LineJoin.Round;

        // strokes[octaveNumber] = list of points for that continuous segment
        var strokes = new Dictionary<int, List<PointF>>();
        var activeOctaves = new HashSet<int>();

        for (int i = 0; i < _historyData.Count; i++)
        {
            double? entry = _historyData[i];
            float x = SplitX - (_historyData.Count - i - 1) * HistorySpacing;

            if (entry.HasValue)
            {
                for (int octave = -1; octave <= 1; octave++)
                {
                    int octaveNumber = (int)Math.Floor(entry.Value / 12) + octave;
                    double y = GetY(entry.Value - 12 * octaveNumber);

                    if (!strokes.TryGetValue(octaveNumber, out var points))
                    {
                        points = new List<PointF>();
                        strokes[octaveNumber] = points;
                    }
                    points.Add(new PointF(x, (float)y));
                    activeOctaves.Add(octaveNumber);
                }
            }
            else
            {
                activeOctaves.Clear();
            }
        }

        // Draw glow layer (wider, semi-transparent) then crisp line on top
        foreach (var stroke in strokes.Values)
        {
            if (stroke.Count > 1)
            {
                g.DrawLines(glowPen, stroke.ToArray());
                g.DrawLines(historyPen, stroke.ToArray());
            }
        }

        // Reset smoothing for grid and note rendering
        g.SmoothingMode = SmoothingMode.None;
        g.CompositingQuality = CompositingQuality.Default;
    }

    /// <summary>
    /// Maps a MIDI note number to a Y coordinate on the canvas.
    /// </summary>
    private static double GetY(double note) => YOffset - (note * PixelsPerSemitone);

    /// <summary>
    /// Converts frequency (Hz) to MIDI note number (float).
    /// </summary>
    private static double FrequencyToMidi(double f)
    {
        if (f <= 0) return 0;
        return 69.0 + 12.0 * Math.Log(f / 440.0) / Math.Log(2.0);
    }

    /// <summary>
    /// Gets the octave number for a MIDI note based on the current notation mode.
    /// </summary>
    private int GetOctave(int midiNumber)
    {
        return _notationMode switch
        {
            NotationMode.Roland => (int)Math.Floor(midiNumber / 12.0) - 1,
            NotationMode.Yamaha => (int)Math.Floor(midiNumber / 12.0) - 2,
            NotationMode.Cakewalk => (int)Math.Floor(midiNumber / 12.0),
            _ => (int)Math.Floor(midiNumber / 12.0) - 1,
        };
    }
}
