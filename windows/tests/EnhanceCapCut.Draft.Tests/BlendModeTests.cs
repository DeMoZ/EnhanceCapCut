using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's BlendModeTests.swift.

public class BlendModeTests
{
    [Fact]
    public void MixModeMaterialBecomesTheLayersBlendMode()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-blend-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            var json = """
            {"tracks": [{"type": "video", "segments": [
                {"id": "S0", "material_id": "V", "extra_material_refs": ["M0"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S1", "material_id": "V", "extra_material_refs": ["M1"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S2", "material_id": "V", "extra_material_refs": ["M2"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S3", "material_id": "V", "extra_material_refs": ["M3"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S4", "material_id": "V", "extra_material_refs": ["M4"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S5", "material_id": "V", "target_timerange": {"start": 0, "duration": 1000000}}]}],
             "materials": {"videos": [{"id": "V", "type": "photo", "path": "PHOTO"}],
              "effects": [
                {"id": "M0", "type": "mix_mode", "name": "Screen", "effect_id": "871339", "resource_id": "6758325170760323597"},
                {"id": "M1", "type": "mix_mode", "name": "", "resource_id": "6758325895519277582"},
                {"id": "M2", "type": "mix_mode", "name": "Soft light"},
                {"id": "M3", "type": "mix_mode", "name": "Normal"},
                {"id": "M4", "type": "mix_mode", "name": "Vivid", "effect_id": "1", "resource_id": "2"}]}}
            """.Replace("PHOTO", photo.Replace("\\", "\\\\"));
            var timeline = TimelineBuilder.Build(Draft.Parse(json), dir);
            var modes = timeline.Layers.ToDictionary(l => l.Id, l => l.BlendMode);
            Assert.Equal(BlendMode.Screen, modes["S0"]);
            Assert.Equal(BlendMode.Multiply, modes["S1"]);
            Assert.Equal(BlendMode.SoftLight, modes["S2"]);
            Assert.Equal(BlendMode.Normal, modes["S3"]);
            Assert.Null(modes["S4"]);
            Assert.Null(modes["S5"]);
            // Only the unknown mode is reported.
            Assert.Equal(["Blend mode/Vivid"], timeline.Report.Unsupported.Select(i => i.Id));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
