using System.Globalization;
using System.Numerics;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>
/// CapCut's layout (port of the macOS FrameCompositor's placement), in Direct2D coordinates: origin at the
/// top-left, y down, so a positive angle turns clockwise on screen.
///
/// Each visual is first fitted inside the canvas (aspect fit), then scaled by the clip scale, rotated clockwise
/// and moved by the clip position (half-canvas units, +y up). Text images are rendered at <c>textScale</c>
/// times their canvas size and placed at 1 / textScale.
/// </summary>
public static class Layout
{
    /// <summary>Maps image pixels (w×h) onto the output (W×H) for a layer's transform.</summary>
    public static Matrix3x2 Placement(LayerTransform t, float w, float h, float outW, float outH, float? textScale = null)
    {
        var fit = textScale is { } s ? 1 / s : Math.Min(outW / w, outH / h);
        var sx = fit * (float)t.ScaleX * (t.FlipH ? -1 : 1);
        var sy = fit * (float)t.ScaleY * (t.FlipV ? -1 : 1);
        return Matrix3x2.CreateTranslation(-w / 2, -h / 2)
            * Matrix3x2.CreateScale(sx, sy)
            * Matrix3x2.CreateRotation((float)(t.Rotation * Math.PI / 180))
            * Matrix3x2.CreateTranslation(outW / 2 * (1 + (float)t.PositionX), outH / 2 * (1 - (float)t.PositionY));
    }

    /// <summary>The cropped source rectangle (left, top, right, bottom) in pixels, or null when the crop is a no-op.</summary>
    public static (float Left, float Top, float Right, float Bottom)? CropRect(Crop crop, float w, float h)
    {
        if (crop.IsIdentity) return null;
        float left = (float)crop.UpperLeft.X * w, top = (float)crop.UpperLeft.Y * h;
        float right = (float)crop.LowerRight.X * w, bottom = (float)crop.LowerRight.Y * h;
        return right - left >= 1 && bottom - top >= 1 ? (left, top, right, bottom) : null;
    }

    /// <summary>Scale and offset that make an image (w×h) cover the output, centered (canvas blur and image backgrounds).</summary>
    public static Matrix3x2 Cover(float w, float h, float outW, float outH)
    {
        var fill = Math.Max(outW / w, outH / h);
        return Matrix3x2.CreateScale(fill) * Matrix3x2.CreateTranslation((outW - w * fill) / 2, (outH - h * fill) / 2);
    }

    /// <summary>Gaussian blur radius of CapCut's "canvas blur" background (as calibrated on the Mac).</summary>
    public static float CanvasBlurRadius(double blur, float outW, float outH) =>
        (float)Math.Max(blur, 0.05) * Math.Min(outW, outH) * 0.08f;

    /// <summary>
    /// Text is rasterized once per clip at the largest scale it reaches (clip scale or keyframes, 1…8),
    /// so it stays sharp.
    /// </summary>
    public static float TextRasterScale(VisualLayer layer)
    {
        var max = Math.Max(Math.Abs(layer.Clip.Scale.X), Math.Abs(layer.Clip.Scale.Y));
        foreach (var p in new[] { LayerProperty.ScaleX, LayerProperty.ScaleY })
        {
            if (layer.Keyframes.TryGetValue(p, out var track) && track.Values.Count > 0)
                max = Math.Max(max, track.Values.Max(Math.Abs));
        }
        return (float)Math.Clamp(max, 1, 8);
    }

    /// <summary>A nested (compound clip) canvas rendered at the same pixel density as its parent.</summary>
    public static (int Width, int Height) NestedSize(Timeline parent, Timeline inner, int outW)
    {
        var k = outW / (double)Math.Max(parent.CanvasWidth, 1);
        return ((int)Math.Max(1, Math.Round(inner.CanvasWidth * k)), (int)Math.Max(1, Math.Round(inner.CanvasHeight * k)));
    }
}

/// <summary>An sRGB colour with straight alpha, 0…1.</summary>
public readonly record struct Rgba(float R, float G, float B, float A)
{
    public static Rgba FromRgb(IReadOnlyList<double> c, double alpha) =>
        new((float)(c.Count > 0 ? c[0] : 1), (float)(c.Count > 1 ? c[1] : 1), (float)(c.Count > 2 ? c[2] : 1), (float)alpha);

    /// <summary>"#RRGGBB" or "#AARRGGBB" (CapCut uses both); unreadable values are white.</summary>
    public static Rgba FromHex(string hex, double alpha = 1)
    {
        var s = hex.Trim().TrimStart('#');
        if (!ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return new(1, 1, 1, (float)alpha);
        double a = 1;
        var rgb = v;
        if (s.Length == 8) { a = ((v >> 24) & 0xFF) / 255.0; rgb = v & 0xFFFFFF; }
        return new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, (float)(a * alpha));
    }
}

/// <summary>
/// Text calibration measured on the author's CapCut exports (the same constants as the macOS TextRasterizer).
/// </summary>
public static class TextCalibration
{
    /// <summary>A style <c>size</c> of 1 equals <c>canvasWidth / 155</c> px of font size for fonts picked in CapCut…</summary>
    public const float SizeDivisor = 155;
    /// <summary>…but <c>canvasWidth / 172</c> for CapCut's default <c>SystemFont/en.ttf</c>.</summary>
    public const float DefaultFontSizeDivisor = 172;
    /// <summary>Lines are spaced by about one font size (plus <c>line_spacing</c>), tighter than the fonts' metrics.</summary>
    public const float LineHeight = 0.97f;
    /// <summary>CapCut wraps a little before <c>line_max_width</c>: for 0.82 its limit measured 0.786–0.817; we use 0.80.</summary>
    public const float WrapFactor = 0.80f / 0.82f;
    /// <summary>Visible outline thickness per unit of CapCut's stroke <c>width</c>.</summary>
    public const float StrokeScale = 0.75f;

    /// <summary>Pixels of font size per unit of CapCut's <c>size</c> for a font file.</summary>
    public static float Unit(string? fontPath, string materialFontPath, float canvasWidth, float scale)
    {
        var path = fontPath ?? materialFontPath;
        var isDefault = path.Length == 0 || path.Replace('\\', '/').Contains("/Font/SystemFont/");
        return canvasWidth / (isDefault ? DefaultFontSizeDivisor : SizeDivisor) * scale;
    }
}

/// <summary>Frame timing of the export, exact for NTSC rates (port of the macOS Exporter.frameDuration).</summary>
public readonly record struct FrameClock(long Numerator, long Denominator)
{
    public static FrameClock For(double fps)
    {
        foreach (var (rate, num, den) in new[] { (23.976, 1001L, 24000L), (29.97, 1001L, 30000L), (59.94, 1001L, 60000L) })
        {
            if (Math.Abs(fps - rate) < 0.01) return new(num, den);
        }
        var rounded = Math.Round(fps);
        return Math.Abs(fps - rounded) < 0.001 ? new(1, (long)rounded) : new(1000, (long)Math.Round(fps * 1000));
    }

    /// <summary>Frames per second as a ratio (denominator / numerator).</summary>
    public double Fps => (double)Denominator / Numerator;

    /// <summary>Start of frame <paramref name="index"/> in µs.</summary>
    public long Microseconds(long index) => index * Numerator * 1_000_000 / Denominator;

    /// <summary>Start of frame <paramref name="index"/> in 100 ns units (Media Foundation time).</summary>
    public long Ticks(long index) => index * Numerator * 10_000_000 / Denominator;

    public int FrameCount(long durationUs) => (int)Math.Max(1, Math.Floor(durationUs / 1_000_000.0 * Fps));
}
