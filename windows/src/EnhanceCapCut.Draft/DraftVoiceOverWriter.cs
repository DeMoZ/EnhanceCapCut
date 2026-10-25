using System.Text;
using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>One language of generated voice-over, to be added to a draft as an audio track.</summary>
/// <param name="Path">The spoken track as one file aligned to the timeline (file time 0 = timeline start).</param>
/// <param name="FileDuration">Length of the file, seconds.</param>
/// <param name="Lines">Where each line was placed, seconds. Every line becomes its own clip on the track.</param>
/// <param name="IsAudible">Muted tracks can be unmuted in CapCut with the track's speaker button.</param>
public sealed record VoiceOverTrack(SubtitleLanguage Language, string Path, double FileDuration,
    IReadOnlyList<(double Start, double End)> Lines, bool IsAudible);

/// <summary>What happens to the project's own sound while the voice-over speaks.</summary>
public enum OriginalAudio { Keep, Lower, Mute }

/// <summary>
/// Adds voice-over to a CapCut draft: one audio track per language with a clip per spoken line
/// (<c>materials.audios</c> of type <c>record</c>, like CapCut's own voiceovers; see docs/FORMAT.md), and optionally
/// ducks the other audio with <c>KFTypeVolume</c> keyframes while the voice speaks. A port of the macOS version's
/// <c>DraftVoiceOverWriter.swift</c>; the two must stay in step.
///
/// Like <see cref="DraftSubtitleWriter"/>, the draft is edited in place with <see cref="JsonScanner"/>: only the arrays
/// we add to and the <c>common_keyframes</c> of ducked segments are rewritten.
/// </summary>
public static class DraftVoiceOverWriter
{
    /// <summary>Name prefix of the tracks we create; a later run replaces tracks with this prefix.</summary>
    public const string TrackNamePrefix = "EnhanceCapCut voice-over";
    /// <summary>First block of the ids of the volume keyframe groups we add, so a later run can find and remove them.</summary>
    public const string KeyframeIDPrefix = "ECC0D0CC-";

    /// <summary>"Lower": −15 dB while the voice speaks, as in SubDub.</summary>
    public const double LowerGain = 0.178;
    /// <summary>Ducking ramps: down before a line starts, back up after it ends (µs).</summary>
    const long FadeDown = 250_000;
    const long FadeUp = 400_000;

    /// <summary>Gain applied while the voice speaks; null = untouched.</summary>
    public static double? DuckGain(OriginalAudio original) => original switch
    {
        OriginalAudio.Keep => null,
        OriginalAudio.Lower => LowerGain,
        _ => 0,
    };

    /// <summary>
    /// Returns the draft with <paramref name="tracks"/> appended as audio tracks. With <paramref name="replacePrevious"/>,
    /// tracks and ducking from an earlier run are removed first. Media paths inside <paramref name="projectFolder"/> are
    /// written with the draft's own project-folder placeholder when it has one, so they move with the project.
    /// </summary>
    public static byte[] Apply(IReadOnlyList<VoiceOverTrack> tracks, byte[] draft, OriginalAudio original, bool replacePrevious,
        string? projectFolder = null)
    {
        var data = draft;
        if (replacePrevious) data = RemoveDucking(data);
        data = AddTracks(tracks, data, replacePrevious, projectFolder);
        if (DuckGain(original) is { } gain && tracks.FirstOrDefault(t => t.IsAudible) is { } spoken)
        {
            var ranges = spoken.Lines.Select(l => (Us(l.Start), Us(l.End))).ToList();
            data = Duck(data, Merge(ranges, FadeDown + FadeUp), gain);
        }
        return data;
    }

    /// <summary>The draft without the voice-over and ducking of earlier runs: what speech recognition should hear.</summary>
    public static byte[] WithoutVoiceOver(byte[] draft) => AddTracks([], RemoveDucking(draft), replacePrevious: true, projectFolder: null);

    // MARK: Tracks

    static byte[] AddTracks(IReadOnlyList<VoiceOverTrack> tracks, byte[] draft, bool replacePrevious, string? projectFolder)
    {
        var scanner = new JsonScanner(draft);
        var root = scanner.Root();
        var tracksRange = scanner.Value("tracks", root) ?? throw new InvalidDataException("The draft has no tracks list.");
        var materialsRange = scanner.Value("materials", root);
        var duration = (scanner.Value("duration", root) is { } d ? scanner.Parse(d) : null) is { ValueKind: JsonValueKind.Number } de
            && de.TryGetInt64(out var dl) ? dl : long.MaxValue;
        var placeholder = projectFolder is null ? null : PlaceholderPrefix(scanner.Bytes);

        var trackElements = scanner.Elements(tracksRange);
        var removed = new HashSet<string>();
        if (replacePrevious)
        {
            trackElements.RemoveAll(range =>
            {
                var track = scanner.Parse(range) ?? default;
                if (track.Str("name")?.StartsWith(TrackNamePrefix, StringComparison.Ordinal) != true) return false;
                foreach (var segment in Segments(track))
                {
                    if (segment.Str("material_id") is { } id) removed.Add(id);
                    foreach (var r in segment.Strings("extra_material_refs") ?? []) removed.Add(r);
                }
                return true;
            });
        }

        var newTracks = new List<string>();
        var added = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string json)
        {
            if (!added.TryGetValue(key, out var list)) added[key] = list = [];
            list.Add(json);
        }
        for (var n = 0; n < tracks.Count; n++)
        {
            var track = tracks[n];
            var trackIndex = trackElements.Count + n;
            var attribute = track.IsAudible ? 0 : 1;
            var path = track.Path;
            if (placeholder is not null && projectFolder is not null)
            {
                var folder = projectFolder.TrimEnd('/', '\\');
                if (path.Length > folder.Length && path.StartsWith(folder, StringComparison.Ordinal) && path[folder.Length] is '/' or '\\')
                    path = placeholder + path[folder.Length..];
            }
            var fileDuration = Us(track.FileDuration);
            var segments = new List<object?>();
            for (var i = 0; i < track.Lines.Count; i++)
            {
                var start = Us(track.Lines[i].Start);
                var end = Math.Min(Math.Min(Us(track.Lines[i].End), duration), fileDuration);
                if (end <= start) continue;
                var materialID = Lenient.NewId();
                var audio = JsonText.Object(DefaultAudio);
                audio["id"] = materialID;
                audio["name"] = $"Voice-over {track.Language.Code} {i + 1}";
                audio["path"] = path;
                audio["duration"] = fileDuration;
                Add("audios", JsonText.Encode(audio));

                var refs = new List<object?>();
                foreach (var (key, template) in ExtraMaterials)
                {
                    var m = JsonText.Object(template);
                    var id = Lenient.NewId();
                    m["id"] = id;
                    refs.Add(id);
                    Add(key, JsonText.Encode(m));
                }
                var s = JsonText.Object(DefaultSegment);
                s["id"] = Lenient.NewId();
                s["material_id"] = materialID;
                s["extra_material_refs"] = refs;
                s["source_timerange"] = TimeRange(start, end - start);
                s["target_timerange"] = TimeRange(start, end - start);
                s["track_render_index"] = trackIndex;
                s["track_attribute"] = attribute;
                segments.Add(s);
            }
            newTracks.Add(JsonText.Encode(new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Lenient.NewId(), ["type"] = "audio", ["flag"] = 0, ["attribute"] = attribute,
                ["name"] = $"{TrackNamePrefix} · {track.Language.Code}", ["is_default_name"] = false, ["segments"] = segments,
            }));
        }

        string Array(IEnumerable<Range> kept, IEnumerable<string> extra) =>
            "[" + string.Join(",", kept.Select(scanner.Text).Concat(extra)) + "]";
        var edits = new JsonEdits();
        edits.Replace(tracksRange, Array(trackElements, newTracks));
        if (materialsRange is { } materials)
        {
            var missing = new SortedDictionary<string, List<string>>(added, StringComparer.Ordinal);
            var members = scanner.Members(materials);
            foreach (var (key, range) in members)
            {
                if (scanner.Bytes[range.Start.Value] != (byte)'[') continue;
                missing.Remove(key);
                var elements = scanner.Elements(range);
                var before = elements.Count;
                if (removed.Count > 0) elements.RemoveAll(r => removed.Contains((scanner.Parse(r) ?? default).Str("id") ?? ""));
                var extra = added.GetValueOrDefault(key) ?? [];
                if (elements.Count != before || extra.Count > 0) edits.Replace(range, Array(elements, extra));
            }
            if (missing.Count > 0)
            {
                var close = materials.End.Value - 1;
                var text = string.Join(",", missing.Select(p => $"\"{p.Key}\":" + Array([], p.Value)));
                edits.Replace(close..close, (members.Count == 0 ? "" : ",") + text);
            }
        }
        else
        {
            var close = root.End.Value - 1;
            var text = string.Join(",", added.Select(p => $"\"{p.Key}\":" + Array([], p.Value)));
            edits.Replace(close..close, ",\"materials\":{" + text + "}");
        }
        return edits.AppliedTo(scanner.Bytes);
    }

    static SortedDictionary<string, object?> TimeRange(long start, long duration) =>
        new(StringComparer.Ordinal) { ["start"] = start, ["duration"] = duration };

    /// <summary><c>##_draftpath_placeholder_&lt;UUID&gt;_##</c>, the project folder in CapCut's own media paths.</summary>
    internal static string? PlaceholderPrefix(byte[] bytes)
    {
        var marker = "##_draftpath_placeholder_"u8;
        var start = bytes.AsSpan().IndexOf(marker);
        if (start < 0) return null;
        for (var i = start + marker.Length; i + 2 < bytes.Length && i - start < 80; i++)
        {
            if (bytes[i] == '_' && bytes[i + 1] == '#' && bytes[i + 2] == '#')
                return Encoding.UTF8.GetString(bytes, start, i + 3 - start);
        }
        return null;
    }

    // MARK: Ducking

    /// <summary>
    /// Volume keyframes on every audible segment that overlaps <paramref name="speech"/>: down to <paramref name="gain"/> ×
    /// its volume while the voice speaks, back up in between. Segments that already have volume keyframes of their own
    /// are left alone.
    /// </summary>
    static byte[] Duck(byte[] draft, List<(long Start, long End)> speech, double gain)
    {
        if (speech.Count == 0) return draft;
        var edits = new JsonEdits();
        var scanner = new JsonScanner(draft);
        ForEachSegment(scanner, (track, segment, members) =>
        {
            if (track.Str("type") is not ("video" or "audio") || ((track.Int64("attribute") ?? 0) & 1) != 0
                || track.Str("name")?.StartsWith(TrackNamePrefix, StringComparison.Ordinal) == true) return;
            var parsed = scanner.Parse(segment) ?? default;
            if (parsed.Get("common_keyframes") is { ValueKind: JsonValueKind.Array } groups
                && groups.EnumerateArray().Any(g => g.Str("property_type") == "KFTypeVolume")) return;
            if (parsed.Obj("target_timerange") is not { } range || range.Int64("start") is not { } start
                || range.Int64("duration") is not { } length || length <= 0) return;
            var volume = parsed.Num("volume") ?? 1;
            if (volume <= 0) return;

            // Timeline points (ducked or not), then clamped into the segment; later points win on a tie.
            var points = new List<(long T, bool Ducked)>();
            foreach (var r in speech.Where(r => r.End + FadeUp > start && r.Start - FadeDown < start + length))
                points.AddRange([(r.Start - FadeDown, false), (r.Start, true), (r.End, true), (r.End + FadeUp, false)]);
            if (points.Count == 0) return;
            var keyframes = new List<(long T, bool Ducked)>();
            foreach (var p in points)
            {
                var local = Math.Min(Math.Max(p.T - start, 0), length);
                if (keyframes.Count > 0 && keyframes[^1].T == local) keyframes[^1] = (local, p.Ducked);
                else keyframes.Add((local, p.Ducked));
            }
            var group = JsonText.Encode(new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = KeyframeIDPrefix + Lenient.NewId()[KeyframeIDPrefix.Length..],
                ["material_id"] = "", ["property_type"] = "KFTypeVolume",
                ["keyframe_list"] = keyframes.Select(k => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Lenient.NewId(), ["curveType"] = "Line", ["time_offset"] = k.T,
                    ["left_control"] = Point(), ["right_control"] = Point(),
                    ["values"] = new List<object?> { volume * (k.Ducked ? gain : 1) }, ["string_value"] = "", ["graphID"] = "",
                }).ToList(),
            });
            if (members.FirstOrDefault(m => m.Key == "common_keyframes") is { Key: not null } existing
                && scanner.Bytes[existing.Value.Start.Value] == (byte)'[')
            {
                var kept = scanner.Elements(existing.Value).Select(scanner.Text);
                edits.Replace(existing.Value, "[" + string.Join(",", kept.Append(group)) + "]");
            }
            else
            {
                var close = segment.End.Value - 1;
                edits.Replace(close..close, (members.Count == 0 ? "" : ",") + $"\"common_keyframes\":[{group}]");
            }
        });
        return edits.AppliedTo(scanner.Bytes);
    }

    static SortedDictionary<string, object?> Point() => new(StringComparer.Ordinal) { ["x"] = 0.0, ["y"] = 0.0 };

    /// <summary>Drops the volume keyframe groups an earlier run added.</summary>
    static byte[] RemoveDucking(byte[] draft)
    {
        var edits = new JsonEdits();
        var changed = false;
        var scanner = new JsonScanner(draft);
        ForEachSegment(scanner, (_, _, members) =>
        {
            if (members.FirstOrDefault(m => m.Key == "common_keyframes") is not { Key: not null } entry
                || scanner.Bytes[entry.Value.Start.Value] != (byte)'[') return;
            var groups = scanner.Elements(entry.Value);
            var kept = groups.Where(g => !((scanner.Parse(g) ?? default).Str("id") ?? "").StartsWith(KeyframeIDPrefix, StringComparison.Ordinal))
                .ToList();
            if (kept.Count == groups.Count) return;
            changed = true;
            edits.Replace(entry.Value, "[" + string.Join(",", kept.Select(scanner.Text)) + "]");
        });
        return changed ? edits.AppliedTo(scanner.Bytes) : draft;
    }

    /// <summary>Calls <paramref name="body"/> with each top-level track (parsed), segment range and the segment's members.</summary>
    static void ForEachSegment(JsonScanner scanner, Action<JsonElement, Range, List<(string Key, Range Value)>> body)
    {
        var root = scanner.Root();
        if (scanner.Value("tracks", root) is not { } tracksRange) return;
        foreach (var trackRange in scanner.Elements(tracksRange))
        {
            var members = scanner.Members(trackRange);
            var track = scanner.Parse(trackRange) ?? default;
            if (members.FirstOrDefault(m => m.Key == "segments") is not { Key: not null } segments
                || scanner.Bytes[segments.Value.Start.Value] != (byte)'[') continue;
            foreach (var segment in scanner.Elements(segments.Value)) body(track, segment, scanner.Members(segment));
        }
    }

    internal static List<(long Start, long End)> Merge(List<(long Start, long End)> ranges, long gap)
    {
        var result = new List<(long Start, long End)>();
        foreach (var r in ranges.OrderBy(r => r.Start))
        {
            if (result.Count > 0 && r.Start - result[^1].End < gap) result[^1] = (result[^1].Start, Math.Max(result[^1].End, r.End));
            else result.Add(r);
        }
        return result;
    }

    static IEnumerable<JsonElement> Segments(JsonElement track) =>
        track.Get("segments") is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object) : [];

    static long Us(double seconds) => Lenient.Round(Math.Max(0, seconds) * 1_000_000);

    // Field sets of a voiceover clip as CapCut 9.5 for macOS writes it (see docs/FORMAT.md). Floating-point fields are
    // written with a decimal point on purpose. Same as the macOS version.

    const string DefaultAudio =
        "{\"id\":\"\",\"unique_id\":\"\",\"type\":\"record\",\"name\":\"\",\"duration\":0,\"path\":\"\",\"category_name\":\"\",\"wave_points\":[],"
        + "\"music_id\":\"\",\"app_id\":0,\"text_id\":\"\",\"tone_type\":\"\",\"source_platform\":0,\"video_id\":\"\",\"effect_id\":\"\","
        + "\"resource_id\":\"\",\"third_resource_id\":\"\",\"category_id\":\"\",\"intensifies_path\":\"\",\"formula_id\":\"\",\"check_flag\":1,"
        + "\"team_id\":\"\",\"local_material_id\":\"\",\"tone_speaker\":\"\",\"mock_tone_speaker\":\"\",\"tone_effect_id\":\"\","
        + "\"tone_effect_name\":\"\",\"tone_platform\":\"\",\"cloned_model_type\":\"\",\"tone_category_id\":\"\",\"tone_category_name\":\"\","
        + "\"tone_second_category_id\":\"\",\"tone_second_category_name\":\"\",\"tone_emotion_name_key\":\"\",\"tone_emotion_style\":\"\","
        + "\"tone_emotion_role\":\"\",\"tone_emotion_selection\":\"\",\"tone_emotion_scale\":0.0,\"moyin_emotion\":\"\",\"request_id\":\"\","
        + "\"query\":\"\",\"search_id\":\"\",\"sound_separate_type\":\"\",\"is_text_edit_overdub\":false,\"is_ugc\":false,"
        + "\"is_ai_clone_tone\":false,\"is_ai_clone_tone_post\":false,\"source_from\":\"\",\"copyright_limit_type\":\"none\","
        + "\"aigc_history_id\":\"\",\"aigc_item_id\":\"\",\"music_source\":\"\",\"pgc_id\":\"\",\"pgc_name\":\"\","
        + "\"similiar_music_info\":{\"original_song_id\":\"\",\"original_song_name\":\"\"},\"ai_music_type\":0,\"ai_music_enter_from\":\"\","
        + "\"lyric_type\":0,\"tts_task_id\":\"\",\"tts_generate_scene\":\"\",\"ai_music_generate_scene\":0,"
        + "\"tts_benefit_info\":{\"benefit_type\":\"none\",\"benefit_log_id\":\"\",\"benefit_log_extra\":\"\",\"benefit_amount\":-1}}";

    /// <summary>Per-clip materials CapCut gives a voiceover clip, by <c>materials</c> key.</summary>
    static readonly (string Key, string Json)[] ExtraMaterials =
    [
        ("speeds", "{\"id\":\"\",\"type\":\"speed\",\"mode\":0,\"speed\":1.0,\"curve_speed\":null}"),
        ("placeholder_infos",
            "{\"id\":\"\",\"type\":\"placeholder_info\",\"meta_type\":\"none\",\"res_path\":\"\",\"res_text\":\"\",\"error_path\":\"\",\"error_text\":\"\"}"),
        ("beats",
            "{\"id\":\"\",\"type\":\"beats\",\"enable_ai_beats\":false,\"gear\":404,\"gear_count\":0,\"mode\":404,\"user_beats\":[],"
            + "\"user_delete_ai_beats\":null,\"ai_beats\":{\"melody_url\":\"\",\"melody_path\":\"\",\"beats_url\":\"\",\"beats_path\":\"\","
            + "\"melody_percents\":[0.0],\"beat_speed_infos\":[]}}"),
        ("sound_channel_mappings", "{\"id\":\"\",\"type\":\"\",\"audio_channel_mapping\":0,\"is_config_open\":false}"),
        ("vocal_separations",
            "{\"id\":\"\",\"type\":\"vocal_separation\",\"choice\":0,\"removed_sounds\":[],\"time_range\":null,\"production_path\":\"\","
            + "\"final_algorithm\":\"\",\"enter_from\":\"\"}"),
    ];

    const string DefaultSegment =
        "{\"id\":\"\",\"source_timerange\":{\"start\":0,\"duration\":0},\"target_timerange\":{\"start\":0,\"duration\":0},"
        + "\"render_timerange\":{\"start\":0,\"duration\":0},\"desc\":\"\",\"state\":0,\"speed\":1.0,\"is_loop\":false,"
        + "\"is_tone_modify\":false,\"reverse\":false,\"intensifies_audio\":false,\"cartoon\":false,\"volume\":1.0,"
        + "\"last_nonzero_volume\":1.0,\"clip\":null,\"uniform_scale\":null,\"material_id\":\"\",\"extra_material_refs\":[],"
        + "\"render_index\":0,\"keyframe_refs\":[],\"enable_lut\":false,\"enable_adjust\":false,\"enable_hsl\":false,\"visible\":true,"
        + "\"group_id\":\"\",\"enable_color_curves\":true,\"enable_hsl_curves\":true,\"track_render_index\":0,\"hdr_settings\":null,"
        + "\"enable_color_wheels\":true,\"track_attribute\":0,\"is_placeholder\":false,\"template_id\":\"\","
        + "\"enable_smart_color_adjust\":false,\"template_scene\":\"default\",\"common_keyframes\":[],\"caption_info\":null,"
        + "\"responsive_layout\":{\"enable\":false,\"target_follow\":\"\",\"size_layout\":0,\"horizontal_pos_layout\":0,"
        + "\"vertical_pos_layout\":0},\"enable_color_match_adjust\":false,\"enable_color_correct_adjust\":false,"
        + "\"enable_adjust_mask\":false,\"raw_segment_id\":\"\",\"lyric_keyframes\":null,\"enable_video_mask\":true,"
        + "\"digital_human_template_group_id\":\"\",\"color_correct_alg_result\":\"\",\"source\":\"segmentsourcenormal\","
        + "\"enable_mask_stroke\":false,\"enable_mask_shadow\":false,\"enable_color_adjust_pro\":false}";
}
