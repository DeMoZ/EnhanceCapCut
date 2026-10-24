using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>One language of generated subtitles, to be added to a draft as a CapCut caption track.</summary>
/// <param name="IsOriginal">The spoken language (CapCut's recognized captions) rather than a translation.</param>
/// <param name="IsVisible">Hidden tracks can be shown in CapCut with the track's eye button.</param>
public sealed record SubtitleTrack(SubtitleLanguage Language, IReadOnlyList<CaptionCue> Cues, bool IsOriginal, bool IsVisible);

public sealed class ProjectOpenException(string name) : Exception(
    $"{name} is open in CapCut. Close the project (back to CapCut's home screen) and try again — "
    + "otherwise CapCut overwrites the subtitles when it saves.");

/// <summary>
/// Adds generated subtitles to a CapCut draft as caption tracks (<c>materials.texts</c> of type <c>subtitle</c>), one
/// text track per language, the way CapCut's own auto-captions are stored (see docs/FORMAT.md). A port of the macOS
/// version's <c>DraftSubtitleWriter.swift</c>; the two must stay in step.
///
/// The draft is edited in place with <see cref="JsonScanner"/>: only the <c>tracks</c>, <c>materials.texts</c> and
/// <c>materials.material_animations</c> arrays are rewritten, and only by appending or dropping elements.
/// </summary>
public static class DraftSubtitleWriter
{
    /// <summary>Name prefix of the tracks we create; a later run replaces tracks with this prefix.</summary>
    public const string TrackNamePrefix = "EnhanceCapCut subtitles";

    /// <summary>
    /// Defaults used when the draft has no captions to copy the style from. Measured on the author's projects:
    /// CapCut's caption preset is size 8, white with a thin outline, near the bottom.
    /// </summary>
    const double DefaultStyleSize = 8.0;
    const double DefaultPositionY = -0.65;

    public sealed record Result(IReadOnlyList<string> Files, string BackupFolder, int CueCount);

    public static string BackupRoot => Path.Combine(
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support"),
        "EnhanceCapCut", "Backups");

    /// <summary>
    /// CapCut keeps a <c>.locked</c> file in an open project's folder, but a crash leaves stale ones, so the lock only
    /// counts while CapCut is running.
    /// </summary>
    public static bool IsOpenInCapCut(CapCutProject project)
    {
        if (!File.Exists(Path.Combine(project.Folder, ".locked"))) return false;
        var processes = Process.GetProcessesByName("CapCut");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>
    /// Writes <paramref name="tracks"/> into the timeline's draft. The timeline's draft and every other copy of it that
    /// CapCut keeps (the root <c>draft_info.json</c> of the main timeline, <c>template-2.tmp</c>) are rewritten; their
    /// previous contents go to <see cref="BackupRoot"/> first.
    /// </summary>
    public static Result Write(IReadOnlyList<SubtitleTrack> tracks, CapCutProject project, ProjectTimeline timeline, bool replacePrevious)
    {
        if (IsOpenInCapCut(project)) throw new ProjectOpenException(project.Name);
        var original = File.ReadAllBytes(timeline.DraftPath);
        var updated = Apply(tracks, original, replacePrevious);

        // Copies of this draft: identical files next to it and, for the main timeline, the root draft.
        var candidates = new List<string> { timeline.DraftPath, Path.Combine(Path.GetDirectoryName(timeline.DraftPath)!, "template-2.tmp") };
        if (timeline.IsMain) candidates.AddRange([project.DraftPath, Path.Combine(project.Folder, "template-2.tmp")]);
        var files = new List<string>();
        foreach (var path in candidates.Select(Path.GetFullPath).Distinct())
        {
            if (path == Path.GetFullPath(timeline.DraftPath) || (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(original)))
                files.Add(path);
        }

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ", CultureInfo.InvariantCulture);
        var backup = Path.Combine(BackupRoot, project.Name, stamp);
        var folder = Path.GetFullPath(project.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in files)
        {
            var dest = Path.Combine(backup, path.StartsWith(folder, StringComparison.Ordinal) ? path[folder.Length..] : Path.GetFileName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(path, dest);
        }
        foreach (var path in files)
        {
            var temp = path + ".ecc-tmp";
            File.WriteAllBytes(temp, updated);
            File.Move(temp, path, overwrite: true);
        }
        return new Result(files, backup, tracks.Sum(t => t.Cues.Count));
    }

    /// <summary>
    /// Returns the draft with <paramref name="tracks"/> appended as caption tracks. With <paramref name="replacePrevious"/>,
    /// tracks from an earlier run (named with <see cref="TrackNamePrefix"/>) are removed together with their materials.
    /// </summary>
    public static byte[] Apply(IReadOnlyList<SubtitleTrack> tracks, byte[] draft, bool replacePrevious, DateTime? now = null)
    {
        var scanner = new JsonScanner(draft);
        var root = scanner.Root();
        var tracksRange = scanner.Value("tracks", root) ?? throw new InvalidDataException("The draft has no tracks list.");
        var materialsRange = scanner.Value("materials", root);
        var fps = (scanner.Value("fps", root) is { } f ? scanner.Parse(f) : null) is { ValueKind: JsonValueKind.Number } fe ? fe.GetDouble() : 30;
        var duration = (scanner.Value("duration", root) is { } d ? scanner.Parse(d) : null) is { ValueKind: JsonValueKind.Number } de
            && de.TryGetInt64(out var dl) ? dl : long.MaxValue;

        var trackElements = scanner.Elements(tracksRange);
        var textsRange = materialsRange is { } m1 ? scanner.Value("texts", m1) : null;
        var animationsRange = materialsRange is { } m2 ? scanner.Value("material_animations", m2) : null;
        var textElements = textsRange is { } t ? scanner.Elements(t) : [];
        var animationElements = animationsRange is { } a ? scanner.Elements(a) : [];

        // Drop our tracks from an earlier run, and the materials only they used.
        var parsedTracks = trackElements.Select(r => scanner.Parse(r) ?? default).ToList();
        if (replacePrevious)
        {
            var removed = new HashSet<string>();
            var kept = new List<Range>();
            for (var i = 0; i < trackElements.Count; i++)
            {
                if (parsedTracks[i].Str("name")?.StartsWith(TrackNamePrefix, StringComparison.Ordinal) == true)
                {
                    foreach (var segment in Segments(parsedTracks[i]))
                    {
                        if (segment.Str("material_id") is { } id) removed.Add(id);
                        foreach (var r in segment.Strings("extra_material_refs") ?? []) removed.Add(r);
                    }
                }
                else
                {
                    kept.Add(trackElements[i]);
                }
            }
            trackElements = kept;
            string Id(Range r) => (scanner.Parse(r) ?? default).Str("id") ?? "";
            textElements.RemoveAll(r => removed.Contains(Id(r)));
            animationElements.RemoveAll(r => removed.Contains(Id(r)));
        }

        // Style and position: copied from a caption already in the draft, else CapCut's defaults.
        JsonElement? styleMaterial = null;
        foreach (var r in textElements)
        {
            if (scanner.Parse(r) is { } e && e.Str("type") == "subtitle") { styleMaterial = e; break; }
        }
        JsonElement? styleSegment = null;
        if (styleMaterial?.Str("id") is { } styleID)
        {
            foreach (var track in parsedTracks)
            {
                styleSegment = Segments(track).Cast<JsonElement?>().FirstOrDefault(s => s!.Value.Str("material_id") == styleID);
                if (styleSegment is not null) break;
            }
        }
        var renderIndex = 14000L;
        foreach (var track in parsedTracks)
            foreach (var segment in Segments(track))
                if (segment.Int64("render_index") is { } i && i >= 14000) renderIndex = Math.Max(renderIndex, i + 1);

        var groupStamp = new DateTimeOffset(now ?? DateTime.UtcNow).ToUnixTimeMilliseconds();
        var newTracks = new List<string>();
        var newTexts = new List<string>();
        var newAnimations = new List<string>();
        for (var n = 0; n < tracks.Count; n++)
        {
            var track = tracks[n];
            var trackIndex = trackElements.Count + n;
            var attribute = track.IsVisible ? 0 : 2;
            var segments = new List<object?>();
            foreach (var cue in track.Cues)
            {
                var start = FrameAligned(cue.Start, fps);
                var end = Math.Min(FrameAligned(cue.End, fps), duration);
                if (end <= start) continue;
                var materialID = Lenient.NewId();
                var animationID = Lenient.NewId();
                newTexts.Add(JsonText.Encode(Material(track, cue, materialID, end - start,
                    $"{track.Language.CapCut}_{groupStamp + n}", styleMaterial)));
                newAnimations.Add(JsonText.Encode(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = animationID, ["type"] = "sticker_animation", ["animations"] = new List<object?>(),
                    ["multi_language_current"] = "none",
                }));
                segments.Add(Segment(Lenient.NewId(), materialID, animationID, start, end - start, renderIndex, trackIndex,
                    attribute, styleSegment));
                renderIndex++;
            }
            newTracks.Add(JsonText.Encode(new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Lenient.NewId(), ["type"] = "text", ["flag"] = 1, ["attribute"] = attribute,
                ["name"] = $"{TrackNamePrefix} · {track.Language.Code}", ["is_default_name"] = false, ["segments"] = segments,
            }));
        }

        string Array(List<Range> kept, List<string> added) =>
            "[" + string.Join(",", kept.Select(scanner.Text).Concat(added)) + "]";
        var edits = new JsonEdits();
        edits.Replace(tracksRange, Array(trackElements, newTracks));
        if (materialsRange is { } materials)
        {
            var additions = new List<string>();
            if (textsRange is { } tr) edits.Replace(tr, Array(textElements, newTexts));
            else additions.Add("\"texts\":" + Array([], newTexts));
            if (animationsRange is { } ar) edits.Replace(ar, Array(animationElements, newAnimations));
            else additions.Add("\"material_animations\":" + Array([], newAnimations));
            if (additions.Count > 0)
            {
                var close = materials.End.Value - 1;
                var empty = scanner.Members(materials).Count == 0;
                edits.Replace(close..close, (empty ? "" : ",") + string.Join(",", additions));
            }
        }
        else
        {
            var close = root.End.Value - 1;
            edits.Replace(close..close, $",\"materials\":{{\"texts\":{Array([], newTexts)},\"material_animations\":{Array([], newAnimations)}}}");
        }
        return edits.AppliedTo(scanner.Bytes);
    }

    static IEnumerable<JsonElement> Segments(JsonElement track) =>
        track.Get("segments") is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object) : [];

    /// <summary>Microseconds, on a frame boundary of the draft's frame rate.</summary>
    static long FrameAligned(double seconds, double fps) =>
        Lenient.Round(Math.Round(Math.Max(0, seconds) * fps, MidpointRounding.AwayFromZero) / fps * 1_000_000);

    static SortedDictionary<string, object?> Material(SubtitleTrack track, CaptionCue cue, string id, long duration, string group,
        JsonElement? template)
    {
        var m = template is { } t ? (SortedDictionary<string, object?>)JsonText.From(t)! : JsonText.Object(DefaultMaterial);
        var text = cue.Text;
        // The style run of the template's content, stretched over the new text (ranges are UTF-16 offsets).
        SortedDictionary<string, object?>? style = null;
        if (m.GetValueOrDefault("content") is string content)
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.Get("styles") is { ValueKind: JsonValueKind.Array } styles && styles.GetArrayLength() > 0
                    && styles[0].ValueKind == JsonValueKind.Object)
                    style = (SortedDictionary<string, object?>)JsonText.From(styles[0])!;
            }
            catch (JsonException)
            {
            }
        }
        style ??= JsonText.Object(DefaultStyle);
        style["range"] = new List<object?> { 0, text.Length };
        m["content"] = JsonText.Encode(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["styles"] = new List<object?> { style }, ["text"] = text,
        });
        if (style.GetValueOrDefault("size") is JsonRaw size
            && double.TryParse(size.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            m["font_size"] = s;

        // Word timings in milliseconds from the clip start; CapCut spreads translated words evenly too.
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var step = duration / 1000.0 / Math.Max(words.Length, 1);
        m["words"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["start_time"] = Enumerable.Range(0, words.Length).Select(i => (object?)Lenient.Round(i * step)).ToList(),
            ["end_time"] = Enumerable.Range(0, words.Length).Select(i => (object?)Lenient.Round((i + 1) * step)).ToList(),
            ["text"] = words.Select(w => (object?)w).ToList(),
        };
        m["current_words"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["start_time"] = new List<object?>(), ["end_time"] = new List<object?>(), ["text"] = new List<object?>(),
        };
        m["id"] = id;
        m["type"] = "subtitle";
        m["recognize_text"] = text.Replace("\n", " ");
        m["base_content"] = "";
        m["language"] = track.Language.CapCut;
        m["group_id"] = group;
        m["recognize_task_id"] = "";
        m["relevance_segment"] = new List<object?>();
        m["translate_original_text"] = "";
        // As in CapCut's own captions: recognized ones are sub type 0 / add type 1, translations 5 / 0.
        m["sub_type"] = track.IsOriginal ? 0 : 5;
        m["add_type"] = track.IsOriginal ? 1 : 0;
        return m;
    }

    static SortedDictionary<string, object?> Segment(string id, string materialID, string animationID, long start, long duration,
        long renderIndex, int trackIndex, int attribute, JsonElement? template)
    {
        var s = JsonText.Object(DefaultSegment);
        if (template is { } t)
        {
            foreach (var key in new[] { "clip", "uniform_scale" })
                if (t.Get(key) is { } v) s[key] = JsonText.From(v);
        }
        s["id"] = id;
        s["material_id"] = materialID;
        s["extra_material_refs"] = new List<object?> { animationID };
        s["target_timerange"] = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["start"] = start, ["duration"] = duration };
        s["render_index"] = renderIndex;
        s["track_render_index"] = trackIndex;
        s["track_attribute"] = attribute;
        s["common_keyframes"] = new List<object?>();
        s["keyframe_refs"] = new List<object?>();
        s["visible"] = true;
        return s;
    }

    // Field sets of a caption as CapCut 9.5 for macOS writes it (see docs/FORMAT.md), used when the draft has no
    // caption to copy. Floating-point fields are written with a decimal point on purpose. Same as the macOS version.

    static readonly string Size = JsonText.Double(DefaultStyleSize);

    static readonly string DefaultStyle =
        "{\"fill\":{\"content\":{\"render_type\":\"solid\",\"solid\":{\"color\":[1.0,1.0,1.0]}}},"
        + "\"font\":{\"id\":\"\",\"path\":\"/Applications/CapCut.app/Contents/Resources/Font/SystemFont/en.ttf\"},"
        + $"\"range\":[0,0],\"size\":{Size}}}";

    static readonly string DefaultMaterial =
        "{\"add_type\":0,\"alignment\":1,\"autoAdaptCanvasEnabled\":false,\"background_alpha\":1.0,\"background_color\":\"\","
        + "\"background_fill\":\"\",\"background_height\":0.14,\"background_horizontal_offset\":0.0,\"background_round_radius\":0.0,"
        + "\"background_style\":0,\"background_vertical_offset\":0.0,\"background_width\":0.14,\"base_content\":\"\","
        + "\"bold_width\":0.0,\"border_alpha\":1.0,\"border_color\":\"\",\"border_mode\":0,\"border_width\":0.08,"
        + "\"caption_template_info\":{\"category_id\":\"\",\"category_name\":\"\",\"effect_id\":\"\",\"is_new\":false,\"path\":\"\","
        + "\"request_id\":\"\",\"resource_id\":\"\",\"resource_name\":\"\",\"source_platform\":0,\"third_resource_id\":\"\"},"
        + "\"check_flag\":7,\"combo_info\":{\"text_templates\":[]},\"content\":\"\",\"current_words\":{\"end_time\":[],\"start_time\":[],\"text\":[]},"
        + "\"cutoff_postfix\":\"\",\"enable_path_typesetting\":false,\"fixed_height\":-1.0,\"fixed_width\":-1.0,"
        + "\"font_category_id\":\"\",\"font_category_name\":\"\",\"font_id\":\"\","
        + "\"font_name\":\"\",\"font_path\":\"/Applications/CapCut.app/Contents/Resources/Font/SystemFont/en.ttf\","
        + $"\"font_resource_id\":\"\",\"font_size\":{Size},\"font_source_platform\":0,\"font_team_id\":\"\","
        + "\"font_third_resource_id\":\"\",\"font_title\":\"none\",\"font_url\":\"\",\"fonts\":[],\"force_apply_line_max_width\":false,"
        + "\"global_alpha\":1.0,\"group_id\":\"\",\"has_shadow\":false,\"id\":\"\",\"initial_scale\":1.0,\"inner_padding\":-1.0,"
        + "\"is_batch_replace\":false,\"is_lyric_effect\":false,\"is_rich_text\":false,\"is_words_linear\":false,"
        + "\"italic_degree\":0,\"ktv_color\":\"\",\"language\":\"\",\"layer_weight\":1,\"letter_spacing\":0.0,\"line_feed\":1,"
        + "\"line_max_width\":0.82,\"line_spacing\":0.02,\"lyric_group_id\":\"\",\"lyrics_template\":{\"category_id\":\"\","
        + "\"category_name\":\"\",\"effect_id\":\"\",\"panel\":\"\",\"path\":\"\",\"request_id\":\"\",\"resource_id\":\"\",\"resource_name\":\"\"},"
        + "\"multi_language_current\":\"none\",\"name\":\"\",\"offset_on_path\":0.0,\"oneline_cutoff\":false,\"operation_type\":0,"
        + "\"original_size\":[],\"preset_category\":\"\",\"preset_category_id\":\"\",\"preset_has_set_alignment\":false,\"preset_id\":\"\","
        + "\"preset_index\":0,\"preset_name\":\"\",\"punc_model\":\"\",\"recognize_model\":\"\",\"recognize_task_id\":\"\",\"recognize_text\":\"\","
        + "\"recognize_type\":0,\"relevance_segment\":[],\"shadow_alpha\":0.9,\"shadow_angle\":-45.0,\"shadow_color\":\"\","
        + "\"shadow_distance\":5.0,\"shadow_point\":{\"x\":0.6363961030678928,\"y\":-0.6363961030678927},\"shadow_smoothing\":0.45,"
        + "\"shadow_thickness_projection_angle\":0.0,\"shadow_thickness_projection_distance\":0.0,"
        + "\"shadow_thickness_projection_enable\":false,\"shape_clip_x\":false,\"shape_clip_y\":false,"
        + "\"single_char_bg_alpha\":1.0,\"single_char_bg_color\":\"\",\"single_char_bg_enable\":false,\"single_char_bg_height\":0.0,"
        + "\"single_char_bg_horizontal_offset\":0.0,\"single_char_bg_round_radius\":0.3,\"single_char_bg_vertical_offset\":0.0,"
        + "\"single_char_bg_width\":0.0,\"source_from\":\"\",\"ssml_content\":\"\",\"style_name\":\"\",\"sub_template_id\":-1,\"sub_type\":0,"
        + "\"subtitle_keywords\":null,\"subtitle_keywords_config\":null,\"subtitle_template_original_fontsize\":0.0,"
        + "\"text_alpha\":1.0,\"text_color\":\"#FFFFFF\",\"text_curve\":null,\"text_exceeds_path_process_type\":0,"
        + "\"text_loop_on_path\":false,\"text_preset_resource_id\":\"\",\"text_size\":30,\"text_to_audio_ids\":[],"
        + "\"text_typesetting_path_index\":0,\"text_typesetting_paths\":null,\"text_typesetting_paths_file\":\"\","
        + "\"translate_original_text\":\"\",\"tts_auto_update\":false,\"type\":\"subtitle\",\"typesetting\":0,\"underline\":false,"
        + "\"underline_offset\":0.22,\"underline_width\":0.05,\"use_effect_default_color\":true,\"words\":{\"end_time\":[],"
        + "\"start_time\":[],\"text\":[]}}";

    static readonly string DefaultSegment =
        "{\"caption_info\":null,\"cartoon\":false,\"clip\":{\"alpha\":1.0,\"flip\":{\"horizontal\":false,\"vertical\":false},"
        + $"\"rotation\":0.0,\"scale\":{{\"x\":1.0,\"y\":1.0}},\"transform\":{{\"x\":0.0,\"y\":{JsonText.Double(DefaultPositionY)}}}}},"
        + "\"color_correct_alg_result\":\"\",\"common_keyframes\":[],\"desc\":\"\",\"digital_human_template_group_id\":\"\","
        + "\"enable_adjust\":false,\"enable_adjust_mask\":false,\"enable_color_adjust_pro\":false,"
        + "\"enable_color_correct_adjust\":false,\"enable_color_curves\":true,\"enable_color_match_adjust\":false,"
        + "\"enable_color_wheels\":true,\"enable_hsl\":false,\"enable_hsl_curves\":true,\"enable_lut\":false,"
        + "\"enable_mask_shadow\":false,\"enable_mask_stroke\":false,\"enable_smart_color_adjust\":false,"
        + "\"enable_video_mask\":true,\"extra_material_refs\":[],\"group_id\":\"\",\"hdr_settings\":null,"
        + "\"hdr_vivid_settings\":null,\"id\":\"\",\"intensifies_audio\":false,\"is_loop\":false,\"is_placeholder\":false,"
        + "\"is_tone_modify\":false,\"keyframe_refs\":[],\"last_nonzero_volume\":1.0,\"lyric_keyframes\":null,\"material_id\":\"\","
        + "\"raw_segment_id\":\"\",\"render_index\":14000,\"render_timerange\":{\"duration\":0,\"start\":0},"
        + "\"responsive_layout\":{\"enable\":false,\"horizontal_pos_layout\":0,\"size_layout\":0,\"target_follow\":\"\","
        + "\"vertical_pos_layout\":0},\"reverse\":false,\"segment_color_tag\":\"\",\"source\":\"segmentsourcenormal\","
        + "\"source_timerange\":null,\"speed\":1.0,\"state\":0,\"target_timerange\":{\"duration\":0,\"start\":0},"
        + "\"template_id\":\"\",\"template_scene\":\"default\",\"track_attribute\":0,\"track_render_index\":0,"
        + "\"uniform_scale\":{\"on\":true,\"value\":1.0},\"visible\":true,\"volume\":1.0}";
}
