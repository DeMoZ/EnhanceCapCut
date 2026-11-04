using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// A clip's masks (port of the macOS FrameCompositor.masked): the union of its masks as an A8 bitmap, applied as the
/// frame's alpha into a bitmap of its own.
/// </summary>
sealed class ClipMaskLayer(IReadOnlyList<ClipMask> masks) : IDisposable
{
    readonly ClipMaskFrames frames = new(masks);
    ID2D1Bitmap1? maskBitmap;
    ID2D1Bitmap1? result;

    /// <summary><paramref name="image"/> with the masks at source time <paramref name="t"/> as its alpha.</summary>
    public ID2D1Bitmap1 Apply(Graphics graphics, ID2D1Bitmap1 image, long t)
    {
        var size = image.PixelSize;
        if (size.Width <= 0 || size.Height <= 0 || frames.FrameAt(t, size.Width, size.Height) is not { } frame) return image;
        if (frame.Changed || maskBitmap is null)
        {
            if (maskBitmap is not { } existing || existing.PixelSize.Width != frame.Width || existing.PixelSize.Height != frame.Height)
            {
                maskBitmap?.Dispose();
                maskBitmap = graphics.CreateMaskBitmap(frame.Width, frame.Height);
            }
            maskBitmap.CopyFromMemory(frame.Pixels, (uint)frame.Width).CheckError();
        }
        if (result is not { } r || r.PixelSize.Width != size.Width || r.PixelSize.Height != size.Height)
        {
            result?.Dispose();
            result = graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target);
        }
        var mask = maskBitmap;
        graphics.DrawInto(result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            // FillOpacityMask needs aliased drawing; the mask is stretched over the whole frame.
            dc.AntialiasMode = AntialiasMode.Aliased;
            using var brush = dc.CreateBitmapBrush(image);
            dc.FillOpacityMask(mask, brush, new Vortice.RawRectF(0, 0, size.Width, size.Height),
                new Vortice.RawRectF(0, 0, mask.PixelSize.Width, mask.PixelSize.Height));
            dc.AntialiasMode = AntialiasMode.PerPrimitive;
        });
        return result;
    }

    public void Dispose()
    {
        maskBitmap?.Dispose();
        result?.Dispose();
    }
}
