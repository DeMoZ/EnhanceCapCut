using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's TextAnimationTests.swift; same cases, same expectations.

public class TextAnimationTests
{
    static string Json(string animations) => $$$"""
        {"tracks": [{"type": "text", "segments": [
           {"id": "S1", "material_id": "X1", "target_timerange": {"start": 0, "duration": 2000000},
            "extra_material_refs": ["M1"]}]}],
         "materials": {
           "texts": [{"id": "X1", "content": "{\"text\":\"Hello\",\"styles\":[{\"range\":[0,5]}]}"}],
           "material_animations": [{"id": "M1", "type": "sticker_animation", "animations": [{{{animations}}}]}]}}
        """;

    [Fact]
    public void ParsesAnimationsAndReportsTheRestByName()
    {
        var timeline = TimelineBuilder.Build(Draft.Parse(Json("""
            {"type": "in", "name": "Typewriter", "duration": 500000, "resource_id": "1"},
            {"type": "out", "name": "渐隐", "duration": 400000},
            {"type": "loop", "name": "Swing", "duration": 600000}
            """)), Path.GetTempPath());
        var spec = Assert.IsType<TextContentLayer>(Assert.Single(timeline.Layers).Content).Spec;
        Assert.Equal(new TextAnimation(TextAnimationKind.Intro, new TextAnimationEffect.Typewriter(), 500000), spec.Animations.Intro);
        Assert.Equal(new TextAnimation(TextAnimationKind.Outro, new TextAnimationEffect.Fade(), 400000), spec.Animations.Outro);
        Assert.Null(spec.Animations.Loop);
        Assert.Equal(["Animation: Swing"], timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}"));
    }

    [Fact]
    public void RecognizesPresetNames()
    {
        const double d = TextAnimations.SlideDistance;
        Assert.Equal(new TextAnimationEffect.Fade(), TextAnimationEffect.Recognize("Fade In", TextAnimationKind.Intro));
        Assert.Equal(new TextAnimationEffect.Slide(-d, 0), TextAnimationEffect.Recognize("Slide Left", TextAnimationKind.Intro));
        Assert.Equal(new TextAnimationEffect.Slide(0, d), TextAnimationEffect.Recognize("向上滑动", TextAnimationKind.Outro));
        Assert.Equal(new TextAnimationEffect.Zoom(0, false), TextAnimationEffect.Recognize("Zoom In", TextAnimationKind.Intro));
        Assert.Equal(new TextAnimationEffect.Zoom(0, false), TextAnimationEffect.Recognize("Zoom Out", TextAnimationKind.Outro));
        Assert.Equal(new TextAnimationEffect.Pop(), TextAnimationEffect.Recognize("Pop Up", TextAnimationKind.Intro));
        Assert.Equal(new TextAnimationEffect.Blink(), TextAnimationEffect.Recognize("Blink", TextAnimationKind.Loop));
        Assert.Null(TextAnimationEffect.Recognize("Blink", TextAnimationKind.Intro));
        Assert.Null(TextAnimationEffect.Recognize("Spin", TextAnimationKind.Intro));
    }

    [Fact]
    public void StateOverTime()
    {
        var animations = new TextAnimations(
            new TextAnimation(TextAnimationKind.Intro, new TextAnimationEffect.Slide(0.2, 0), 500_000),
            new TextAnimation(TextAnimationKind.Outro, new TextAnimationEffect.Fade(), 500_000),
            new TextAnimation(TextAnimationKind.Loop, new TextAnimationEffect.Pulse(), 400_000));
        var start = animations.State(0, 2_000_000);
        Assert.Equal(-0.2, start.OffsetX, 9); // an intro sliding right arrives from the left
        Assert.Equal(0, start.Alpha, 9);
        // Loop between intro and outro: a cycle starts at rest and peaks halfway.
        Assert.Equal(1, animations.State(500_000, 2_000_000).Scale, 9);
        Assert.Equal(1 + TextAnimations.PulseScale, animations.State(700_000, 2_000_000).Scale, 9);
        Assert.Equal(0.5, animations.State(1_750_000, 2_000_000).Alpha, 9);
        // Intro and outro longer than the clip are squeezed into it.
        Assert.Equal(1, animations.State(250_000, 500_000).Alpha, 9);
        Assert.Equal(0.5, animations.State(375_000, 500_000).Alpha, 9);
    }

    [Fact]
    public void TypewriterRevealsWholeCharacters()
    {
        var typewriter = new TextAnimations(new TextAnimation(TextAnimationKind.Intro, new TextAnimationEffect.Typewriter(), 1_000_000));
        Assert.Equal(0.5, typewriter.State(500_000, 2_000_000).Reveal, 9);
        Assert.Equal(2, TextAnimationState.VisibleLength("Hi 👋🏽!", 0.5));
        Assert.Equal(7, TextAnimationState.VisibleLength("Hi 👋🏽!", 0.8)); // an emoji is one character
        Assert.Equal(8, TextAnimationState.VisibleLength("Hi 👋🏽!", 1));
        Assert.Equal(0, TextAnimationState.VisibleLength("Hi", 0));
    }
}
