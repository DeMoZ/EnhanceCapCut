namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's StickerTests.swift; same cases, same expectations.

public class StickerTests
{
    [Fact]
    public void ClockHandsLoopFromTheRestPose()
    {
        var start = ClockSticker.HandAngles(0);
        Assert.Equal(0, start.Minute, 3);
        Assert.Equal(90, start.Hour, 3);
        // Halfway between the first two samples (1/30 s).
        var early = ClockSticker.HandAngles(33_333);
        Assert.Equal(18.5, early.Minute, 0.1);
        Assert.Equal(96.5, early.Hour, 0.1);
        // The hands rest at the end of the loop, then it starts over.
        var rest = ClockSticker.HandAngles(ClockSticker.Period - 50_000);
        Assert.Equal(0, rest.Minute, 3);
        Assert.Equal(90, rest.Hour, 3);
        var again = ClockSticker.HandAngles(ClockSticker.Period + 33_333);
        Assert.Equal(early.Minute, again.Minute, 3);
        Assert.Equal(early.Hour, again.Hour, 3);
    }

    [Fact]
    public void ClockTimeStickersBecomeLayersOtherStickersAreReported()
    {
        var json = """
        {"tracks": [{"type": "sticker", "segments": [
            {"id": "S0", "material_id": "M0", "render_index": 14000, "target_timerange": {"start": 0, "duration": 1000000},
             "clip": {"scale": {"x": 0.25, "y": 0.25}, "transform": {"x": 0.5, "y": 0.1}}},
            {"id": "S1", "material_id": "M1", "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S2", "material_id": "M2", "target_timerange": {"start": 0, "duration": 1000000}}]}],
         "materials": {"stickers": [
            {"id": "M0", "type": "sticker", "name": "Clock Time EN White Outline Progress Bar", "resource_id": "7528282080199904565"},
            {"id": "M1", "type": "sticker", "name": "Clock Time EN Blue White Minimalist", "resource_id": "7528280675909209405"},
            {"id": "M2", "type": "sticker", "name": "Pet Meme", "resource_id": "7616938154540469509"}]}}
        """;
        var timeline = TimelineBuilder.Build(Draft.Parse(json), Path.GetTempPath());
        Assert.Equal(["S0", "S1"], timeline.Layers.Select(l => l.Id).Order());
        var styles = timeline.Layers.Where(l => l.Content is StickerContent)
            .ToDictionary(l => l.Id, l => ((StickerContent)l.Content).Clock.Style);
        Assert.Equal(ClockSticker.ClockStyle.WhiteOutline, styles["S0"]);
        Assert.Equal(ClockSticker.ClockStyle.BlueWhite, styles["S1"]);
        var s0 = timeline.Layers.First(l => l.Id == "S0");
        Assert.Equal(14000, s0.RenderIndex);
        Assert.Equal(0.5, s0.Transform(0).PositionX, 9);
        Assert.Equal(["Sticker: Pet Meme"], timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}"));
    }
}
