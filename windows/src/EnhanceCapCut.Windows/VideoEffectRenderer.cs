using System.Numerics;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// A clip's video effects (port of the macOS VideoEffectRenderer; see FORMAT.md › Video effects): the combined
/// <see cref="VideoEffectState"/> applied inside the clip's cropped picture — blur, zoom and rotation about its centre
/// with clamped edges, then the mix towards white. The result has the clip's full size, so the crop still applies.
/// </summary>
sealed class VideoEffectRenderer(Graphics graphics) : IDisposable
{
    readonly Dictionary<string, (ID2D1Bitmap1 Picture, ID2D1Bitmap1 Result)> bitmaps = [];

    public ID2D1Bitmap1 Apply(VisualLayer layer, ID2D1Bitmap1 image, long t)
    {
        if (layer.Effects.Count == 0) return image;
        var offset = t - layer.Target.Start;
        var state = VideoEffectState.Combined(layer.Effects.Select(e => e.State(offset)));
        if (state.IsIdentity) return image;

        var size = image.PixelSize;
        float x0 = 0, y0 = 0, x1 = size.Width, y1 = size.Height;
        if (Layout.CropRect(layer.Crop, size.Width, size.Height) is { } crop) (x0, y0, x1, y1) = crop;
        int w = Math.Max((int)MathF.Round(x1 - x0), 1), h = Math.Max((int)MathF.Round(y1 - y0), 1);
        if (!bitmaps.TryGetValue(layer.Id, out var pair) || pair.Result.PixelSize.Width != size.Width
            || pair.Result.PixelSize.Height != size.Height || pair.Picture.PixelSize.Width != w || pair.Picture.PixelSize.Height != h)
        {
            if (bitmaps.Remove(layer.Id, out var old)) { old.Picture.Dispose(); old.Result.Dispose(); }
            pair = (graphics.CreateBitmap(w, h, BitmapOptions.Target),
                graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target));
            bitmaps[layer.Id] = pair;
        }
        // The cropped picture on its own, so the edges clamp at the crop.
        graphics.DrawInto(pair.Picture, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.DrawBitmap(image, new Vortice.RawRectF(0, 0, w, h), 1, InterpolationMode.Linear,
                new Vortice.RawRectF(x0, y0, x1, y1), null);
        });
        graphics.DrawInto(pair.Result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            using var border = new Border(dc) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp };
            border.SetInput(0, pair.Picture, true);
            ID2D1Image picture = border.Output;
            using var blur = new GaussianBlur(dc)
            {
                StandardDeviation = (float)(state.Blur * Math.Min(w, h)),
                BorderMode = BorderMode.Hard,
            };
            if (state.Blur > 0)
            {
                blur.SetInput(0, picture, true);
                picture = blur.Output;
            }
            // Mix towards white on straight colour: rgb·(1−f) + f, alpha kept.
            float k = (float)(1 - state.Flash), f = (float)state.Flash;
            using var flash = new ColorMatrix(dc)
            {
                Matrix = new Matrix5x4(k, 0, 0, 0, 0, k, 0, 0, 0, 0, k, 0, 0, 0, 0, 1, f, f, f, 0),
            };
            if (state.Flash > 0)
            {
                flash.SetInput(0, picture, true);
                picture = flash.Output;
            }
            dc.PushAxisAlignedClip(new Vortice.RawRectF(x0, y0, x0 + w, y0 + h), AntialiasMode.Aliased);
            dc.Transform = Matrix3x2.CreateTranslation(-w / 2f, -h / 2f)
                * Matrix3x2.CreateScale((float)state.Zoom)
                * Matrix3x2.CreateRotation((float)(state.Rotation * Math.PI / 180))
                * Matrix3x2.CreateTranslation(x0 + w / 2f, y0 + h / 2f);
            dc.DrawImage(picture, InterpolationMode.Linear);
            dc.Transform = Matrix3x2.Identity;
            dc.PopAxisAlignedClip();
        });
        return pair.Result;
    }

    public void Release(HashSet<string> active)
    {
        foreach (var id in bitmaps.Keys.Where(id => !active.Contains(id)).ToList())
        {
            bitmaps[id].Picture.Dispose();
            bitmaps[id].Result.Dispose();
            bitmaps.Remove(id);
        }
    }

    public void Dispose()
    {
        foreach (var (picture, result) in bitmaps.Values) { picture.Dispose(); result.Dispose(); }
        bitmaps.Clear();
    }
}
