namespace EnhanceCapCut.Draft.Tests;

public class ExportSettingsTests
{
    [Fact]
    public void OutputSize()
    {
        var s = new ExportSettings();
        Assert.Equal((1080, 1920), s.OutputSize(1080, 1920));
        s.Aspect = ExportSettings.AspectRatio.Landscape;
        Assert.Equal((1920, 1080), s.OutputSize(1080, 1920));
        s.Aspect = ExportSettings.AspectRatio.Square;
        s.OutputResolution = ExportSettings.Resolution.P720;
        Assert.Equal((720, 720), s.OutputSize(1080, 1920));
        s.Aspect = ExportSettings.AspectRatio.Project;
        s.OutputResolution = ExportSettings.Resolution.P2160;
        Assert.Equal((2160, 2880), s.OutputSize(1440, 1920));
    }

    [Fact]
    public void FileNameTemplate()
    {
        var s = new ExportSettings { Codec = ExportSettings.VideoCodec.H264, FileNameTemplate = "{project}-{res}-{fps}-{codec}" };
        Assert.Equal("Trip-1080x1920-30-h264.mp4", s.FileName("Trip", 1080, 1920, 30));
        s.FileNameTemplate = "{project}: {fps}?";
        Assert.Equal("Trip- 29.97-.mp4", s.FileName("Trip", 1080, 1920, 29.97));
    }

    [Fact]
    public void FileNameUsesGregorianDates()
    {
        var s = new ExportSettings { FileNameTemplate = "{date}" };
        Assert.StartsWith("2026-10-15", s.FileName("P", 0, 0, 30, new DateTime(2026, 10, 15, 12, 0, 0)));
    }

    [Fact]
    public void DecodingOldSettingsKeepsDefaults()
    {
        var decoded = ExportSettings.Parse("""{"codec":"h264","quality":55,"aspect":"prores-only-value"}""");
        Assert.Equal(ExportSettings.VideoCodec.H264, decoded.Codec);
        Assert.Equal(55, decoded.Quality);
        Assert.True(decoded.BurnInSubtitles);
        Assert.Equal(ExportSettings.AspectRatio.Project, decoded.Aspect);
        // Settings round-trip through their JSON.
        var copy = ExportSettings.Parse(decoded.ToJson());
        Assert.Equal(decoded.ToJson(), copy.ToJson());
    }
}
