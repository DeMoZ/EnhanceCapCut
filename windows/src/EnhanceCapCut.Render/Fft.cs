namespace EnhanceCapCut.Render;

/// <summary>
/// Mixed-radix complex FFT (recursive Cooley–Tukey over the factors 4, 2, 3, 5 and any other primes) for the voice
/// processing: 2048 points for the spectral gates, 960 for the voice isolation model. Unnormalized in both
/// directions (the inverse of the forward transform is <c>n ×</c> the input).
/// </summary>
public sealed class Fft
{
    public int Size { get; }
    readonly int[] factors;
    readonly double[] cos, sin;
    readonly double[] tmpRe, tmpIm;

    public Fft(int size)
    {
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        var list = new List<int>();
        var n = size;
        foreach (var p in new[] { 4, 2, 3, 5 })
        {
            while (n % p == 0) { list.Add(p); n /= p; }
        }
        for (var p = 7; n > 1; p += 2)
        {
            while (n % p == 0) { list.Add(p); n /= p; }
        }
        factors = [.. list];
        cos = new double[size];
        sin = new double[size];
        for (var i = 0; i < size; i++)
        {
            cos[i] = Math.Cos(2 * Math.PI * i / size);
            sin[i] = Math.Sin(2 * Math.PI * i / size);
        }
        var largest = factors.Length > 0 ? factors.Max() : 1;
        tmpRe = new double[largest];
        tmpIm = new double[largest];
    }

    /// <summary>Transforms <paramref name="inRe"/>/<paramref name="inIm"/> into <paramref name="outRe"/>/<paramref name="outIm"/> (separate arrays).</summary>
    public void Transform(double[] inRe, double[] inIm, double[] outRe, double[] outIm, bool inverse = false) =>
        Recurse(inRe, inIm, outRe, outIm, Size, 1, 0, 0, 0, inverse ? 1 : -1);

    void Recurse(double[] xr, double[] xi, double[] yr, double[] yi, int n, int stride, int inOff, int outOff, int fi, int sign)
    {
        if (n == 1)
        {
            yr[outOff] = xr[inOff];
            yi[outOff] = xi[inOff];
            return;
        }
        var p = factors[fi];
        var m = n / p;
        for (var j = 0; j < p; j++) Recurse(xr, xi, yr, yi, m, stride * p, inOff + j * stride, outOff + j * m, fi + 1, sign);
        // Twiddles of this level are W_n^e = W_Size^(e·step).
        var step = Size / n;
        for (var k = 0; k < m; k++)
        {
            // Twiddled inputs t_j = y[j·m + k] · W_n^(j·k).
            for (var j = 0; j < p; j++)
            {
                double a = yr[outOff + j * m + k], b = yi[outOff + j * m + k];
                var e = j * k * step % Size;
                double c = cos[e], s = sign * sin[e];
                tmpRe[j] = a * c - b * s;
                tmpIm[j] = a * s + b * c;
            }
            // p-point DFT of the twiddled inputs.
            for (var q = 0; q < p; q++)
            {
                double sr = 0, si = 0;
                for (var j = 0; j < p; j++)
                {
                    var e = j * q * m * step % Size;
                    double c = cos[e], s = sign * sin[e];
                    sr += tmpRe[j] * c - tmpIm[j] * s;
                    si += tmpRe[j] * s + tmpIm[j] * c;
                }
                yr[outOff + q * m + k] = sr;
                yi[outOff + q * m + k] = si;
            }
        }
    }
}
