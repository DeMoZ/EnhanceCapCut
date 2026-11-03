using System.Globalization;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>
/// A filter's lookup table as RGBA float entries (red fastest, then green, then blue), with the filter's intensity
/// baked in: the table is blended with the identity, which equals blending the filtered frame with the original.
/// The PNG lookup images are decoded by the platform (WIC on Windows) and passed to <see cref="FromPixels"/>.
/// </summary>
public sealed class ColorLut(int size, float[] data)
{
    public int Size { get; } = size;
    /// <summary>N³ × RGBA.</summary>
    public float[] Data { get; } = data;

    /// <summary>Entries as RGB triples in 0…1 (red fastest) → RGBA entries at <paramref name="intensity"/>.</summary>
    public static ColorLut Blend(int n, float[] rgb, double intensity)
    {
        var a = (float)Math.Clamp(intensity, 0, 1);
        var data = new float[n * n * n * 4];
        for (var b = 0; b < n; b++)
        for (var g = 0; g < n; g++)
        for (var r = 0; r < n; r++)
        {
            var i = (b * n + g) * n + r;
            float[] id = [r / (float)(n - 1), g / (float)(n - 1), b / (float)(n - 1)];
            for (var c = 0; c < 3; c++) data[i * 4 + c] = id[c] + (rgb[i * 3 + c] - id[c]) * a;
            data[i * 4 + 3] = 1;
        }
        return new ColorLut(n, data);
    }

    /// <summary>An Adobe/Resolve <c>.cube</c> 3D table (<c>LUT_3D_SIZE N</c>, then N³ lines of <c>r g b</c>, red fastest).</summary>
    public static (int Size, float[] Rgb)? ParseCube(string text)
    {
        var n = 0;
        var rgb = new List<float>();
        foreach (var line in text.Split('\n', '\r'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].StartsWith('#')) continue;
            if (parts[0] == "LUT_3D_SIZE" && parts.Length == 2) { n = int.TryParse(parts[1], out var size) ? size : 0; continue; }
            if (parts.Length == 3
                && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                rgb.AddRange([r, g, b]);
            }
        }
        if (n < 2 || n > ColorFilter.MaxSize || rgb.Count != n * n * n * 3) return null;
        return (n, rgb.ToArray());
    }

    /// <summary>The table in 8-bit pixels (top row first, <paramref name="bytesPerPixel"/> apart, RGB or BGR order)
    /// laid out as the image size says; null when the size isn't a lookup layout.</summary>
    public static (int Size, float[] Rgb)? FromPixels(byte[] pixels, int width, int height, int stride, int bytesPerPixel = 4,
        bool bgr = false)
    {
        if (ColorFilter.LayoutFor(width, height) is not { } layout) return null;
        var n = layout.Size;
        var rgb = new float[n * n * n * 3];
        for (var b = 0; b < n; b++)
        for (var g = 0; g < n; g++)
        for (var r = 0; r < n; r++)
        {
            var (x, y) = layout.Pixel(r, g, b);
            var o = y * stride + x * bytesPerPixel;
            var i = ((b * n + g) * n + r) * 3;
            for (var c = 0; c < 3; c++) rgb[i + c] = pixels[o + (bgr ? 2 - c : c)] / 255f;
        }
        return (n, rgb);
    }
}
