//! Adds generated subtitles to a CapCut draft as caption tracks (`materials.texts` of type `subtitle`), one text track
//! per language, the way CapCut's own auto-captions are stored (see docs/FORMAT.md): the port of
//! `DraftSubtitleWriter.swift`.
//!
//! Only the `tracks`, `materials.texts` and `materials.material_animations` arrays are rewritten, and only by appending
//! or dropping elements.

use std::collections::HashSet;
use std::ops::Range;
use std::sync::OnceLock;

use serde_json::{json, Map, Value};

use super::{bridged_int, capcut_language, object_array, parse, string_array, IdSource, WriteError};
use crate::draft::json::{json_text, JsonEdits, JsonScanner};
use crate::subtitles::CaptionCue;

/// One language of generated subtitles, to be added to a draft as a CapCut caption track.
#[derive(Debug, Clone, PartialEq)]
pub struct SubtitleTrack {
    /// Our language code: `ru`, `en`, `pt-BR`, `zh-Hans`…
    pub language: String,
    pub cues: Vec<CaptionCue>,
    /// The spoken language (CapCut's recognized captions) rather than a translation.
    pub is_original: bool,
    /// Hidden tracks can be shown in CapCut with the track's eye button.
    pub is_visible: bool,
}

/// Name prefix of the tracks we create; a later run replaces tracks with this prefix.
pub const TRACK_NAME_PREFIX: &str = "EnhanceCapCut subtitles";

/// Defaults used when the draft has no captions to copy the style from. Measured on the author's projects: CapCut's
/// caption preset is size 8, white with a thin outline, near the bottom.
pub const DEFAULT_STYLE_SIZE: f64 = 8.0;
pub const DEFAULT_POSITION_Y: f64 = -0.65;

/// Caption size per language, relative to the copied or default style. Thai glyphs come out about half the size of
/// Latin ones in CapCut's caption font, and the author doubled every Thai caption by hand. Not applied when the style
/// is copied from a caption that is already Thai.
pub fn size_factor(language: &str, template: Option<&Map<String, Value>>) -> f64 {
    if !language.starts_with("th") {
        return 1.0;
    }
    let template_is_thai =
        template.and_then(|t| t.get("language")).and_then(Value::as_str).is_some_and(|l| l.starts_with("th"));
    if template_is_thai {
        1.0
    } else {
        2.0
    }
}

/// Returns the draft with `tracks` appended as caption tracks. With `replace_previous`, tracks from an earlier run
/// (named with `TRACK_NAME_PREFIX`) are removed together with their materials. `now_ms` stamps the caption groups.
pub fn apply(
    tracks: &[SubtitleTrack],
    draft: &[u8],
    replace_previous: bool,
    ids: &mut dyn IdSource,
    now_ms: i64,
) -> Result<Vec<u8>, WriteError> {
    let scanner = JsonScanner::new(draft);
    let root = scanner.root()?;
    let tracks_range = scanner.value("tracks", root.clone())?.ok_or(WriteError::NoTracks)?;
    let materials_range = scanner.value("materials", root.clone())?;
    let fps =
        scanner.value("fps", root.clone())?.and_then(|r| scanner.object(r)).and_then(|v| v.as_f64()).unwrap_or(30.0);
    let duration = scanner
        .value("duration", root.clone())?
        .and_then(|r| scanner.object(r))
        .and_then(|v| bridged_int(Some(&v)))
        .unwrap_or(i64::MAX);

    let mut track_elements = scanner.elements(tracks_range.clone())?;
    let texts_range = match &materials_range {
        Some(m) => scanner.value("texts", m.clone())?,
        None => None,
    };
    let animations_range = match &materials_range {
        Some(m) => scanner.value("material_animations", m.clone())?,
        None => None,
    };
    let mut text_elements = match &texts_range {
        Some(r) => scanner.elements(r.clone())?,
        None => Vec::new(),
    };
    let mut animation_elements = match &animations_range {
        Some(r) => scanner.elements(r.clone())?,
        None => Vec::new(),
    };

    let object = |r: &Range<usize>| scanner.object(r.clone()).filter(Value::is_object).unwrap_or_else(|| json!({}));
    let parsed_tracks: Vec<Value> = track_elements.iter().map(object).collect();

    // Drop our tracks from an earlier run, and the materials only they used.
    if replace_previous {
        let mut removed: HashSet<String> = HashSet::new();
        let mut kept = Vec::new();
        for (range, track) in track_elements.iter().zip(&parsed_tracks) {
            if track.get("name").and_then(Value::as_str).is_some_and(|n| n.starts_with(TRACK_NAME_PREFIX)) {
                for segment in object_array(track.get("segments")).into_iter().flatten() {
                    if let Some(id) = segment.get("material_id").and_then(Value::as_str) {
                        removed.insert(id.to_string());
                    }
                    removed.extend(string_array(segment.get("extra_material_refs")).unwrap_or_default());
                }
            } else {
                kept.push(range.clone());
            }
        }
        track_elements = kept;
        let id = |r: &Range<usize>| object(r).get("id").and_then(Value::as_str).unwrap_or("").to_string();
        text_elements.retain(|r| !removed.contains(&id(r)));
        animation_elements.retain(|r| !removed.contains(&id(r)));
    }

    // Style and position: copied from a caption already in the draft, else CapCut's defaults.
    let materials: Vec<Value> =
        text_elements.iter().filter_map(|r| scanner.object(r.clone())).filter(Value::is_object).collect();
    let style_material = materials.iter().find(|m| m.get("type").and_then(Value::as_str) == Some("subtitle"));
    let mut style_segment: Option<&Value> = None;
    if let Some(id) = style_material.and_then(|m| m.get("id")).and_then(Value::as_str) {
        for track in &parsed_tracks {
            let found = object_array(track.get("segments"))
                .and_then(|s| s.iter().find(|s| s.get("material_id").and_then(Value::as_str) == Some(id)));
            if let Some(s) = found {
                style_segment = Some(s);
                break;
            }
        }
    }
    let mut render_index: i64 = 14000;
    for track in &parsed_tracks {
        for segment in object_array(track.get("segments")).into_iter().flatten() {
            if let Some(i) = bridged_int(segment.get("render_index")).filter(|&i| i >= 14000) {
                render_index = render_index.max(i + 1);
            }
        }
    }

    let mut new_tracks: Vec<String> = Vec::new();
    let mut new_texts: Vec<String> = Vec::new();
    let mut new_animations: Vec<String> = Vec::new();
    for (n, track) in tracks.iter().enumerate() {
        let track_index = (track_elements.len() + n) as i64;
        let attribute = if track.is_visible { 0 } else { 2 };
        let language = capcut_language(&track.language);
        let mut segments: Vec<Value> = Vec::new();
        for cue in &track.cues {
            let start = frame_aligned(cue.start, fps);
            let end = frame_aligned(cue.end, fps).min(duration);
            if end <= start {
                continue;
            }
            let material_id = ids.next_id();
            let animation_id = ids.next_id();
            let group = format!("{language}_{}", now_ms + n as i64);
            new_texts.push(json_text(&material(
                track,
                &language,
                cue,
                &material_id,
                end - start,
                &group,
                style_material.and_then(Value::as_object),
            )));
            new_animations.push(json_text(&json!({
                "id": animation_id, "type": "sticker_animation", "animations": [],
                "multi_language_current": "none",
            })));
            segments.push(segment(
                &ids.next_id(),
                &material_id,
                &animation_id,
                start,
                end - start,
                render_index,
                track_index,
                attribute,
                style_segment.and_then(Value::as_object),
            ));
            render_index += 1;
        }
        new_tracks.push(json_text(&json!({
            "id": ids.next_id(), "type": "text", "flag": 1, "attribute": attribute,
            "name": format!("{TRACK_NAME_PREFIX} · {}", track.language), "is_default_name": false,
            "segments": segments,
        })));
    }

    let array = |kept: &[Range<usize>], added: &[String]| -> String {
        let mut items: Vec<String> =
            kept.iter().map(|r| String::from_utf8_lossy(&scanner.bytes[r.clone()]).into_owned()).collect();
        items.extend(added.iter().cloned());
        format!("[{}]", items.join(","))
    };
    let mut edits = JsonEdits::new();
    edits.replace(tracks_range, &array(&track_elements, &new_tracks));
    if let Some(materials_range) = materials_range {
        let mut additions: Vec<String> = Vec::new();
        match texts_range {
            Some(r) => edits.replace(r, &array(&text_elements, &new_texts)),
            None => additions.push(format!("\"texts\":{}", array(&[], &new_texts))),
        }
        match animations_range {
            Some(r) => edits.replace(r, &array(&animation_elements, &new_animations)),
            None => additions.push(format!("\"material_animations\":{}", array(&[], &new_animations))),
        }
        if !additions.is_empty() {
            let close = materials_range.end - 1;
            let empty = scanner.members(materials_range)?.is_empty();
            edits.replace(close..close, &format!("{}{}", if empty { "" } else { "," }, additions.join(",")));
        }
    } else {
        let close = root.end - 1;
        edits.replace(
            close..close,
            &format!(
                ",\"materials\":{{\"texts\":{},\"material_animations\":{}}}",
                array(&[], &new_texts),
                array(&[], &new_animations)
            ),
        );
    }
    Ok(edits.applied(scanner.bytes))
}

/// Microseconds, on a frame boundary of the draft's frame rate.
fn frame_aligned(seconds: f64, fps: f64) -> i64 {
    ((seconds.max(0.0) * fps).round() / fps * 1_000_000.0).round() as i64
}

fn material(
    track: &SubtitleTrack,
    language: &str,
    cue: &CaptionCue,
    id: &str,
    duration: i64,
    group: &str,
    template: Option<&Map<String, Value>>,
) -> Value {
    let mut m = template.cloned().unwrap_or_else(|| default_material().as_object().unwrap().clone());
    let text = &cue.text;
    // The style run of the template's content, stretched over the new text (ranges count code points).
    let template_content = m
        .get("content")
        .and_then(Value::as_str)
        .and_then(|c| serde_json::from_str::<Value>(c).ok())
        .filter(Value::is_object);
    let mut style = template_content
        .as_ref()
        .and_then(|c| object_array(c.get("styles")))
        .and_then(|s| s.first().cloned())
        .unwrap_or_else(|| default_style().clone());
    let style_map = style.as_object_mut().expect("style object");
    style_map.insert("range".into(), json!([0, text.chars().count()]));
    let size = style_map.get("size").and_then(Value::as_f64);
    if let Some(size) = size {
        style_map.insert("size".into(), json!(size * size_factor(&track.language, template)));
    }
    let new_size = style_map.get("size").and_then(Value::as_f64);
    m.insert("content".into(), Value::String(json_text(&json!({"styles": [style], "text": text}))));
    if let Some(size) = new_size {
        m.insert("font_size".into(), json!(size));
    }

    // Word timings in milliseconds from the clip start; CapCut spreads translated words evenly too.
    let words: Vec<&str> = text.split(char::is_whitespace).filter(|w| !w.is_empty()).collect();
    let step = duration as f64 / 1000.0 / words.len().max(1) as f64;
    let starts: Vec<i64> = (0..words.len()).map(|i| (i as f64 * step).round() as i64).collect();
    let ends: Vec<i64> = (0..words.len()).map(|i| ((i + 1) as f64 * step).round() as i64).collect();
    m.insert("words".into(), json!({"start_time": starts, "end_time": ends, "text": words}));
    m.insert("current_words".into(), json!({"start_time": [], "end_time": [], "text": []}));
    m.insert("id".into(), json!(id));
    m.insert("type".into(), json!("subtitle"));
    m.insert("recognize_text".into(), json!(text.replace('\n', " ")));
    m.insert("base_content".into(), json!(""));
    m.insert("language".into(), json!(language));
    m.insert("group_id".into(), json!(group));
    m.insert("recognize_task_id".into(), json!(""));
    m.insert("relevance_segment".into(), json!([]));
    m.insert("translate_original_text".into(), json!(""));
    // As in CapCut's own captions: recognized ones are sub type 0 / add type 1, translations 5 / 0.
    m.insert("sub_type".into(), json!(if track.is_original { 0 } else { 5 }));
    m.insert("add_type".into(), json!(if track.is_original { 1 } else { 0 }));
    Value::Object(m)
}

#[allow(clippy::too_many_arguments)]
fn segment(
    id: &str,
    material_id: &str,
    animation_id: &str,
    start: i64,
    duration: i64,
    render_index: i64,
    track_index: i64,
    attribute: i64,
    template: Option<&Map<String, Value>>,
) -> Value {
    let mut s = default_segment().as_object().unwrap().clone();
    if let Some(template) = template {
        for key in ["clip", "uniform_scale"] {
            if let Some(value) = template.get(key) {
                s.insert(key.into(), value.clone());
            }
        }
    }
    s.insert("id".into(), json!(id));
    s.insert("material_id".into(), json!(material_id));
    s.insert("extra_material_refs".into(), json!([animation_id]));
    s.insert("target_timerange".into(), json!({"start": start, "duration": duration}));
    s.insert("render_index".into(), json!(render_index));
    s.insert("track_render_index".into(), json!(track_index));
    s.insert("track_attribute".into(), json!(attribute));
    s.insert("common_keyframes".into(), json!([]));
    s.insert("keyframe_refs".into(), json!([]));
    s.insert("visible".into(), json!(true));
    Value::Object(s)
}

// MARK: Defaults
//
// Field sets of a caption as CapCut 9.5 for macOS writes it (see docs/FORMAT.md), used when the draft has no caption
// to copy. Floating-point fields are written with a decimal point on purpose.

fn default_style() -> &'static Value {
    static V: OnceLock<Value> = OnceLock::new();
    V.get_or_init(|| {
        parse(
            r##"{"fill":{"content":{"render_type":"solid","solid":{"color":[1.0,1.0,1.0]}}},
            "font":{"id":"","path":"/Applications/CapCut.app/Contents/Resources/Font/SystemFont/en.ttf"},
            "range":[0,0],"size":8.0}"##,
        )
    })
}

fn default_material() -> &'static Value {
    static V: OnceLock<Value> = OnceLock::new();
    V.get_or_init(|| {
        parse(
            r##"{"add_type":0,"alignment":1,"autoAdaptCanvasEnabled":false,"background_alpha":1.0,"background_color":"",
        "background_fill":"","background_height":0.14,"background_horizontal_offset":0.0,"background_round_radius":0.0,
        "background_style":0,"background_vertical_offset":0.0,"background_width":0.14,"base_content":"",
        "bold_width":0.0,"border_alpha":1.0,"border_color":"","border_mode":0,"border_width":0.08,
        "caption_template_info":{"category_id":"","category_name":"","effect_id":"","is_new":false,"path":"",
        "request_id":"","resource_id":"","resource_name":"","source_platform":0,"third_resource_id":""},
        "check_flag":7,"combo_info":{"text_templates":[]},"content":"","current_words":{"end_time":[],"start_time":[],"text":[]},
        "cutoff_postfix":"","enable_path_typesetting":false,"fixed_height":-1.0,"fixed_width":-1.0,
        "font_category_id":"","font_category_name":"","font_id":"",
        "font_name":"","font_path":"/Applications/CapCut.app/Contents/Resources/Font/SystemFont/en.ttf",
        "font_resource_id":"","font_size":8.0,"font_source_platform":0,"font_team_id":"",
        "font_third_resource_id":"","font_title":"none","font_url":"","fonts":[],"force_apply_line_max_width":false,
        "global_alpha":1.0,"group_id":"","has_shadow":false,"id":"","initial_scale":1.0,"inner_padding":-1.0,
        "is_batch_replace":false,"is_lyric_effect":false,"is_rich_text":false,"is_words_linear":false,
        "italic_degree":0,"ktv_color":"","language":"","layer_weight":1,"letter_spacing":0.0,"line_feed":1,
        "line_max_width":0.82,"line_spacing":0.02,"lyric_group_id":"","lyrics_template":{"category_id":"",
        "category_name":"","effect_id":"","panel":"","path":"","request_id":"","resource_id":"","resource_name":""},
        "multi_language_current":"none","name":"","offset_on_path":0.0,"oneline_cutoff":false,"operation_type":0,
        "original_size":[],"preset_category":"","preset_category_id":"","preset_has_set_alignment":false,"preset_id":"",
        "preset_index":0,"preset_name":"","punc_model":"","recognize_model":"","recognize_task_id":"","recognize_text":"",
        "recognize_type":0,"relevance_segment":[],"shadow_alpha":0.9,"shadow_angle":-45.0,"shadow_color":"",
        "shadow_distance":5.0,"shadow_point":{"x":0.6363961030678928,"y":-0.6363961030678927},"shadow_smoothing":0.45,
        "shadow_thickness_projection_angle":0.0,"shadow_thickness_projection_distance":0.0,
        "shadow_thickness_projection_enable":false,"shape_clip_x":false,"shape_clip_y":false,
        "single_char_bg_alpha":1.0,"single_char_bg_color":"","single_char_bg_enable":false,"single_char_bg_height":0.0,
        "single_char_bg_horizontal_offset":0.0,"single_char_bg_round_radius":0.3,"single_char_bg_vertical_offset":0.0,
        "single_char_bg_width":0.0,"source_from":"","ssml_content":"","style_name":"","sub_template_id":-1,"sub_type":0,
        "subtitle_keywords":null,"subtitle_keywords_config":null,"subtitle_template_original_fontsize":0.0,
        "text_alpha":1.0,"text_color":"#FFFFFF","text_curve":null,"text_exceeds_path_process_type":0,
        "text_loop_on_path":false,"text_preset_resource_id":"","text_size":30,"text_to_audio_ids":[],
        "text_typesetting_path_index":0,"text_typesetting_paths":null,"text_typesetting_paths_file":"",
        "translate_original_text":"","tts_auto_update":false,"type":"subtitle","typesetting":0,"underline":false,
        "underline_offset":0.22,"underline_width":0.05,"use_effect_default_color":true,"words":{"end_time":[],
        "start_time":[],"text":[]}}"##,
        )
    })
}

fn default_segment() -> &'static Value {
    static V: OnceLock<Value> = OnceLock::new();
    V.get_or_init(|| {
        parse(
            r##"{"caption_info":null,"cartoon":false,"clip":{"alpha":1.0,"flip":{"horizontal":false,"vertical":false},
        "rotation":0.0,"scale":{"x":1.0,"y":1.0},"transform":{"x":0.0,"y":-0.65}},
        "color_correct_alg_result":"","common_keyframes":[],"desc":"","digital_human_template_group_id":"",
        "enable_adjust":false,"enable_adjust_mask":false,"enable_color_adjust_pro":false,
        "enable_color_correct_adjust":false,"enable_color_curves":true,"enable_color_match_adjust":false,
        "enable_color_wheels":true,"enable_hsl":false,"enable_hsl_curves":true,"enable_lut":false,
        "enable_mask_shadow":false,"enable_mask_stroke":false,"enable_smart_color_adjust":false,
        "enable_video_mask":true,"extra_material_refs":[],"group_id":"","hdr_settings":null,
        "hdr_vivid_settings":null,"id":"","intensifies_audio":false,"is_loop":false,"is_placeholder":false,
        "is_tone_modify":false,"keyframe_refs":[],"last_nonzero_volume":1.0,"lyric_keyframes":null,"material_id":"",
        "raw_segment_id":"","render_index":14000,"render_timerange":{"duration":0,"start":0},
        "responsive_layout":{"enable":false,"horizontal_pos_layout":0,"size_layout":0,"target_follow":"",
        "vertical_pos_layout":0},"reverse":false,"segment_color_tag":"","source":"segmentsourcenormal",
        "source_timerange":null,"speed":1.0,"state":0,"target_timerange":{"duration":0,"start":0},
        "template_id":"","template_scene":"default","track_attribute":0,"track_render_index":0,
        "uniform_scale":{"on":true,"value":1.0},"visible":true,"volume":1.0}"##,
        )
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::writers::CountingIds;

    const DRAFT: &str = r#"{"id":"D","fps":30.0,"duration":10000000,"tracks":[{"id":"V","type":"video","flag":0,"attribute":0,"segments":[]}],"materials":{"videos":[],"texts":[],"material_animations":[]},"canvas_config":{"ratio":"original","width":1080,"height":1920}}"#;

    fn tracks() -> Vec<SubtitleTrack> {
        vec![
            SubtitleTrack {
                language: "ru".into(),
                cues: vec![CaptionCue::new(0.5, 2.0, "Привет, мир"), CaptionCue::new(2.0, 3.01, "Вторая\nстрока")],
                is_original: true,
                is_visible: false,
            },
            SubtitleTrack {
                language: "en".into(),
                cues: vec![CaptionCue::new(0.5, 2.0, "Hello, world")],
                is_original: false,
                is_visible: true,
            },
        ]
    }

    fn run(tracks: &[SubtitleTrack], draft: &[u8], replace: bool) -> Vec<u8> {
        apply(tracks, draft, replace, &mut CountingIds::default(), 1_700_000_000_000).unwrap()
    }

    fn parsed(bytes: &[u8]) -> Value {
        serde_json::from_slice(bytes).unwrap()
    }

    /// Port of `SubtitleWriterTests.testAddsCaptionTracks`.
    #[test]
    fn adds_caption_tracks() {
        let out = run(&tracks(), DRAFT.as_bytes(), true);
        let d = parsed(&out);
        let tracks = d["tracks"].as_array().unwrap();
        assert_eq!(tracks.len(), 3);
        assert_eq!(tracks[1]["attribute"], 2); // hidden
        assert_eq!(tracks[2]["attribute"], 0);
        assert_eq!(tracks[1]["type"], "text");
        let segments = tracks[1]["segments"].as_array().unwrap();
        assert_eq!(segments.len(), 2);
        assert_eq!(segments[1]["target_timerange"]["start"], 2_000_000);
        assert_eq!(segments[1]["target_timerange"]["duration"], 1_000_000); // 3.01 s snaps to the 3 s frame
        assert_eq!(segments[0]["track_render_index"], 1);
        let texts = d["materials"]["texts"].as_array().unwrap();
        assert_eq!(texts.len(), 3);
        assert_eq!(d["materials"]["material_animations"].as_array().unwrap().len(), 3);
        let first = texts.iter().find(|t| t["id"] == segments[0]["material_id"]).unwrap();
        assert_eq!(first["type"], "subtitle");
        assert_eq!(first["language"], "ru-RU");
        let content: Value = serde_json::from_str(first["content"].as_str().unwrap()).unwrap();
        assert_eq!(content["text"], "Привет, мир");
        assert_eq!(content["styles"][0]["range"], json!([0, 11]));
        // Untouched parts stay byte-for-byte, and floats keep their decimal point.
        let text = String::from_utf8(out).unwrap();
        assert!(text.starts_with(
            r#"{"id":"D","fps":30.0,"duration":10000000,"tracks":[{"id":"V","type":"video","flag":0,"attribute":0,"segments":[]},"#
        ));
        assert!(text.ends_with(r#""canvas_config":{"ratio":"original","width":1080,"height":1920}}"#));
        assert!(text.contains(r#""line_max_width":0.82"#));
        assert!(text.contains(r#""font_size":8.0"#));
    }

    /// Port of `SubtitleWriterTests.testReplacesPreviousRun`.
    #[test]
    fn replaces_previous_run() {
        let once = run(&tracks(), DRAFT.as_bytes(), true);
        let twice = run(&tracks(), &once, true);
        let d = parsed(&twice);
        assert_eq!(d["tracks"].as_array().unwrap().len(), 3);
        assert_eq!(d["materials"]["texts"].as_array().unwrap().len(), 3);
        assert_eq!(d["materials"]["material_animations"].as_array().unwrap().len(), 3);
        let kept = run(&tracks(), &once, false);
        assert_eq!(parsed(&kept)["tracks"].as_array().unwrap().len(), 5);
    }

    /// Port of `SubtitleWriterTests.testCopiesStyleOfExistingCaption`.
    #[test]
    fn copies_style_of_existing_caption() {
        let styled = run(&tracks(), DRAFT.as_bytes(), true);
        // Make the generated captions look like the user's own: bigger and higher.
        let text = String::from_utf8(styled)
            .unwrap()
            .replace(r#"\"size\":8.0"#, r#"\"size\":13.0"#)
            .replace(r#""y":-0.65"#, r#""y":-0.4"#)
            .replace(TRACK_NAME_PREFIX, "Mine");
        // Ids from a second counter, so they don't repeat the first run's.
        let out = apply(&tracks()[..1], text.as_bytes(), true, &mut CountingIds(1000), 0).unwrap();
        let d = parsed(&out);
        let track = d["tracks"].as_array().unwrap().last().unwrap().clone();
        let segment = &track["segments"][0];
        assert_eq!(segment["clip"]["transform"]["y"], -0.4);
        let material =
            d["materials"]["texts"].as_array().unwrap().iter().find(|t| t["id"] == segment["material_id"]).unwrap();
        assert_eq!(material["font_size"], 13.0);
    }

    /// Port of `SubtitleWriterTests.testThaiCaptionsAreTwiceAsLarge`.
    #[test]
    fn thai_captions_are_twice_as_large() {
        let mut all = tracks();
        all.push(SubtitleTrack {
            language: "th".into(),
            cues: vec![CaptionCue::new(0.5, 2.0, "สวัสดี")],
            is_original: false,
            is_visible: true,
        });
        let out = parsed(&run(&all, DRAFT.as_bytes(), true));
        let texts = out["materials"]["texts"].as_array().unwrap();
        let size = |language: &str| {
            let m = texts.iter().find(|t| t["language"] == language).unwrap();
            let content: Value = serde_json::from_str(m["content"].as_str().unwrap()).unwrap();
            (m["font_size"].as_f64(), content["styles"][0]["size"].as_f64())
        };
        assert_eq!(size("th-TH"), (Some(16.0), Some(16.0)));
        assert_eq!(size("en-US").0, Some(8.0));
        // Copied from a caption that is already Thai: its size is kept, not doubled again.
        let thai = json!({"language": "th-TH"});
        let russian = json!({"language": "ru-RU"});
        assert_eq!(size_factor("th", thai.as_object()), 1.0);
        assert_eq!(size_factor("th", russian.as_object()), 2.0);
        assert_eq!(size_factor("en", None), 1.0);
    }

    #[test]
    fn missing_lists_are_added() {
        let out = run(&tracks()[1..], br#"{"duration": 1000000, "tracks": []}"#, true);
        let d = parsed(&out);
        assert_eq!(d["materials"]["texts"].as_array().unwrap().len(), 1);
        // A cue past the draft's end is cut there.
        assert_eq!(d["tracks"][0]["segments"][0]["target_timerange"], json!({"start": 500_000, "duration": 500_000}));
        let out = run(&tracks()[1..], br#"{"tracks": [], "materials": {}}"#, true);
        assert!(String::from_utf8(out).unwrap().contains(r#""materials": {"texts":["#));
        assert!(matches!(apply(&tracks(), b"{}", true, &mut CountingIds::default(), 0), Err(WriteError::NoTracks)));
    }

    #[test]
    fn words_are_spread_over_the_clip() {
        let track = SubtitleTrack {
            language: "en".into(),
            cues: vec![CaptionCue::new(0.0, 1.0, "one two\nthree")],
            is_original: true,
            is_visible: true,
        };
        let d = parsed(&run(&[track], DRAFT.as_bytes(), true));
        let m = &d["materials"]["texts"][0];
        assert_eq!(
            m["words"],
            json!({"start_time": [0, 333, 667], "end_time": [333, 667, 1000], "text": ["one", "two", "three"]})
        );
        assert_eq!(m["recognize_text"], "one two three");
        assert_eq!((m["sub_type"].clone(), m["add_type"].clone()), (json!(0), json!(1)));
        assert_eq!(m["group_id"], "en-US_1700000000000");
        assert_eq!(d["tracks"][1]["segments"][0]["render_index"], 14000);
    }
}
