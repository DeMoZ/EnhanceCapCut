namespace EnhanceCapCut.Render;

// "Stabilize" (stable.stable_level): CapCut computes the camera path itself and keeps nothing usable in the project
// (matrix_path is empty in the author's drafts), so we estimate the shake ourselves: block matching between
// consecutive downscaled frames gives a motion per frame, the accumulated path is smoothed and every frame is moved
// onto the smooth path, zoomed in so the moved edges stay outside the picture.
//
// Port of the macOS Stabilizer.swift; see FORMAT.md › Stabilization.

/// <summary>An 8-bit-range grey image for motion estimation (row-major, top row first).</summary>
public sealed class GrayImage(int width, int height, float[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public float[] Pixels { get; } = pixels;

    public float this[int x, int y] => Pixels[y * Width + x];

    /// <summary>
    /// One channel of <paramref name="source"/> box-averaged by <paramref name="factor"/> in both directions; with
    /// <paramref name="bgra"/> the pixels are BGRA and their luma is used.
    /// </summary>
    public static GrayImage Downscaled(ReadOnlySpan<byte> source, int width, int height, int rowBytes, int factor,
        bool bgra = false)
    {
        var f = Math.Max(factor, 1);
        int w = Math.Max(width / f, 1), h = Math.Max(height / f, 1);
        var pixels = new float[w * h];
        var scale = 1f / (f * f);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < f; dy++)
                {
                    var row = (y * f + dy) * rowBytes;
                    for (var dx = 0; dx < f; dx++)
                    {
                        if (bgra)
                        {
                            var p = row + (x * f + dx) * 4;
                            // BT.601 luma weights in 1/256.
                            sum += (29 * source[p] + 150 * source[p + 1] + 77 * source[p + 2]) >> 8;
                        }
                        else
                        {
                            sum += source[row + x * f + dx];
                        }
                    }
                }
                pixels[y * w + x] = sum * scale;
            }
        }
        return new GrayImage(w, h, pixels);
    }

    /// <summary>Half the size, 2×2 box filter.</summary>
    public GrayImage Halved()
    {
        int w = Math.Max(Width / 2, 1), h = Math.Max(Height / 2, 1);
        var output = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int x0 = Math.Min(2 * x, Width - 1), x1 = Math.Min(2 * x + 1, Width - 1);
                int y0 = Math.Min(2 * y, Height - 1), y1 = Math.Min(2 * y + 1, Height - 1);
                output[y * w + x] = (this[x0, y0] + this[x1, y0] + this[x0, y1] + this[x1, y1]) / 4;
            }
        }
        return new GrayImage(w, h, output);
    }

    /// <summary>Turned clockwise by <paramref name="quarterTurns"/> × 90°.</summary>
    public GrayImage Rotated(int quarterTurns)
    {
        var q = ((quarterTurns % 4) + 4) % 4;
        if (q == 0) return this;
        int w = q == 2 ? Width : Height, h = q == 2 ? Height : Width;
        var output = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (sx, sy) = q switch
                {
                    1 => (y, Height - 1 - x),
                    2 => (Width - 1 - x, Height - 1 - y),
                    _ => (Width - 1 - y, x),
                };
                output[y * w + x] = this[sx, sy];
            }
        }
        return new GrayImage(w, h, output);
    }
}

/// <summary>
/// A small similarity motion about the image centre, in units of the image height (+x right, +y down, angle in
/// radians clockwise on screen, <c>LogScale</c> = ln(scale)). Applied to a point p (centred): s·R(angle)·p + (dx, dy).
/// </summary>
public readonly record struct Motion(double Dx = 0, double Dy = 0, double Angle = 0, double LogScale = 0)
{
    public static readonly Motion Zero = new();
    public static Motion operator +(Motion a, Motion b) => new(a.Dx + b.Dx, a.Dy + b.Dy, a.Angle + b.Angle, a.LogScale + b.LogScale);
    public static Motion operator -(Motion a, Motion b) => new(a.Dx - b.Dx, a.Dy - b.Dy, a.Angle - b.Angle, a.LogScale - b.LogScale);
    public static Motion operator *(Motion a, double k) => new(a.Dx * k, a.Dy * k, a.Angle * k, a.LogScale * k);
}

/// <summary>Frame-to-frame motion by block matching on an image pyramid and a robust affine fit.</summary>
public static class MotionEstimator
{
    /// <summary>Analysis images are box-downscaled so their longer side is about this many pixels.</summary>
    public const int AnalysisSize = 320;
    /// <summary>Blocks on a Grid × Grid lattice over the frame.</summary>
    const int Grid = 10;
    /// <summary>Half the side of a matched block, in pixels of each pyramid level.</summary>
    const int BlockRadius = 6;
    /// <summary>Pyramid levels (level 2 = ¼ size); the search covers ±CoarseSearch·4 pixels at full analysis size.</summary>
    const int Levels = 3;
    const int CoarseSearch = 6;
    const int FineSearch = 2;
    /// <summary>Blocks flatter than this (standard deviation, 0…255) carry no motion.</summary>
    const float MinContrast = 3;
    /// <summary>Fewer usable blocks than this: no motion is measured for the frame.</summary>
    const int MinBlocks = 8;

    /// <summary>The box-downscale factor for a frame of <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static int Factor(int width, int height) => Math.Max(1, Math.Max(width, height) / AnalysisSize);

    record struct Point(double X, double Y, double U, double V);

    /// <summary>u = Ux·x + Uy·y + Tx, v = Vx·x + Vy·y + Ty (centred block positions).</summary>
    record struct Affine(double Ux, double Uy, double Tx, double Vx, double Vy, double Ty);

    /// <summary>The motion that takes <paramref name="previous"/> to <paramref name="current"/> (both the same size), or null when it can't be measured.</summary>
    public static Motion? Estimate(GrayImage previous, GrayImage current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height || previous.Width < 32 || previous.Height < 32)
            return null;
        var a = new List<GrayImage> { previous };
        var b = new List<GrayImage> { current };
        for (var i = 1; i < Levels; i++)
        {
            a.Add(a[^1].Halved());
            b.Add(b[^1].Halved());
        }
        int w = previous.Width, h = previous.Height;
        var points = new List<Point>();
        var top = 1 << (Levels - 1);
        // Blocks stay inside the coarsest level; shifts that would leave the frame are skipped by Cost.
        double margin = (BlockRadius + 2) * top;
        for (var gy = 0; gy < Grid; gy++)
        {
            for (var gx = 0; gx < Grid; gx++)
            {
                var cx = margin + (w - 2 * margin) * (gx + 0.5) / Grid;
                var cy = margin + (h - 2 * margin) * (gy + 0.5) / Grid;
                if (Contrast(a[0], (int)cx, (int)cy) < MinContrast) continue;
                int u = 0, v = 0;
                for (var level = Levels - 1; level >= 0; level--)
                {
                    var k = 1 << level;
                    int px = (int)cx / k, py = (int)cy / k;
                    if (level < Levels - 1) { u *= 2; v *= 2; }
                    var r = level == Levels - 1 ? CoarseSearch : FineSearch;
                    (u, v) = BestShift(a[level], b[level], px, py, (u, v), r);
                }
                var (su, sv) = Subpixel(a[0], b[0], (int)cx, (int)cy, u, v);
                points.Add(new Point(cx - w / 2.0, cy - h / 2.0, u + su, v + sv));
            }
        }
        if (Fit(points) is not { } fit) return null;
        // Moving subjects (people, water) disagree with the camera motion: drop the worst matches and refit.
        for (var i = 0; i < 3; i++)
        {
            var residuals = points.Select(p => Residual(p, fit)).Order().ToList();
            var limit = Math.Max(residuals[residuals.Count / 2] * 2.5, 0.5);
            var kept = points.Where(p => Residual(p, fit) <= limit).ToList();
            if (kept.Count < MinBlocks || Fit(kept) is not { } refit) break;
            fit = refit;
            if (kept.Count == points.Count) break;
        }
        // Rotation and zoom come from how the shifts change along x only: a rolling shutter makes the shifts of a
        // pan change down the frame (u and v with y), which a similarity fit would read as rotation and zoom.
        double n = h;
        double cosine = 1 + fit.Ux, sine = fit.Vx;
        return new Motion(fit.Tx / n, fit.Ty / n, Math.Atan2(sine, cosine), Math.Log(Math.Max(Math.Sqrt(cosine * cosine + sine * sine), 1e-6)));
    }

    static float Contrast(GrayImage img, int cx, int cy)
    {
        float sum = 0, sq = 0, n = 0;
        for (var y = cy - BlockRadius; y <= cy + BlockRadius; y++)
        {
            for (var x = cx - BlockRadius; x <= cx + BlockRadius; x++)
            {
                var p = img[x, y];
                sum += p; sq += p * p; n++;
            }
        }
        var mean = sum / n;
        return MathF.Sqrt(Math.Max(sq / n - mean * mean, 0));
    }

    /// <summary>
    /// Mean-removed sum of squared differences between the block at (cx, cy) in <paramref name="a"/> and the block
    /// shifted by (u, v) in <paramref name="b"/>; null when the shifted block leaves <paramref name="b"/>.
    /// </summary>
    static float? Cost(GrayImage a, GrayImage b, int cx, int cy, int u, int v)
    {
        const int r = BlockRadius;
        if (cx - r < 0 || cy - r < 0 || cx + r >= a.Width || cy + r >= a.Height
            || cx + u - r < 0 || cy + v - r < 0 || cx + u + r >= b.Width || cy + v + r >= b.Height) return null;
        float sa = 0, sb = 0;
        for (var y = -r; y <= r; y++)
        {
            for (var x = -r; x <= r; x++)
            {
                sa += a[cx + x, cy + y];
                sb += b[cx + u + x, cy + v + y];
            }
        }
        float n = (2 * r + 1) * (2 * r + 1);
        var offset = (sb - sa) / n;
        float total = 0;
        for (var y = -r; y <= r; y++)
        {
            for (var x = -r; x <= r; x++)
            {
                var d = b[cx + u + x, cy + v + y] - a[cx + x, cy + y] - offset;
                total += d * d;
            }
        }
        return total;
    }

    static (int U, int V) BestShift(GrayImage a, GrayImage b, int cx, int cy, (int U, int V) around, int search)
    {
        var best = around;
        var bestCost = float.PositiveInfinity;
        for (var v = around.V - search; v <= around.V + search; v++)
        {
            for (var u = around.U - search; u <= around.U + search; u++)
            {
                // Ties keep the smaller shift, so flat areas don't drift.
                if (Cost(a, b, cx, cy, u, v) is { } c
                    && (c < bestCost || (c == bestCost && Math.Abs(u) + Math.Abs(v) < Math.Abs(best.U) + Math.Abs(best.V))))
                {
                    bestCost = c;
                    best = (u, v);
                }
            }
        }
        return best;
    }

    /// <summary>Parabola through the costs around the best integer shift, per axis.</summary>
    static (double, double) Subpixel(GrayImage a, GrayImage b, int cx, int cy, int u, int v)
    {
        static double Vertex(float? m, float? c, float? p)
        {
            if (m is not { } mm || c is not { } cc || p is not { } pp) return 0;
            var d = mm - 2 * cc + pp;
            if (d <= 0) return 0;
            return Math.Max(-0.5f, Math.Min(0.5f, (mm - pp) / (2 * d)));
        }
        var c = Cost(a, b, cx, cy, u, v);
        return (Vertex(Cost(a, b, cx, cy, u - 1, v), c, Cost(a, b, cx, cy, u + 1, v)),
            Vertex(Cost(a, b, cx, cy, u, v - 1), c, Cost(a, b, cx, cy, u, v + 1)));
    }

    /// <summary>Least squares through the block centres and their shifts.</summary>
    static Affine? Fit(List<Point> points)
    {
        if (points.Count < MinBlocks) return null;
        double n = points.Count;
        double mx = points.Sum(p => p.X) / n, my = points.Sum(p => p.Y) / n;
        double mu = points.Sum(p => p.U) / n, mv = points.Sum(p => p.V) / n;
        double sxx = 0, sxy = 0, syy = 0, sxu = 0, syu = 0, sxv = 0, syv = 0;
        foreach (var p in points)
        {
            double x = p.X - mx, y = p.Y - my, u = p.U - mu, v = p.V - mv;
            sxx += x * x; sxy += x * y; syy += y * y;
            sxu += x * u; syu += y * u; sxv += x * v; syv += y * v;
        }
        var det = sxx * syy - sxy * sxy;
        if (det <= 1e-9 * Math.Max(sxx * syy, 1)) return null;
        var ux = (sxu * syy - syu * sxy) / det;
        var uy = (syu * sxx - sxu * sxy) / det;
        var vx = (sxv * syy - syv * sxy) / det;
        var vy = (syv * sxx - sxv * sxy) / det;
        return new Affine(ux, uy, mu - ux * mx - uy * my, vx, vy, mv - vx * mx - vy * my);
    }

    static double Residual(Point p, Affine f)
    {
        var ex = f.Ux * p.X + f.Uy * p.Y + f.Tx - p.U;
        var ey = f.Vx * p.X + f.Vy * p.Y + f.Ty - p.V;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}

/// <summary>
/// The per-frame corrections for one clip: frame times (source µs, ascending) and, for each frame, the motion that
/// moves it onto the smoothed camera path, zoom included (so the moved edges stay outside the picture).
/// </summary>
public sealed class StabilizationPlan(IReadOnlyList<long> times, IReadOnlyList<Motion> corrections)
{
    public IReadOnlyList<long> Times { get; } = times;
    public IReadOnlyList<Motion> Corrections { get; } = corrections;

    /// <summary>The correction of the frame nearest to source time <paramref name="t"/>.</summary>
    public Motion CorrectionAt(long t)
    {
        if (Times.Count == 0) return Motion.Zero;
        int lo = 0, hi = Times.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Times[mid] < t) lo = mid + 1; else hi = mid;
        }
        if (lo == Times.Count) return Corrections[lo - 1];
        if (lo > 0 && t - Times[lo - 1] <= Times[lo] - t) return Corrections[lo - 1];
        return Corrections[lo];
    }
}

public static class Stabilizer
{
    /// <summary>
    /// CapCut's levels: 1 = "Least cropping", 2 = "Recommended", 3 = "Most stable" (only 3 seen in the author's
    /// drafts). Smoothing = Gaussian sigma of the camera path in seconds; MaxZoom = the most the frame is enlarged to
    /// keep the moved edges out of the picture. The zoom follows the shake, like CapCut's: on ShrimpFishing1 (level 3)
    /// a steady pan got 1.035, a shaky close-up 1.11–1.19.
    /// </summary>
    public static (double Smoothing, double MaxZoom) Parameters(int level) => level switch
    {
        <= 1 => (0.3, 1.08),
        2 => (0.6, 1.15),
        _ => (1.0, 1.2),
    };

    /// <summary>
    /// The plan for frames at <paramref name="times"/> (µs) with motions[i] = motion from frame i−1 to frame i
    /// (motions[0] is ignored; null = not measured, taken as no motion). <paramref name="aspect"/> = frame width / height.
    /// </summary>
    public static StabilizationPlan Plan(IReadOnlyList<long> times, IReadOnlyList<Motion?> motions, int level, double aspect)
    {
        var (smoothing, maxZoom) = Parameters(level);
        return Plan(times, motions, aspect, smoothing, maxZoom);
    }

    public static StabilizationPlan Plan(IReadOnlyList<long> times, IReadOnlyList<Motion?> motions, double aspect,
        double smoothing, double maxZoom)
    {
        var count = Math.Min(times.Count, motions.Count);
        if (count == 0) return new StabilizationPlan([], []);
        var path = new Motion[count];
        for (var i = 1; i < count; i++) path[i] = path[i - 1] + (motions[i] ?? Motion.Zero);
        var duration = (times[count - 1] - times[0]) / 1_000_000.0;
        var fps = count > 1 && duration > 0 ? (count - 1) / duration : 30;
        var sigma = smoothing * fps;
        var smooth = Gaussian(path, sigma);
        var raw = Enumerable.Range(0, count).Select(i => smooth[i] - path[i]).ToArray();

        // The zoom: what each frame needs, held over the smoothing window and eased, at most maxZoom.
        var need = raw.Select(m => RequiredZoom(m, aspect, maxZoom)).ToArray();
        var window = Math.Max((int)Math.Round(sigma, MidpointRounding.AwayFromZero), 1);
        var held = Enumerable.Range(0, count).Select(i => Window(need, i, window).Max()).ToArray();
        var zooms = Gaussian(held, sigma / 2).Select(z => Math.Min(Math.Max(z, 1), maxZoom)).ToArray();

        // How much of each correction fits in its zoom; eased (lowest over the window first) so the corrections don't
        // jump where the limit bites. Scaling the whole correction keeps its frame-to-frame part, the shake removal.
        var fits = Enumerable.Range(0, count).Select(i => ClampFactor(raw[i], zooms[i], aspect)).ToArray();
        var lowest = Enumerable.Range(0, count).Select(i => Window(fits, i, window).Min()).ToArray();
        var factors = Gaussian(lowest, window / 2.0);
        var corrections = Enumerable.Range(0, count).Select(i =>
        {
            var c = raw[i] * factors[i];
            c *= ClampFactor(c, zooms[i], aspect);
            return c with { LogScale = c.LogScale + Math.Log(zooms[i]) };
        }).ToArray();
        return new StabilizationPlan(times.Take(count).ToArray(), corrections);
    }

    static ArraySegment<double> Window(double[] values, int i, int radius)
    {
        var from = Math.Max(0, i - radius);
        return new ArraySegment<double>(values, from, Math.Min(values.Length - 1, i + radius) - from + 1);
    }

    /// <summary>The least zoom (1…<paramref name="limit"/>) at which the frame moved by <paramref name="m"/> still covers the picture.</summary>
    public static double RequiredZoom(Motion m, double aspect, double limit)
    {
        if (Covers(m, 1, aspect)) return 1;
        if (!Covers(m, limit, aspect)) return limit;
        double lo = 1, hi = limit;
        for (var i = 0; i < 20; i++)
        {
            var mid = (lo + hi) / 2;
            if (Covers(m, mid, aspect)) hi = mid; else lo = mid;
        }
        return hi;
    }

    public static double[] Gaussian(double[] values, double sigma) =>
        Gaussian(values.Select(v => new Motion(v)).ToArray(), sigma).Select(m => m.Dx).ToArray();

    /// <summary>Gaussian smoothing with the kernel renormalized at the ends.</summary>
    public static Motion[] Gaussian(Motion[] values, double sigma)
    {
        if (sigma <= 0.01 || values.Length <= 1) return values;
        var radius = (int)Math.Ceiling(3 * sigma);
        var weights = Enumerable.Range(0, radius + 1).Select(k => Math.Exp(-(double)(k * k) / (2 * sigma * sigma))).ToArray();
        var result = new Motion[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var sum = Motion.Zero;
            var total = 0.0;
            for (var j = Math.Max(0, i - radius); j <= Math.Min(values.Length - 1, i + radius); j++)
            {
                var w = weights[Math.Abs(i - j)];
                sum += values[j] * w;
                total += w;
            }
            result[i] = sum * (1 / total);
        }
        return result;
    }

    /// <summary>How far (0…1) <paramref name="correction"/> can be scaled before the corrected, zoomed frame stops covering the picture.</summary>
    public static double ClampFactor(Motion correction, double zoom, double aspect)
    {
        if (Covers(correction, zoom, aspect)) return 1;
        double lo = 0, hi = 1;
        for (var i = 0; i < 20; i++)
        {
            var mid = (lo + hi) / 2;
            if (Covers(correction * mid, zoom, aspect)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>Whether the frame (centred, <paramref name="aspect"/> × 1) moved by <paramref name="m"/> and enlarged by <paramref name="zoom"/> covers its own rectangle.</summary>
    public static bool Covers(Motion m, double zoom, double aspect)
    {
        var s = Math.Exp(m.LogScale) * zoom;
        double c = Math.Cos(m.Angle), sn = Math.Sin(m.Angle);
        foreach (var (x, y) in new[] { (-aspect / 2, -0.5), (aspect / 2, -0.5), (aspect / 2, 0.5), (-aspect / 2, 0.5) })
        {
            // The picture corner in frame coordinates: invert p' = s·R·p + d.
            double px = x - m.Dx, py = y - m.Dy;
            double fx = (c * px + sn * py) / s, fy = (-sn * px + c * py) / s;
            if (Math.Abs(fx) > aspect / 2 + 1e-9 || Math.Abs(fy) > 0.5 + 1e-9) return false;
        }
        return true;
    }
}
