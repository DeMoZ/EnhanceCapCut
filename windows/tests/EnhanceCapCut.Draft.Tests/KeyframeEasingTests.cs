using System.Text.Json;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's KeyframeEasingTests.swift.

public class KeyframeEasingTests
{
    /// <summary>Two keyframes 0 → 100 over 1 s; <paramref name="curve"/> and the handles go on both.</summary>
    static KeyframeGroup Group(string curve, double @out, double incoming)
    {
        var json = $$$"""
        {"property_type": "KFTypePositionX", "keyframe_list": [
          {"time_offset": 0, "values": [0], "curveType": "{{{curve}}}",
           "left_control": {"x": 0, "y": 0}, "right_control": {"x": {{{@out.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}, "y": 0}},
          {"time_offset": 1000000, "values": [100], "curveType": "{{{curve}}}",
           "left_control": {"x": {{{incoming.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}, "y": 0}, "right_control": null}]}
        """;
        return KeyframeGroup.From(JsonDocument.Parse(json).RootElement)!;
    }

    [Fact]
    public void LineKeyframesStayLinearWhateverTheHandles()
    {
        var track = KeyframeTrack.From(Group("Line", 400_000, -400_000))!;
        Assert.Null(track.Handles);
        Assert.Equal(25, track.Value(250_000), 9);
        Assert.Equal([0L, 1_000_000L], track.Breaks(50_000));
    }

    [Fact]
    public void CurvedKeyframesFollowTheCubicBezier()
    {
        var track = KeyframeTrack.From(Group("Cubic", 400_000, -400_000))!;
        Assert.Equal(0, track.Value(0));
        Assert.InRange(track.Value(250_000), 13.5297, 13.5317);
        Assert.Equal(50, track.Value(500_000), 6);
        Assert.Equal(100, track.Value(250_000) + track.Value(750_000), 6);
        Assert.Equal(100, track.Value(1_000_000));
        Assert.Equal([0L, 250_000L, 500_000L, 750_000L, 1_000_000L], track.Breaks(250_000));
        Assert.Equal(track.Value(250_000) / 2, track.Scaled(0.5).Value(250_000), 9);
    }

    [Fact]
    public void ZeroHandlesAndHandlesPastTheSpanStayMonotonic()
    {
        var straight = KeyframeTrack.From(Group("Cubic", 0, 0))!;
        Assert.InRange(straight.Value(300_000), 29.999, 30.001);
        var wild = KeyframeTrack.From(Group("Cubic", 5_000_000, -5_000_000))!;
        var last = -1.0;
        for (long t = 0; t <= 1_000_000; t += 10_000)
        {
            var v = wild.Value(t);
            Assert.True(v >= last - 1e-6);
            last = v;
        }
    }

    /// <summary>
    /// Clip keyframes are on the source time axis: a clip trimmed to start 5 s into its media has keyframes from
    /// 5 000 000 (Durian1, checked against CapCut's export).
    /// </summary>
    [Fact]
    public void ClipKeyframesAreOnTheSourceTimeAxis()
    {
        var layer = new VisualLayer("L", new PhotoContent("/tmp/x.jpg"), 0, 0, true, new TimeRange(10_000_000, 2_000_000),
            new TimeMap(5_000_000, 2_000_000, 2_000_000), new Crop(), new Clip(), true,
            new Dictionary<LayerProperty, KeyframeTrack> { [LayerProperty.PositionX] = new([5_000_000, 6_000_000], [0, 1]) },
            null);
        Assert.Equal(0, layer.Transform(10_000_000).PositionX, 9);
        Assert.Equal(0.5, layer.Transform(10_500_000).PositionX, 9);
        Assert.Equal(1, layer.Transform(11_500_000).PositionX, 9);
    }
}
