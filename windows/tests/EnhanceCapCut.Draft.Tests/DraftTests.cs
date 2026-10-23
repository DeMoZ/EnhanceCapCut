using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's CoreTests.swift; same cases, same expectations.

public class TimeMapTests
{
    static readonly Point2[] FlashIn = [new(0, 1), new(0.19, 5.2), new(0.78, 5.2), new(0.94, 1), new(1, 1)];

    [Fact]
    public void ConstantSpeed()
    {
        var map = new TimeMap(1_000_000, 4_000_000, 2_000_000);
        Assert.True(map.IsConstant);
        Assert.Equal(2, map.AverageSpeed, 9);
        Assert.Equal(1_000_000, map.SourceTime(0));
        Assert.Equal(3_000_000, map.SourceTime(1_000_000));
        Assert.Equal(5_000_000, map.SourceTime(2_000_000));
    }

    [Fact]
    public void CurveCoversWholeSourceAndIsMonotonic()
    {
        // CapCut's "Flash in" preset.
        var map = new TimeMap(0, 10_000_000, 2_000_000, FlashIn);
        Assert.False(map.IsConstant);
        Assert.Equal(0, map.SourceTime(0));
        Assert.Equal(10_000_000, map.SourceTime(2_000_000));
        long last = -1;
        for (long o = 0; o <= 2_000_000; o += 10_000)
        {
            var s = map.SourceTime(o);
            Assert.True(s >= last);
            last = s;
        }
        // Fast in the middle, slow at the ends.
        Assert.True(map.Speed(0.5) > map.Speed(0.02) * 3);
    }

    [Fact]
    public void OffsetIsInverseOfSourceTime()
    {
        var map = new TimeMap(500_000, 10_000_000, 2_000_000, FlashIn);
        for (long o = 0; o <= 2_000_000; o += 100_000)
            Assert.InRange(map.Offset(map.SourceTime(o)), o - 2_000, o + 2_000);
        var constant = new TimeMap(0, 4_000_000, 1_000_000);
        Assert.Equal(500_000, constant.Offset(2_000_000));
        Assert.Equal(1_000_000, constant.Offset(9_000_000));
    }

    [Fact]
    public void SliceFollowsTheCurve()
    {
        var map = new TimeMap(0, 10_000_000, 2_000_000, FlashIn);
        var slice = map.Slice(400_000, 1_400_000);
        Assert.Equal(1_000_000, slice.TargetDuration);
        for (long o = 0; o <= 1_000_000; o += 50_000)
        {
            var expected = map.SourceTime(400_000 + o);
            Assert.InRange(slice.SourceTime(o), expected - 1_000, expected + 1_000);
        }
        // A slice of the constant-speed middle collapses to a constant map.
        Assert.True(map.Slice(800_000, 1_200_000).IsConstant);
        Assert.True(new TimeMap(0, 3, 1).Slice(0, 1).IsConstant);
    }

    [Fact]
    public void SampledCompositionOfTwoSpeeds()
    {
        // 2x over 3x = 6x.
        var outer = new TimeMap(1_000_000, 2_000_000, 1_000_000);
        var inner = new TimeMap(0, 9_000_000, 3_000_000);
        var composed = new TimeMap(1_000_000, u => inner.PreciseSourceTime(outer.PreciseSourceTime(u * 1_000_000)));
        Assert.True(composed.IsConstant);
        Assert.Equal(3_000_000, composed.SourceStart);
        Assert.Equal(6, composed.AverageSpeed, 6);
    }
}

public class KeyframeTests
{
    [Fact]
    public void LinearInterpolationAndClamping()
    {
        var track = new KeyframeTrack([0, 1_000, 3_000], [0, 1, -1]);
        Assert.Equal(0, track.Value(-5));
        Assert.Equal(0.5, track.Value(500), 9);
        Assert.Equal(0, track.Value(2_000), 9);
        Assert.Equal(-1, track.Value(9_000));
    }
}

public class DraftParsingTests
{
    /// <summary>A minimal synthetic draft with odd values (nulls, wrong types, unknown keys) that must not break parsing.</summary>
    const string Json = """
    {
      "id": "D", "name": "Test", "duration": 3000000, "fps": 30.0, "unknown_field": {"x": [1, 2]},
      "canvas_config": {"width": 1080, "height": 1920, "ratio": "9:16", "background": null},
      "config": {"video_mute": false},
      "tracks": [
        {"id": "T1", "type": "video", "attribute": 0, "flag": 0, "segments": [
          {"id": "S1", "material_id": "V1", "source_timerange": {"start": 0, "duration": 6000000},
           "target_timerange": {"start": 0, "duration": 3000000}, "speed": 2.0, "volume": 1.5,
           "clip": {"scale": {"x": 1.2, "y": 1.2}, "rotation": 90, "transform": {"x": 0.1, "y": -0.2},
                    "flip": {"horizontal": true, "vertical": false}, "alpha": 0.5},
           "extra_material_refs": ["SP1", "C1", "TR1"], "render_index": 0,
           "common_keyframes": [{"property_type": "KFTypePositionX", "keyframe_list": [
             {"time_offset": 0, "values": [0.0]}, {"time_offset": 1000000, "values": [0.5]}]}]}
        ]},
        {"id": "T2", "type": "audio", "attribute": 1, "segments": []},
        {"id": "T3", "type": "text", "attribute": 0, "segments": [
          {"id": "S2", "material_id": "X1", "target_timerange": {"start": 0, "duration": 1000000}, "clip": null}
        ]}
      ],
      "materials": {
        "videos": [{"id": "V1", "type": "video", "path": "/nonexistent/clip.mov", "width": 1920, "height": 1080,
                    "crop": {"upper_left_x": 0.1, "upper_left_y": 0.0, "lower_right_x": 0.9, "lower_right_y": 1.0}}],
        "speeds": [{"id": "SP1", "type": "speed", "mode": 0, "speed": 2.0, "curve_speed": null}],
        "canvases": [{"id": "C1", "type": "canvas_blur", "blur": 0.5, "color": ""}],
        "transitions": [{"id": "TR1", "type": "transition", "name": "Flashback"}],
        "texts": [{"id": "X1", "type": "subtitle", "font_size": "not a number",
                   "content": "{\"text\":\"Hello\",\"styles\":[{\"range\":[0,5],\"size\":8,\"fill\":{\"content\":{\"solid\":{\"color\":[1,0,0]}}}}]}"}],
        "audios": null
      }
    }
    """;

    static string Temp => Path.GetTempPath();

    [Fact]
    public void ParsesLeniently()
    {
        var draft = Draft.Parse(Json);
        Assert.Equal(1080, draft.Canvas.Width);
        Assert.Equal(3, draft.Tracks.Count);
        Assert.True(draft.Tracks[1].IsMuted);
        var segment = draft.Tracks[0].Segments[0];
        Assert.Equal(1.2, segment.Clip?.Scale.X);
        Assert.True(segment.Clip?.FlipHorizontal);
        Assert.Equal(2, segment.Keyframes[0].Keyframes.Count);
        Assert.Equal(0.1, draft.Materials.Videos[0].Crop.UpperLeft.X);
        // An invalid number falls back to the default.
        Assert.Equal(15, draft.Materials.Texts[0].FontSize);
        var content = TextContent.Parse(draft.Materials.Texts[0].Content);
        Assert.NotNull(content);
        Assert.Equal("Hello", content.Text);
        Assert.Equal([1.0, 0, 0], content.Styles[0].FillColor);
    }

    [Fact]
    public void TimelineReportsMissingMediaAndUnsupported()
    {
        var timeline = TimelineBuilder.Build(Draft.Parse(Json), Temp);
        Assert.Equal(["/nonexistent/clip.mov"], timeline.Report.MissingFiles);
        Assert.Contains(timeline.Report.Unsupported, i => i.Category == "Transition" && i.Name == "Flashback");
        // The missing video produces no layer; the subtitle does.
        Assert.Single(timeline.Layers);
        Assert.True(timeline.HasSubtitles);
        Assert.Empty(timeline.WithoutSubtitles().Layers);
    }

    [Fact]
    public void ReportsUnsupportedClipFeatures()
    {
        var photo = Path.Combine(Temp, $"ecc-report-test-{Guid.NewGuid()}.png");
        File.WriteAllBytes(photo, [0]);
        try
        {
            var escaped = photo.Replace("\\", "\\\\");
            var json = $$$"""
            {
              "tracks": [
                {"type": "video", "segments": [
                  {"id": "S1", "material_id": "V1", "target_timerange": {"start": 0, "duration": 1000000}, "is_loop": true,
                   "common_keyframes": [
                     {"property_type": "KFTypePositionX", "keyframe_list": [{"time_offset": 0, "values": [0]}]},
                     {"property_type": "KFTypeCommonMaskFeather", "keyframe_list": [{"time_offset": 0, "values": [0]}]},
                     {"property_type": "KFTypeCommonMaskSizeWidth", "keyframe_list": [{"time_offset": 0, "values": [0]}]},
                     {"property_type": "KFTypeSaturation", "keyframe_list": [{"time_offset": 0, "values": [0]}]}]}
                ]},
                {"type": "audio", "segments": [
                  {"id": "S2", "material_id": "A1", "target_timerange": {"start": 0, "duration": 1000000},
                   "common_keyframes": [{"property_type": "KFTypeVolume", "keyframe_list": [{"time_offset": 0, "values": [1]}]}]}
                ]},
                {"type": "text", "segments": [
                  {"id": "S3", "material_id": "X1", "target_timerange": {"start": 0, "duration": 1000000}}
                ]}
              ],
              "materials": {
                "videos": [{"id": "V1", "type": "photo", "path": "{{{escaped}}}",
                            "stable": {"stable_level": 3}, "matting": {"flag": 1}, "is_set_beauty_mode": true,
                            "video_algorithm": {"algorithms": [{"type": "QualityEnhance"}, {"type": "AigcTypeGenImage"}],
                                                "super_resolution": {"level": 1}, "deflicker": null}}],
                "audios": [{"id": "A1", "path": "/nonexistent/a.m4a"}],
                "texts": [{"id": "X1", "text_curve": {"enable": true, "angle": 72},
                           "content": "{\"text\":\"Hi\",\"styles\":[{\"range\":[0,2],\"strokes\":[{},{}]}]}"}]
              }
            }
            """;
            var timeline = TimelineBuilder.Build(Draft.Parse(json), Temp);
            var found = timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}").ToHashSet();
            HashSet<string> expected =
            [
                "Keyframes: mask animation", "Keyframes: Saturation", "Keyframes: Volume", "Playback: loop",
                "Stabilization: level 3", "Remove background: cutout mode 1", "Beauty / makeup: retouch",
                "AI processing: QualityEnhance", "AI processing: super_resolution",
                "Text: curved text (rendered straight)", "Text: multiple outlines (first one rendered)",
            ];
            Assert.Equal(expected, found);
            // Unsupported features don't drop the clips.
            Assert.Equal(2, timeline.Layers.Count);
        }
        finally
        {
            File.Delete(photo);
        }
    }

    [Fact]
    public void PlaceholderPathResolution()
    {
        var path = DraftLoader.ResolvePath("##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##/audio/a.aac",
            "/Users/x/Projects/My Project");
        Assert.Equal("/Users/x/Projects/My Project/audio/a.aac", path);
        Assert.Null(DraftLoader.ResolvePath("", Temp));
    }
}

public class LayerTests
{
    [Fact]
    public void KeyframesOverrideClipAndUniformScale()
    {
        var clip = new Clip { Scale = new(2, 2) };
        var layer = new VisualLayer("L", new PhotoContent("/tmp/x.jpg"), 0, 0, true, new TimeRange(1_000, 2_000),
            new TimeMap(0, 2_000, 2_000), new Crop(), clip, true,
            new Dictionary<LayerProperty, KeyframeTrack> { [LayerProperty.ScaleX] = new([0, 2_000], [1, 3]) }, null);
        var t = layer.Transform(2_000);
        Assert.Equal(2, t.ScaleX, 9);
        // Uniform scale follows the X keyframes.
        Assert.Equal(2, t.ScaleY, 9);
        Assert.True(layer.IsActive(1_000));
        Assert.False(layer.IsActive(3_000));
    }
}

public class ExportSettingsTests
{
    [Fact]
    public void OutputSize()
    {
        var s = new ExportSettings();
        Assert.Equal((1080, 1920), s.OutputSize(1080, 1920));
        s.Aspect = ExportSettings.AspectRatio.Landscape;
        Assert.Equal((1920, 1080), s.OutputSize(1080, 1920));
        s.Aspect = ExportSettings.AspectRatio.Square;
        s.OutputResolution = ExportSettings.Resolution.P720;
        Assert.Equal((720, 720), s.OutputSize(1080, 1920));
        s.Aspect = ExportSettings.AspectRatio.Project;
        s.OutputResolution = ExportSettings.Resolution.P2160;
        Assert.Equal((2160, 2880), s.OutputSize(1440, 1920));
    }

    [Fact]
    public void FileNameTemplate()
    {
        var s = new ExportSettings { Codec = ExportSettings.VideoCodec.H264, FileNameTemplate = "{project}-{res}-{fps}-{codec}" };
        Assert.Equal("Trip-1080x1920-30-h264.mp4", s.FileName("Trip", 1080, 1920, 30));
        s.FileNameTemplate = "{project}: {fps}?";
        Assert.Equal("Trip- 29.97-.mp4", s.FileName("Trip", 1080, 1920, 29.97));
    }

    [Fact]
    public void FileNameUsesGregorianDates()
    {
        var s = new ExportSettings { FileNameTemplate = "{date}" };
        Assert.StartsWith("2026-10-15", s.FileName("P", 0, 0, 30, new DateTime(2026, 10, 15, 12, 0, 0)));
    }

    [Fact]
    public void DecodingOldSettingsKeepsDefaults()
    {
        var decoded = ExportSettings.Parse("""{"codec":"h264","quality":55,"aspect":"prores-only-value"}""");
        Assert.Equal(ExportSettings.VideoCodec.H264, decoded.Codec);
        Assert.Equal(55, decoded.Quality);
        Assert.True(decoded.BurnInSubtitles);
        Assert.Equal(ExportSettings.AspectRatio.Project, decoded.Aspect);
        // Settings round-trip through their JSON.
        var copy = ExportSettings.Parse(decoded.ToJson());
        Assert.Equal(decoded.ToJson(), copy.ToJson());
    }
}

public class ProjectTimelinesTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ProjectTimelinesTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "draft_info.json"), "{}");
    }

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void ProjectWithoutTimelinesFileHasTheRootDraft()
    {
        var timelines = new CapCutProject(folder).Timelines();
        Assert.Single(timelines);
        Assert.True(timelines[0].IsMain);
        Assert.Equal(Path.Combine(folder, "draft_info.json"), timelines[0].DraftPath);
    }

    [Fact]
    public void TimelinesSkipDeletedAndMissingDrafts()
    {
        var root = Path.Combine(folder, "Timelines");
        foreach (var id in new[] { "B", "D" })
        {
            Directory.CreateDirectory(Path.Combine(root, id));
            File.WriteAllText(Path.Combine(root, id, "draft_info.json"), "{}");
        }
        File.WriteAllText(Path.Combine(root, "project.json"), """
        {"main_timeline_id": "A", "timelines": [
          {"id": "A", "name": "Timeline 01"}, {"id": "B", "name": "Timeline 02"},
          {"id": "C", "name": "Timeline 03"}, {"id": "D", "name": "Gone", "is_marked_delete": true}]}
        """);
        var timelines = new CapCutProject(folder).Timelines();
        Assert.Equal(["Timeline 01", "Timeline 02"], timelines.Select(t => t.Name));
        // The main timeline without its own folder falls back to the root draft.
        Assert.Equal(Path.Combine(folder, "draft_info.json"), timelines[0].DraftPath);
        Assert.True(timelines[0].IsMain);
        Assert.False(timelines[1].IsMain);
    }
}
