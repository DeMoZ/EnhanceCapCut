using System.Runtime.InteropServices;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// CapCut's automatic skin retouch, approximated (port of the macOS SkinRetouch; see FORMAT.md › Retouch and makeup):
/// a blurred copy of the frame drawn over it through the <see cref="SkinRetouchMath"/> skin matte.
/// </summary>
public sealed class SkinRetoucher : IDisposable
{
    readonly Graphics graphics;
    readonly ID2D1LookupTable3D matte;
    readonly Dictionary<string, ID2D1Bitmap1> results = [];

    public SkinRetoucher(Graphics graphics)
    {
        this.graphics = graphics;
        using var context = graphics.Context.QueryInterface<ID2D1DeviceContext2>();
        const uint n = SkinRetouchMath.CubeSize;
        var bytes = MemoryMarshal.AsBytes(SkinRetouchMath.MatteTable().AsSpan()).ToArray();
        // Row = one run of the fastest-changing (blue) axis, plane = rows of green.
        matte = context.CreateLookupTable3D(BufferPrecision.PerChannel32Float, [n, n, n], bytes, (uint)bytes.Length,
            [n * 16, n * n * 16]);
    }

    /// <summary><paramref name="image"/> with its skin smoothed, in a bitmap kept for clip <paramref name="id"/>.</summary>
    public ID2D1Bitmap1 Apply(string id, ID2D1Bitmap1 image)
    {
        var size = image.PixelSize;
        if (!results.TryGetValue(id, out var result) || result.PixelSize.Width != size.Width || result.PixelSize.Height != size.Height)
        {
            result?.Dispose();
            result = graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target);
            results[id] = result;
        }
        var sigma = (float)(SkinRetouchMath.BlurRadius * Math.Min(size.Width, size.Height));
        graphics.DrawInto(result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            var bounds = new Vortice.RawRectF(0, 0, size.Width, size.Height);
            dc.DrawBitmap(image, bounds, 1, InterpolationMode.NearestNeighbor, bounds, null);
            using var border = new Border(dc) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp };
            border.SetInput(0, image, true);
            using var blur = new GaussianBlur(dc) { StandardDeviation = sigma, BorderMode = BorderMode.Hard };
            blur.SetInput(0, border.Output, true);
            using var matteEffect = new LookupTable3D(dc) { LUT = matte, AlphaMode = Vortice.DCommon.AlphaMode.Straight };
            matteEffect.SetInput(0, image, true);
            using var matteBorder = new Border(dc) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp };
            matteBorder.SetInput(0, matteEffect.Output, true);
            using var matteBlur = new GaussianBlur(dc) { StandardDeviation = sigma, BorderMode = BorderMode.Hard };
            matteBlur.SetInput(0, matteBorder.Output, true);
            using var alphaMask = new AlphaMask(dc);
            alphaMask.SetInput(0, blur.Output, true);
            alphaMask.SetInput(1, matteBlur.Output, true);
            dc.PushAxisAlignedClip(bounds, AntialiasMode.Aliased);
            dc.DrawImage(alphaMask.Output, InterpolationMode.Linear);
            dc.PopAxisAlignedClip();
        });
        return result;
    }

    /// <summary>Frees the bitmaps of clips no longer on screen.</summary>
    public void Release(IReadOnlySet<string> active)
    {
        foreach (var id in results.Keys.Where(id => !active.Contains(id)).ToList()) { results[id].Dispose(); results.Remove(id); }
    }

    public void Dispose()
    {
        matte.Dispose();
        foreach (var r in results.Values) r.Dispose();
    }
}
