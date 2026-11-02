using System.Text.Json;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>
/// CapCut's "Remove background" mask frames for one clip (port of the macOS CutoutMask; see FORMAT.md › Remove
/// background).
///
/// Each file in the <c>mask</c> folder is one raw 8-bit frame (0 = removed, 255 = kept), upright, at a low resolution
/// (e.g. 448×256 for 16:9 footage), named by its source time in µs. The size is in <c>../maskinfo/&lt;time&gt;</c>.
/// </summary>
public sealed class CutoutMask
{
    public Cutout Cutout { get; }
    /// <summary>Frame times, ascending.</summary>
    public IReadOnlyList<long> Times { get; }
    /// <summary>From <c>maskinfo</c>, or inferred from the first frame read.</summary>
    (int Width, int Height)? size;

    CutoutMask(Cutout cutout, List<long> times)
    {
        Cutout = cutout;
        Times = times;
        size = StoredSize(Path.Combine(Path.GetDirectoryName(cutout.Directory.TrimEnd('/', '\\')) ?? "", "maskinfo"));
    }

    public static CutoutMask? Open(Cutout cutout)
    {
        if (!Directory.Exists(cutout.Directory)) return null;
        var times = Directory.EnumerateFiles(cutout.Directory)
            .Select(f => long.TryParse(Path.GetFileName(f), out var t) ? t : (long?)null)
            .OfType<long>().Order().ToList();
        return times.Count == 0 ? null : new CutoutMask(cutout, times);
    }

    /// <summary>
    /// The mask frame at source time <paramref name="t"/>: one byte per pixel, top row first, already inverted for
    /// "Invert". <paramref name="aspect"/> is the frame's width / height, used when <c>maskinfo</c> is missing.
    /// </summary>
    public (byte[] Pixels, int Width, int Height)? FrameAt(long t, double aspect)
    {
        byte[] data;
        try { data = File.ReadAllBytes(Path.Combine(Cutout.Directory, Nearest(Times, t).ToString())); }
        catch (IOException) { return null; }
        if (size is not { } s || s.Width * s.Height != data.Length) size = InferSize(data.Length, aspect);
        if (size is not { } known) return null;
        if (Cutout.Inverted) for (var i = 0; i < data.Length; i++) data[i] = (byte)(255 - data[i]);
        return (data, known.Width, known.Height);
    }

    /// <summary>The time in <paramref name="times"/> (ascending, non-empty) closest to <paramref name="t"/>.</summary>
    public static long Nearest(IReadOnlyList<long> times, long t)
    {
        int lo = 0, hi = times.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (times[mid] < t) lo = mid + 1; else hi = mid;
        }
        if (lo == times.Count) return times[lo - 1];
        if (lo > 0 && t - times[lo - 1] <= times[lo] - t) return times[lo - 1];
        return times[lo];
    }

    /// <summary><c>boundingBox.width/height</c> of the first file in a <c>maskinfo</c> folder.</summary>
    public static (int Width, int Height)? StoredSize(string info)
    {
        try
        {
            if (!Directory.Exists(info)) return null;
            var first = Directory.EnumerateFiles(info).Order(StringComparer.Ordinal).FirstOrDefault();
            if (first is null) return null;
            using var json = JsonDocument.Parse(File.ReadAllBytes(first));
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("boundingBox", out var box) || box.ValueKind != JsonValueKind.Object
                || !box.TryGetProperty("width", out var width) || !width.TryGetInt32(out var w)
                || !box.TryGetProperty("height", out var height) || !height.TryGetInt32(out var h))
                return null;
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    /// <summary>Width and height that multiply to <paramref name="bytes"/> with the ratio closest to <paramref name="aspect"/>, for a frame without <c>maskinfo</c>.</summary>
    public static (int Width, int Height)? InferSize(int bytes, double aspect)
    {
        if (bytes <= 0 || aspect <= 0) return null;
        (int, int)? best = null;
        var bestError = double.PositiveInfinity;
        for (var h = 1; h <= bytes; h++)
        {
            if (bytes % h != 0) continue;
            var w = bytes / h;
            var error = Math.Abs(Math.Log((double)w / h / aspect));
            if (error < bestError) (best, bestError) = ((w, h), error);
        }
        return bestError < 0.1 ? best : null;
    }
}
