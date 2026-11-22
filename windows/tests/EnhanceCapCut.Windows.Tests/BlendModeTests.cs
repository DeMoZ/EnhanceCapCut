using EnhanceCapCut.Windows;
using Vortice.Direct2D1;

namespace EnhanceCapCut.Windows.Tests;

/// <summary>The Direct2D overlay blend (port of the macOS pixel check in BlendModeTests). Windows only; elsewhere it
/// does nothing.</summary>
public sealed class BlendModeTests
{
    static ID2D1Bitmap1 Solid(Graphics graphics, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Premultiplied BGRA.
            pixels[i] = (byte)(b * a / 255); pixels[i + 1] = (byte)(g * a / 255); pixels[i + 2] = (byte)(r * a / 255); pixels[i + 3] = a;
        }
        var bitmap = graphics.CreateBitmap(4, 4);
        bitmap.CopyFromMemory(pixels, 16).CheckError();
        return bitmap;
    }

    static int[] Blend(Graphics graphics, EnhanceCapCut.Draft.BlendMode mode, byte alpha)
    {
        using var backdrop = Solid(graphics, 128, 128, 128, 255);
        using var layer = Solid(graphics, 204, 51, 128, alpha);
        using var target = graphics.CreateBitmap(4, 4, BitmapOptions.Target);
        graphics.DrawInto(target, dc => Compositor.DrawBlended(dc, backdrop, layer, mode));
        using var readback = graphics.CreateBitmap(4, 4, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
        readback.CopyFromBitmap(target).CheckError();
        var mapped = readback.Map(MapOptions.Read);
        var bgra = new byte[4];
        try { System.Runtime.InteropServices.Marshal.Copy(mapped.Bits, bgra, 0, 4); }
        finally { readback.Unmap(); }
        return [bgra[2], bgra[1], bgra[0], bgra[3]];
    }

    static void AssertClose(int[] expected, int[] actual, string message) =>
        Assert.True(expected.Zip(actual).All(p => Math.Abs(p.First - p.Second) <= 2),
            $"{message}: [{string.Join(", ", actual)}] vs [{string.Join(", ", expected)}]");

    [Fact]
    public void BlendsSrgbValues()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var graphics = new Graphics();
        AssertClose([230, 153, 191, 255], Blend(graphics, EnhanceCapCut.Draft.BlendMode.Screen, 255), "screen = 1 − (1 − a)(1 − b)");
        AssertClose([102, 26, 64, 255], Blend(graphics, EnhanceCapCut.Draft.BlendMode.Multiply, 255), "multiply = a · b");
        AssertClose([128, 51, 128, 255], Blend(graphics, EnhanceCapCut.Draft.BlendMode.Darken, 255), "darken = min");
        AssertClose([204, 128, 128, 255], Blend(graphics, EnhanceCapCut.Draft.BlendMode.Lighten, 255), "lighten = max");
        AssertClose([179, 140, 160, 255], Blend(graphics, EnhanceCapCut.Draft.BlendMode.Screen, 128),
            "opacity mixes the blended colour with the backdrop");
    }
}
