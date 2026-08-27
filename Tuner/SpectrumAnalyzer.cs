namespace Tuner;

/// <summary>
/// Computes FFT magnitudes from audio samples for spectrum visualization.
/// Uses a Hann window and logarithmic frequency scaling for musical analysis.
/// </summary>
public class SpectrumAnalyzer
{
    private readonly Fft _fft;
    private readonly double[] _window;
    private readonly double[] _inputBuffer;
    private readonly double[] _fftInput;
    private readonly double[] _fftOutput;

    public int SpectrumSize { get; }

    /// <summary>
    /// Creates a spectrum analyzer for the given FFT size.
    /// </summary>
    /// <param name="fftSize">FFT size (power of 2). Higher = more frequency resolution.</param>
    public SpectrumAnalyzer(int fftSize = 4096)
    {
        _fft = new Fft(fftSize);
        SpectrumSize = fftSize / 2; // We only use the positive half
        _window = new double[fftSize];
        _inputBuffer = new double[fftSize];
        _fftInput = new double[fftSize];
        _fftOutput = new double[2 * fftSize];

        // Pre-compute Hann window
        for (int i = 0; i < fftSize; i++)
            _window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));
    }

    /// <summary>
    /// Computes magnitude spectrum from time-domain samples.
    /// Returns magnitude in dB scale (normalized 0..1).
    /// </summary>
    public double[] ComputeSpectrum(double[] samples, double sampleRate)
    {
        int len = Math.Min(samples.Length, _inputBuffer.Length);

        // Copy and apply window
        for (int i = 0; i < len; i++)
            _inputBuffer[i] = samples[i] * _window[i];
        for (int i = len; i < _inputBuffer.Length; i++)
            _inputBuffer[i] = 0;

        // Compute real FFT
        _fft.RealTransform(_fftOutput, _inputBuffer);

        // Compute magnitudes (only positive frequencies)
        var magnitudes = new double[SpectrumSize];
        double maxMag = 0;

        for (int i = 0; i < SpectrumSize; i++)
        {
            double re = _fftOutput[2 * i];
            double im = _fftOutput[2 * i + 1];
            double mag = Math.Sqrt(re * re + im * im);

            // Convert to dB: 20 * log10(mag), clamped
            if (mag > 1e-10)
                magnitudes[i] = 20.0 * Math.Log10(mag);
            else
                magnitudes[i] = -100;

            if (magnitudes[i] > maxMag)
                maxMag = magnitudes[i];
        }

        // Normalize to 0..1 range relative to peak
        double floor = maxMag - 80; // 80 dB range
        for (int i = 0; i < SpectrumSize; i++)
        {
            magnitudes[i] = (magnitudes[i] - floor) / (maxMag - floor);
            if (magnitudes[i] < 0) magnitudes[i] = 0;
            if (magnitudes[i] > 1) magnitudes[i] = 1;
        }

        return magnitudes;
    }

    /// <summary>
    /// Maps a spectrum bin index to its frequency in Hz.
    /// </summary>
    public double BinToFrequency(int bin, double sampleRate)
    {
        return bin * sampleRate / (SpectrumSize * 2);
    }

    /// <summary>
    /// Maps a frequency in Hz to a spectrum bin index.
    /// </summary>
    public int FrequencyToBin(double frequency, double sampleRate)
    {
        return (int)(frequency * SpectrumSize * 2 / sampleRate);
    }
}
