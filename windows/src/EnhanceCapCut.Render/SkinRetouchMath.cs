namespace EnhanceCapCut.Render;

/// <summary>
/// CapCut's automatic skin retouch ("Sparkly Skin" and the smoothing that comes with it; see FORMAT.md › Retouch and
/// makeup), approximated (port of the macOS SkinRetouchMath, on sRGB values 0…1): skin-coloured pixels are blended
/// with a blurred copy of the frame. Skin is found by colour only (BT.601 Y′CbCr bands), baked into a 3D lookup
/// table. Calibrated on TwoHoursGay: CapCut's export keeps the colours and lowers the face's fine detail (high-pass
/// deviation) by about a quarter relative to the rest of the frame; ours by ~17 %, as strong as it goes before the eyes
/// blur.
/// </summary>
public static class SkinRetouchMath
{
    /// <summary>Points per side of the lookup table.</summary>
    public const int CubeSize = 32;
    /// <summary>How much of the blurred copy replaces a skin pixel.</summary>
    public const double Strength = 0.5;
    /// <summary>Blur sigma, as a fraction of the frame's shorter side.</summary>
    public const double BlurRadius = 0.0025;
    /// <summary>Skin's Cr (0.713·(R − Y′)): soft lower and upper bands.</summary>
    static readonly (double Low, double LowFull, double HighFull, double High) CrRange = (0.02, 0.05, 0.17, 0.22);
    /// <summary>Skin's −Cb (0.564·(Y′ − B)): above grey, below saturated oranges.</summary>
    static readonly (double Low, double LowFull, double HighFull, double High) BlueDeficitRange = (0.0, 0.02, 0.16, 0.22);
    /// <summary>Y′: very dark and blown-out pixels are left alone.</summary>
    static readonly (double Low, double LowFull, double HighFull, double High) LumaRange = (0.08, 0.15, 0.92, 0.98);

    static double Smoothstep(double lo, double hi, double x)
    {
        var t = Math.Clamp((x - lo) / Math.Max(hi - lo, 1e-9), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static double Band(double x, (double Low, double LowFull, double HighFull, double High) r) =>
        Smoothstep(r.Low, r.LowFull, x) * (1 - Smoothstep(r.HighFull, r.High, x));

    /// <summary>How much a pixel is smoothed, 0…<see cref="Strength"/>.</summary>
    public static double Weight(double r, double g, double b)
    {
        var y = 0.299 * r + 0.587 * g + 0.114 * b;
        double cb = 0.564 * (b - y), cr = 0.713 * (r - y);
        return Strength * Band(cr, CrRange) * Band(-cb, BlueDeficitRange) * Band(y, LumaRange);
    }

    /// <summary>
    /// RGBA floats for Direct2D's 3D lookup table, blue changing fastest and red slowest: the pixel's weight as a grey,
    /// opaque (the LookupTable3D effect keeps the input's alpha, so the weight is moved to alpha afterwards).
    /// </summary>
    public static float[] MatteTable(int size = CubeSize)
    {
        var data = new float[size * size * size * 4];
        var step = 1.0 / (size - 1);
        var i = 0;
        for (var r = 0; r < size; r++)
        for (var g = 0; g < size; g++)
        for (var b = 0; b < size; b++)
        {
            var w = (float)Weight(r * step, g * step, b * step);
            data[i] = w; data[i + 1] = w; data[i + 2] = w; data[i + 3] = 1;
            i += 4;
        }
        return data;
    }
}
