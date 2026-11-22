using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's ClipAnimationTests.swift; same cases, same expectations.

public class ClipAnimationTests
{
    static readonly LayerTransform Identity = new(1, 1, 0, 0, 0, false, false, 1);

    /// <summary>A 2 s photo clip on the main track with the given <paramref name="animations"/>.</summary>
    static Timeline Build(string animations)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-anim-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            var escaped = photo.Replace("\\", "\\\\");
            var json = $$$"""
                {"tracks": [{"type": "video", "segments": [
                   {"id": "S1", "material_id": "V1", "target_timerange": {"start": 1000000, "duration": 2000000},
                    "clip": {"scale": {"x": 0.8, "y": 0.8}, "transform": {"x": 0.1, "y": 0}, "alpha": 1},
                    "extra_material_refs": ["M1"]}]}],
                 "materials": {
                   "videos": [{"id": "V1", "type": "photo", "path": "{{{escaped}}}"}],
                   "material_animations": [{"id": "M1", "type": "sticker_animation", "animations": [{{{animations}}}]}]}}
                """;
            return TimelineBuilder.Build(Draft.Parse(json), dir);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParsesInAndOutAndReportsTheRestByName()
    {
        var timeline = Build("""
            {"type": "in", "name": "Zoom In", "duration": 500000, "start": 0},
            {"type": "out", "name": "Fade Out", "duration": 400000, "start": 0},
            {"type": "group", "name": "Swing", "duration": 2000000}
            """);
        var layer = Assert.Single(timeline.Layers);
        Assert.Equal(
        [
            new ClipAnimation(ClipAnimation.AnimationKind.In, ClipAnimation.AnimationPreset.ZoomIn, 0, 500_000),
            new ClipAnimation(ClipAnimation.AnimationKind.Out, ClipAnimation.AnimationPreset.Fade, 1_600_000, 2_000_000),
        ], layer.Animations);
        Assert.Equal(["Animation: Swing"], timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}"));
    }

    [Fact]
    public void TransformOverTime()
    {
        var layer = Assert.Single(Build("""
            {"type": "in", "name": "Slide Right", "duration": 500000},
            {"type": "out", "name": "Fade Out", "duration": 500000}
            """).Layers);
        // An In "Slide Right" arrives from the left, off-canvas, and ends at the clip's own position.
        Assert.Equal(0.1 - ClipAnimation.SlideDistance, layer.Transform(1_000_000).PositionX, 9);
        Assert.Equal(0.1 - ClipAnimation.SlideDistance / 8, layer.Transform(1_250_000).PositionX, 9);
        Assert.Equal(0.1, layer.Transform(1_500_000).PositionX, 9);
        // Nothing runs mid-clip.
        Assert.Equal(layer.Transform(1_500_000), layer.Transform(2_000_000));
        Assert.Equal(0.8, layer.Transform(2_000_000).ScaleX, 9);
        // The Out fades in the clip's last 0.5 s.
        Assert.Equal(1 - 0.125, layer.Transform(2_750_000).Alpha, 9);
        Assert.Equal(0, layer.Transform(2_999_999).Alpha, 4);
    }

    [Fact]
    public void InAndOutLongerThanTheClipAreSqueezed()
    {
        var layer = Assert.Single(Build("""
            {"type": "in", "name": "Fade In", "duration": 3000000},
            {"type": "out", "name": "Zoom Out", "duration": 1000000}
            """).Layers);
        Assert.Equal([(0L, 1_500_000L), (1_500_000L, 2_000_000L)], layer.Animations.Select(a => (a.Start, a.End)));
    }

    [Fact]
    public void ZoomAndRotate()
    {
        static LayerTransform At(ClipAnimation.AnimationKind kind, ClipAnimation.AnimationPreset preset, double p) =>
            new ClipAnimation(kind, preset, 0, 1).Apply(Identity, p);
        const ClipAnimation.AnimationKind In = ClipAnimation.AnimationKind.In, Out = ClipAnimation.AnimationKind.Out;
        // Zoom In grows into the clip, and keeps growing out of it.
        Assert.Equal(ClipAnimation.ZoomInScale, At(In, ClipAnimation.AnimationPreset.ZoomIn, 0).ScaleX, 9);
        Assert.Equal(ClipAnimation.ZoomOutScale, At(In, ClipAnimation.AnimationPreset.ZoomOut, 0).ScaleX, 9);
        Assert.Equal(ClipAnimation.ZoomOutScale, At(Out, ClipAnimation.AnimationPreset.ZoomIn, 1).ScaleX, 9);
        Assert.Equal(ClipAnimation.ZoomInScale, At(Out, ClipAnimation.AnimationPreset.ZoomOut, 1).ScaleX, 9);
        Assert.Equal(-ClipAnimation.RotateDegrees, At(In, ClipAnimation.AnimationPreset.Rotate, 0).Rotation, 9);
        Assert.Equal(-ClipAnimation.RotateDegrees, At(Out, ClipAnimation.AnimationPreset.RotateCounterclockwise, 1).Rotation, 9);
        Assert.Equal(Identity, At(In, ClipAnimation.AnimationPreset.Shake, 0));
        Assert.Equal(Identity, At(In, ClipAnimation.AnimationPreset.Fade, 1));
    }

    [Fact]
    public void RecognizesPresetNames()
    {
        Assert.Equal(ClipAnimation.AnimationPreset.Fade, ClipAnimation.PresetNamed("Fade In"));
        Assert.Equal(ClipAnimation.AnimationPreset.Fade, ClipAnimation.PresetNamed("渐隐"));
        Assert.Equal(ClipAnimation.AnimationPreset.ZoomIn, ClipAnimation.PresetNamed("Zoom In 2"));
        Assert.Equal(ClipAnimation.AnimationPreset.ZoomOut, ClipAnimation.PresetNamed("缩小"));
        Assert.Equal(ClipAnimation.AnimationPreset.SlideUp, ClipAnimation.PresetNamed("向上滑动"));
        Assert.Equal(ClipAnimation.AnimationPreset.SlideLeft, ClipAnimation.PresetNamed("Slide Left"));
        Assert.Equal(ClipAnimation.AnimationPreset.RotateCounterclockwise, ClipAnimation.PresetNamed("Rotate Counterclockwise"));
        Assert.Equal(ClipAnimation.AnimationPreset.Rotate, ClipAnimation.PresetNamed("Spin"));
        Assert.Equal(ClipAnimation.AnimationPreset.Shake, ClipAnimation.PresetNamed("Shake 1"));
        Assert.Null(ClipAnimation.PresetNamed("Swing"));
        // A slide without a direction isn't guessed.
        Assert.Null(ClipAnimation.PresetNamed("Slide"));
    }
}
