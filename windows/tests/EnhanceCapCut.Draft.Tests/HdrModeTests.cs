using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's HDRModeTests.swift; same cases, same expectations.

public class HdrModeTests
{
    [Fact]
    public void HdrModeZeroTurnsToneMappingOff()
    {
        var photo = Path.Combine(Path.GetTempPath(), $"ecc-hdr-{Guid.NewGuid()}.png");
        File.WriteAllBytes(photo, [0]);
        try
        {
            static string Segment(string id, string settings) =>
                $"{{\"id\": \"{id}\", \"material_id\": \"V1\", \"target_timerange\": {{\"start\": 0, \"duration\": 1000000}}{settings}}}";
            var json = $$"""
            {
              "tracks": [{"type": "video", "segments": [
                {{Segment("Off", ", \"hdr_settings\": {\"mode\": 0, \"intensity\": 1.0, \"nits\": 1000}")}},
                {{Segment("On", ", \"hdr_settings\": {\"mode\": 4, \"intensity\": 1.0, \"nits\": 1000}")}},
                {{Segment("None", "")}}]}],
              "materials": {"videos": [{"id": "V1", "type": "photo", "path": "{{photo.Replace("\\", "\\\\")}}"}]}
            }
            """;
            var timeline = TimelineBuilder.Build(Draft.Parse(json), Path.GetTempPath());
            var toneMaps = timeline.Layers.ToDictionary(l => l.Id, l => l.ToneMapsHdr);
            Assert.Equal(new Dictionary<string, bool> { ["Off"] = false, ["On"] = true, ["None"] = true }, toneMaps);
        }
        finally { File.Delete(photo); }
    }
}
