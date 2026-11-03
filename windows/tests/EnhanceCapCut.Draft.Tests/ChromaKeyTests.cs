using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's ChromaKeyTests.swift.

public class ChromaKeyTests
{
    [Fact]
    public void ChromaMaterialBecomesTheLayersKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-chroma-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            var json = """
            {"tracks": [{"type": "video", "segments": [
                {"id": "S0", "material_id": "V", "extra_material_refs": ["C0"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S1", "material_id": "V", "extra_material_refs": ["C1"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S2", "material_id": "V", "extra_material_refs": ["C2"], "target_timerange": {"start": 0, "duration": 1000000}}]}],
             "materials": {"videos": [{"id": "V", "type": "photo", "path": "PHOTO"}],
              "chromas": [
                {"id": "C0", "type": "chroma", "color": "#FF00FF00", "intensity_value": 0.4, "shadow_value": 0.2,
                 "edge_smooth_value": 0.3, "spill_value": 1.5},
                {"id": "C1", "type": "chroma", "color": "", "intensity_value": 0.4},
                {"id": "C2", "type": "chroma", "color": "#00FF00", "intensity_value": 0}]}}
            """.Replace("PHOTO", photo.Replace("\\", "\\\\"));
            var timeline = TimelineBuilder.Build(Draft.Parse(json), dir);
            var keys = timeline.Layers.ToDictionary(l => l.Id, l => l.ChromaKey);
            Assert.Equal(new ChromaKey(0, 1, 0, 0.4, 0.2, 0.3, 1), keys["S0"]);
            // No colour picked, or intensity 0: the clip is shown as it is.
            Assert.Null(keys["S1"]);
            Assert.Null(keys["S2"]);
            Assert.Empty(timeline.Report.Unsupported);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
