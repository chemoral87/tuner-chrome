namespace Tuner;

/// <summary>
/// Radix-4 FFT implementation ported from pitchy library.
/// </summary>
public class Fft
{
    public int Size { get; }
    private readonly int _csize;
    private readonly double[] _table;
    private readonly int _width;
    private readonly int[] _bitrev;
    private double[]? _out;
    private double[]? _data;
    private int _inv;

    public Fft(int size)
    {
        if (size <= 1 || (size & (size - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two and bigger than 1", nameof(size));

        Size = size;
        _csize = size << 1;

        // Build twiddle factor table
        _table = new double[size * 2];
        for (int i = 0; i < _table.Length; i += 2)
        {
            double angle = Math.PI * i / size;
            _table[i] = Math.Cos(angle);
            _table[i + 1] = -Math.Sin(angle);
        }

        // Find size's power of two
        int power = 0;
        for (int t = 1; size > t; t <<= 1)
            power++;

        // Calculate initial step width
        _width = power % 2 == 0 ? power - 1 : power;

        // Pre-compute bit-reversal patterns
        _bitrev = new int[1 << _width];
        for (int j = 0; j < _bitrev.Length; j++)
        {
            _bitrev[j] = 0;
            for (int shift = 0; shift < _width; shift += 2)
            {
                int revShift = _width - shift - 2;
                _bitrev[j] |= ((j >>> shift) & 3) << revShift;
            }
        }
    }

    public double[] CreateComplexArray()
    {
        var res = new double[_csize];
        return res;
    }

    public void RealTransform(double[] outArr, double[] data)
    {
        if (outArr == data)
            throw new ArgumentException("Input and output buffers must be different");

        _out = outArr;
        _data = data;
        _inv = 0;
        _RealTransform4();
        _out = null;
        _data = null;
    }

    public void InverseTransform(double[] outArr, double[] data)
    {
        if (outArr == data)
            throw new ArgumentException("Input and output buffers must be different");

        _out = outArr;
        _data = data;
        _inv = 1;
        _Transform4();
        for (int i = 0; i < outArr.Length; i++)
            outArr[i] /= Size;
        _out = null;
        _data = null;
    }

    public void CompleteSpectrum(double[] spectrum)
    {
        int size = _csize;
        int half = size >>> 1;
        for (int i = 2; i < half; i += 2)
        {
            spectrum[size - i] = spectrum[i];
            spectrum[size - i + 1] = -spectrum[i + 1];
        }
    }

    private void _Transform4()
    {
        var outArr = _out!;
        int size = _csize;
        int width = _width;
        int step = 1 << width;
        int len = (size / step) << 1;

        int outOff;
        int t;
        if (len == 4)
        {
            for (outOff = 0, t = 0; outOff < size; outOff += len, t++)
            {
                int off = _bitrev[t];
                _SingleTransform2(outOff, off, step);
            }
        }
        else
        {
            for (outOff = 0, t = 0; outOff < size; outOff += len, t++)
            {
                int off = _bitrev[t];
                _SingleTransform4(outOff, off, step);
            }
        }

        int inv = _inv == 1 ? -1 : 1;
        var table = _table;
        for (step >>= 2; step >= 2; step >>= 2)
        {
            len = (size / step) << 1;
            int quarterLen = len >>> 2;

            for (outOff = 0; outOff < size; outOff += len)
            {
                int limit = outOff + quarterLen;
                for (int i = outOff, k = 0; i < limit; i += 2, k += step)
                {
                    int A = i;
                    int B = A + quarterLen;
                    int C = B + quarterLen;
                    int D = C + quarterLen;

                    double Ar = outArr[A], Ai = outArr[A + 1];
                    double Br = outArr[B], Bi = outArr[B + 1];
                    double Cr = outArr[C], Ci = outArr[C + 1];
                    double Dr = outArr[D], Di = outArr[D + 1];

                    double MAr = Ar, MAi = Ai;
                    double tableBr = table[k], tableBi = inv * table[k + 1];
                    double MBr = Br * tableBr - Bi * tableBi;
                    double MBi = Br * tableBi + Bi * tableBr;

                    double tableCr = table[2 * k], tableCi = inv * table[2 * k + 1];
                    double MCr = Cr * tableCr - Ci * tableCi;
                    double MCi = Cr * tableCi + Ci * tableCr;

                    double tableDr = table[3 * k], tableDi = inv * table[3 * k + 1];
                    double MDr = Dr * tableDr - Di * tableDi;
                    double MDi = Dr * tableDi + Di * tableDr;

                    double T0r = MAr + MCr, T0i = MAi + MCi;
                    double T1r = MAr - MCr, T1i = MAi - MCi;
                    double T2r = MBr + MDr, T2i = MBi + MDi;
                    double T3r = inv * (MBr - MDr), T3i = inv * (MBi - MDi);

                    outArr[A] = T0r + T2r;
                    outArr[A + 1] = T0i + T2i;
                    outArr[C] = T0r - T2r;
                    outArr[C + 1] = T0i - T2i;
                    outArr[B] = T1r + T3i;
                    outArr[B + 1] = T1i - T3r;
                    outArr[D] = T1r - T3i;
                    outArr[D + 1] = T1i + T3r;
                }
            }
        }
    }

    private void _SingleTransform2(int outOff, int off, int step)
    {
        var outArr = _out!;
        var data = _data!;

        double evenR = data[off], evenI = data[off + 1];
        double oddR = data[off + step], oddI = data[off + step + 1];

        outArr[outOff] = evenR + oddR;
        outArr[outOff + 1] = evenI + oddI;
        outArr[outOff + 2] = evenR - oddR;
        outArr[outOff + 3] = evenI - oddI;
    }

    private void _SingleTransform4(int outOff, int off, int step)
    {
        var outArr = _out!;
        var data = _data!;
        int inv = _inv == 1 ? -1 : 1;
        int step2 = step * 2, step3 = step * 3;

        double Ar = data[off], Ai = data[off + 1];
        double Br = data[off + step], Bi = data[off + step + 1];
        double Cr = data[off + step2], Ci = data[off + step2 + 1];
        double Dr = data[off + step3], Di = data[off + step3 + 1];

        double T0r = Ar + Cr, T0i = Ai + Ci;
        double T1r = Ar - Cr, T1i = Ai - Ci;
        double T2r = Br + Dr, T2i = Bi + Di;
        double T3r = inv * (Br - Dr), T3i = inv * (Bi - Di);

        outArr[outOff] = T0r + T2r;
        outArr[outOff + 1] = T0i + T2i;
        outArr[outOff + 2] = T1r + T3i;
        outArr[outOff + 3] = T1i - T3r;
        outArr[outOff + 4] = T0r - T2r;
        outArr[outOff + 5] = T0i - T2i;
        outArr[outOff + 6] = T1r - T3i;
        outArr[outOff + 7] = T1i + T3r;
    }

    private void _RealTransform4()
    {
        var outArr = _out!;
        int size = _csize;
        int width = _width;
        int step = 1 << width;
        int len = (size / step) << 1;

        int outOff, t;
        if (len == 4)
        {
            for (outOff = 0, t = 0; outOff < size; outOff += len, t++)
            {
                int off = _bitrev[t];
                _SingleRealTransform2(outOff, off >>> 1, step >>> 1);
            }
        }
        else
        {
            for (outOff = 0, t = 0; outOff < size; outOff += len, t++)
            {
                int off = _bitrev[t];
                _SingleRealTransform4(outOff, off >>> 1, step >>> 1);
            }
        }

        int inv = _inv == 1 ? -1 : 1;
        var table = _table;
        for (step >>= 2; step >= 2; step >>= 2)
        {
            len = (size / step) << 1;
            int halfLen = len >>> 1;
            int quarterLen = halfLen >>> 1;
            int hquarterLen = quarterLen >>> 1;

            for (outOff = 0; outOff < size; outOff += len)
            {
                for (int i = 0, k = 0; i <= hquarterLen; i += 2, k += step)
                {
                    int A = outOff + i;
                    int B = A + quarterLen;
                    int C = B + quarterLen;
                    int D = C + quarterLen;

                    double Ar = outArr[A], Ai = outArr[A + 1];
                    double Br = outArr[B], Bi = outArr[B + 1];
                    double Cr = outArr[C], Ci = outArr[C + 1];
                    double Dr = outArr[D], Di = outArr[D + 1];

                    double MAr = Ar, MAi = Ai;
                    double tableBr = table[k], tableBi = inv * table[k + 1];
                    double MBr = Br * tableBr - Bi * tableBi;
                    double MBi = Br * tableBi + Bi * tableBr;

                    double tableCr = table[2 * k], tableCi = inv * table[2 * k + 1];
                    double MCr = Cr * tableCr - Ci * tableCi;
                    double MCi = Cr * tableCi + Ci * tableCr;

                    double tableDr = table[3 * k], tableDi = inv * table[3 * k + 1];
                    double MDr = Dr * tableDr - Di * tableDi;
                    double MDi = Dr * tableDi + Di * tableDr;

                    double T0r = MAr + MCr, T0i = MAi + MCi;
                    double T1r = MAr - MCr, T1i = MAi - MCi;
                    double T2r = MBr + MDr, T2i = MBi + MDi;
                    double T3r = inv * (MBr - MDr), T3i = inv * (MBi - MDi);

                    outArr[A] = T0r + T2r;
                    outArr[A + 1] = T0i + T2i;
                    outArr[B] = T1r + T3i;
                    outArr[B + 1] = T1i - T3r;

                    if (i == 0)
                    {
                        outArr[C] = T0r - T2r;
                        outArr[C + 1] = T0i - T2i;
                        continue;
                    }
                    if (i == hquarterLen)
                        continue;

                    double ST0r = T1r, ST0i = -T1i;
                    double ST1r = T0r, ST1i = -T0i;
                    double ST2r = -inv * T3i, ST2i = -inv * T3r;
                    double ST3r = -inv * T2i, ST3i = -inv * T2r;

                    int SA = outOff + quarterLen - i;
                    int SB = outOff + halfLen - i;

                    outArr[SA] = ST0r + ST2r;
                    outArr[SA + 1] = ST0i + ST2i;
                    outArr[SB] = ST1r + ST3i;
                    outArr[SB + 1] = ST1i - ST3r;
                }
            }
        }
    }

    private void _SingleRealTransform2(int outOff, int off, int step)
    {
        var outArr = _out!;
        var data = _data!;

        double evenR = data[off];
        double oddR = data[off + step];

        outArr[outOff] = evenR + oddR;
        outArr[outOff + 1] = 0;
        outArr[outOff + 2] = evenR - oddR;
        outArr[outOff + 3] = 0;
    }

    private void _SingleRealTransform4(int outOff, int off, int step)
    {
        var outArr = _out!;
        var data = _data!;
        int inv = _inv == 1 ? -1 : 1;
        int step2 = step * 2, step3 = step * 3;

        double Ar = data[off];
        double Br = data[off + step];
        double Cr = data[off + step2];
        double Dr = data[off + step3];

        double T0r = Ar + Cr;
        double T1r = Ar - Cr;
        double T2r = Br + Dr;
        double T3r = inv * (Br - Dr);

        outArr[outOff] = T0r + T2r;
        outArr[outOff + 1] = 0;
        outArr[outOff + 2] = T1r;
        outArr[outOff + 3] = -T3r;
        outArr[outOff + 4] = T0r - T2r;
        outArr[outOff + 5] = 0;
        outArr[outOff + 6] = T1r;
        outArr[outOff + 7] = T3r;
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
