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
    private const int HistorySpacing = 3;      // Pixels per history sample
    // The history pane is SplitX pixels wide, so this is exactly how many samples fit on
    // screen. One sample is one analysis frame (BufferMilliseconds), i.e. the curve spans
    // ~4.7 s of real time and nothing is drawn off-canvas.
    private const int MaxHistory = SplitX / HistorySpacing;
    private const double PixelsPerSemitone = 45.0 / 2.0; // 22.5 px/semitone
    private const double YOffset = 300;        // Bottom of canvas in note coordinates
    private const double ClarityThreshold = 0.9;         // gate for the note name / cents readout
    private const double HistoryClarityThreshold = 0.75; // gate for the curve - looser, so breathier onsets and glides still draw
    private const double HistoryHoldClarity = 0.45;      // below this the signal is really gone: break the curve
    // NSDF clarity is amplitude-invariant, so a quiet room can still yield a confident-looking
    // reading at a random pitch. A minimum signal level (-48 dBFS) keeps that out of both the
    // readout and the curve. Raise it if room noise still shows up; lower it for a very quiet voice.
    private const double MinRms = 0.004;
    private const int HistoryHoldFrames = 2;             // ...but hold the last value through brief dips (~100 ms)
    private const double LabelSmoothingFactor = 0.4;     // ~30 ms time constant for the readout only
    // A voice cannot move 2 semitones inside one 50 ms frame, so a jump that large is a real
    // leap (new note, octave correction) rather than a glide and may go straight to the label.
    private const double LabelSnapThreshold = 2.0;

    // --- Color palette (matching tuner.js) ---
    private static readonly Color BgColor = Color.Black;
    private static readonly Color GridColor = Color.FromArgb(32, 255, 255, 255); // #fff2 ≈ 12.5% white
    private static readonly Color KeyLineColor = Color.FromArgb(255, 255, 165, 0); // #ffa500 orange - grid line is in the selected key
    private static readonly Color GridLabelColor = Color.FromArgb(32, 255, 255, 255);
    private static readonly Color HighlightColor = Color.FromArgb(255, 215, 252, 112); // #d7fc70
    private static readonly Color InKeyColor = Color.FromArgb(255, 255, 64, 64); // #ff4040 - note belongs to the selected key
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
    private long _samplesWritten;        // total samples handed over by the audio callback
    private long _lastAnalyzedSamples;   // sample count at the last analysis frame

    // --- Pitch state ---
    // _labelNote is the lightly smoothed value used for the note name and cents readout.
    // The history deliberately stores the raw detection instead, so the curve is the voice.
    private double _labelNote = double.NaN;
    private double _lastRawNote = double.NaN;
    private int _invalidFrames;
    private readonly List<double?> _historyData = new();

    // --- Notation ---
    private NotationMode _notationMode = NotationMode.Roland;

    // --- Key highlighting (major scale) ---
    // Tray-selected key root. A detected note belonging to that key is drawn in InKeyColor
    // (red) instead of the usual green highlight / blue note. -1 means no key is selected,
    // which leaves the rendering exactly as it was before this option existed.
    private int _keyRoot = -1;
    private static readonly string[] KeyRootNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    // Semitone offsets of a major scale above its root (W-W-H-W-W-W-H).
    private static readonly int[] MajorScaleDegrees = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>
    /// True when the pitch class belongs to the major scale of the selected key root.
    /// Tested against the rounded pitch class - the note actually shown on screen - so a
    /// slightly flat F# in C major does not flicker in and out of key.
    /// </summary>
    private bool IsInSelectedKey(int pitchClass)
    {
        if (_keyRoot < 0) return false;
        int degree = ((pitchClass - _keyRoot) % 12 + 12) % 12;
        return MajorScaleDegrees.Contains(degree);
    }

    // --- UI ---
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly Panel _canvas;
    private Label? _statusLabel;

    // --- Tray icon ---
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private ToolStripMenuItem _startupMenuItem = null!;
    private ToolStripMenuItem _moveMenuItem = null!;
    private ToolStripMenuItem _keyMenuItem = null!;

    // --- Win32 click-through ---
    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>
    /// True while clicks should pass through the window to whatever is beneath it.
    /// Declared in <see cref="CreateParams"/> rather than poked straight into the live window
    /// with SetWindowLong: WinForms re-applies CreateParams whenever it updates styles, and
    /// uses it again whenever it (re)creates the handle, so a manually written ex-style bit
    /// can be silently reverted. Declaring it here keeps it in sync on every WinForms path.
    /// </summary>
    private bool _clickThrough = true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            if (_clickThrough)
                cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    // --- Move mode (temporary drag via tray menu) ---
    private bool _moveMode;
    private Point _moveStart;

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

        // The window is click-through by default: WS_EX_TRANSPARENT is declared in
        // CreateParams, so it is already set the moment the handle is created.



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

        // Key submenu: notes belonging to the selected major scale are highlighted in red.
        // Dropdown layout is load-bearing: item 0 is "None", item n + 1 is pitch class n.
        _keyMenuItem = new ToolStripMenuItem("Key (major scale)");
        var noKeyItem = new ToolStripMenuItem("None") { Checked = true };
        noKeyItem.Click += (_, _) =>
        {
            SelectKey(-1);
            SaveSettings();
        };
        _keyMenuItem.DropDownItems.Add(noKeyItem);
        for (int root = 0; root < KeyRootNames.Length; root++)
        {
            int selected = root;
            var item = new ToolStripMenuItem(KeyRootNames[selected]);
            item.Click += (_, _) =>
            {
                SelectKey(selected);
                SaveSettings();
            };
            _keyMenuItem.DropDownItems.Add(item);
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
        _trayMenu.Items.Add(_keyMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_startupMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _moveMenuItem = new ToolStripMenuItem("Move Window (hold & drag)");
        _moveMenuItem.Click += (_, _) => SetMoveMode(!_moveMode);

        _trayMenu.Items.Add("Show Window", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add(_moveMenuItem);
        _trayMenu.Items.Add("Center on Screen", null, (_, _) => CenterOnScreen());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Close", null, (_, _) => { StopAudio(); SaveSettings(); Application.Exit(); });

        _trayIcon = new NotifyIcon
        {
            Text = "Tuner",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
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
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += Canvas_MouseUp;
        // A drag can end without ever raising MouseUp (capture lost, Alt+Tab, release
        // over another window). End move mode there too so the window can never be
        // left hittable with move mode reported as off.
        _canvas.MouseCaptureChanged += (_, _) =>
        {
            if (_moveMode && !_canvas.Capture)
                SetMoveMode(false);
        };
    }

    /// <summary>
    /// Single owner of move-mode state: the drag flag, the tray checkmark, the cursor
    /// and the click-through exit style must always change together. Enabling move mode
    /// makes the window hittable so it can be dragged; disabling it must restore
    /// WS_EX_TRANSPARENT so clicks pass through to whatever is underneath again.
    /// </summary>
    private void SetMoveMode(bool enable)
    {
        _moveMode = enable;
        _moveMenuItem.Checked = enable;
        Cursor = enable ? Cursors.SizeAll : Cursors.Default;
        SetClickThrough(!enable);
    }

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!_moveMode || e.Button != MouseButtons.Left) return;
        _moveStart = e.Location;
        _canvas.Capture = true; // keep receiving MouseMove/MouseUp if the cursor slips off
    }

    private void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_moveMode || e.Button != MouseButtons.Left) return;
        Location = new Point(Location.X + e.X - _moveStart.X, Location.Y + e.Y - _moveStart.Y);
    }

    private void Canvas_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_moveMode || e.Button != MouseButtons.Left) return;
        SetMoveMode(false);
        _canvas.Capture = false;
    }

    private void SetClickThrough(bool enable)
    {
        _clickThrough = enable;
        if (IsHandleCreated)
            UpdateStyles(); // re-applies CreateParams (and so the bit) to the live window
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

    /// <summary>
    /// Selects the key used for in-key highlighting and keeps the tray submenu in sync.
    /// The submenu layout is fixed: item 0 is "None", item n + 1 is pitch class n.
    /// </summary>
    private void SelectKey(int root)
    {
        _keyRoot = root;
        for (int i = 0; i < _keyMenuItem.DropDownItems.Count; i++)
        {
            if (_keyMenuItem.DropDownItems[i] is ToolStripMenuItem item)
                item.Checked = i == root + 1;
        }
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

    // --- Settings persistence (position, opacity, notation, key) ---
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
            if (s.ScaleRoot >= -1 && s.ScaleRoot < KeyRootNames.Length)
                SelectKey(s.ScaleRoot);

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
                ScaleRoot = _keyRoot,
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
        // -1 = no key selected. Defaulted so settings files written before this option
        // existed load with highlighting off rather than silently selecting C.
        public int ScaleRoot { get; set; } = -1;
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

            _samplesWritten = 0;
            _lastAnalyzedSamples = 0;
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
            _samplesWritten += sampleCount;
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

            // Analyse once per audio buffer (~20 fps with 50 ms buffers) rather than once per
            // repaint. The old 60 fps sampling re-read a window that only overlapped by 30 ms,
            // so roughly two of every three history points were duplicates and the curve's
            // horizontal scale was driven by timer jitter instead of real time.
            if (_samplesWritten == _lastAnalyzedSamples) return;

            // Take the most recent FftSize samples
            input = _sampleBuffer.GetRange(_sampleBuffer.Count - FftSize, FftSize).ToArray();
            sampleRate = _latestSampleRate;
            _lastAnalyzedSamples = _samplesWritten;
        }

        // Run pitch detection
        var (pitch, clarity) = _detector.FindPitch(input, sampleRate);

        // Level gate - nothing is reported from a frame that holds no real sound.
        double sumSquares = 0;
        foreach (float sample in input) sumSquares += sample * (double)sample;
        if (Math.Sqrt(sumSquares / input.Length) < MinRms)
        {
            pitch = double.NaN;
            clarity = 0;
        }

        _latestPitch = pitch;
        _latestClarity = clarity;

        // Convert to MIDI note number (NaN when the frame produced no usable pitch)
        double fnote = FrequencyToMidi(pitch);
        bool valid = double.IsFinite(fnote);

        // Only the readout is smoothed, and only lightly: a short time constant keeps the
        // digits steady without letting the displayed note lag a moving voice.
        if (valid)
        {
            if (double.IsNaN(_labelNote) || Math.Abs(fnote - _labelNote) > LabelSnapThreshold)
                _labelNote = fnote;
            else
                _labelNote += (fnote - _labelNote) * LabelSmoothingFactor;
        }

        AppendHistory(valid ? fnote : null, clarity);

        // Trigger repaint
        _canvas.Invalidate();
    }

    /// <summary>
    /// Adds one history entry for the current analysis frame.
    /// The entry is the raw detection - no EMA, no snapping - so the drawn curve follows the
    /// actual voice. A short hold bridges the brief clarity dips a glissando causes (a moving
    /// pitch is not periodic across the 46 ms window, so its NSDF peak is lower than a held
    /// note's); once the signal really disappears the entry is null and the stroke breaks.
    /// </summary>
    private void AppendHistory(double? rawNote, double clarity)
    {
        if (rawNote.HasValue && clarity >= HistoryClarityThreshold)
        {
            _lastRawNote = rawNote.Value;
            _invalidFrames = 0;
            _historyData.Add(rawNote.Value);
        }
        else if (!double.IsNaN(_lastRawNote) && clarity >= HistoryHoldClarity && _invalidFrames < HistoryHoldFrames)
        {
            _invalidFrames++;
            _historyData.Add(_lastRawNote);
        }
        else
        {
            _invalidFrames = 0;
            _lastRawNote = double.NaN;
            _historyData.Add(null);
        }

        if (_historyData.Count > MaxHistory)
            _historyData.RemoveAt(0);
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;

        // Clear canvas
        g.Clear(BgColor);

        // Use cached pitch data from the last analysis frame
        double note = _labelNote;
        double pitch = _latestPitch;
        double clarity = _latestClarity;

        // --- Draw chromatic grid (13 horizontal lines) ---
        using var gridPen = new Pen(GridColor, 2f);
        using var keyPen = new Pen(KeyLineColor, 2f);
        using var labelFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(GridLabelColor);

        for (int i = 0; i <= 12; i++)
        {
            float y = (float)GetY(i);
            // Lines belonging to the selected key are orange, so the scale reads at a glance
            // even before a note is played. Line i is the pitch class i % 12 (line 12 = octave).
            g.DrawLine(IsInSelectedKey(i % 12) ? keyPen : gridPen, 0, y, CanvasWidth, y);
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

            // Note belongs to the selected key -> red for both the horizontal grid line and
            // the note itself; otherwise the original green highlight and blue note colours.
            bool inKey = IsInSelectedKey(pitchClass);
            Color highlightColor = inKey ? InKeyColor : HighlightColor;
            Color noteColor = inKey ? InKeyColor : NoteDisplayColor;

            // --- Highlight the grid line of the nearest pitch class ---
            using var highlightPen = new Pen(Color.FromArgb(255, highlightColor), 2f);
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
                    highlightPen.Color = Color.FromArgb(Math.Clamp(a, 0, 255), highlightColor);
                    float y = (float)GetY(i);
                    g.DrawLine(highlightPen, 0, y, CanvasWidth, y);
                }
            }

            // --- Note display (right side) for octaves -1, 0, +1 ---
            using var noteBrush = new SolidBrush(noteColor);
            using var smearBrush = new SolidBrush(noteColor);
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
                smearBrush.Color = Color.FromArgb(Math.Clamp(smearA, 0, 255), noteColor);
                g.FillRectangle(smearBrush, SplitX, (float)Math.Min(y, yR) - 1, CanvasWidth - SplitX, (float)Math.Abs(y - yR) + 2);

                // Pitch line
                noteBrush.Color = Color.FromArgb((int)(o * 255), noteColor);
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
        // The curve plots the raw detection, one point per analysis frame, so it is the voice
        // itself rather than a smoothed or snapped version of it.
        // Each continuous detection run produces one stroke per octave (-1, 0, +1).
        // Null entries end the open strokes, creating disconnected segments.
        // Uses anti-aliasing and a subtle glow effect for a polished look.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;

        using var historyPen = new Pen(HistoryLineColor, 2f);
        using var glowPen = new Pen(Color.FromArgb(40, 187, 238, 255), 6f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
        historyPen.EndCap = LineCap.Round;
        historyPen.StartCap = LineCap.Round;
        historyPen.LineJoin = LineJoin.Round;

        // open[octaveNumber] = the stroke currently being built for that octave;
        // strokes = every completed stroke. A gap commits the open strokes and starts fresh,
        // so silence is drawn as a break instead of a straight line across it.
        var strokes = new List<List<PointF>>();
        var open = new Dictionary<int, List<PointF>>();

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

                    if (!open.TryGetValue(octaveNumber, out var points))
                    {
                        points = new List<PointF>();
                        open[octaveNumber] = points;
                    }
                    points.Add(new PointF(x, (float)y));
                }
            }
            else
            {
                strokes.AddRange(open.Values);
                open.Clear();
            }
        }
        strokes.AddRange(open.Values);

        // Draw glow layer (wider, semi-transparent) then crisp line on top
        foreach (var stroke in strokes)
        {
            if (stroke.Count <= 1) continue;
            using var path = BuildSmoothPath(stroke);
            g.DrawPath(glowPen, path);
            g.DrawPath(historyPen, path);
        }

        // Reset smoothing for grid and note rendering
        g.SmoothingMode = SmoothingMode.None;
        g.CompositingQuality = CompositingQuality.Default;
    }

    /// <summary>
    /// Turns the sampled history into a curve. Each segment runs between the midpoints of two
    /// neighbouring samples and uses the sample itself as its control point, which rounds off
    /// the corners a 20 Hz polyline would show while still running along the measured values.
    /// </summary>
    private static GraphicsPath BuildSmoothPath(List<PointF> points)
    {
        var path = new GraphicsPath();
        path.StartFigure();

        if (points.Count == 2)
        {
            path.AddLine(points[0], points[1]);
            return path;
        }

        path.AddLine(points[0], Midpoint(points[0], points[1]));
        for (int i = 1; i < points.Count - 1; i++)
        {
            PointF start = Midpoint(points[i - 1], points[i]);
            PointF end = Midpoint(points[i], points[i + 1]);
            path.AddBezier(start, points[i], points[i], end);
        }
        path.AddLine(Midpoint(points[^2], points[^1]), points[^1]);
        return path;
    }

    private static PointF Midpoint(PointF a, PointF b) => new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    /// <summary>
    /// Maps a MIDI note number to a Y coordinate on the canvas.
    /// </summary>
    private static double GetY(double note) => YOffset - (note * PixelsPerSemitone);

    /// <summary>
    /// Converts frequency (Hz) to MIDI note number (float).
    /// </summary>
    private static double FrequencyToMidi(double f)
    {
        // An unusable reading must stay unusable. Returning a number here (this used to map
        // 0 Hz to MIDI note 0) fed detection dropouts into the smoother, which dragged the
        // displayed note down and then forced a snap on the next good frame.
        if (!(f > 0) || !double.IsFinite(f)) return double.NaN;
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
