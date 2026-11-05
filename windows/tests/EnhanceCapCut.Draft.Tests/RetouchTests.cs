namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's RetouchTests.swift.

public class RetouchTests
{
    [Fact]
    public void SkinRetouchIsRenderedAndMakeupLooksAreReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-retouch-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            var json = """
            {"tracks": [{"type": "video", "segments": [
                {"id": "S0", "material_id": "V", "extra_material_refs": ["E0", "E1", "R"], "target_timerange": {"start": 0, "duration": 1000000}},
                {"id": "S1", "material_id": "V", "extra_material_refs": ["E2", "R", "D"], "target_timerange": {"start": 1000000, "duration": 1000000}}]}],
             "materials": {"videos": [{"id": "V", "type": "photo", "path": "PHOTO"}],
              "effects": [
                {"id": "E0", "type": "figure", "sub_type": "auto_beauty", "name": "Sparkly Skin"},
                {"id": "E1", "type": "figure", "sub_type": "auto_beauty", "name": "", "intensity_key": "face_adjust_fuling"},
                {"id": "E2", "type": "figure", "sub_type": "exclusion_face", "name": "Cherry Cola"},
                {"id": "R", "type": "makeup_root", "sub_type": "auto_beauty", "name": "makeup-root"}],
              "manual_deformations": [{"id": "D", "type": "manual_deformation"}]}}
            """.Replace("PHOTO", photo.Replace("\\", "\\\\"));
            var timeline = TimelineBuilder.Build(Draft.Parse(json), dir);
            var retouch = timeline.Layers.ToDictionary(l => l.Id, l => l.RetouchesSkin);
            Assert.True(retouch["S0"]);
            // A makeup look alone is not a skin retouch.
            Assert.False(retouch["S1"]);
            Assert.Equal(["Beauty / makeup: Cherry Cola ×1", "Manual deformation: body/face reshape ×1"],
                timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name} ×{i.Count}").Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
