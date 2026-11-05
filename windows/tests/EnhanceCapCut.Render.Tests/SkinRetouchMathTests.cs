using EnhanceCapCut.Render;

namespace EnhanceCapCut.Render.Tests;

// Ported from the macOS version's RetouchTests.swift.

public class SkinRetouchMathTests
{
    [Fact]
    public void SkinWeight()
    {
        Assert.Equal(SkinRetouchMath.Strength, SkinRetouchMath.Weight(0.545, 0.361, 0.337), 9);
        Assert.True(SkinRetouchMath.Weight(0.85, 0.65, 0.55) > 0.4, "light skin");
        Assert.True(SkinRetouchMath.Weight(0.35, 0.22, 0.16) > 0.4, "dark skin");
        Assert.Equal(0, SkinRetouchMath.Weight(0.31, 0.48, 0.69)); // sky
        Assert.Equal(0, SkinRetouchMath.Weight(0.5, 0.5, 0.5)); // grey
        Assert.Equal(0, SkinRetouchMath.Weight(0.78, 0.16, 0.2)); // saturated red
        Assert.Equal(0, SkinRetouchMath.Weight(0.94, 0.59, 0.16)); // orange
        Assert.Equal(0, SkinRetouchMath.Weight(0.14, 0.33, 0.29)); // green
    }

    [Fact]
    public void TableHasTheWeightAsAlpha()
    {
        var table = SkinRetouchMath.MatteTable(4);
        Assert.Equal(4 * 4 * 4 * 4, table.Length);
        // Blue changes fastest: entry (r, g, b) is at ((r·4 + g)·4 + b)·4.
        var i = ((3 * 4 + 2) * 4 + 1) * 4;
        Assert.Equal((float)SkinRetouchMath.Weight(1, 2 / 3.0, 1 / 3.0), table[i + 3]);
        Assert.Equal(1, table[i]);
    }
}
