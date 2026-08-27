namespace Tuner;

/// <summary>
/// Performs autocorrelation on input arrays using FFT.
/// Ported from pitchy library's Autocorrelator class.
/// </summary>
public class Autocorrelator
{
    private readonly int _inputLength;
    private readonly Fft _fft;
    private readonly double[] _paddedInputBuffer;
    private readonly double[] _transformBuffer;
    private readonly double[] _inverseBuffer;

    public int InputLength => _inputLength;

    public Autocorrelator(int inputLength)
    {
        if (inputLength < 1)
            throw new ArgumentException("Input length must be at least one", nameof(inputLength));

        _inputLength = inputLength;
        int fftSize = NextPow2(2 * inputLength);
        _fft = new Fft(fftSize);
        _paddedInputBuffer = new double[_fft.Size];
        _transformBuffer = new double[2 * _fft.Size];
        _inverseBuffer = new double[2 * _fft.Size];
    }

    public double[] Autocorrelate(double[] input, double[]? output = null)
    {
        output ??= new double[input.Length];

        if (input.Length != _inputLength)
            throw new ArgumentException($"Input must have length {_inputLength} but had length {input.Length}");

        // Step 0: pad the input array with zeros
        for (int i = 0; i < input.Length; i++)
            _paddedInputBuffer[i] = input[i];
        for (int i = input.Length; i < _paddedInputBuffer.Length; i++)
            _paddedInputBuffer[i] = 0;

        // Step 1: get the DFT of the input array
        _fft.RealTransform(_transformBuffer, _paddedInputBuffer);
        _fft.CompleteSpectrum(_transformBuffer);

        // Step 2: multiply each entry by its conjugate
        for (int i = 0; i < _transformBuffer.Length; i += 2)
        {
            _transformBuffer[i] = _transformBuffer[i] * _transformBuffer[i] + _transformBuffer[i + 1] * _transformBuffer[i + 1];
            _transformBuffer[i + 1] = 0;
        }

        // Step 3: perform the inverse transform
        _fft.InverseTransform(_inverseBuffer, _transformBuffer);

        for (int i = 0; i < input.Length; i++)
            output[i] = _inverseBuffer[2 * i];

        return output;
    }

    private static int NextPow2(int v)
    {
        v += v == 0 ? 1 : 0;
        --v;
        v |= v >>> 1;
        v |= v >>> 2;
        v |= v >>> 4;
        v |= v >>> 8;
        v |= v >>> 16;
        return v + 1;
    }
}
