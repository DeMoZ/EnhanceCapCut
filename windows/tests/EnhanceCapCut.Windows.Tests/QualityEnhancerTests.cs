using EnhanceCapCut.Windows;
using Vortice.Direct2D1;

namespace EnhanceCapCut.Windows.Tests;

/// <summary>The Direct2D "Enhance quality" (port of the macOS QualityEnhanceTests' pixel check). Windows only;
/// elsewhere it does nothing.</summary>
public sealed class QualityEnhancerTests
{
    [Fact]
    public void UpscalesAndSharpensEdges()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A soft vertical edge: dark left half, light right half, a 4 px ramp between.
        const int w = 64, h = 16;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = (byte)Math.Clamp(60 + (x - 30) * 30, 60, 180);
            pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 255;
        }
        using var graphics = new Graphics();
        using var image = graphics.CreateBitmap(w, h);
        image.CopyFromMemory(pixels, w * 4).CheckError();
        using var enhancer = new QualityEnhancer(graphics);
        var result = enhancer.Apply("S", image);
        const int ow = 2 * w, oh = 2 * h;
        Assert.Equal(ow, result.PixelSize.Width);
        Assert.Equal(oh, result.PixelSize.Height);
        using var readback = graphics.CreateBitmap(ow, oh, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
        readback.CopyFromBitmap(result).CheckError();
        var mapped = readback.Map(MapOptions.Read);
        var output = new byte[ow * oh * 4];
        try
        {
            for (var y = 0; y < oh; y++)
                System.Runtime.InteropServices.Marshal.Copy(mapped.Bits + y * (int)mapped.Pitch, output, y * ow * 4, ow * 4);
        }
        finally
        {
            readback.Unmap();
        }
        var row = Enumerable.Range(0, ow).Select(x => (int)output[(h * ow + x) * 4 + 2]).ToList();
        Assert.True(row.Min() < 55, $"the dark side overshoots below 60 next to the edge (min {row.Min()})");
        Assert.True(row.Max() > 185, $"the light side overshoots above 180 next to the edge (max {row.Max()})");
        Assert.InRange(row[4], 58, 62);
        Assert.InRange(row[ow - 5], 178, 182);
        Assert.Equal(255, output[(h * ow + 4) * 4 + 3]);
    }
}
