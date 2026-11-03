using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;

namespace EnhanceCapCut.Render.Tests;

// Ported from the macOS version's ChromaKeyTests.swift.

public class ChromaKeyMathTests
{
    [Fact]
    public void MatteAndSpill()
    {
        var green = new ChromaKey(0.1, 0.8, 0.2, 0.5);
        // The picked colour and nearby greens are removed; skin and blue are kept.
        Assert.Equal(0, ChromaKeyMath.Alpha(green, 0.1, 0.8, 0.2));
        Assert.Equal(0, ChromaKeyMath.Alpha(green, 0.15, 0.7, 0.2));
        Assert.Equal(1, ChromaKeyMath.Alpha(green, 0.9, 0.6, 0.5));
        Assert.Equal(1, ChromaKeyMath.Alpha(green, 0.2, 0.3, 0.9));
        Assert.Equal(1, ChromaKeyMath.Alpha(green with { Intensity = 0 }, 0.1, 0.8, 0.2));
        // Low intensity removes only the colour itself.
        Assert.True(ChromaKeyMath.Alpha(green with { Intensity = 0.05 }, 0.15, 0.6, 0.2) > 0.5);
        Assert.Equal(0, ChromaKeyMath.Alpha(green with { Intensity = 1 }, 0.02, 0.2, 0.05));
        // "Shadow" keeps dark pixels.
        Assert.Equal(1, ChromaKeyMath.Alpha(green with { Intensity = 1, Shadow = 1 }, 0.02, 0.2, 0.05));
        // A white screen is keyed by brightness.
        var white = new ChromaKey(1, 1, 1, 0.3);
        Assert.Equal(0, ChromaKeyMath.Alpha(white, 0.95, 0.95, 0.95));
        Assert.Equal(1, ChromaKeyMath.Alpha(white, 0.3, 0.3, 0.3));

        var spill = green with { Spill = 1 };
        var fixedColor = ChromaKeyMath.Despill(spill, 0.6, 0.7, 0.5);
        Assert.True(fixedColor.G < 0.7);
        Assert.Equal(0.299 * 0.6 + 0.587 * 0.7 + 0.114 * 0.5,
            0.299 * fixedColor.R + 0.587 * fixedColor.G + 0.114 * fixedColor.B, 2);
        // Colours away from the key are left alone.
        Assert.Equal((0.8, 0.2, 0.8), ChromaKeyMath.Despill(spill, 0.8, 0.2, 0.8));
    }

    [Fact]
    public void TablesRunBlueFastest()
    {
        var key = new ChromaKey(0, 1, 0, 0.5, Spill: 1);
        var matte = ChromaKeyMath.MatteTable(key, 3);
        var color = ChromaKeyMath.ColorTable(key, 3);
        Assert.Equal(3 * 3 * 3 * 4, matte.Length);
        // Entry (r, g, b) is at ((r * 3 + g) * 3 + b) * 4; pure green (0, 2, 0) is removed, pure blue (0, 0, 2) kept.
        Assert.Equal(0f, matte[(2 * 3 + 0) * 4 + 3]);
        Assert.Equal(1f, matte[(0 * 3 + 0) * 3 * 4 + 2 * 4 + 3]);
        Assert.Equal(1f, color[3]);
        Assert.Equal(1f, color[(2 * 9 + 2 * 3 + 2) * 4]);
    }
}
