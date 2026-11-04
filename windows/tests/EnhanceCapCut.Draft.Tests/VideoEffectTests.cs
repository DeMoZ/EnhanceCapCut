using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

public class VideoEffectTests
{
    [Fact]
    public void ClipEffectsAreRenderedOthersReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-fx-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            var escaped = photo.Replace("\\", "\\\\");
            var json = $$$"""
            {"tracks": [
              {"type": "video", "segments": [{"id": "S", "material_id": "V", "extra_material_refs": ["E1", "E2", "E3"],
                                              "target_timerange": {"start": 0, "duration": 2000000}}]},
              {"type": "effect", "segments": [{"id": "T", "material_id": "E4",
                                               "target_timerange": {"start": 0, "duration": 1000000}}]}],
             "materials": {"videos": [{"id": "V", "type": "photo", "path": "{{{escaped}}}"}],
              "video_effects": [
                {"id": "E1", "name": "Pudding Bounce", "effect_id": "7534579654615649553", "apply_target_type": 0},
                {"id": "E2", "name": "Hazy Rotation", "effect_id": "7486402432189091127", "apply_target_type": 0,
                 "adjust_params": [{"name": "effects_adjust_speed", "value": 0.6666666666}]},
                {"id": "E3", "name": "Card Slash", "effect_id": "7474875426633026821", "apply_target_type": 0},
                {"id": "E4", "name": "Hazy Rotation", "effect_id": "7486402432189091127", "apply_target_type": 2}]}}
            """;
            var timeline = TimelineBuilder.Build(Draft.Parse(json), dir);
            var layer = timeline.Layers.Single(l => l.Id == "S");
            Assert.Equal([VideoEffectKind.PuddingBounce, VideoEffectKind.HazyRotation], layer.Effects.Select(e => e.Kind));
            Assert.Equal(2, layer.Effects[1].Speed, 6);
            var found = timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}").ToHashSet();
            // Unknown effects and effect-track effects are still reported.
            Assert.Equal(["Video effect: Card Slash", "Video effect: Hazy Rotation"], found.Order());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EffectsOpenTheClipAndSettle()
    {
        var pudding = new VideoEffect(VideoEffectKind.PuddingBounce);
        var first = pudding.State(0);
        Assert.True(first.Zoom > 1.3);
        Assert.True(first.Flash > 0.5);
        Assert.True(first.Blur > 0);
        Assert.True(pudding.State(1_000_000).IsIdentity);

        var hazy = new VideoEffect(VideoEffectKind.HazyRotation);
        Assert.Equal(2, hazy.State(0).Zoom, 9);
        Assert.True(hazy.State(0).Rotation < -10);
        Assert.True(hazy.State(400_000).IsIdentity);
        // Twice the speed: the same look at half the time.
        Assert.Equal(hazy.State(200_000), new VideoEffect(VideoEffectKind.HazyRotation, Speed: 2).State(100_000));
        Assert.True(new VideoEffect(VideoEffectKind.HazyRotation, Start: 500_000).State(100_000).IsIdentity);
    }
}
