namespace Tuner;

/// <summary>
/// McLeod Pitch Method (MPM) pitch detector with clean radix-2 FFT.
/// Uses double[] throughout for precision (matching JS's64-bit numbers).
/// </summary>
public sealed class PitchDetector
{
    private readonly int _inputLength;
    private readonly int _fftSize;
    private readonly double[] _fftReal;
    private readonly double[] _fftImag;
    private readonly double[] _paddedInput;
    private readonly double[] _nsdf;
    private readonly int[] _bitReversal;

    // Precomputed twiddle factors
    private readonly double[] _cosTable;
    private readonly double[] _sinTable;

    private const double ClarityThreshold = 0.9;

    public int InputLength => _inputLength;

    public PitchDetector(int inputLength)
    {
        _inputLength = inputLength;
        _fftSize = NextPowerOfTwo(2 * inputLength); // 4096 for input 2048

        _fftReal = new double[_fftSize];
        _fftImag = new double[_fftSize];
        _paddedInput = new double[_fftSize];
        _nsdf = new double[inputLength];

        // Precompute bit-reversal table
        _bitReversal = new int[_fftSize];
        int logN = 0;
        for (int t = 1; t < _fftSize; t <<= 1)
            logN++;
        for (int i = 0; i < _fftSize; i++)
        {
            int rev = 0;
            int x = i;
            for (int j = 0; j < logN; j++)
            {
                rev = (rev << 1) | (x & 1);
                x >>= 1;
            }
            _bitReversal[i] = rev;
        }

        // Precompute twiddle factors: exp(-2*pi*k/N) for forward FFT
        _cosTable = new double[_fftSize / 2];
        _sinTable = new double[_fftSize / 2];
        for (int k = 0; k < _fftSize / 2; k++)
        {
            double angle = -2.0 * Math.PI * k / _fftSize;
            _cosTable[k] = Math.Cos(angle);
            _sinTable[k] = Math.Sin(angle);
        }
    }

    /// <summary>
    /// Detect pitch from time-domain input.
    /// Returns (frequency_hz, clarity).
    /// </summary>
    public (double pitch, double clarity) FindPitch(float[] input, double sampleRate)
    {
        ComputeNsdf(input);

        // Find key maxima
        var keyMaxIndices = GetKeyMaximumIndices(_nsdf, _inputLength);
        if (keyMaxIndices.Count == 0)
            return (0, 0);

        // Find the highest key maximum
        double nMax = double.MinValue;
        foreach (int idx in keyMaxIndices)
        {
            if (_nsdf[idx] > nMax)
                nMax = _nsdf[idx];
        }

        // Find first key maximum >= 0.9 * nMax (the fundamental)
        int resultIndex = -1;
        foreach (int idx in keyMaxIndices)
        {
            if (_nsdf[idx] >= ClarityThreshold * nMax)
            {
                resultIndex = idx;
                break;
            }
        }

        if (resultIndex <= 0)
            return (0, 0);

        double clarity = Math.Clamp(_nsdf[resultIndex], 0, 1);
        return (sampleRate / resultIndex, clarity);
    }

    /// <summary>
    /// Compute NSDF from input samples.
    /// NSDF(tau) = 2 * r'(tau) / m'(tau)
    /// where r' is autocorrelation and m' is the triangular normalization.
    /// </summary>
    private void ComputeNsdf(float[] input)
    {
        // Step 1: Autocorrelation via FFT
        // Pad input into _paddedInput (double precision)
        for (int j = 0; j < _inputLength; j++)
            _paddedInput[j] = input[j];
        for (int j = _inputLength; j < _fftSize; j++)
            _paddedInput[j] = 0;

        // Forward real FFT
        RealFft(_paddedInput, _fftReal, _fftImag);

        // Power spectrum: |X[k]|^2
        for (int k = 0; k < _fftSize; k++)
        {
            _fftReal[k] = _fftReal[k] * _fftReal[k] + _fftImag[k] * _fftImag[k];
            _fftImag[k] = 0;
        }

        // Inverse FFT to get autocorrelation
        InverseFft(_fftReal, _fftImag);

        // Step 2: NSDF normalization
        // m'(0) = 2 * r'(0), then subtract edge samples incrementally
        double m = 2.0 * _fftReal[0];
        int i;
        for (i = 0; i < _inputLength && m > 0; i++)
        {
            _nsdf[i] = 2.0 * _fftReal[i] / m;
            m -= input[i] * (double)input[i] + input[_inputLength - 1 - i] * (double)input[_inputLength - 1 - i];
        }
        for (; i < _inputLength; i++)
        {
            _nsdf[i] = 0;
        }
    }

    /// <summary>
    /// Radix-2 Cooley-Tukey FFT (in-place, decimation-in-time).
    /// </summary>
    private void RealFft(double[] input, double[] real, double[] imag)
    {
        int n = _fftSize;

        // Bit-reversal permutation
        for (int i = 0; i < n; i++)
        {
            real[i] = input[_bitReversal[i]];
            imag[i] = 0;
        }

        // Butterfly stages
        for (int size = 2; size <= n; size *= 2)
        {
            int halfSize = size / 2;
            int tableStep = _fftSize / size;

            for (int i = 0; i < n; i += size)
            {
                for (int j = 0; j < halfSize; j++)
                {
                    int tableIdx = j * tableStep;
                    double wr = _cosTable[tableIdx];
                    double wi = _sinTable[tableIdx];

                    int uIdx = i + j;
                    int tIdx = i + j + halfSize;

                    double tr = real[tIdx] * wr - imag[tIdx] * wi;
                    double ti = real[tIdx] * wi + imag[tIdx] * wr;

                    real[tIdx] = real[uIdx] - tr;
                    imag[tIdx] = imag[uIdx] - ti;
                    real[uIdx] += tr;
                    imag[uIdx] += ti;
                }
            }
        }
    }

    /// <summary>
    /// Radix-2 Cooley-Tukey inverse FFT (in-place, decimation-in-time).
    /// Uses conjugate twiddle factors and divides by N at the end.
    /// </summary>
    private void InverseFft(double[] real, double[] imag)
    {
        int n = _fftSize;

        // Bit-reversal permutation
        var tmpReal = new double[n];
        var tmpImag = new double[n];
        for (int i = 0; i < n; i++)
        {
            tmpReal[i] = real[_bitReversal[i]];
            tmpImag[i] = imag[_bitReversal[i]];
        }
        Array.Copy(tmpReal, real, n);
        Array.Copy(tmpImag, imag, n);

        // Butterfly stages (conjugate twiddle factors = negate sin)
        for (int size = 2; size <= n; size *= 2)
        {
            int halfSize = size / 2;
            int tableStep = _fftSize / size;

            for (int i = 0; i < n; i += size)
            {
                for (int j = 0; j < halfSize; j++)
                {
                    int tableIdx = j * tableStep;
                    double wr = _cosTable[tableIdx];   // cos is same
                    double wi = -_sinTable[tableIdx];   // sin is negated (conjugate)

                    int uIdx = i + j;
                    int tIdx = i + j + halfSize;

                    double tr = real[tIdx] * wr - imag[tIdx] * wi;
                    double ti = real[tIdx] * wi + imag[tIdx] * wr;

                    real[tIdx] = real[uIdx] - tr;
                    imag[tIdx] = imag[uIdx] - ti;
                    real[uIdx] += tr;
                    imag[uIdx] += ti;
                }
            }
        }

        // Divide by N
        for (int i = 0; i < n; i++)
        {
            real[i] /= n;
            imag[i] /= n;
        }
    }

    /// <summary>
    /// Find key maximum indices in NSDF.
    /// A key maximum is the highest peak between a positively-sloped zero crossing
    /// and the next negatively-sloped zero crossing.
    /// </summary>
    private static List<int> GetKeyMaximumIndices(double[] nsdf, int length)
    {
        var keyIndices = new List<int>();
        bool lookingForMaximum = false;
        double max = double.MinValue;
        int maxIndex = -1;

        for (int i = 1; i < length; i++)
        {
            if (nsdf[i - 1] <= 0 && nsdf[i] > 0)
            {
                // Positively sloped zero crossing - start looking for a peak
                lookingForMaximum = true;
                maxIndex = i;
                max = nsdf[i];
            }
            else if (nsdf[i - 1] > 0 && nsdf[i] <= 0)
            {
                // Negatively sloped zero crossing - record the peak
                lookingForMaximum = false;
                if (maxIndex != -1)
                    keyIndices.Add(maxIndex);
            }
            else if (lookingForMaximum && nsdf[i] > max)
            {
                max = nsdf[i];
                maxIndex = i;
            }
        }

        return keyIndices;
    }

    private static int NextPowerOfTwo(int v)
    {
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }
}
