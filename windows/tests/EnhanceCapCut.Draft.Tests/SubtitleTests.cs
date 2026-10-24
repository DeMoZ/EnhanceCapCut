using System.Text;
using System.Text.Json;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's SubtitleTests.swift; same cases, same expectations.

public class SubtitleWriterTests
{
    const string Draft =
        "{\"id\":\"D\",\"fps\":30.0,\"duration\":10000000,\"tracks\":[{\"id\":\"V\",\"type\":\"video\",\"flag\":0,\"attribute\":0,\"segments\":[]}],"
        + "\"materials\":{\"videos\":[],\"texts\":[],\"material_animations\":[]},\"canvas_config\":{\"ratio\":\"original\",\"width\":1080,\"height\":1920}}";

    static List<SubtitleTrack> Tracks() =>
    [
        new(SubtitleLanguage.Find("ru")!, [new(0.5, 2, "Привет, мир"), new(2, 3.01, "Вторая\nстрока")], IsOriginal: true, IsVisible: false),
        new(SubtitleLanguage.Find("en")!, [new(0.5, 2, "Hello, world")], IsOriginal: false, IsVisible: true),
    ];

    static JsonElement Json(byte[] data) => JsonDocument.Parse(data).RootElement;
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void AddsCaptionTracks()
    {
        var output = DraftSubtitleWriter.Apply(Tracks(), Bytes(Draft), replacePrevious: true);
        var d = Json(output);
        var tracks = d.GetProperty("tracks");
        Assert.Equal(3, tracks.GetArrayLength());
        Assert.Equal(2, tracks[1].GetProperty("attribute").GetInt32());  // hidden
        Assert.Equal(0, tracks[2].GetProperty("attribute").GetInt32());
        Assert.Equal("text", tracks[1].GetProperty("type").GetString());

        var segments = tracks[1].GetProperty("segments");
        Assert.Equal(2, segments.GetArrayLength());
        var range = segments[1].GetProperty("target_timerange");
        Assert.Equal(2_000_000, range.GetProperty("start").GetInt64());
        Assert.Equal(1_000_000, range.GetProperty("duration").GetInt64());  // 3.01 s snaps to the 3 s frame
        Assert.Equal(1, segments[0].GetProperty("track_render_index").GetInt32());

        var materials = d.GetProperty("materials");
        var texts = materials.GetProperty("texts");
        Assert.Equal(3, texts.GetArrayLength());
        Assert.Equal(3, materials.GetProperty("material_animations").GetArrayLength());
        var id = segments[0].GetProperty("material_id").GetString();
        var first = texts.EnumerateArray().First(t => t.GetProperty("id").GetString() == id);
        Assert.Equal("subtitle", first.GetProperty("type").GetString());
        Assert.Equal("ru-RU", first.GetProperty("language").GetString());
        var content = JsonDocument.Parse(first.GetProperty("content").GetString()!).RootElement;
        Assert.Equal("Привет, мир", content.GetProperty("text").GetString());
        var styleRange = content.GetProperty("styles")[0].GetProperty("range");
        Assert.Equal([0, 11], styleRange.EnumerateArray().Select(e => e.GetInt32()));

        // Untouched parts stay byte-for-byte, and floats keep their decimal point.
        var text = Encoding.UTF8.GetString(output);
        Assert.StartsWith("{\"id\":\"D\",\"fps\":30.0,\"duration\":10000000,\"tracks\":[{\"id\":\"V\",\"type\":\"video\",\"flag\":0,\"attribute\":0,\"segments\":[]},", text);
        Assert.EndsWith("\"canvas_config\":{\"ratio\":\"original\",\"width\":1080,\"height\":1920}}", text);
        Assert.Contains("\"line_max_width\":0.82", text);
        Assert.Contains("\"font_size\":8.0", text);
    }

    [Fact]
    public void ReplacesPreviousRun()
    {
        var once = DraftSubtitleWriter.Apply(Tracks(), Bytes(Draft), replacePrevious: true);
        var twice = DraftSubtitleWriter.Apply(Tracks(), once, replacePrevious: true);
        var d = Json(twice);
        Assert.Equal(3, d.GetProperty("tracks").GetArrayLength());
        Assert.Equal(3, d.GetProperty("materials").GetProperty("texts").GetArrayLength());
        Assert.Equal(3, d.GetProperty("materials").GetProperty("material_animations").GetArrayLength());

        var kept = DraftSubtitleWriter.Apply(Tracks(), once, replacePrevious: false);
        Assert.Equal(5, Json(kept).GetProperty("tracks").GetArrayLength());
    }

    [Fact]
    public void CopiesStyleOfExistingCaption()
    {
        var styled = DraftSubtitleWriter.Apply(Tracks(), Bytes(Draft), replacePrevious: true);
        // Make the generated captions look like the user's own: bigger and higher.
        var text = Encoding.UTF8.GetString(styled)
            .Replace("\\\"size\\\":8.0", "\\\"size\\\":13.0")
            .Replace("\"y\":-0.65", "\"y\":-0.4")
            .Replace(DraftSubtitleWriter.TrackNamePrefix, "Mine");
        var output = DraftSubtitleWriter.Apply(Tracks().Take(1).ToList(), Bytes(text), replacePrevious: true);
        var d = Json(output);
        var tracks = d.GetProperty("tracks");
        var segment = tracks[tracks.GetArrayLength() - 1].GetProperty("segments")[0];
        Assert.Equal(-0.4, segment.GetProperty("clip").GetProperty("transform").GetProperty("y").GetDouble());
        var id = segment.GetProperty("material_id").GetString();
        var material = d.GetProperty("materials").GetProperty("texts").EnumerateArray().First(t => t.GetProperty("id").GetString() == id);
        Assert.Equal(13, material.GetProperty("font_size").GetDouble());
    }

    [Fact]
    public void ScannerHandlesStringsWithBrackets()
    {
        var s = new JsonScanner(Bytes("{\"a\":\"x]}\\\"[{\",\"b\":[1,{\"c\":\"]\"},[2]],\"d\":null}"));
        var root = s.Root();
        Assert.Equal(["a", "b", "d"], s.Members(root).Select(m => m.Key));
        Assert.Equal(3, s.Elements(s.Value("b", root)!.Value).Count);
    }

    [Fact]
    public void SegmenterSplitsAtPauses()
    {
        TimedWord[] words = [new("Привет", 0, 0.4), new("мир", 0.45, 0.8), new("как", 2.0, 2.2), new("дела", 2.25, 2.6)];
        var cues = Segmenter.Cues(words, "ru");
        Assert.Equal(["Привет мир.", "Как дела."], cues.Select(c => c.Text));
        Assert.Equal(0, cues[0].Start);
    }

    [Fact]
    public void WrapsLongLinesIntoTwoBalancedOnes()
    {
        var wrapped = LineWrapper.Wrap("They add a little sugar and form such sausages for the market.", "en");
        var lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.True(Math.Abs(lines[0].Length - lines[1].Length) < 12);
    }
}
