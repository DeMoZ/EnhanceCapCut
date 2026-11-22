using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's StabilizationTests.swift.
public class StabilizationTests
{
    [Fact]
    public void StabilizedVideoClipsAreNotReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-stable-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "v.mov"), photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(video, [0]);
            File.WriteAllBytes(photo, [0]);
            string Escaped(string p) => p.Replace("\\", "\\\\");
            var json = $$$"""
            {"tracks": [{"type": "video", "segments": [
               {"id": "S1", "material_id": "V1", "target_timerange": {"start": 0, "duration": 1000000}},
               {"id": "S2", "material_id": "V2", "target_timerange": {"start": 1000000, "duration": 1000000}},
               {"id": "S3", "material_id": "V3", "target_timerange": {"start": 2000000, "duration": 1000000}}]}],
             "materials": {"videos": [
               {"id": "V1", "type": "video", "path": "{{{Escaped(video)}}}", "stable": {"stable_level": 3, "matrix_path": ""}},
               {"id": "V2", "type": "video", "path": "{{{Escaped(video)}}}"},
               {"id": "V3", "type": "photo", "path": "{{{Escaped(photo)}}}", "stable": {"stable_level": 2}}]}}
            """;
            var timeline = TimelineBuilder.Build(Draft.Parse(json), dir);
            Assert.Equal([3, 0, 0], timeline.Layers.Select(l => l.Stabilization));
            Assert.Equal(["Stabilization: level 2"], timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
