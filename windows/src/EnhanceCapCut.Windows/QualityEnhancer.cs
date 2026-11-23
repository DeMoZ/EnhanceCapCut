using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// CapCut's AI "Enhance quality", approximated (port of the macOS QualityEnhance; see <see cref="QualityEnhanceMath"/>):
/// the frame drawn upscaled with high-quality cubic sampling, blurred a little and sharpened with an unsharp mask.
/// </summary>
public sealed class QualityEnhancer(Graphics graphics) : IDisposable
{
    readonly Dictionary<string, (ID2D1Bitmap1 Upscaled, ID2D1Bitmap1 Result)> results = [];

    /// <summary><paramref name="image"/> enhanced, in a bitmap kept for clip <paramref name="id"/>.</summary>
    public ID2D1Bitmap1 Apply(string id, ID2D1Bitmap1 image)
    {
        var size = image.PixelSize;
        var k = QualityEnhanceMath.Scale(size.Width, size.Height);
        int w = size.Width * k, h = size.Height * k;
        if (!results.TryGetValue(id, out var r) || r.Result.PixelSize.Width != w || r.Result.PixelSize.Height != h)
        {
            if (r.Result is not null) { r.Upscaled.Dispose(); r.Result.Dispose(); }
            r = (graphics.CreateBitmap(w, h, BitmapOptions.Target), graphics.CreateBitmap(w, h, BitmapOptions.Target));
            results[id] = r;
        }
        var bounds = new Vortice.RawRectF(0, 0, w, h);
        graphics.DrawInto(r.Upscaled, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.DrawBitmap(image, bounds, 1, InterpolationMode.HighQualityCubic,
                new Vortice.RawRectF(0, 0, size.Width, size.Height), null);
        });
        var a = (float)QualityEnhanceMath.SharpenAmount;
        graphics.DrawInto(r.Result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            using var border = new Border(dc) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp };
            border.SetInput(0, r.Upscaled, true);
            using var denoise = new GaussianBlur(dc)
            {
                StandardDeviation = (float)QualityEnhanceMath.DenoiseSigma, BorderMode = BorderMode.Hard,
            };
            denoise.SetInput(0, border.Output, true);
            using var blur = new GaussianBlur(dc)
            {
                StandardDeviation = (float)QualityEnhanceMath.SharpenSigma, BorderMode = BorderMode.Hard,
            };
            blur.SetInput(0, denoise.Output, true);
            // d + a·(d − b) = (1 + a)·d − a·b
            using var sharpen = new ArithmeticComposite(dc)
            {
                Coefficients = new System.Numerics.Vector4(0, 1 + a, -a, 0),
                ClampOutput = true,
            };
            sharpen.SetInput(0, denoise.Output, true);
            sharpen.SetInput(1, blur.Output, true);
            dc.PushAxisAlignedClip(bounds, AntialiasMode.Aliased);
            dc.DrawImage(sharpen.Output, InterpolationMode.Linear);
            dc.PopAxisAlignedClip();
        });
        return r.Result;
    }

    /// <summary>Frees the bitmaps of clips no longer on screen.</summary>
    public void Release(IReadOnlySet<string> active)
    {
        foreach (var id in results.Keys.Where(id => !active.Contains(id)).ToList())
        {
            results[id].Upscaled.Dispose();
            results[id].Result.Dispose();
            results.Remove(id);
        }
    }

    public void Dispose()
    {
        foreach (var (upscaled, result) in results.Values) { upscaled.Dispose(); result.Dispose(); }
        results.Clear();
    }
}
