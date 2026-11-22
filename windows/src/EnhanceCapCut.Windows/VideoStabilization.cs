using System.Numerics;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// "Stabilize" on Windows (port of the macOS VideoStabilization): reads a clip's frames once (downscaled luma) to plan
/// the corrections with <see cref="Stabilizer"/>, then draws each frame moved onto the smoothed camera path.
/// </summary>
public sealed class VideoStabilization(StabilizationPlan plan) : IDisposable
{
    public StabilizationPlan Plan { get; } = plan;
    ID2D1Bitmap1? result;

    /// <summary>
    /// The plan for source times <paramref name="fromUs"/>..<paramref name="toUs"/> of <paramref name="path"/> at
    /// CapCut's <paramref name="level"/>.
    /// </summary>
    public static StabilizationPlan Analyze(string path, long fromUs, long toUs, int level)
    {
        using var reader = new MfVideoReader(path, fromUs);
        var turns = reader.Rotation / 90;
        var times = new List<long>();
        var motions = new List<Motion?>();
        GrayImage? previous = null;
        var aspect = 16.0 / 9;
        var version = -1;
        // FrameAt returns the latest frame at or before a time; stepping finer than any frame rate visits each one.
        for (var t = fromUs; t <= toUs; t += 2000)
        {
            if (reader.FrameAt(t) is not { } frame || frame.Version == version) continue;
            version = frame.Version;
            var image = GrayImage.Downscaled(frame.Pixels, frame.Width, frame.Height, frame.Stride,
                MotionEstimator.Factor(frame.Width, frame.Height), bgra: true).Rotated(turns);
            aspect = (double)image.Width / Math.Max(image.Height, 1);
            motions.Add(previous is null ? null : MotionEstimator.Estimate(previous, image));
            times.Add(frame.Time);
            previous = image;
        }
        return Stabilizer.Plan(times, motions, level, aspect);
    }

    /// <summary><paramref name="image"/> (a whole upright frame) moved by the correction at source time <paramref name="t"/>.</summary>
    public ID2D1Bitmap1 Apply(Graphics graphics, ID2D1Bitmap1 image, long t)
    {
        if (Plan.Times.Count == 0) return image;
        var size = image.PixelSize;
        if (result is not { } target || target.PixelSize.Width != size.Width || target.PixelSize.Height != size.Height)
        {
            result?.Dispose();
            result = graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target);
        }
        var c = Plan.CorrectionAt(t);
        float w = size.Width, h = size.Height, s = (float)Math.Exp(c.LogScale);
        // Motion is in y-down units of the frame height with clockwise angles, like Direct2D.
        var transform = Matrix3x2.CreateTranslation(-w / 2, -h / 2)
            * Matrix3x2.CreateScale(s)
            * Matrix3x2.CreateRotation((float)c.Angle)
            * Matrix3x2.CreateTranslation(w / 2 + (float)c.Dx * h, h / 2 + (float)c.Dy * h);
        graphics.DrawInto(result!, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.Transform = transform;
            dc.DrawBitmap(image, 1, InterpolationMode.Linear);
        });
        return result!;
    }

    public void Dispose() => result?.Dispose();
}
