using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;

namespace EnhanceCapCut.Render.Tests;

// Ported from the macOS version's ColorFilterTests.swift.

public class ColorLutTests
{
    [Fact]
    public void ReadsLookupPixelsAndCubes()
    {
        // A 64×64 grid (N = 16) with red inverted, as BGRA.
        var layout = ColorFilter.LayoutFor(64, 64)!;
        var pixels = new byte[64 * 64 * 4];
        for (var b = 0; b < 16; b++)
        for (var g = 0; g < 16; g++)
        for (var r = 0; r < 16; r++)
        {
            var (x, y) = layout.Pixel(r, g, b);
            var o = (y * 64 + x) * 4;
            pixels[o + 2] = (byte)(255 - r * 17);
            pixels[o + 1] = (byte)(g * 17);
            pixels[o] = (byte)(b * 17);
        }
        var table = ColorLut.FromPixels(pixels, 64, 64, 64 * 4, 4, bgr: true)!.Value;
        Assert.Equal(16, table.Size);
        var i = ((15 * 16 + 0) * 16 + 15) * 3;
        Assert.Equal(0, table.Rgb[i], 0.01);
        Assert.Equal(1, table.Rgb[i + 2], 0.01);
        Assert.Null(ColorLut.FromPixels(pixels, 60, 60, 60 * 4));

        var cube = ColorLut.ParseCube("""
        # comment
        TITLE "x"
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """)!.Value;
        Assert.Equal(2, cube.Size);
        Assert.Equal(24, cube.Rgb.Length);
        Assert.Null(ColorLut.ParseCube("LUT_3D_SIZE 2\n0 0 0\n")); // too few entries
    }

    [Fact]
    public void IntensityBlendsWithTheOriginal()
    {
        // A 2-entry table that maps everything to black, at half intensity: halfway between identity and black.
        var lut = ColorLut.Blend(2, new float[24], 0.5);
        Assert.Equal(32, lut.Data.Length);
        Assert.Equal([0.5f, 0.5f, 0.5f, 1f], lut.Data[28..32]);
        Assert.Equal(0.5f, lut.Data[4]); // entry (1,0,0) red
        Assert.Equal(0f, lut.Data[5]);
    }
}
