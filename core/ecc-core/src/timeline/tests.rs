//! Ports of the Swift timeline tests (`CoreTests.swift`: TimeMapTests, KeyframeTests, DraftParsingTests' builder
//! cases, LayerTests; `KeyframeEasingTests.swift`; and the feature test files named in each section).

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

use super::*;
use crate::draft::{Draft, KeyframeGroup, Point2};
use crate::env::CoreEnvironment;
use crate::testing::{language, TempDir};

pub(crate) fn approx(a: f64, b: f64, accuracy: f64) {
    assert!((a - b).abs() <= accuracy, "{a} != {b} ± {accuracy}");
}

/// Builds `json` as the draft of a project in `project`, with effect caches under `project/cache`.
pub(crate) fn build_in(json: &str, project: &Path) -> Timeline {
    let draft = Draft::parse(json).unwrap_or_else(|e| panic!("{e}\n{json}"));
    let env = CoreEnvironment {
        projects_root: project.to_path_buf(),
        capcut_cache_roots: vec![project.join("cache")],
        backup_root: project.join("backups"),
    };
    TimelineBuilder::build(&draft, project, &env)
}

pub(crate) fn build(json: &str) -> Timeline {
    build_in(json, Path::new("/tmp"))
}

/// "Category: name" of every report entry.
pub(crate) fn report_set(t: &Timeline) -> BTreeSet<String> {
    t.report.unsupported.iter().map(|i| format!("{}: {}", i.category, i.name)).collect()
}

pub(crate) fn set(items: &[&str]) -> BTreeSet<String> {
    items.iter().map(|s| s.to_string()).collect()
}

pub(crate) fn layer<'a>(t: &'a Timeline, id: &str) -> &'a VisualLayer {
    t.layers.iter().find(|l| l.id == id).unwrap_or_else(|| panic!("no layer {id}"))
}

/// A one-byte file at `dir/name` (media only has to exist).
pub(crate) fn touch(dir: &Path, name: &str) -> PathBuf {
    let path = dir.join(name);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, [0u8]).unwrap();
    path
}

// MARK: - TimeMapTests

const FLASH_IN: [Point2; 5] = [
    Point2::new(0.0, 1.0),
    Point2::new(0.19, 5.2),
    Point2::new(0.78, 5.2),
    Point2::new(0.94, 1.0),
    Point2::new(1.0, 1.0),
];

#[test]
fn constant_speed() {
    let map = TimeMap::constant(1_000_000, 4_000_000, 2_000_000);
    assert!(map.is_constant());
    approx(map.average_speed(), 2.0, 1e-9);
    assert_eq!(map.source_time(0), 1_000_000);
    assert_eq!(map.source_time(1_000_000), 3_000_000);
    assert_eq!(map.source_time(2_000_000), 5_000_000);
}

#[test]
fn curve_covers_whole_source_and_is_monotonic() {
    let map = TimeMap::new(0, 10_000_000, 2_000_000, &FLASH_IN);
    assert!(!map.is_constant());
    assert_eq!(map.source_time(0), 0);
    assert_eq!(map.source_time(2_000_000), 10_000_000);
    let mut last = -1;
    for o in (0..=2_000_000).step_by(10_000) {
        let s = map.source_time(o);
        assert!(s >= last);
        last = s;
    }
    // Fast in the middle, slow at the ends.
    assert!(map.speed(0.5) > map.speed(0.02) * 3.0);
}

#[test]
fn offset_is_inverse_of_source_time() {
    let map = TimeMap::new(500_000, 10_000_000, 2_000_000, &FLASH_IN);
    for o in (0..=2_000_000).step_by(100_000) {
        approx(map.offset_at_source_time(map.source_time(o)) as f64, o as f64, 2_000.0);
    }
    let constant = TimeMap::constant(0, 4_000_000, 1_000_000);
    assert_eq!(constant.offset_at_source_time(2_000_000), 500_000);
    assert_eq!(constant.offset_at_source_time(9_000_000), 1_000_000);
}

#[test]
fn slice_follows_the_curve() {
    let map = TimeMap::new(0, 10_000_000, 2_000_000, &FLASH_IN);
    let slice = map.slice(400_000, 1_400_000);
    assert_eq!(slice.target_duration, 1_000_000);
    for o in (0..=1_000_000).step_by(50_000) {
        approx(slice.source_time(o) as f64, map.source_time(400_000 + o) as f64, 1_000.0);
    }
    // A slice of the constant-speed middle collapses to a constant map.
    assert!(map.slice(800_000, 1_200_000).is_constant());
    assert!(TimeMap::constant(0, 3, 1).slice(0, 1).is_constant());
}

#[test]
fn sampled_composition_of_two_speeds() {
    // 2x over 3x = 6x.
    let outer = TimeMap::constant(1_000_000, 2_000_000, 1_000_000);
    let inner = TimeMap::constant(0, 9_000_000, 3_000_000);
    let composed =
        TimeMap::sampled(1_000_000, |u| inner.precise_source_time(outer.precise_source_time(u * 1_000_000.0)));
    assert!(composed.is_constant());
    assert_eq!(composed.source_start, 3_000_000);
    approx(composed.average_speed(), 6.0, 1e-6);
}

#[test]
fn vectorized_sampling_equals_single_calls() {
    let map = TimeMap::new(500_000, 10_000_000, 2_000_000, &FLASH_IN);
    let offsets: Vec<i64> = (-100_000..=2_100_000).step_by(50_000).collect();
    let single: Vec<i64> = offsets.iter().map(|&o| map.source_time(o)).collect();
    assert_eq!(map.source_times(&offsets), single);
    let track = KeyframeTrack::new(vec![0, 1_000, 3_000], vec![0.0, 1.0, -1.0]);
    assert_eq!(track.values_at(&[-5, 500, 2_000, 9_000]), [0.0, 0.5, 0.0, -1.0]);
}

// MARK: - KeyframeTests

#[test]
fn linear_interpolation_and_clamping() {
    let track = KeyframeTrack::new(vec![0, 1_000, 3_000], vec![0.0, 1.0, -1.0]);
    assert_eq!(track.value(-5), 0.0);
    approx(track.value(500), 0.5, 1e-9);
    approx(track.value(2_000), 0.0, 1e-9);
    assert_eq!(track.value(9_000), -1.0);
}

// MARK: - KeyframeEasingTests

/// Two keyframes 0 → 100 over 1 s; `curve` and the handles go on both.
fn easing_group(curve: &str, out: f64, incoming: f64) -> KeyframeGroup {
    let json = format!(
        r#"{{"property_type": "KFTypePositionX", "keyframe_list": [
          {{"time_offset": 0, "values": [0], "curveType": "{curve}",
           "left_control": {{"x": 0, "y": 0}}, "right_control": {{"x": {out}, "y": 0}}}},
          {{"time_offset": 1000000, "values": [100], "curveType": "{curve}",
           "left_control": {{"x": {incoming}, "y": 0}}, "right_control": null}}]}}"#
    );
    KeyframeGroup::from_value(&serde_json::from_str(&json).unwrap()).unwrap()
}

#[test]
fn line_keyframes_stay_linear_whatever_the_handles() {
    let track = KeyframeTrack::from_group(&easing_group("Line", 400_000.0, -400_000.0)).unwrap();
    assert!(track.handles.is_none());
    approx(track.value(250_000), 25.0, 1e-9);
    assert_eq!(track.breaks(50_000), [0, 1_000_000]);
}

#[test]
fn curved_keyframes_follow_the_cubic_bezier() {
    let track = KeyframeTrack::from_group(&easing_group("Cubic", 400_000.0, -400_000.0)).unwrap();
    // Ease in-out: slow at both ends, symmetric, exact at the middle and the keyframes.
    assert_eq!(track.value(0), 0.0);
    approx(track.value(250_000), 13.5307, 1e-3);
    approx(track.value(500_000), 50.0, 1e-6);
    approx(track.value(250_000) + track.value(750_000), 100.0, 1e-6);
    assert_eq!(track.value(1_000_000), 100.0);
    assert_eq!(track.breaks(250_000), [0, 250_000, 500_000, 750_000, 1_000_000]);
    // Scaling (volume envelopes) scales the handles too.
    approx(track.scaled(0.5).value(250_000), track.value(250_000) / 2.0, 1e-9);
}

#[test]
fn zero_handles_and_handles_past_the_span_stay_monotonic() {
    let straight = KeyframeTrack::from_group(&easing_group("Cubic", 0.0, 0.0)).unwrap();
    approx(straight.value(300_000), 30.0, 1e-3);
    let wild = KeyframeTrack::from_group(&easing_group("Cubic", 5_000_000.0, -5_000_000.0)).unwrap();
    let mut last = -1.0;
    for t in (0..=1_000_000).step_by(10_000) {
        let v = wild.value(t);
        assert!(v >= last - 1e-6);
        last = v;
    }
}

/// Clip keyframes are on the source time axis: a clip trimmed to start 5 s into its media has keyframes from
/// 5 000 000 (Durian1, checked against CapCut's export).
#[test]
fn clip_keyframes_are_on_the_source_time_axis() {
    let mut layer = VisualLayer::new(
        "L",
        VisualContent::Photo("/tmp/x.jpg".into()),
        TimeRange::new(10_000_000, 2_000_000),
        TimeMap::constant(5_000_000, 2_000_000, 2_000_000),
    );
    layer.keyframes.insert(LayerProperty::PositionX, KeyframeTrack::new(vec![5_000_000, 6_000_000], vec![0.0, 1.0]));
    approx(layer.transform(10_000_000).position_x, 0.0, 1e-9);
    approx(layer.transform(10_500_000).position_x, 0.5, 1e-9);
    approx(layer.transform(11_500_000).position_x, 1.0, 1e-9);
}

// MARK: - LayerTests

#[test]
fn keyframes_override_clip_and_uniform_scale() {
    let mut layer = VisualLayer::new(
        "L",
        VisualContent::Photo("/tmp/x.jpg".into()),
        TimeRange::new(1_000, 2_000),
        TimeMap::constant(0, 2_000, 2_000),
    );
    layer.clip.scale = Point2::new(2.0, 2.0);
    layer.keyframes.insert(LayerProperty::ScaleX, KeyframeTrack::new(vec![0, 2_000], vec![1.0, 3.0]));
    let t = layer.transform(2_000);
    approx(t.scale_x, 2.0, 1e-9);
    approx(t.scale_y, 2.0, 1e-9);
    assert!(layer.is_active(1_000));
    assert!(!layer.is_active(3_000));
}

// MARK: - DraftParsingTests (builder)

#[test]
fn timeline_reports_missing_media_and_unsupported() {
    let _english = language("en");
    let timeline = build(crate::draft::model::tests::ODD_DRAFT);
    assert_eq!(timeline.report.missing_files, ["/nonexistent/clip.mov"]);
    assert!(report_set(&timeline).contains("Transition: Sand Spread (shown as a cross-fade)"));
    // The missing video produces no layer; the subtitle does.
    assert_eq!(timeline.layers.len(), 1);
    assert!(timeline.has_subtitles());
    assert_eq!(timeline.without_subtitles().layers.len(), 0);
}

#[test]
fn reports_unsupported_clip_features() {
    let _english = language("en");
    let dir = TempDir::new();
    let photo = touch(dir.path(), "ecc-report-test.png");
    let json = format!(
        r#"{{
      "tracks": [
        {{"type": "video", "segments": [
          {{"id": "S1", "material_id": "V1", "target_timerange": {{"start": 0, "duration": 1000000}}, "is_loop": true,
           "common_keyframes": [
             {{"property_type": "KFTypePositionX", "keyframe_list": [{{"time_offset": 0, "values": [0]}}]}},
             {{"property_type": "KFTypeCommonMaskFeather", "keyframe_list": [{{"time_offset": 0, "values": [0]}}]}},
             {{"property_type": "KFTypeCommonMaskTwist", "keyframe_list": [{{"time_offset": 0, "values": [0]}}]}},
             {{"property_type": "KFTypeSaturation", "keyframe_list": [{{"time_offset": 0, "values": [0]}}]}}]}}
        ]}},
        {{"type": "audio", "segments": [
          {{"id": "S2", "material_id": "A1", "target_timerange": {{"start": 0, "duration": 1000000}},
           "common_keyframes": [{{"property_type": "KFTypeVolume", "keyframe_list": [{{"time_offset": 0, "values": [1]}}]}}]}}
        ]}},
        {{"type": "text", "segments": [
          {{"id": "S3", "material_id": "X1", "target_timerange": {{"start": 0, "duration": 1000000}}}}
        ]}}
      ],
      "materials": {{
        "videos": [{{"id": "V1", "type": "photo", "path": "{}",
                    "stable": {{"stable_level": 3}}, "matting": {{"flag": 1}}, "is_set_beauty_mode": true,
                    "video_algorithm": {{"algorithms": [{{"type": "QualityEnhance"}}, {{"type": "AigcTypeGenImage"}}],
                                        "super_resolution": {{"level": 1}}, "deflicker": null}}}}],
        "audios": [{{"id": "A1", "path": "/nonexistent/a.m4a"}}],
        "texts": [{{"id": "X1", "text_curve": {{"enable": true, "angle": 72}},
                   "content": "{{\"text\":\"Hi\",\"styles\":[{{\"range\":[0,2],\"strokes\":[{{}},{{}}]}}]}}"}}]
      }}
    }}"#,
        photo.display()
    );
    let timeline = build(&json);
    assert_eq!(
        report_set(&timeline),
        set(&[
            "Keyframes: mask animation",
            "Keyframes: Saturation",
            "Playback: loop",
            "Stabilization: level 3",
            "Remove background: result not saved in the project",
            "Beauty / makeup: retouch",
            "AI processing: super_resolution",
            "Text: curved text (rendered straight)",
            "Text: multiple outlines (first one rendered)",
        ])
    );
    assert_eq!(timeline.layers.len(), 2, "unsupported features don't drop the clips");
}

#[test]
fn transitions_join_adjacent_clips() {
    let _english = language("en");
    let dir = TempDir::new();
    let photo = touch(dir.path(), "ecc-transition-test.png");
    let segment = |id: &str, start: i64, duration: i64, refs: &[&str]| {
        let refs: Vec<String> = refs.iter().map(|r| format!("\"{r}\"")).collect();
        format!(
            r#"{{"id": "{id}", "material_id": "V1", "target_timerange": {{"start": {start}, "duration": {duration}}},
             "extra_material_refs": [{}]}}"#,
            refs.join(",")
        )
    };
    let json = format!(
        r#"{{
      "tracks": [{{"type": "video", "segments": [{}, {}, {}, {}]}}],
      "materials": {{
        "videos": [{{"id": "V1", "type": "photo", "path": "{}"}}],
        "transitions": [
          {{"id": "F", "type": "transition", "name": "Flashback", "duration": 200000, "is_overlap": true}},
          {{"id": "X", "type": "transition", "name": "Spin Shift", "duration": 1000000, "is_overlap": true}}]
      }}
    }}"#,
        segment("A", 0, 2_000_000, &["F"]),
        segment("B", 2_000_000, 300_000, &["X"]),
        segment("C", 2_300_000, 1_000_000, &["F"]),
        segment("D", 4_000_000, 1_000_000, &[]),
        photo.display()
    );
    let timeline = build(&json);
    // Centred on the cut.
    let flashback = layer(&timeline, "A").transition_out.clone().unwrap();
    assert_eq!(flashback.kind, TransitionKind::Flashback);
    assert_eq!(timeline.layers[flashback.next].id, "B");
    assert_eq!(flashback.range, TimeRange::new(1_900_000, 200_000));
    assert_eq!(flashback.progress(2_000_000), 0.5);
    // Kept within half of the shorter clip.
    let other = layer(&timeline, "B").transition_out.clone().unwrap();
    assert_eq!(other.kind, TransitionKind::CrossFade);
    assert_eq!(other.range, TimeRange::new(2_150_000, 300_000));
    // C and D don't touch.
    assert!(layer(&timeline, "C").transition_out.is_none());
    assert!(layer(&timeline, "D").transition_out.is_none());
    let report: Vec<String> =
        timeline.report.unsupported.iter().map(|i| format!("{}: {}", i.category, i.name)).collect();
    assert_eq!(report, ["Transition: Spin Shift (shown as a cross-fade)"]);

    approx(FlashbackLook::strength(0.0), 0.0, 1e-9);
    approx(FlashbackLook::strength(0.5), 1.0, 1e-9);
    assert_eq!(FlashbackLook::mix(0.2), 0.0);
    approx(FlashbackLook::mix(0.5), 0.5, 1e-9);
    assert_eq!(FlashbackLook::mix(0.8), 1.0);
    approx(FlashbackLook::copy(0, 1.0).0, 1.0, 1e-9);
}

#[test]
fn cutout_uses_capcuts_mask_frames() {
    let _english = language("en");
    let dir = TempDir::new();
    let d = dir.path();
    for path in ["matting/auto/2/mask", "matting/custom/2/mask", "matting/custom/C1/5/mask", "matting/unpicked/2/mask"]
    {
        touch(&d.join(path), "0");
    }
    let photo = touch(d, "p.png");
    let root = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##/matting/";
    // Auto cutout; custom cutout with its result; custom cutout with none (whole clip); auto cutout with feather;
    // auto cutout whose result is gone.
    let mattings = [
        format!(r#"{{"flag": 3, "path": "{root}auto", "custom_matting_id": "X", "reverse": true}}"#),
        format!(r#"{{"flag": 1, "path": "{root}custom", "custom_matting_id": "C1"}}"#),
        format!(r#"{{"flag": 1, "path": "{root}unpicked", "custom_matting_id": "C2"}}"#),
        format!(r#"{{"flag": 3, "path": "{root}auto", "feather": 20, "enable_matting_stroke": true}}"#),
        format!(r#"{{"flag": 3, "path": "{root}gone"}}"#),
    ];
    let videos: Vec<String> = mattings
        .iter()
        .enumerate()
        .map(|(i, m)| format!(r#"{{"id": "V{i}", "type": "photo", "path": "{}", "matting": {m}}}"#, photo.display()))
        .collect();
    let segments: Vec<String> = (0..mattings.len())
        .map(|i| {
            format!(
                r#"{{"id": "S{i}", "material_id": "V{i}", "target_timerange": {{"start": 0, "duration": 1000000}}}}"#
            )
        })
        .collect();
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [{}]}}], "materials": {{"videos": [{}]}}}}"#,
        segments.join(","),
        videos.join(",")
    );
    let timeline = build_in(&json, d);
    let path = |id: &str| {
        layer(&timeline, id)
            .cutout
            .as_ref()
            .map(|c| c.directory.strip_prefix(d).unwrap().to_string_lossy().replace('\\', "/"))
    };
    assert_eq!(path("S0").as_deref(), Some("matting/auto/2/mask"));
    assert!(layer(&timeline, "S0").cutout.as_ref().unwrap().inverted);
    assert_eq!(path("S1").as_deref(), Some("matting/custom/C1/5/mask"));
    assert!(path("S2").is_none(), "a custom cutout without a picked subject shows the whole clip");
    assert!(path("S3").is_some());
    assert!(path("S4").is_none());
    assert_eq!(
        report_set(&timeline),
        set(&[
            "Remove background: edge expansion or feather",
            "Remove background: stroke",
            "Remove background: result not saved in the project",
        ])
    );
}

#[test]
fn text_effects_apply_only_with_effect_style() {
    let _english = language("en");
    let text = |id: &str, effect: bool| {
        let style = if effect { r#", \"effectStyle\": {\"path\": \"/cache/e\", \"id\": \"1\"}"# } else { "" };
        format!(
            r#"{{"id": "{id}", "content": "{{\"text\": \"hi\", \"styles\": [{{\"range\": [0, 2], \"fill\": {{\"content\": {{\"solid\": {{\"color\": [0.2, 0.4, 0.6]}}}}}}{style}}}]}}"}}"#
        )
    };
    // Stale effect (style reset): plain text. Glow effects: approximated. Other effects and word art without its
    // material: reported.
    let cases: [(&str, bool, Option<&str>); 5] = [
        ("ART EN 3D WHITE AND BLUE ", false, Some("E0")),
        ("ART Artistic EN Yellow Glow", true, Some("E1")),
        ("Soft glow", true, Some("E2")),
        ("融化的爱心", true, Some("E3")),
        ("", true, None),
    ];
    let effects: Vec<String> = cases
        .iter()
        .enumerate()
        .map(|(i, c)| format!(r#"{{"id": "E{i}", "type": "text_effect", "name": "{}"}}"#, c.0))
        .collect();
    let texts: Vec<String> = cases.iter().enumerate().map(|(i, c)| text(&format!("T{i}"), c.1)).collect();
    let segments: Vec<String> = cases
        .iter()
        .enumerate()
        .map(|(i, c)| {
            let refs = c.2.map_or("[]".to_string(), |r| format!(r#"["{r}", "{r}"]"#));
            format!(
                r#"{{"id": "S{i}", "material_id": "T{i}", "extra_material_refs": {refs}, "target_timerange": {{"start": 0, "duration": 1000000}}}}"#
            )
        })
        .collect();
    let json = format!(
        r#"{{"tracks": [{{"type": "text", "segments": [{}]}}], "materials": {{"texts": [{}], "effects": [{}]}}}}"#,
        segments.join(","),
        texts.join(","),
        effects.join(",")
    );
    let timeline = build(&json);
    let glows: Vec<(String, TextGlow)> = timeline
        .layers
        .iter()
        .filter_map(|l| match &l.content {
            VisualContent::Text(spec) => spec.glow.clone().map(|g| (l.id.clone(), g)),
            _ => None,
        })
        .collect();
    assert_eq!(
        glows,
        [
            ("S1".to_string(), TextGlow { color: vec![1.0, 0.86, 0.1], radius: TextEffectLook::GLOW_RADIUS }),
            ("S2".to_string(), TextGlow { color: vec![0.2, 0.4, 0.6], radius: TextEffectLook::GLOW_RADIUS }),
        ]
    );
    let found: BTreeSet<String> =
        timeline.report.unsupported.iter().map(|i| format!("{}: {} ×{}", i.category, i.name, i.count)).collect();
    assert_eq!(found, set(&["Text effect: 融化的爱心 ×1", "Text effect: word art ×1"]));
}

#[test]
fn compound_with_detached_sound_plays_only_the_audio_track() {
    let dir = TempDir::new();
    let media = touch(dir.path(), "a.caf");
    let nested = format!(
        r#"{{"duration": 1000000, "materials": {{"audios": [{{"id": "A", "path": "{}", "type": "record"}}]}},
         "tracks": [{{"type": "audio", "segments": [{{"material_id": "A", "target_timerange": {{"start": 0, "duration": 1000000}}}}]}}]}}"#,
        media.display()
    );
    // "Extract audio" on a compound clip: the video keeps the compound and gets has_sound_separated, an audio track
    // gets a second copy of the compound with the sound.
    let json = format!(
        r#"{{
      "duration": 1000000,
      "materials": {{
        "videos": [{{"id": "V", "type": "video", "has_audio": true, "has_sound_separated": true}}],
        "audios": [{{"id": "VA", "type": "video_original_sound"}}],
        "drafts": [{{"id": "CV", "draft": {nested}}}, {{"id": "CA", "draft": {nested}}}]
      }},
      "tracks": [
        {{"type": "video", "segments": [{{"material_id": "V", "extra_material_refs": ["CV"],
                                        "target_timerange": {{"start": 0, "duration": 1000000}}}}]}},
        {{"type": "audio", "segments": [{{"material_id": "VA", "extra_material_refs": ["CA"],
                                        "target_timerange": {{"start": 0, "duration": 1000000}}}}]}}
      ]
    }}"#
    );
    let timeline = build_in(&json, dir.path());
    assert_eq!(timeline.audio_clips.len(), 1);
}

#[test]
fn capitalized_like_foundation() {
    use super::builder::capitalized;
    for (input, expected) in [
        ("effect", "Effect"),
        ("video_effect", "Video_Effect"),
        ("smart-crop", "Smart-Crop"),
        ("ABC dEF", "Abc Def"),
        ("3d effect", "3D Effect"),
        ("", ""),
        ("über", "Über"),
    ] {
        assert_eq!(capitalized(input), expected);
    }
}

// MARK: - Report categories (task 3.4): one entry per category, and rendered features stay out

#[test]
fn every_report_category() {
    let _english = language("en");
    let dir = TempDir::new();
    let d = dir.path();
    let video = touch(d, "v.mov");
    let photo = touch(d, "p.png");
    let json = format!(
        r#"{{
      "tracks": [
        {{"type": "video", "segments": [
          {{"id": "V1", "material_id": "MV", "reverse": true, "target_timerange": {{"start": 0, "duration": 1000000}},
           "extra_material_refs": ["CANVAS", "VS", "FX-UNKNOWN", "COLOR", "SMART", "TEXTSHAPE", "EFFECT", "OTHER",
                                   "LOOK"]}},
          {{"id": "V2", "material_id": "MO", "target_timerange": {{"start": 1000000, "duration": 1000000}}}}]}},
        {{"type": "adjust", "segments": [{{"id": "J", "material_id": "NOTHING", "target_timerange": {{"start": 0, "duration": 1}}}}]}},
        {{"type": "effect", "segments": [{{"id": "E", "material_id": "VFX", "target_timerange": {{"start": 0, "duration": 1}}}}]}}
      ],
      "materials": {{
        "videos": [{{"id": "MV", "type": "video", "path": "{v}", "duration": 5000000}},
                   {{"id": "MO", "type": "gif", "path": "{p}"}}],
        "canvases": [{{"id": "CANVAS", "type": "canvas_gradient"}}],
        "vocal_separations": [{{"id": "VS", "choice": 9}}],
        "video_effects": [{{"id": "VFX", "name": "Shake", "effect_id": "1", "apply_target_type": 2}}],
        "effects": [
          {{"id": "FX-UNKNOWN", "type": "mix_mode", "name": "Vivid"}},
          {{"id": "COLOR", "type": "color_correct"}},
          {{"id": "SMART", "type": "smart_color_adjust"}},
          {{"id": "TEXTSHAPE", "type": "text_shape", "name": "Bubble"}},
          {{"id": "EFFECT", "type": "something_new", "name": "Sparkle"}},
          {{"id": "LOOK", "type": "face_makeup", "name": "Doll"}}],
        "beats": [{{"id": "BEAT"}}],
        "placeholder_things": [{{"id": "OTHER", "type": "thing"}}]
      }}
    }}"#,
        v = video.display(),
        p = photo.display()
    );
    let timeline = build_in(&json, d);
    assert_eq!(
        report_set(&timeline),
        set(&[
            "Adjust: segment",
            "Audio: vocal separation mode 9",
            "Beauty / makeup: Doll",
            "Blend mode: Vivid",
            "Canvas: canvas_gradient",
            "Color adjustment: color correct",
            "Color adjustment: smart color adjust",
            "Effect: Sparkle",
            "Media: gif clip",
            "Other: placeholder_things (thing)",
            "Reverse playback: reversed clip without CapCut's reversed copy (rendered forward)",
            "Text shape: Bubble",
            "Video effect: Shake",
        ])
    );
    // The reversed clip without CapCut's copy plays the mirrored range forward.
    assert_eq!(layer(&timeline, "V1").time_map.source_start, 4_000_000);
    assert!(timeline.report.missing_files.is_empty());
}

#[test]
fn rendered_features_are_not_reported() {
    let dir = TempDir::new();
    let d = dir.path();
    let photo = touch(d, "p.png");
    let reversed = touch(d, "reversed.mov");
    let video = touch(d, "v.mov");
    let json = format!(
        r##"{{
      "tracks": [
        {{"type": "video", "segments": [
          {{"id": "A", "material_id": "P", "target_timerange": {{"start": 0, "duration": 1000000}},
           "extra_material_refs": ["FLASH", "BLEND", "RETOUCH", "ROOT", "PUDDING", "SPEED", "CANVAS", "CHROMA", "HSL0",
                                   "ANIM", "MASK"],
           "common_keyframes": [{{"property_type": "KFTypeAlpha", "keyframe_list": [{{"time_offset": 0, "values": [1]}}]}},
                                {{"property_type": "KFTypeCommonMaskPositionX", "keyframe_list": [{{"time_offset": 0, "values": [0]}}]}}]}},
          {{"id": "B", "material_id": "R", "reverse": true, "target_timerange": {{"start": 1000000, "duration": 1000000}}}}]}},
        {{"type": "sticker", "segments": [{{"id": "C", "material_id": "CLOCK", "target_timerange": {{"start": 0, "duration": 1}}}}]}}
      ],
      "materials": {{
        "videos": [{{"id": "P", "type": "photo", "path": "{p}", "video_algorithm": {{"algorithms": [{{"type": "QualityEnhance"}}]}}}},
                   {{"id": "R", "type": "video", "path": "{v}", "reverse_path": "{r}"}}],
        "transitions": [{{"id": "FLASH", "name": "Flashback", "duration": 100000}}],
        "effects": [{{"id": "BLEND", "type": "mix_mode", "name": "Screen"}},
                    {{"id": "RETOUCH", "type": "figure", "sub_type": "auto_beauty", "name": "Smooth"}},
                    {{"id": "ROOT", "type": "makeup_root"}}],
        "video_effects": [{{"id": "PUDDING", "name": "Pudding Bounce", "effect_id": "7534579654615649553"}}],
        "speeds": [{{"id": "SPEED", "speed": 1}}],
        "canvases": [{{"id": "CANVAS", "type": "canvas_blur"}}],
        "chromas": [{{"id": "CHROMA", "color": "#00FF00", "intensity_value": 0.5}}],
        "hsl": [{{"id": "HSL0", "type": "hsl"}}],
        "material_animations": [{{"id": "ANIM", "animations": [{{"type": "in", "name": "Fade In", "duration": 100000}}]}}],
        "common_mask": [{{"id": "MASK", "resource_type": "circle", "name": "Circle"}}],
        "stickers": [{{"id": "CLOCK", "name": "Clock", "resource_id": "7528282080199904565"}}]
      }}
    }}"##,
        p = photo.display(),
        v = video.display(),
        r = reversed.display()
    );
    let timeline = build_in(&json, d);
    assert!(timeline.report.is_empty(), "{:?}", timeline.report);
    assert_eq!(timeline.layers.len(), 3);
    assert_eq!(layer(&timeline, "B").content, VisualContent::Video(reversed));
}

// MARK: - Compound clips' audio (`nest_audio`), vertical text, captions

#[test]
fn compound_audio_follows_the_parent_speed() {
    let dir = TempDir::new();
    let media = touch(dir.path(), "a.caf");
    let nested = format!(
        r#"{{"duration": 4000000, "materials": {{
             "audios": [{{"id": "A", "path": "{}"}}], "audio_fades": [{{"id": "F", "fade_in_duration": 400000}}]}},
         "tracks": [{{"type": "audio", "segments": [{{"material_id": "A", "extra_material_refs": ["F"],
           "target_timerange": {{"start": 1000000, "duration": 2000000}}, "volume": 0.5}}]}}]}}"#,
        media.display()
    );
    // The compound plays its 0–4 s at 2x over 2 s starting at 10 s, at half volume.
    let json = format!(
        r#"{{"materials": {{"videos": [{{"id": "V", "type": "video", "has_audio": true}}],
         "drafts": [{{"id": "C", "draft": {nested}}}]}},
         "tracks": [{{"type": "video", "segments": [{{"material_id": "V", "extra_material_refs": ["C"], "volume": 0.5,
           "source_timerange": {{"start": 0, "duration": 4000000}}, "target_timerange": {{"start": 10000000, "duration": 2000000}}}}]}}]}}"#
    );
    let timeline = build_in(&json, dir.path());
    assert_eq!(timeline.audio_clips.len(), 1);
    let clip = &timeline.audio_clips[0];
    assert_eq!(clip.target, TimeRange::new(10_500_000, 1_000_000));
    assert_eq!((clip.time_map.source_start, clip.time_map.source_duration), (0, 2_000_000));
    approx(clip.volume, 0.25, 1e-12);
    assert_eq!(clip.fade_in, 200_000, "the fade follows the speed too");
    assert_eq!(clip.fade_out, 0);
    let VisualContent::Compound(inner) = &timeline.layers[0].content else { panic!("compound") };
    assert!(inner.layers.is_empty());
}

#[test]
fn vertical_typesetting_is_passed_through() {
    let json = r#"{"tracks": [{"type": "text", "segments": [
        {"id": "S", "material_id": "X", "target_timerange": {"start": 0, "duration": 1000000}}]}],
      "materials": {"texts": [{"id": "X", "typesetting": 1, "content": "{\"text\":\"縦書き\"}"}]}}"#;
    let timeline = build(json);
    let VisualContent::Text(spec) = &timeline.layers[0].content else { panic!("text") };
    assert_eq!(spec.material.typesetting, 1);
    assert!(timeline.report.is_empty(), "vertical text is rendered, not reported");
}

#[test]
fn without_subtitles_keeps_transitions_pointing_at_their_clips() {
    let dir = TempDir::new();
    let photo = touch(dir.path(), "p.png");
    let json = format!(
        r#"{{"tracks": [
        {{"type": "text", "segments": [{{"id": "CAP", "material_id": "X", "render_index": 0,
          "target_timerange": {{"start": 0, "duration": 1000000}}}}]}},
        {{"type": "video", "segments": [
          {{"id": "A", "material_id": "P", "render_index": 1, "extra_material_refs": ["T"], "target_timerange": {{"start": 0, "duration": 1000000}}}},
          {{"id": "B", "material_id": "P", "render_index": 1, "target_timerange": {{"start": 1000000, "duration": 1000000}}}}]}}],
      "materials": {{"videos": [{{"id": "P", "type": "photo", "path": "{}"}}],
        "texts": [{{"id": "X", "type": "subtitle", "content": "{{\"text\":\"Hi\"}}"}}],
        "transitions": [{{"id": "T", "name": "Flashback", "duration": 200000}}]}}}}"#,
        photo.display()
    );
    let timeline = build_in(&json, dir.path());
    assert_eq!(timeline.layers.iter().map(|l| l.id.as_str()).collect::<Vec<_>>(), ["CAP", "A", "B"]);
    assert_eq!(timeline.layers[layer_index(&timeline, "A")].transition_out.as_ref().unwrap().next, 2);
    let plain = timeline.without_subtitles();
    assert_eq!(plain.layers.iter().map(|l| l.id.as_str()).collect::<Vec<_>>(), ["A", "B"]);
    assert_eq!(plain.layers[0].transition_out.as_ref().unwrap().next, 1);
    assert!(!plain.has_subtitles());
}

fn layer_index(t: &Timeline, id: &str) -> usize {
    t.layers.iter().position(|l| l.id == id).unwrap()
}

// MARK: - frame(t) (task 3.5)

/// A timeline with keyframes (clip and mask), a text animation, a clock sticker, video effects, a transition and a
/// filter-track filter.
fn rich_timeline(dir: &Path) -> Timeline {
    let photo = touch(dir, "p.png");
    write_lut(&dir.join("cache/111/x/filter.png"));
    let json = format!(
        r#"{{"tracks": [
        {{"type": "video", "segments": [
          {{"id": "A", "material_id": "P", "extra_material_refs": ["T", "PUDDING", "MASK", "SPEED"],
           "target_timerange": {{"start": 0, "duration": 2000000}},
           "common_keyframes": [
             {{"property_type": "KFTypePositionX", "keyframe_list": [{{"time_offset": 0, "values": [0]}},
               {{"time_offset": 3000000, "values": [1], "curveType": "Cubic", "left_control": {{"x": -500000, "y": 0}}}}]}},
             {{"property_type": "KFTypeCommonMaskPositionX", "keyframe_list": [{{"time_offset": 0, "values": [-0.5]}},
               {{"time_offset": 4000000, "values": [0.5]}}]}}]}},
          {{"id": "B", "material_id": "P", "target_timerange": {{"start": 2000000, "duration": 2000000}}}}]}},
        {{"type": "text", "segments": [{{"id": "X", "material_id": "TXT", "extra_material_refs": ["ANIM"],
          "target_timerange": {{"start": 500000, "duration": 1000000}}}}]}},
        {{"type": "sticker", "segments": [{{"id": "C", "material_id": "CLOCK", "target_timerange": {{"start": 1000000, "duration": 3000000}}}}]}},
        {{"type": "filter", "segments": [{{"id": "F", "material_id": "FILTER", "render_index": 20000,
          "target_timerange": {{"start": 3000000, "duration": 1000000}}}}]}}],
      "materials": {{
        "videos": [{{"id": "P", "type": "photo", "path": "{}"}}],
        "speeds": [{{"id": "SPEED", "mode": 1, "speed": 2, "curve_speed": {{"speed_points": [{{"x": 0, "y": 1}}, {{"x": 0.5, "y": 3}}, {{"x": 1, "y": 1}}]}}}}],
        "transitions": [{{"id": "T", "name": "Dissolve", "duration": 400000}}],
        "video_effects": [{{"id": "PUDDING", "name": "Pudding Bounce", "effect_id": "7534579654615649553"}}],
        "common_mask": [{{"id": "MASK", "resource_type": "circle", "name": "Circle"}}],
        "texts": [{{"id": "TXT", "content": "{{\"text\":\"Hello\"}}"}}],
        "material_animations": [{{"id": "ANIM", "animations": [{{"type": "in", "name": "Typewriter", "duration": 400000}}]}}],
        "stickers": [{{"id": "CLOCK", "name": "Clock", "resource_id": "7528280675909209405"}}],
        "effects": [{{"id": "FILTER", "type": "filter", "name": "Warm", "resource_id": "111"}}]
      }}}}"#,
        photo.display()
    );
    build_in(&json, dir)
}

fn write_lut(path: &Path) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    let mut bytes = vec![0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    bytes.extend(64u32.to_be_bytes());
    bytes.extend(64u32.to_be_bytes());
    std::fs::write(path, bytes).unwrap();
}

/// The times the golden dumps sample: 0, every clip boundary ±1 µs, every keyframe time ±1 µs and 16 even steps.
fn sample_times(t: &Timeline) -> Vec<i64> {
    let mut times = vec![0];
    for l in &t.layers {
        for edge in [l.target.start, l.target.end()] {
            times.extend([edge - 1, edge, edge + 1]);
        }
        for k in l.keyframes.values() {
            for &time in &k.times {
                let offset = l.time_map.offset_at_source_time(time);
                let at = l.target.start + offset;
                times.extend([at - 1, at, at + 1]);
            }
        }
        if let Some(tr) = &l.transition_out {
            times.extend([tr.range.start, tr.range.start + tr.range.duration / 2, tr.range.end() - 1]);
        }
    }
    times.extend((0..=16).map(|i| t.duration * i / 16));
    times.retain(|&x| x >= 0);
    times.sort();
    times.dedup();
    times
}

#[test]
fn frame_equals_per_layer_evaluation() {
    let dir = TempDir::new();
    let timeline = rich_timeline(dir.path());
    assert_eq!(timeline.filters.len(), 1);
    assert!(timeline.layers.iter().any(|l| l.transition_out.is_some()));
    for t in sample_times(&timeline) {
        let frame = timeline.frame(t);
        assert_eq!(frame.time, t);
        // On screen: active layers, plus both clips of a running transition.
        let mut expected: Vec<usize> =
            timeline.layers.iter().enumerate().filter(|(_, l)| l.is_active(t)).map(|(i, _)| i).collect();
        for l in &timeline.layers {
            if let Some(tr) = l.transition_out.as_ref().filter(|tr| tr.contains(t)) {
                expected.push(layer_index(&timeline, &l.id));
                expected.push(tr.next);
            }
        }
        expected.sort();
        expected.dedup();
        assert_eq!(frame.layers.iter().map(|f| f.index).collect::<Vec<_>>(), expected, "layers at {t}");
        for f in &frame.layers {
            let l = &timeline.layers[f.index];
            let offset = t - l.target.start;
            assert_eq!(f.offset, offset);
            assert_eq!(f.source_time, l.time_map.source_time(offset));
            assert_eq!(f.transform, l.transform(t));
            let masks: Vec<MaskGeometry> = l.masks.iter().map(|m| m.geometry_at(f.source_time)).collect();
            assert_eq!(f.masks, masks);
            match &l.content {
                VisualContent::Text(spec) => {
                    assert_eq!(f.text, Some(spec.animations.state(offset, l.target.duration)))
                }
                _ => assert_eq!(f.text, None),
            }
            if l.effects.is_empty() {
                assert_eq!(f.effect, None);
            } else {
                assert_eq!(f.effect, Some(VideoEffectState::combined(l.effects.iter().map(|e| e.state(offset)))));
            }
            match &l.content {
                VisualContent::Sticker(_) => assert_eq!(f.clock_hands, Some(ClockSticker::hand_angles(f.source_time))),
                _ => assert_eq!(f.clock_hands, None),
            }
            let transition = l.transition_out.as_ref().filter(|tr| tr.contains(t));
            assert_eq!(f.transition.map(|x| (x.next, x.progress)), transition.map(|tr| (tr.next, tr.progress(t))));
        }
        let filters: Vec<usize> =
            timeline.filters.iter().enumerate().filter(|(_, x)| x.is_active(t)).map(|(i, _)| i).collect();
        assert_eq!(frame.filters, filters);
    }
}

#[test]
fn frame_holds_both_clips_of_a_transition() {
    let dir = TempDir::new();
    let timeline = rich_timeline(dir.path());
    let a = layer_index(&timeline, "A");
    let b = layer_index(&timeline, "B");
    let transition = timeline.layers[a].transition_out.clone().unwrap();
    assert_eq!(transition.next, b);
    assert_eq!(transition.range, TimeRange::new(1_800_000, 400_000));
    // Before the cut the incoming clip is already on screen, after it the outgoing one still is.
    let before = timeline.frame(1_900_000);
    assert!(before.layers.iter().any(|f| f.index == b));
    let after = timeline.frame(2_100_000);
    let held = after.layers.iter().find(|f| f.index == a).unwrap();
    assert_eq!(held.offset, 2_100_000);
    approx(held.transition.unwrap().progress, 0.75, 1e-12);
    // The typewriter reveals the text over 0.4 s.
    let x = layer_index(&timeline, "X");
    let text = timeline.frame(700_000).layers.into_iter().find(|f| f.index == x).unwrap().text.unwrap();
    approx(text.reveal, 0.5, 1e-12);
}
