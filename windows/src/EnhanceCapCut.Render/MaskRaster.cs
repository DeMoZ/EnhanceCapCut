using System.Text.Json;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>
/// Draws CapCut's clip masks into one 8-bit coverage frame (0 = hidden, 255 = shown), top row first, in the clip's
/// own frame (before crop and the clip transform). Shapes are signed distance fields, so the feather is a linear ramp
/// across the edge. Port of the macOS MaskRaster.swift; keep the two in step.
/// </summary>
public static class MaskRaster
{
    /// <summary>
    /// Width of the soft edge at feather 1, as a fraction of the frame's shorter side, centred on the edge. Checked
    /// against the Pullups7 export (feather 0.1 masks: frame differences only in the last few pixels of the ramp).
    /// </summary>
    public const double FeatherScale = 0.5;
    /// <summary>The mask raster's longer side at most; masks are smooth, so they're drawn smaller than the frame and scaled.</summary>
    public const int MaxSide = 1024;
    /// <summary>Inner/outer radius of the "Star" (a regular pentagram).</summary>
    public const double StarInnerRatio = 0.381966;

    public abstract record Layer;
    public sealed record ShapeLayer(MaskShape Shape, MaskGeometry Geometry, bool Inverted) : Layer;
    /// <summary>A brush frame: <paramref name="Pixels"/> is <paramref name="Width"/>×<paramref name="Height"/>, top row first.</summary>
    public sealed record BitmapLayer(byte[] Pixels, int Width, int Height, bool Inverted) : Layer;

    /// <summary>The raster size for a frame of <paramref name="width"/>×<paramref name="height"/> pixels.</summary>
    public static (int Width, int Height) RasterSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return (1, 1);
        var k = Math.Min(1, MaxSide / Math.Max(width, height));
        return (Math.Max((int)Math.Round(width * k, MidpointRounding.AwayFromZero), 1),
            Math.Max((int)Math.Round(height * k, MidpointRounding.AwayFromZero), 1));
    }

    /// <summary>The union of <paramref name="layers"/> (a pixel is shown when any mask shows it).</summary>
    public static byte[] Render(IReadOnlyList<Layer> layers, int width, int height)
    {
        var output = new byte[width * height];
        foreach (var layer in layers)
        {
            switch (layer)
            {
                case ShapeLayer s: DrawShape(s.Shape, s.Geometry, s.Inverted, width, height, output); break;
                case BitmapLayer b: DrawBitmap(b.Pixels, b.Width, b.Height, b.Inverted, width, height, output); break;
            }
        }
        return output;
    }

    static void DrawShape(MaskShape shape, MaskGeometry g, bool inverted, int width, int height, byte[] output)
    {
        double w = width, h = height;
        double cx = w / 2 * (1 + g.CenterX), cy = h / 2 * (1 - g.CenterY);
        double halfW = Math.Abs(g.Width) * w / 2, halfH = Math.Abs(g.Height) * h / 2;
        var angle = g.Rotation * Math.PI / 180;
        double cosA = Math.Cos(angle), sinA = Math.Sin(angle);
        var soft = Math.Max(Math.Max(g.Feather, 0) * FeatherScale * Math.Min(w, h), 1);
        for (var y = 0; y < height; y++)
        {
            var qy = y + 0.5 - cy;
            for (var x = 0; x < width; x++)
            {
                var qx = x + 0.5 - cx;
                // Into the mask's frame (y down): undo the clockwise rotation.
                var lx = qx * cosA + qy * sinA;
                var ly = -qx * sinA + qy * cosA;
                var d = Distance(shape, lx, ly, halfW, halfH, g.RoundCorner);
                var a = Math.Min(Math.Max(0.5 - d / soft, 0), 1);
                if (inverted) a = 1 - a;
                var v = (byte)Math.Round(a * 255, MidpointRounding.AwayFromZero);
                var i = y * width + x;
                if (v > output[i]) output[i] = v;
            }
        }
    }

    static void DrawBitmap(byte[] pixels, int bw, int bh, bool inverted, int outWidth, int outHeight, byte[] output)
    {
        if (bw <= 0 || bh <= 0 || pixels.Length < bw * bh) return;
        double sx = (double)bw / outWidth, sy = (double)bh / outHeight;
        for (var y = 0; y < outHeight; y++)
        {
            var fy = Math.Min(Math.Max((y + 0.5) * sy - 0.5, 0), bh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, bh - 1);
            var ty = fy - y0;
            for (var x = 0; x < outWidth; x++)
            {
                var fx = Math.Min(Math.Max((x + 0.5) * sx - 0.5, 0), bw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, bw - 1);
                var tx = fx - x0;
                var top = pixels[y0 * bw + x0] * (1 - tx) + pixels[y0 * bw + x1] * tx;
                var bottom = pixels[y1 * bw + x0] * (1 - tx) + pixels[y1 * bw + x1] * tx;
                var v = top * (1 - ty) + bottom * ty;
                if (inverted) v = 255 - v;
                var b = (byte)Math.Min(Math.Max(Math.Round(v, MidpointRounding.AwayFromZero), 0), 255);
                var i = y * outWidth + x;
                if (b > output[i]) output[i] = b;
            }
        }
    }

    /// <summary>
    /// Signed distance (raster pixels, negative inside) from a point in the mask's frame (origin at its center, y down)
    /// to the edge of <paramref name="shape"/> filling a box of half-size <paramref name="halfW"/>×<paramref name="halfH"/>.
    /// </summary>
    public static double Distance(MaskShape shape, double x, double y, double halfW, double halfH, double roundCorner)
    {
        switch (shape)
        {
            case MaskShape.Line:
                // Keeps what's above the line.
                return y;
            case MaskShape.Mirror:
                return Math.Abs(y) - halfH;
            case MaskShape.Rectangle:
            {
                var r = Math.Min(Math.Max(roundCorner, 0), 1) * Math.Min(halfW, halfH);
                double qx = Math.Abs(x) - halfW + r, qy = Math.Abs(y) - halfH + r;
                return Hypot(Math.Max(qx, 0), Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - r;
            }
            case MaskShape.Circle:
            {
                if (halfW <= 0 || halfH <= 0) return 1e9;
                var k0 = Hypot(x / halfW, y / halfH);
                var k1 = Hypot(x / (halfW * halfW), y / (halfH * halfH));
                return k1 > 0 ? k0 * (k0 - 1) / k1 : -Math.Min(halfW, halfH);
            }
            case MaskShape.Heart:
            {
                // Unit heart: tip at (0, 0), lobes up to y ≈ 1.104, x within ±0.604.
                double sx = 2 * halfW / 1.2071, sy = 2 * halfH / 1.1036;
                if (sx <= 0 || sy <= 0) return 1e9;
                return Heart(x / sx, -y / sy + 0.5518) * Math.Min(sx, sy);
            }
            case MaskShape.Star:
            {
                // Unit star (outer radius 1, a point up): width 1.902, height 1.809, box center 0.0955 above the center.
                double sx = 2 * halfW / 1.9021, sy = 2 * halfH / 1.8090;
                if (sx <= 0 || sy <= 0) return 1e9;
                return Star(x / sx, -y / sy + 0.0955, StarInnerRatio) * Math.Min(sx, sy);
            }
            default:
                return 1e9;
        }
    }

    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>Distance to a heart shape (y up, tip at the origin).</summary>
    public static double Heart(double px, double py)
    {
        var x = Math.Abs(px);
        if (py + x > 1) return Hypot(x - 0.25, py - 0.75) - Math.Sqrt(2) / 4;
        var a = x * x + (py - 1) * (py - 1);
        var m = 0.5 * Math.Max(x + py, 0);
        var b = (x - m) * (x - m) + (py - m) * (py - m);
        return Math.Sqrt(Math.Min(a, b)) * (x - py < 0 ? -1 : 1);
    }

    /// <summary>Distance to a five-pointed star of outer radius 1 (y up, a point up); <paramref name="inner"/> is the inner radius.</summary>
    public static double Star(double px, double py, double inner)
    {
        const double k1x = 0.809016994375, k1y = -0.587785252292;
        const double k2x = -k1x, k2y = k1y;
        double x = Math.Abs(px), y = py;
        var d = Math.Max(k1x * x + k1y * y, 0);
        x -= 2 * d * k1x; y -= 2 * d * k1y;
        d = Math.Max(k2x * x + k2y * y, 0);
        x -= 2 * d * k2x; y -= 2 * d * k2y;
        x = Math.Abs(x);
        y -= 1;
        // Edge from the tip (0, 1) to the inner corner at radius `inner`.
        double bax = inner * -k1y, bay = inner * k1x - 1;
        var h = Math.Min(Math.Max((x * bax + y * bay) / (bax * bax + bay * bay), 0), 1);
        var dist = Hypot(x - bax * h, y - bay * h);
        return (y * bax - x * bay) < 0 ? -dist : dist;
    }
}

/// <summary>
/// CapCut's brush strokes for one mask: <c>&lt;µs&gt;</c> files of raw 8-bit frames named by source time, with their
/// size in <c>data.json</c> (<c>data[].width/height</c>).
/// </summary>
public static class BrushFrames
{
    public static (int Width, int Height)? Size(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "data.json");
            if (!File.Exists(path)) return null;
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0) return null;
            var first = data[0];
            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("width", out var width) || !width.TryGetInt32(out var w)
                || !first.TryGetProperty("height", out var height) || !height.TryGetInt32(out var h))
                return null;
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>A clip's masks at a given time, as one coverage frame; redrawn only when something changed.</summary>
public sealed class ClipMaskFrames
{
    sealed class Brush(string folder, List<long> times, (int Width, int Height)? size)
    {
        public string Folder { get; } = folder;
        public List<long> Times { get; } = times;
        public (int Width, int Height)? Size { get; set; } = size;
        public long? Time { get; set; }
        public byte[]? Pixels { get; set; }
    }

    public IReadOnlyList<ClipMask> Masks { get; }
    readonly Dictionary<int, Brush> brushes = [];
    string? lastKey;
    (byte[] Pixels, int Width, int Height)? last;

    public ClipMaskFrames(IReadOnlyList<ClipMask> masks)
    {
        Masks = masks;
        for (var i = 0; i < masks.Count; i++)
        {
            if (masks[i].BrushFolder is { } folder)
                brushes[i] = new Brush(folder, ClipMask.BrushTimes(folder), BrushFrames.Size(folder));
        }
    }

    /// <summary>
    /// The coverage frame at source time <paramref name="t"/> for a clip frame of <paramref name="width"/>×<paramref name="height"/>
    /// pixels; <c>Changed</c> is false when it is the same as the previous call's.
    /// </summary>
    public (byte[] Pixels, int Width, int Height, bool Changed)? FrameAt(long t, double width, double height)
    {
        var (w, h) = MaskRaster.RasterSize(width, height);
        var layers = new List<MaskRaster.Layer>();
        var key = new System.Text.StringBuilder($"{w}x{h}");
        for (var i = 0; i < Masks.Count; i++)
        {
            var mask = Masks[i];
            if (mask.Shape is { } shape)
            {
                var g = mask.GeometryAt(t);
                layers.Add(new MaskRaster.ShapeLayer(shape, g, mask.Inverted));
                key.Append($"|{shape} {g} {mask.Inverted}");
                continue;
            }
            if (!brushes.TryGetValue(i, out var brush) || brush.Times.Count == 0) continue;
            var time = CutoutMask.Nearest(brush.Times, t);
            if (brush.Time != time)
            {
                byte[] data;
                try { data = File.ReadAllBytes(Path.Combine(brush.Folder, time.ToString())); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (brush.Size is not { } s || s.Width * s.Height != data.Length)
                    brush.Size = CutoutMask.InferSize(data.Length, width / Math.Max(height, 1));
                brush.Time = time;
                brush.Pixels = data;
            }
            if (brush.Size is not { } size || brush.Pixels is not { } pixels) continue;
            layers.Add(new MaskRaster.BitmapLayer(pixels, size.Width, size.Height, mask.Inverted));
            key.Append($"|brush {i} {time} {mask.Inverted}");
        }
        if (layers.Count == 0) return null;
        var k = key.ToString();
        if (k == lastKey && last is { } previous) return (previous.Pixels, previous.Width, previous.Height, false);
        var result = MaskRaster.Render(layers, w, h);
        last = (result, w, h);
        lastKey = k;
        return (result, w, h, true);
    }
}
