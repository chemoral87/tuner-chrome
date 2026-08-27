namespace Tuner;

/// <summary>
/// Detects pitch using the McLeod Pitch Method (MPM).
/// Ported from pitchy library's PitchDetector class.
/// </summary>
public class PitchDetector
{
    private readonly Autocorrelator _autocorrelator;
    private readonly double[] _nsdfBuffer;
    private readonly double _clarityThreshold = 0.9;

    public int InputLength => _autocorrelator.InputLength;

    public PitchDetector(int inputLength)
    {
        _autocorrelator = new Autocorrelator(inputLength);
        _nsdfBuffer = new double[inputLength];
    }

    /// <summary>
    /// Returns the detected pitch in Hz and clarity (0..1).
    /// </summary>
    public (double pitch, double clarity) FindPitch(double[] input, double sampleRate)
    {
        ComputeNsdf(input);

        var keyMaximumIndices = GetKeyMaximumIndices(_nsdfBuffer);

        if (keyMaximumIndices.Count == 0)
            return (0, 0);

        double nMax = keyMaximumIndices.Max(i => _nsdfBuffer[i]);

        int? resultIndex = null;
        foreach (int i in keyMaximumIndices)
        {
            if (_nsdfBuffer[i] >= _clarityThreshold * nMax)
            {
                resultIndex = i;
                break;
            }
        }

        if (resultIndex == null)
            return (0, 0);

        double clarity = Math.Min(_nsdfBuffer[resultIndex.Value], 1.0);
        return (sampleRate / resultIndex.Value, clarity);
    }

    private void ComputeNsdf(double[] input)
    {
        _autocorrelator.Autocorrelate(input, _nsdfBuffer);

        double m = 2 * _nsdfBuffer[0];
        int i;
        for (i = 0; i < _nsdfBuffer.Length && m > 0; i++)
        {
            _nsdfBuffer[i] = 2 * _nsdfBuffer[i] / m;
            m -= Math.Pow(input[i], 2) + Math.Pow(input[input.Length - i - 1], 2);
        }

        for (; i < _nsdfBuffer.Length; i++)
            _nsdfBuffer[i] = 0;
    }

    private static List<int> GetKeyMaximumIndices(double[] input)
    {
        var keyIndices = new List<int>();
        bool lookingForMaximum = false;
        double max = double.MinValue;
        int maxIndex = -1;

        for (int i = 1; i < input.Length; i++)
        {
            if (input[i - 1] <= 0 && input[i] > 0)
            {
                lookingForMaximum = true;
                maxIndex = i;
                max = input[i];
            }
            else if (input[i - 1] > 0 && input[i] <= 0)
            {
                lookingForMaximum = false;
                if (maxIndex != -1)
                    keyIndices.Add(maxIndex);
            }
            else if (lookingForMaximum && input[i] > max)
            {
                max = input[i];
                maxIndex = i;
            }
        }

        return keyIndices;
    }
}
