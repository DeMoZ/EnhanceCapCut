using EnhanceCapCut.Render;

namespace EnhanceCapCut.Render.Tests;

public class QualityEnhanceMathTests
{
    [Fact]
    public void UpscalesOnlySmallFrames()
    {
        Assert.Equal(2, QualityEnhanceMath.Scale(360, 640));
        Assert.Equal(2, QualityEnhanceMath.Scale(1280, 720));
        Assert.Equal(1, QualityEnhanceMath.Scale(1080, 1920));
    }
}
