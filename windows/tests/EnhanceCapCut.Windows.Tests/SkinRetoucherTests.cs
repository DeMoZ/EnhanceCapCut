using EnhanceCapCut.Windows;
using Vortice.Direct2D1;

namespace EnhanceCapCut.Windows.Tests;

/// <summary>The Direct2D skin retouch (port of the macOS RetouchTests' pixel check). Windows only; elsewhere it does
/// nothing.</summary>
public sealed class SkinRetoucherTests
{
    [Fact]
    public void SoftensSkinOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A checkerboard of two skin tones next to a checkerboard of two blues; blur sigma = 1 px.
        const int w = 800, h = 400;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var odd = (x + y) % 2 == 1;
            (byte R, byte G, byte B) c = x < w / 2 ? (odd ? ((byte)150, (byte)100, (byte)90) : ((byte)130, (byte)85, (byte)78))
                : (odd ? ((byte)60, (byte)90, (byte)200) : ((byte)40, (byte)70, (byte)180));
            pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
        }
        using var graphics = new Graphics();
        using var image = graphics.CreateBitmap(w, h);
        image.CopyFromMemory(pixels, w * 4).CheckError();
        using var retoucher = new SkinRetoucher(graphics);
        var result = retoucher.Apply("S", image);
        Assert.Equal(w, result.PixelSize.Width);
        using var readback = graphics.CreateBitmap(w, h, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
        readback.CopyFromBitmap(result).CheckError();
        var mapped = readback.Map(MapOptions.Read);
        var output = new byte[w * h * 4];
        try
        {
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(mapped.Bits + y * (int)mapped.Pitch, output, y * w * 4, w * 4);
        }
        finally
        {
            readback.Unmap();
        }
        int Contrast(int x)
        {
            int i = (200 * w + x) * 4, j = i + 4;
            return Math.Abs(output[i + 2] - output[j + 2]);
        }
        Assert.True(Contrast(200) < 15, $"skin is smoothed (was 20, now {Contrast(200)})");
        Assert.Equal(20, Contrast(600));
        Assert.Equal(255, output[(200 * w + 200) * 4 + 3]);
    }
}
