using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>
/// The "Chroma key" colour functions (port of the macOS ChromaKeyMath; see FORMAT.md › Chroma key), on sRGB values
/// 0…1. Our own keyer, not CapCut's: pixels are compared with the picked colour in Y′CbCr (BT.601). Both functions
/// are baked into 3D lookup tables once per clip; the feather is a blur of the matte. The constants are not calibrated
/// against a CapCut export (none of the author's projects use the feature yet).
/// </summary>
public static class ChromaKeyMath
{
    /// <summary>Points per side of the lookup tables.</summary>
    public const int CubeSize = 64;
    /// <summary>Distance from the key colour (in CbCr, plus weighted Y′) that is fully removed at "Intensity" 1.</summary>
    public const double KeyRange = 0.30;
    /// <summary>Width of the soft band between removed and kept, in the same distance.</summary>
    public const double KeySoftness = 0.06;
    /// <summary>Y′ below which pixels stay opaque at "Shadow" 1 (shadows cast on the screen are kept).</summary>
    public const double ShadowRange = 0.35;
    /// <summary>Width of the soft band above that Y′.</summary>
    public const double ShadowSoftness = 0.08;
    /// <summary>Matte blur sigma at "Edge feather" 1, as a fraction of the frame's shorter side.</summary>
    public const double EdgeSmoothRadius = 0.006;
    /// <summary>Key colours with less CbCr than this are keyed mostly by brightness (white, grey and black screens).</summary>
    public const double NeutralChroma = 0.02;
    public const double SaturatedChroma = 0.12;
    /// <summary>How much a Y′ difference counts for a saturated key: light and shadow on a green screen are still screen.</summary>
    public const double SaturatedLumaWeight = 0.2;

    static (double Y, double Cb, double Cr) Ycc(double r, double g, double b)
    {
        var y = 0.299 * r + 0.587 * g + 0.114 * b;
        return (y, 0.564 * (b - y), 0.713 * (r - y));
    }

    static double Smoothstep(double lo, double hi, double x)
    {
        var t = Math.Clamp((x - lo) / Math.Max(hi - lo, 1e-9), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Opacity of a pixel: 0 = the key colour (removed), 1 = kept.</summary>
    public static double Alpha(ChromaKey key, double r, double g, double b)
    {
        if (key.Intensity <= 0) return 1;
        var k = Ycc(key.Red, key.Green, key.Blue);
        var p = Ycc(r, g, b);
        var keyChroma = Math.Sqrt(k.Cb * k.Cb + k.Cr * k.Cr);
        var lumaWeight = 1 - (1 - SaturatedLumaWeight) * Smoothstep(NeutralChroma, SaturatedChroma, keyChroma);
        double dcb = p.Cb - k.Cb, dcr = p.Cr - k.Cr, dy = lumaWeight * (p.Y - k.Y);
        var distance = Math.Sqrt(dcb * dcb + dcr * dcr + dy * dy);
        var removed = KeyRange * key.Intensity;
        var a = Smoothstep(removed, removed + KeySoftness, distance);
        if (key.Shadow > 0)
        {
            var dark = ShadowRange * key.Shadow;
            a = Math.Max(a, 1 - Smoothstep(dark, dark + ShadowSoftness, p.Y));
        }
        return a;
    }

    /// <summary>The pixel with the key colour's cast taken out ("Spill"): its CbCr along the key's hue is reduced.</summary>
    public static (double R, double G, double B) Despill(ChromaKey key, double r, double g, double b)
    {
        var k = Ycc(key.Red, key.Green, key.Blue);
        var keyChroma = Math.Sqrt(k.Cb * k.Cb + k.Cr * k.Cr);
        if (key.Spill <= 0 || keyChroma < NeutralChroma) return (r, g, b);
        var p = Ycc(r, g, b);
        double ux = k.Cb / keyChroma, uy = k.Cr / keyChroma;
        var along = p.Cb * ux + p.Cr * uy;
        if (along <= 0) return (r, g, b);
        var cb = p.Cb - ux * along * key.Spill;
        var cr = p.Cr - uy * along * key.Spill;
        double r2 = p.Y + cr / 0.713, b2 = p.Y + cb / 0.564;
        var g2 = (p.Y - 0.299 * r2 - 0.114 * b2) / 0.587;
        return (Math.Clamp(r2, 0, 1), Math.Clamp(g2, 0, 1), Math.Clamp(b2, 0, 1));
    }

    /// <summary>
    /// RGBA floats for Direct2D's 3D lookup table, blue changing fastest and red slowest: the pixel's opacity as a grey,
    /// opaque (the LookupTable3D effect keeps the input's alpha, so the opacity is moved to alpha afterwards).
    /// </summary>
    public static float[] MatteTable(ChromaKey key, int size = CubeSize) =>
        Table(size, (r, g, b) => { var a = (float)Alpha(key, r, g, b); return (a, a, a, 1); });

    /// <summary>Straight RGBA floats, blue changing fastest: the despilled colour, opaque.</summary>
    public static float[] ColorTable(ChromaKey key, int size = CubeSize) =>
        Table(size, (r, g, b) => { var c = Despill(key, r, g, b); return ((float)c.R, (float)c.G, (float)c.B, 1); });

    static float[] Table(int size, Func<double, double, double, (float, float, float, float)> f)
    {
        var data = new float[size * size * size * 4];
        var step = 1.0 / (size - 1);
        var i = 0;
        for (var r = 0; r < size; r++)
        for (var g = 0; g < size; g++)
        for (var b = 0; b < size; b++)
        {
            var (x, y, z, w) = f(r * step, g * step, b * step);
            data[i] = x; data[i + 1] = y; data[i + 2] = z; data[i + 3] = w;
            i += 4;
        }
        return data;
    }
}
