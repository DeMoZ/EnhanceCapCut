using EnhanceCapCut.Render;

namespace EnhanceCapCut.Render.Tests;

// Ported from the macOS version's StabilizationTests.swift.
public class StabilizerTests
{
    /// <summary>A deterministic textured frame.</summary>
    static GrayImage Texture(int width, int height)
    {
        var pixels = new float[width * height];
        uint seed = 12345;
        double Next()
        {
            seed = unchecked(seed * 1_664_525 + 1_013_904_223);
            return (seed >> 8) / (double)(1 << 24);
        }
        var blobs = Enumerable.Range(0, 160).Select(_ => (X: Next() * width, Y: Next() * height, R: 2 + Next() * 6, A: (float)(Next() * 200 - 100))).ToList();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float v = 128;
                foreach (var (bx, by, r, a) in blobs)
                {
                    var d2 = ((x - bx) * (x - bx) + (y - by) * (y - by)) / (r * r);
                    if (d2 < 9) v += a * (float)Math.Exp(-d2);
                }
                pixels[y * width + x] = Math.Clamp(v, 0, 255);
            }
        }
        return new GrayImage(width, height, pixels);
    }

    /// <summary><paramref name="g"/> moved by <paramref name="m"/> (bilinear, edges clamped), as the renderers draw it.</summary>
    static GrayImage Warp(GrayImage g, Motion m)
    {
        int w = g.Width, h = g.Height;
        var output = new float[w * h];
        double s = Math.Exp(m.LogScale), c = Math.Cos(m.Angle), sn = Math.Sin(m.Angle);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double qx = x + 0.5 - w / 2.0 - m.Dx * h, qy = y + 0.5 - h / 2.0 - m.Dy * h;
                var px = (c * qx + sn * qy) / s + w / 2.0 - 0.5;
                var py = (-sn * qx + c * qy) / s + h / 2.0 - 0.5;
                int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
                double fx = px - x0, fy = py - y0;
                float At(int xx, int yy) => g[Math.Clamp(xx, 0, w - 1), Math.Clamp(yy, 0, h - 1)];
                output[y * w + x] = (float)((At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy)
                    + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy);
            }
        }
        return new GrayImage(w, h, output);
    }

    [Fact]
    public void EstimatedMotionMatchesTheAppliedOne()
    {
        var a = Texture(320, 180);
        foreach (var m in new[] { new Motion(0.03, -0.02), new Motion(-0.01, 0.015, 0.02), new Motion(0, 0, -0.015, 0.02) })
        {
            var e = MotionEstimator.Estimate(a, Warp(a, m));
            Assert.NotNull(e);
            Assert.Equal(m.Dx, e.Value.Dx, 0.002);
            Assert.Equal(m.Dy, e.Value.Dy, 0.002);
            Assert.Equal(m.Angle, e.Value.Angle, 0.003);
            Assert.Equal(m.LogScale, e.Value.LogScale, 0.003);
        }
        var flat = new GrayImage(64, 64, Enumerable.Repeat(9f, 4096).ToArray());
        Assert.Null(MotionEstimator.Estimate(flat, flat));
    }

    [Fact]
    public void PlanRemovesShakeAndKeepsThePictureCovered()
    {
        // A slow pan with a fast shake on top, 30 fps.
        const int count = 150;
        var times = Enumerable.Range(0, count).Select(i => i * 33_333L).ToList();
        var motions = Enumerable.Range(0, count).Select(i => i == 0 ? (Motion?)null
            : new Motion(0.002 + 0.006 * Math.Sin(i * 1.9), 0.005 * Math.Cos(i * 2.3), 0.004 * Math.Sin(i * 1.3))).ToList();
        static double Jitter(Motion[] path) => Math.Sqrt(Enumerable.Range(1, path.Length - 2).Sum(k =>
        {
            var s = path[k + 1] - path[k] * 2 + path[k - 1];
            return s.Dx * s.Dx + s.Dy * s.Dy;
        }));
        var path = new Motion[count];
        for (var i = 1; i < count; i++) path[i] = path[i - 1] + motions[i]!.Value;
        for (var level = 1; level <= 3; level++)
        {
            var plan = Stabilizer.Plan(times, motions, level, 16.0 / 9);
            Assert.Equal(count, plan.Corrections.Count);
            var output = Enumerable.Range(0, count).Select(i => (path[i] + plan.Corrections[i]) with { LogScale = 0 }).ToArray();
            Assert.True(Jitter(output) < Jitter(path) / 4, $"level {level}");
            foreach (var c in plan.Corrections)
            {
                Assert.True(Stabilizer.Covers(c, 1, 16.0 / 9), $"level {level}: edges show");
                Assert.True(Math.Exp(c.LogScale) <= Stabilizer.Parameters(level).MaxZoom + 0.01);
            }
            Assert.Equal(plan.Corrections[1].Dx, plan.CorrectionAt(40_000).Dx);
        }
    }

    [Fact]
    public void GrayImageHelpers()
    {
        var g = new GrayImage(3, 2, [1, 2, 3, 4, 5, 6]);
        Assert.Equal([4, 1, 5, 2, 6, 3], g.Rotated(1).Pixels);
        Assert.Equal([6, 5, 4, 3, 2, 1], g.Rotated(2).Pixels);
        Assert.Equal([3, 6, 2, 5, 1, 4], g.Rotated(3).Pixels);
        Assert.Equal([25, 45], GrayImage.Downscaled(new byte[] { 0, 10, 20, 30, 40, 50, 60, 70 }, 4, 2, 4, 2).Pixels);
        // BGRA pixels give their luma.
        Assert.Equal([255, 0, 149], GrayImage.Downscaled(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255, 0, 255, 0, 255 }, 3, 1, 12, 1, bgra: true).Pixels);
    }
}
