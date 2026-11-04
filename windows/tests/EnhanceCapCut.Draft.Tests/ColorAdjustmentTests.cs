using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's ColorAdjustmentTests.swift; same cases, same expectations.

public class ColorAdjustmentTests
{
    /// <summary>A clip with one HSL material per value of <paramref name="hsl"/> (hue, saturation, lightness).</summary>
    static HashSet<string> Report(params (double Hue, double Saturation, double Lightness)[] hsl)
    {
        var refs = string.Join(",", hsl.Select((_, i) => $"\"H{i}\""));
        var materials = string.Join(",", hsl.Select((v, i) =>
            $"{{\"id\": \"H{i}\", \"type\": \"hsl\", \"hsl_color_type\": 1, \"hue\": {v.Hue}, \"saturation\": {v.Saturation}, \"lightness\": {v.Lightness}}}"));
        var json = $$"""
        {
          "tracks": [{"type": "video", "segments": [
            {"id": "S1", "material_id": "V1", "target_timerange": {"start": 0, "duration": 1000000},
             "extra_material_refs": [{{refs}}]}]}],
          "materials": {"videos": [{"id": "V1", "type": "video", "path": "/nonexistent/clip.mov"}], "hsl": [{{materials}}]}
        }
        """;
        var timeline = TimelineBuilder.Build(Draft.Parse(json), Path.GetTempPath());
        return timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}").ToHashSet();
    }

    [Fact]
    public void NeutralHslIsNotReported() =>
        Assert.DoesNotContain("HSL color: color adjustment", Report((0, 0, 0)));

    [Fact]
    public void ChangedHslIsReported() =>
        Assert.Contains("HSL color: color adjustment", Report((0, 0, 0), (0, 25, 0)));
}
