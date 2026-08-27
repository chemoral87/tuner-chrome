using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;

namespace Tuner;

public partial class MainWindow : Window
{
    // Audio
    private WaveInEvent? _waveIn;
    private PitchDetector? _detector;
    private SpectrumAnalyzer? _spectrumAnalyzer;
    private double _sampleRate = 44100;

    // Buffer to hold audio samples from the audio callback
    private readonly object _bufferLock = new();
    private readonly float[] _inputBuffer = new float[4096];
    private bool _hasNewData;

    // State
    private double _note;
    private double _currentPitch;
    private double _currentClarity;
    private bool _isRunning;

    // Calibration: reference frequency for A4 (default 440 Hz)
    private double _referenceFrequency = 440.0;

    // Notation modes
    private enum NotationMode { Roland, Yamaha, Cakewalk }
    private NotationMode _notationMode = NotationMode.Roland;

    // Pitch history for scrolling line
    private readonly List<double?> _historyData = new();
    private const int MaxHistoryLength = 560 / 3;

    // Colors matching the Chrome extension
    private static readonly SolidColorBrush GridLineColor = new(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush PitchLineColor = new(Color.FromArgb(200, 187, 238, 255));
    private static readonly SolidColorBrush NoteNameBrush = new(Color.FromArgb(60, 255, 255, 255));

    // Spectrum colors
    private static readonly SolidColorBrush SpectrumBarColor = new(Color.FromArgb(200, 100, 180, 255));
    private static readonly SolidColorBrush SpectrumPeakColor = new(Color.FromArgb(220, 215, 252, 112));

    private static readonly string[] PitchClasses =
        { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

    private DispatcherTimer? _renderTimer;

    // Spectrum state
    private double[]? _lastSpectrum;

    // System tray
    private TrayIconManager? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        // Show settings panel on mouse hover
        MouseEnter += (_, _) => CalibrationPanel.Opacity = 1;
        MouseLeave += (_, _) => CalibrationPanel.Opacity = 0;
        CalibrationPanel.MouseEnter += (_, _) => CalibrationPanel.Opacity = 1;
        CalibrationPanel.MouseLeave += (_, _) => CalibrationPanel.Opacity = 0;

        // Set up system tray
        _trayIcon = new TrayIconManager(
            onShow: ShowWindow,
            onExit: ExitApplication
        );
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartAudio();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of closing
        e.Cancel = true;
        Hide();
    }

    private void ShowWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApplication()
    {
        Dispatcher.Invoke(() =>
        {
            StopAudio();
            _trayIcon?.Dispose();
            Application.Current.Shutdown();
        });
    }

    private void StartAudio()
    {
        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat((int)_sampleRate, 16, 1),
                BufferMilliseconds = 50
            };

            _sampleRate = _waveIn.WaveFormat.SampleRate;
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();

            _detector = new PitchDetector(_inputBuffer.Length);
            _spectrumAnalyzer = new SpectrumAnalyzer(4096);
            _isRunning = true;

            // Start render loop
            _renderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps
            };
            _renderTimer.Tick += RenderTick;
            _renderTimer.Start();

            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            MessageBox.Show(
                $"Could not access microphone.\n\n{ex.Message}\n\nPlease check that microphone access is allowed.",
                "Microphone Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void StopAudio()
    {
        _isRunning = false;
        _renderTimer?.Stop();
        try { _waveIn?.StopRecording(); } catch { }
        _waveIn?.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_detector == null || !_isRunning) return;

        int bytesPerSample = 2; // 16-bit
        int channels = _waveIn!.WaveFormat.Channels;
        int sampleCount = e.BytesRecorded / (bytesPerSample * channels);

        int bufferLen = Math.Min(sampleCount, _inputBuffer.Length);
        lock (_bufferLock)
        {
            for (int i = 0; i < bufferLen; i++)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i * bytesPerSample * channels);
                _inputBuffer[i] = sample / 32768f;
            }
            _hasNewData = true;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"Recording stopped: {e.Exception.Message}";
            });
        }
    }

    private void RenderTick(object? sender, EventArgs e)
    {
        if (_detector == null || _spectrumAnalyzer == null) return;

        double pitch = 0, clarity = 0;
        double[]? spectrum = null;

        lock (_bufferLock)
        {
            if (_hasNewData)
            {
                var input = new double[_inputBuffer.Length];
                for (int i = 0; i < _inputBuffer.Length; i++)
                    input[i] = _inputBuffer[i];

                (pitch, clarity) = _detector.FindPitch(input, _sampleRate);
                spectrum = _spectrumAnalyzer.ComputeSpectrum(input, _sampleRate);
                _hasNewData = false;
            }
        }

        _currentPitch = pitch;
        _currentClarity = clarity;

        if (spectrum != null)
            _lastSpectrum = spectrum;

        double fnote = FrequencyToMidi(pitch);

        // Smooth note tracking (matching the JS: note += (fnote - note) / 5)
        if (double.IsFinite(fnote) && !double.IsNaN(fnote))
        {
            if (Math.Abs(fnote - _note) < 1)
                _note += (fnote - _note) / 5.0;
            else
                _note = fnote;
        }

        // Update pitch history
        if (clarity >= 0.9)
            _historyData.Add(_note);
        else
            _historyData.Add(null);

        while (_historyData.Count > MaxHistoryLength)
            _historyData.RemoveAt(0);

        // Draw both canvases
        RenderPitchTracker();

        // Update tray tooltip with current note
        UpdateTrayTooltip();

        // Only render spectrum if window is visible (save GPU when minimized)
        if (IsVisible)
            RenderSpectrum();
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon == null || _currentClarity < 0.9) return;

        double p = _note % 12;
        if (p < 0) p += 12;
        int closestNote = (int)Math.Round(_note);
        int pitchClass = ((closestNote % 12) + 12) % 12;
        int octave = GetOctave(closestNote);
        string name = PitchClasses[pitchClass];
        string noteText = $"{name}{octave}";
        int deviation = (int)Math.Round((p - Math.Round(p)) * 100);

        _trayIcon.UpdateTooltip(noteText, _currentPitch, deviation);
    }

    /// <summary>
    /// Converts a frequency (Hz) to a MIDI note number using the current reference frequency.
    /// MIDI 69 = A4 = _referenceFrequency.
    /// </summary>
    private double FrequencyToMidi(double f)
    {
        if (f <= 0) return 0;
        return 69.0 + 12.0 * Math.Log(f / _referenceFrequency) / Math.Log(2.0);
    }

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

    private double GetY(double note) => 300 - (note * 45.0) / 2.0;

    #region Calibration Slider

    private void CalibrationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _referenceFrequency = e.NewValue;
        CalibrationValue.Text = $"{(int)_referenceFrequency} Hz";
    }

    #endregion

    #region Pitch Tracker Rendering

    private void RenderPitchTracker()
    {
        PitchCanvas.Children.Clear();

        double width = PitchCanvas.ActualWidth;
        double height = PitchCanvas.ActualHeight;
        if (width <= 0) width = 480;
        if (height <= 0) height = 320;

        double pitch = _currentPitch;
        double clarity = _currentClarity;

        // --- Draw pitch history line ---
        if (_historyData.Count > 1)
        {
            var strokes = new List<List<Point>>();
            var active = new Dictionary<int, List<Point>>();

            for (int i = 0; i < _historyData.Count; i++)
            {
                double x = 280 - (_historyData.Count - i - 1) * 3;
                var used = new HashSet<int>();

                if (_historyData[i] != null)
                {
                    for (int octave = -1; octave <= 1; octave++)
                    {
                        int octaveNumber = (int)Math.Floor(_historyData[i]!.Value / 12) + octave;
                        double y = GetY(_historyData[i]!.Value - 12 * octaveNumber);

                        if (!active.ContainsKey(octaveNumber))
                        {
                            var line = new List<Point>();
                            active[octaveNumber] = line;
                            strokes.Add(line);
                        }
                        active[octaveNumber].Add(new Point(x, y));
                        used.Add(octaveNumber);
                    }
                }

                var keysToRemove = active.Keys.Where(k => !used.Contains(k)).ToList();
                foreach (var key in keysToRemove)
                    active.Remove(key);
            }

            foreach (var stroke in strokes)
            {
                if (stroke.Count > 1)
                {
                    var points = new PointCollection(stroke.Select(p => p));
                    var polyline = new Polyline
                    {
                        Points = points,
                        Stroke = PitchLineColor,
                        StrokeThickness = 2
                    };
                    PitchCanvas.Children.Add(polyline);
                }
            }
        }

        // --- Draw grid lines and note names ---
        for (int i = 0; i <= 12; i++)
        {
            double y = GetY(i);

            var gridLine = new Line
            {
                X1 = 0, Y1 = y - 1,
                X2 = width, Y2 = y - 1,
                Stroke = GridLineColor,
                StrokeThickness = 2
            };
            PitchCanvas.Children.Add(gridLine);

            var noteNameText = new TextBlock
            {
                Text = PitchClasses[i % 12],
                FontSize = 12,
                Foreground = NoteNameBrush
            };
            Canvas.SetLeft(noteNameText, 32);
            Canvas.SetTop(noteNameText, y - 22);
            PitchCanvas.Children.Add(noteNameText);
        }

        // --- Draw active pitch indicator ---
        if (clarity >= 0.9)
        {
            double o = (clarity - 0.9) / 0.1;
            double p = _note % 12;
            if (p < 0) p += 12;

            int closestNote = (int)Math.Round(_note);
            int octave = GetOctave(closestNote);
            int pitchClass = ((closestNote % 12) + 12) % 12;
            string name = PitchClasses[pitchClass];

            // Highlight active note lines
            for (int i = 0; i < 12; i++)
            {
                double dist = p - i;
                if (dist > 6) dist -= 12;
                if (dist < -6) dist += 12;
                dist = Math.Abs(dist);
                if (dist < 0.5)
                {
                    double alpha = (1 - dist / 0.5) * o;
                    var highlightLine = new Line
                    {
                        X1 = 0, Y1 = GetY(i) - 1,
                        X2 = width, Y2 = GetY(i) - 1,
                        Stroke = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), 215, 252, 112)),
                        StrokeThickness = 2
                    };
                    PitchCanvas.Children.Add(highlightLine);
                }
            }

            // Draw pitch line and note label across octaves
            for (int i = -1; i <= 1; i++)
            {
                double y = GetY(p + 12 * i);
                double yR = GetY(Math.Round(p) + 12 * i);
                double lineAlpha = o * (1 - Math.Abs(p - Math.Round(p)) / 0.5);

                // Connection line from note position to rounded position
                if (Math.Abs(y - yR) > 1)
                {
                    var connectLine = new Line
                    {
                        X1 = 280, Y1 = Math.Min(y, yR) - 1,
                        X2 = width, Y2 = Math.Min(y, yR) - 1,
                        Stroke = new SolidColorBrush(Color.FromArgb((byte)(lineAlpha * 255), 187, 238, 255)),
                        StrokeThickness = Math.Abs(y - yR) + 2
                    };
                    PitchCanvas.Children.Add(connectLine);
                }

                // Main pitch indicator line
                var pitchLine = new Line
                {
                    X1 = 280, Y1 = y - 1,
                    X2 = width, Y2 = y - 1,
                    Stroke = PitchLineColor,
                    StrokeThickness = 2
                };
                PitchCanvas.Children.Add(pitchLine);

                // Note name label
                string noteName = $"{name}{octave + i}";
                var noteLabel = new TextBlock
                {
                    Text = noteName,
                    FontSize = 20,
                    Foreground = PitchLineColor
                };
                Canvas.SetLeft(noteLabel, 290);
                Canvas.SetTop(noteLabel, y - 28);
                PitchCanvas.Children.Add(noteLabel);

                // Cents + Hz info
                int deviation = (int)Math.Round((p - Math.Round(p)) * 100);
                string cents = deviation >= 0 ? $"+{deviation}" : $"{deviation}";
                int freq = (int)Math.Round(pitch);
                var infoLabel = new TextBlock
                {
                    Text = $"{cents} ({freq} Hz)",
                    FontSize = 12,
                    Foreground = PitchLineColor
                };
                Canvas.SetLeft(infoLabel, 290 + 30);
                Canvas.SetTop(infoLabel, y - 28);
                PitchCanvas.Children.Add(infoLabel);
            }

            NoteDisplay.Text = $"{name}{octave}";
            int deviationDisplay = (int)Math.Round((p - Math.Round(p)) * 100);
            string centsStr = deviationDisplay >= 0 ? $"+{deviationDisplay}" : $"{deviationDisplay}";
            CentsDisplay.Text = $"{centsStr} ({(int)Math.Round(pitch)} Hz)";
            NoteDisplayBottom.Text = $"{name}{octave}";
        }
        else
        {
            NoteDisplay.Text = "";
            CentsDisplay.Text = "";
            NoteDisplayBottom.Text = "";
        }
    }

    #endregion

    #region Spectrum Analyzer Rendering

    private void RenderSpectrum()
    {
        SpectrumCanvas.Children.Clear();

        double width = SpectrumCanvas.ActualWidth;
        double height = SpectrumCanvas.ActualHeight;
        if (width <= 0) width = 480;
        if (height <= 0) height = 160;

        // Reserve space for frequency labels at the bottom
        double labelHeight = 16;
        double plotHeight = height - labelHeight;

        if (_lastSpectrum == null || _spectrumAnalyzer == null) return;

        // Map spectrum to logarithmic frequency scale (20 Hz to 20 kHz)
        double minFreq = 20;
        double maxFreq = 20000;
        double logMin = Math.Log10(minFreq);
        double logMax = Math.Log10(maxFreq);

        int barCount = (int)(width / 3); // ~3px per bar
        if (barCount < 1) barCount = 1;

        double barWidth = width / barCount;
        double gap = Math.Max(barWidth * 0.15, 0.5);

        // Find peak bin for highlighting
        int peakBin = 0;
        double peakVal = 0;

        for (int bar = 0; bar < barCount; bar++)
        {
            // Map bar position to logarithmic frequency
            double logFreq = logMin + (logMax - logMin) * bar / barCount;
            double freq = Math.Pow(10, logFreq);
            int bin = _spectrumAnalyzer.FrequencyToBin(freq, _sampleRate);

            if (bin >= _lastSpectrum.Length) bin = _lastSpectrum.Length - 1;
            if (bin < 0) bin = 0;

            double magnitude = _lastSpectrum[bin];

            // Track peak
            if (magnitude > peakVal)
            {
                peakVal = magnitude;
                peakBin = bar;
            }

            // Color gradient: blue at low magnitudes, green at high
            byte r = (byte)(50 + 165 * magnitude);
            byte g = (byte)(120 + 132 * magnitude);
            byte b = (byte)(255 - 155 * magnitude);
            var barColor = new SolidColorBrush(Color.FromArgb(200, r, g, b));

            double barHeight = magnitude * plotHeight;
            if (barHeight < 1) barHeight = 1;

            var rect = new Rectangle
            {
                Width = barWidth - gap,
                Height = barHeight,
                Fill = barColor
            };
            Canvas.SetLeft(rect, bar * barWidth + gap / 2);
            Canvas.SetTop(rect, plotHeight - barHeight);
            SpectrumCanvas.Children.Add(rect);
        }

        // Highlight the peak bar
        if (peakVal > 0.1)
        {
            double logFreq = logMin + (logMax - logMin) * peakBin / barCount;
            double peakFreq = Math.Pow(10, logFreq);

            double peakBarHeight = peakVal * plotHeight;
            var peakRect = new Rectangle
            {
                Width = barWidth - gap,
                Height = peakBarHeight,
                Fill = SpectrumPeakColor
            };
            Canvas.SetLeft(peakRect, peakBin * barWidth + gap / 2);
            Canvas.SetTop(peakRect, plotHeight - peakBarHeight);
            SpectrumCanvas.Children.Add(peakRect);

            // Show peak frequency
            var peakLabel = new TextBlock
            {
                Text = $"{(int)peakFreq} Hz",
                FontSize = 10,
                Foreground = SpectrumPeakColor
            };
            Canvas.SetLeft(peakLabel, peakBin * barWidth + barWidth);
            Canvas.SetTop(peakLabel, plotHeight - peakBarHeight - 14);
            SpectrumCanvas.Children.Add(peakLabel);
        }
    }

    #endregion

    private void NotationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _notationMode = NotationSelector.SelectedIndex switch
        {
            0 => NotationMode.Roland,
            1 => NotationMode.Yamaha,
            2 => NotationMode.Cakewalk,
            _ => NotationMode.Roland,
        };
    }
}
