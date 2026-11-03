using System.Runtime.InteropServices;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;
using ChromaKey = EnhanceCapCut.Draft.ChromaKey;

namespace EnhanceCapCut.Windows;

/// <summary>
/// "Chroma key" for one clip (port of the macOS ChromaKeyFilter; see FORMAT.md › Chroma key): the
/// <see cref="ChromaKeyMath"/> matte and spill tables as Direct2D 3D lookup tables, the matte blurred for "Edge feather"
/// and applied as the frame's alpha.
/// </summary>
sealed class ChromaKeyer : IDisposable
{
    readonly Graphics graphics;
    readonly ID2D1LookupTable3D matte;
    readonly ID2D1LookupTable3D? color;
    ID2D1Bitmap1? result;

    public ChromaKey Key { get; }

    public ChromaKeyer(Graphics graphics, ChromaKey key)
    {
        this.graphics = graphics;
        Key = key;
        using var context = graphics.Context.QueryInterface<ID2D1DeviceContext2>();
        matte = Table(context, ChromaKeyMath.MatteTable(key));
        color = key.Spill > 0 ? Table(context, ChromaKeyMath.ColorTable(key)) : null;
    }

    static ID2D1LookupTable3D Table(ID2D1DeviceContext2 context, float[] data)
    {
        const uint n = ChromaKeyMath.CubeSize;
        var bytes = MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
        // Row = one run of the fastest-changing (blue) axis, plane = rows of green.
        return context.CreateLookupTable3D(BufferPrecision.PerChannel32Float, [n, n, n], bytes, (uint)bytes.Length,
            [n * 16, n * n * 16]);
    }

    /// <summary><paramref name="image"/> with the key colour removed and its spill taken out of what's kept.</summary>
    public ID2D1Bitmap1 Apply(ID2D1Bitmap1 image)
    {
        var size = image.PixelSize;
        if (result is not { } existing || existing.PixelSize.Width != size.Width || existing.PixelSize.Height != size.Height)
        {
            result?.Dispose();
            result = graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target);
        }
        graphics.DrawInto(result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            using var matteEffect = new LookupTable3D(dc) { LUT = matte, AlphaMode = Vortice.DCommon.AlphaMode.Straight };
            matteEffect.SetInput(0, image, true);
            using var border = new Border(dc) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp };
            using var blur = new GaussianBlur(dc)
            {
                StandardDeviation = (float)(ChromaKeyMath.EdgeSmoothRadius * Key.EdgeSmooth * Math.Min(size.Width, size.Height)),
                BorderMode = BorderMode.Hard,
            };
            ID2D1Image mask = matteEffect.Output;
            if (Key.EdgeSmooth > 0)
            {
                border.SetInput(0, mask, true);
                blur.SetInput(0, border.Output, true);
                mask = blur.Output;
            }
            using var colorEffect = new LookupTable3D(dc) { AlphaMode = Vortice.DCommon.AlphaMode.Straight };
            ID2D1Image kept = image;
            if (color is not null)
            {
                colorEffect.LUT = color;
                colorEffect.SetInput(0, image, true);
                kept = colorEffect.Output;
            }
            using var alphaMask = new AlphaMask(dc);
            alphaMask.SetInput(0, kept, true);
            alphaMask.SetInput(1, mask, true);
            dc.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size.Width, size.Height), AntialiasMode.Aliased);
            dc.DrawImage(alphaMask.Output, InterpolationMode.Linear);
            dc.PopAxisAlignedClip();
        });
        return result;
    }

    public void Dispose()
    {
        matte.Dispose();
        color?.Dispose();
        result?.Dispose();
    }
}
