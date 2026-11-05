using EnhanceCapCut.Windows;
using Vortice.Direct2D1;

namespace EnhanceCapCut.Windows.Tests;

/// <summary>The Direct2D colour-table effects: skin retouch and chroma key (ports of the macOS pixel checks in
/// RetouchTests and ChromaKeyTests). Windows only; elsewhere they do nothing.</summary>
public sealed class ColorTableEffectTests
{
    /// <summary>A premultiplied BGRA bitmap filled with <paramref name="color"/>(x, y) → RGB.</summary>
    static ID2D1Bitmap1 Bitmap(Graphics graphics, int w, int h, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var c = color(x, y);
            pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
        }
        var bitmap = graphics.CreateBitmap(w, h);
        bitmap.CopyFromMemory(pixels, (uint)(w * 4)).CheckError();
        return bitmap;
    }

    /// <summary>The bitmap's BGRA bytes, top row first.</summary>
    static byte[] Read(Graphics graphics, ID2D1Bitmap1 bitmap)
    {
        int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height;
        using var readback = graphics.CreateBitmap(w, h, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
        readback.CopyFromBitmap(bitmap).CheckError();
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
        return output;
    }

    [Fact]
    public void ChromaKeyRemovesTheScreen()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var graphics = new Graphics();
        using var image = Bitmap(graphics, 16, 8, (x, _) => x < 8 ? ((byte)26, (byte)204, (byte)51) : ((byte)230, (byte)153, (byte)128));
        using var keyer = new ChromaKeyer(graphics, new EnhanceCapCut.Draft.ChromaKey(0.1, 0.8, 0.2, 0.5));
        var output = Read(graphics, keyer.Apply(image));
        var row = 4 * 16 * 4;
        Assert.True(output[row + 1 * 4 + 3] == 0, $"screen is transparent (alpha {output[row + 1 * 4 + 3]})");
        Assert.True(output[row + 14 * 4 + 3] == 255, $"subject is opaque (alpha {output[row + 14 * 4 + 3]})");
    }

    [Fact]
    public void SoftensSkinOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A checkerboard of two skin tones next to a checkerboard of two blues; blur sigma = 1 px.
        const int w = 800, h = 400;
        using var graphics = new Graphics();
        using var image = Bitmap(graphics, w, h, (x, y) =>
        {
            var odd = (x + y) % 2 == 1;
            return x < w / 2 ? (odd ? ((byte)150, (byte)100, (byte)90) : ((byte)130, (byte)85, (byte)78))
                : (odd ? ((byte)60, (byte)90, (byte)200) : ((byte)40, (byte)70, (byte)180));
        });
        using var retoucher = new SkinRetoucher(graphics);
        var result = retoucher.Apply("S", image);
        Assert.Equal(w, result.PixelSize.Width);
        var output = Read(graphics, result);
        int Contrast(int x)
        {
            int i = (200 * w + x) * 4, j = i + 4;
            return Math.Abs(output[i + 2] - output[j + 2]);
        }
        Assert.True(Contrast(200) < 15, $"skin is smoothed (was 20, now {Contrast(200)})");
        Assert.True(Contrast(600) == 20, $"blue is kept (now {Contrast(600)})");
        Assert.Equal(255, output[(200 * w + 200) * 4 + 3]);
    }
}
