using System.Text;
using System.Text.Json;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's VoiceOverTests.swift; same cases, same expectations.

public class VoiceOverWriterTests
{
    const string Project = "/Projects/Demo";
    const string Placeholder = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##";

    /// <summary>A video clip over the whole 10 s, a music clip from 6 s, and one with volume keyframes of its own.</summary>
    const string Draft =
        "{\"id\":\"D\",\"fps\":30.0,\"duration\":10000000,\"tracks\":["
        + "{\"id\":\"V\",\"type\":\"video\",\"flag\":0,\"attribute\":0,\"segments\":[{\"id\":\"S1\",\"material_id\":\"M1\","
        + "\"target_timerange\":{\"start\":0,\"duration\":10000000},\"volume\":0.5,\"common_keyframes\":[]}]},"
        + "{\"id\":\"A\",\"type\":\"audio\",\"flag\":0,\"attribute\":0,\"segments\":[{\"id\":\"S2\",\"material_id\":\"M2\","
        + "\"target_timerange\":{\"start\":6000000,\"duration\":4000000},\"volume\":1.0}]},"
        + "{\"id\":\"B\",\"type\":\"audio\",\"flag\":0,\"attribute\":0,\"segments\":[{\"id\":\"S3\",\"material_id\":\"M3\","
        + "\"target_timerange\":{\"start\":0,\"duration\":4000000},\"volume\":1.0,\"common_keyframes\":[{\"id\":\"K\","
        + "\"property_type\":\"KFTypeVolume\",\"keyframe_list\":[{\"time_offset\":0,\"values\":[1.0]}]}]}]}],"
        + "\"materials\":{\"videos\":[{\"id\":\"M1\",\"path\":\"" + Placeholder + "/clip.mp4\"}],\"audios\":[],\"speeds\":[]}}";

    static List<VoiceOverTrack> Tracks() =>
    [
        new(SubtitleLanguage.Find("en")!, Project + "/EnhanceCapCut/voiceover 1/en.wav", 10, [(1, 2.5), (3, 4)], IsAudible: true),
        new(SubtitleLanguage.Find("th")!, "/elsewhere/th.wav", 10, [(1, 2)], IsAudible: false),
    ];

    static JsonElement Json(byte[] data) => JsonDocument.Parse(data).RootElement;
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    static JsonElement Segments(JsonElement d, int track) => d.GetProperty("tracks")[track].GetProperty("segments");

    static List<JsonElement> VolumeKeyframes(JsonElement segment) =>
        segment.TryGetProperty("common_keyframes", out var groups)
            ? groups.EnumerateArray().Where(g => g.GetProperty("property_type").GetString() == "KFTypeVolume").ToList()
            : [];

    /// <summary>Replace voice: the original is muted like Mute and a background track (always audible) fills in.</summary>
    [Fact]
    public void ReplaceAddsAnAudibleBackgroundAndMutesTheOriginal()
    {
        var all = Tracks();
        all.Add(new VoiceOverTrack(all[0].Language, Project + "/EnhanceCapCut/voiceover 1/background.wav", 10, [(0.75, 4.4)],
            IsAudible: true, IsBackground: true));
        var output = DraftVoiceOverWriter.Apply(all, Bytes(Draft), OriginalAudio.Replace, replacePrevious: true, Project);
        var d = Json(output);
        var tracks = d.GetProperty("tracks");
        Assert.Equal(6, tracks.GetArrayLength());
        Assert.Equal("EnhanceCapCut voice-over · background", tracks[5].GetProperty("name").GetString());
        Assert.Equal(0, tracks[5].GetProperty("attribute").GetInt32());
        // Ducked to silence under the voice's lines, not the background's.
        var video = VolumeKeyframes(Segments(d, 0)[0]);
        var values = video[0].GetProperty("keyframe_list").EnumerateArray().Select(k => k.GetProperty("values")[0].GetDouble());
        Assert.Equal([0.5, 0, 0, 0.5], values);
        // A later run takes the background away too.
        Assert.Equal(3, Json(DraftVoiceOverWriter.WithoutVoiceOver(output)).GetProperty("tracks").GetArrayLength());
    }

    [Fact]
    public void BackgroundGainCrossFadesWithTheDucking()
    {
        (long, long) r = (1_000_000, 2_000_000);
        Assert.Equal(0, DraftVoiceOverWriter.BackgroundGain(500_000, r));
        Assert.Equal(0.5, DraftVoiceOverWriter.BackgroundGain(875_000, r), 9);
        Assert.Equal(1, DraftVoiceOverWriter.BackgroundGain(1_500_000, r));
        Assert.Equal(0.5, DraftVoiceOverWriter.BackgroundGain(2_200_000, r), 9);
        Assert.Equal(0, DraftVoiceOverWriter.BackgroundGain(2_400_000, r));
    }

    [Fact]
    public void AddsAudioTracksWithAClipPerLine()
    {
        var output = DraftVoiceOverWriter.Apply(Tracks(), Bytes(Draft), OriginalAudio.Keep, replacePrevious: true, Project);
        var d = Json(output);
        var all = d.GetProperty("tracks");
        Assert.Equal(5, all.GetArrayLength());
        Assert.Equal("audio", all[3].GetProperty("type").GetString());
        Assert.Equal(0, all[3].GetProperty("attribute").GetInt32());
        Assert.Equal(1, all[4].GetProperty("attribute").GetInt32());  // muted
        Assert.Equal("EnhanceCapCut voice-over · en", all[3].GetProperty("name").GetString());

        var lines = Segments(d, 3);
        Assert.Equal(2, lines.GetArrayLength());
        var target = lines[0].GetProperty("target_timerange");
        Assert.Equal(1_000_000, target.GetProperty("start").GetInt64());
        Assert.Equal(1_500_000, target.GetProperty("duration").GetInt64());
        var source = lines[0].GetProperty("source_timerange");
        Assert.Equal(1_000_000, source.GetProperty("start").GetInt64());
        Assert.Equal(1_500_000, source.GetProperty("duration").GetInt64());
        Assert.Equal(3, lines[0].GetProperty("track_render_index").GetInt32());
        Assert.Equal(5, lines[0].GetProperty("extra_material_refs").GetArrayLength());

        var materials = d.GetProperty("materials");
        var audios = materials.GetProperty("audios");
        Assert.Equal(3, audios.GetArrayLength());
        var id = lines[0].GetProperty("material_id").GetString();
        var first = audios.EnumerateArray().First(a => a.GetProperty("id").GetString() == id);
        Assert.Equal("record", first.GetProperty("type").GetString());
        Assert.Equal(10_000_000, first.GetProperty("duration").GetInt64());
        // Inside the project: written with the draft's placeholder; elsewhere: absolute.
        Assert.Equal(Placeholder + "/EnhanceCapCut/voiceover 1/en.wav", first.GetProperty("path").GetString());
        Assert.Contains(audios.EnumerateArray(), a => a.GetProperty("path").GetString() == "/elsewhere/th.wav");
        Assert.Equal(3, materials.GetProperty("speeds").GetArrayLength());
        Assert.Equal(3, materials.GetProperty("vocal_separations").GetArrayLength());

        // Keep: nothing else changes.
        Assert.Empty(VolumeKeyframes(Segments(d, 0)[0]));
        Assert.StartsWith("{\"id\":\"D\",\"fps\":30.0,\"duration\":10000000,\"tracks\":[{\"id\":\"V\",\"type\":\"video\"",
            Encoding.UTF8.GetString(output));
    }

    [Fact]
    public void DucksOtherAudioWhileTheVoiceSpeaks()
    {
        var d = Json(DraftVoiceOverWriter.Apply(Tracks(), Bytes(Draft), OriginalAudio.Lower, replacePrevious: true, Project));
        // The two lines (1–2.5 s, 3–4 s) are less than the ramps apart, so they duck as one: 1–4 s.
        var video = VolumeKeyframes(Segments(d, 0)[0]).First();
        Assert.StartsWith(DraftVoiceOverWriter.IDPrefix, video.GetProperty("id").GetString());
        var list = video.GetProperty("keyframe_list").EnumerateArray().ToList();
        Assert.Equal([750_000L, 1_000_000, 4_000_000, 4_400_000], list.Select(k => k.GetProperty("time_offset").GetInt64()));
        var values = list.Select(k => k.GetProperty("values")[0].GetDouble()).ToList();
        Assert.Equal(0.5, values[0]);
        Assert.Equal(0.5 * DraftVoiceOverWriter.LowerGain, values[1], 9);
        Assert.Equal(0.5, values[3]);

        // Music from 6 s: no speech there. The clip with its own volume keyframes is left alone.
        Assert.Empty(VolumeKeyframes(Segments(d, 1)[0]));
        Assert.Equal(["K"], VolumeKeyframes(Segments(d, 2)[0]).Select(g => g.GetProperty("id").GetString()));
        // Voice-over tracks aren't ducked.
        Assert.Empty(VolumeKeyframes(Segments(d, 3)[0]));
    }

    /// <summary>
    /// A video clip with its own (off) vocal separation, a compound clip, CapCut music and an extracted sound, all under
    /// the voice, and a clip after it.
    /// </summary>
    const string VocalDraft =
        "{\"id\":\"D\",\"duration\":10000000,\"tracks\":["
        + "{\"type\":\"video\",\"attribute\":0,\"segments\":[{\"id\":\"S1\",\"material_id\":\"M1\",\"extra_material_refs\":[\"VS1\"],"
        + "\"target_timerange\":{\"start\":0,\"duration\":5000000},\"source_timerange\":{\"start\":2000000,\"duration\":5000000},"
        + "\"volume\":1.0},{\"id\":\"S2\",\"material_id\":\"M1\",\"extra_material_refs\":[],"
        + "\"target_timerange\":{\"start\":6000000,\"duration\":4000000},\"volume\":1.0}]},"
        + "{\"type\":\"video\",\"attribute\":0,\"segments\":[{\"id\":\"S3\",\"material_id\":\"M1\",\"extra_material_refs\":[\"C1\"],"
        + "\"target_timerange\":{\"start\":0,\"duration\":5000000},\"volume\":1.0}]},"
        + "{\"type\":\"audio\",\"attribute\":0,\"segments\":[{\"id\":\"S4\",\"material_id\":\"A1\","
        + "\"target_timerange\":{\"start\":0,\"duration\":5000000},\"volume\":1.0},{\"id\":\"S5\",\"material_id\":\"A2\","
        + "\"target_timerange\":{\"start\":5000000,\"duration\":1000000},\"volume\":1.0}]}],"
        + "\"materials\":{\"videos\":[{\"id\":\"M1\",\"type\":\"video\",\"duration\":20000000}],"
        + "\"audios\":[{\"id\":\"A1\",\"type\":\"music\",\"duration\":5000000},{\"id\":\"A2\",\"type\":\"extract_music\",\"duration\":1000000}],"
        + "\"drafts\":[{\"id\":\"C1\"}],\"vocal_separations\":[{\"id\":\"VS1\",\"type\":\"vocal_separation\",\"choice\":0}]}}";

    static JsonElement? Separation(JsonElement d, JsonElement segment)
    {
        var refs = segment.GetProperty("extra_material_refs").EnumerateArray().Select(r => r.GetString()).ToList();
        foreach (var s in d.GetProperty("materials").GetProperty("vocal_separations").EnumerateArray())
            if (refs.Contains(s.GetProperty("id").GetString())) return s;
        return null;
    }

    static List<long> OurChoices(JsonElement d) => d.GetProperty("materials").GetProperty("vocal_separations").EnumerateArray()
        .Where(s => s.GetProperty("id").GetString()!.StartsWith(DraftVoiceOverWriter.IDPrefix, StringComparison.Ordinal))
        .Select(s => s.GetProperty("choice").GetInt64()).ToList();

    /// <summary>Remove vocal: CapCut's "Remove vocal" on the clips under the voice, muting where CapCut can't separate.</summary>
    [Fact]
    public void RemoveVocalTurnsOnCapCutsSeparation()
    {
        List<VoiceOverTrack> line = [Tracks()[0] with { Lines = [(1, 5.5)] }];
        var once = DraftVoiceOverWriter.Apply(line, Bytes(VocalDraft), OriginalAudio.RemoveVocal, replacePrevious: true, Project);
        var d = Json(once);
        var s1 = Segments(d, 0)[0];
        var own = Separation(d, s1)!.Value;
        Assert.StartsWith(DraftVoiceOverWriter.IDPrefix, own.GetProperty("id").GetString());
        Assert.Equal(1, own.GetProperty("choice").GetInt64());
        Assert.Equal("vocal_separate", own.GetProperty("final_algorithm").GetString());
        Assert.Equal(2_000_000, own.GetProperty("time_range").GetProperty("start").GetInt64());
        Assert.Empty(VolumeKeyframes(s1));
        // The clip's own entry stays, unchanged, for whatever else refers to it.
        Assert.Equal(0, d.GetProperty("materials").GetProperty("vocal_separations").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == "VS1").GetProperty("choice").GetInt64());
        // No speech over the clip from 6 s.
        Assert.Null(Separation(d, Segments(d, 0)[1]));
        // Compound clips and CapCut music can't be separated: muted instead.
        foreach (var segment in new[] { Segments(d, 1)[0], Segments(d, 2)[0] })
        {
            Assert.False(segment.TryGetProperty("extra_material_refs", out var refs) && Separation(d, segment) is not null);
            Assert.Single(VolumeKeyframes(segment));
        }
        // A segment without separation material gets one.
        var s5 = Segments(d, 2)[1];
        Assert.Equal(1, Separation(d, s5)!.Value.GetProperty("choice").GetInt64());
        Assert.Empty(VolumeKeyframes(s5));

        // Running again reuses our entries; keeping the original sound turns them off.
        var twice = DraftVoiceOverWriter.Apply(line, once, OriginalAudio.RemoveVocal, replacePrevious: true, Project);
        d = Json(twice);
        Assert.Equal([1L, 1L], OurChoices(d));
        Assert.Equal(own.GetProperty("id").GetString(), Separation(d, Segments(d, 0)[0])!.Value.GetProperty("id").GetString());
        var kept = Json(DraftVoiceOverWriter.Apply(line, twice, OriginalAudio.Keep, replacePrevious: true, Project));
        Assert.Equal([0L, 0L], OurChoices(kept));
        Assert.Empty(VolumeKeyframes(Segments(kept, 1)[0]));
        Assert.Equal([0L, 0L], OurChoices(Json(DraftVoiceOverWriter.WithoutVoiceOver(twice))));
    }

    [Fact]
    public void ReplacesPreviousRun()
    {
        var once = DraftVoiceOverWriter.Apply(Tracks(), Bytes(Draft), OriginalAudio.Mute, replacePrevious: true, Project);
        var twice = DraftVoiceOverWriter.Apply(Tracks(), once, OriginalAudio.Mute, replacePrevious: true, Project);
        var d = Json(twice);
        Assert.Equal(5, d.GetProperty("tracks").GetArrayLength());
        var materials = d.GetProperty("materials");
        Assert.Equal(3, materials.GetProperty("audios").GetArrayLength());
        Assert.Equal(3, materials.GetProperty("beats").GetArrayLength());
        var video = VolumeKeyframes(Segments(d, 0)[0]);
        Assert.Single(video);
        Assert.Equal(0, video[0].GetProperty("keyframe_list")[1].GetProperty("values")[0].GetDouble());

        // Keep the original sound this time: the earlier ducking goes too.
        var kept = DraftVoiceOverWriter.Apply(Tracks(), once, OriginalAudio.Keep, replacePrevious: true, Project);
        Assert.Empty(VolumeKeyframes(Segments(Json(kept), 0)[0]));

        // What speech recognition hears: no voice-over, no ducking.
        var clean = Json(DraftVoiceOverWriter.WithoutVoiceOver(once));
        Assert.Equal(3, clean.GetProperty("tracks").GetArrayLength());
        Assert.Equal(0, clean.GetProperty("materials").GetProperty("audios").GetArrayLength());
        Assert.Empty(VolumeKeyframes(Segments(clean, 0)[0]));
    }

    [Fact]
    public void DuckingReachesTheTimeline()
    {
        var media = Path.Combine(Path.GetTempPath(), $"ecc-duck-{Guid.NewGuid()}.m4a");
        File.WriteAllBytes(media, [0]);
        try
        {
            var source = "{\"duration\":10000000,\"tracks\":[{\"type\":\"audio\",\"segments\":[{\"id\":\"S\",\"material_id\":\"A\","
                + "\"target_timerange\":{\"start\":0,\"duration\":10000000},\"volume\":0.5}]}],"
                + "\"materials\":{\"audios\":[{\"id\":\"A\",\"path\":" + JsonSerializer.Serialize(media) + "}]}}";
            var ducked = DraftVoiceOverWriter.Apply([Tracks()[0]], Bytes(source), OriginalAudio.Lower, replacePrevious: true);
            var timeline = TimelineBuilder.Build(EnhanceCapCut.Draft.Draft.Parse(Encoding.UTF8.GetString(ducked)), Path.GetTempPath());
            var clip = timeline.AudioClips.First(c => c.Path == media);
            Assert.Equal(0.5, clip.Volume);
            var envelope = clip.VolumeEnvelope;
            Assert.NotNull(envelope);
            Assert.Equal(1, envelope.Value(0));
            Assert.Equal(DraftVoiceOverWriter.LowerGain, envelope.Value(2_000_000), 9);
            Assert.DoesNotContain(timeline.Report.Unsupported, i => i.Category == "Keyframes");
        }
        finally
        {
            File.Delete(media);
        }
    }

    [Fact]
    public void FindsThePlaceholder()
    {
        Assert.Equal(Placeholder, DraftVoiceOverWriter.PlaceholderPrefix(Bytes(Draft)));
        Assert.Null(DraftVoiceOverWriter.PlaceholderPrefix(Bytes("{\"tracks\":[]}")));
    }
}
