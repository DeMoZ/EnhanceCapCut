namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's QualityEnhanceTests.swift.

public class QualityEnhanceTests
{
    [Fact]
    public void EnhanceQualityIsRenderedAndOtherAIProcessingIsReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-enhance-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            var json = """
            {"tracks": [{"type": "video", "segments": [
                {"id": "S0", "material_id": "V0", "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S1", "material_id": "V1", "target_timerange": {"start": 1000000, "duration": 1000000}}]}],
             "materials": {"videos": [
                {"id": "V0", "type": "photo", "path": "PHOTO", "video_algorithm": {"algorithms": [{"type": "QualityEnhance"}],
                 "path": "##_draftpath_placeholder_X_##/Resources/videoAlg/a_0_1000000.mp4", "quality_enhance": {"level": 0}}},
                {"id": "V1", "type": "photo", "path": "PHOTO", "video_algorithm": {"algorithms": [{"type": "GenAiSmartEdit"}]}}]}}
            """.Replace("PHOTO", photo.Replace("\\", "\\\\"));
            var timeline = TimelineBuilder.Build(Draft.Parse(json), dir);
            var enhance = timeline.Layers.ToDictionary(l => l.Id, l => l.EnhancesQuality);
            Assert.True(enhance["S0"]);
            Assert.False(enhance["S1"]);
            Assert.Equal(["AI processing: GenAiSmartEdit ×1"],
                timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name} ×{i.Count}"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
