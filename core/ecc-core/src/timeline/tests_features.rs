//! Ports of the Swift feature tests: `ClipAnimationTests`, `TextAnimationTests`, `VideoEffectTests` and the others
//! named in each section.

use std::path::{Path, PathBuf};

use super::tests::{approx, build, build_in, layer, report_set, set, touch};
use super::*;
use crate::testing::{language, TempDir};

// MARK: - ClipAnimationTests

/// A 2 s photo clip on the main track with the given `animations`.
fn clip_with(animations: &str) -> (Timeline, TempDir) {
    let dir = TempDir::new();
    let photo = touch(dir.path(), "p.png");
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [
           {{"id": "S1", "material_id": "V1", "target_timerange": {{"start": 1000000, "duration": 2000000}},
            "clip": {{"scale": {{"x": 0.8, "y": 0.8}}, "transform": {{"x": 0.1, "y": 0}}, "alpha": 1}},
            "extra_material_refs": ["M1"]}}]}}],
         "materials": {{
           "videos": [{{"id": "V1", "type": "photo", "path": "{}"}}],
           "material_animations": [{{"id": "M1", "type": "sticker_animation", "animations": [{animations}]}}]}}}}"#,
        crate::testing::slash(&photo)
    );
    (build_in(&json, dir.path()), dir)
}

#[test]
fn clip_animations_parse_in_and_out_and_report_the_rest_by_name() {
    let _english = language("en");
    let (timeline, _dir) = clip_with(
        r#"{"type": "in", "name": "Zoom In", "duration": 500000, "start": 0},
           {"type": "out", "name": "Fade Out", "duration": 400000, "start": 0},
           {"type": "group", "name": "Swing", "duration": 2000000}"#,
    );
    assert_eq!(
        timeline.layers[0].animations,
        [
            ClipAnimation { kind: ClipAnimationKind::In, preset: ClipAnimationPreset::ZoomIn, start: 0, end: 500_000 },
            ClipAnimation {
                kind: ClipAnimationKind::Out,
                preset: ClipAnimationPreset::Fade,
                start: 1_600_000,
                end: 2_000_000
            },
        ]
    );
    assert_eq!(report_set(&timeline), set(&["Animation: Swing"]));
}

#[test]
fn clip_animation_transform_over_time() {
    let (timeline, _dir) = clip_with(
        r#"{"type": "in", "name": "Slide Right", "duration": 500000},
           {"type": "out", "name": "Fade Out", "duration": 500000}"#,
    );
    let layer = &timeline.layers[0];
    // An In "Slide Right" arrives from the left, off-canvas, and ends at the clip's own position.
    approx(layer.transform(1_000_000).position_x, 0.1 - ClipAnimation::SLIDE_DISTANCE, 1e-9);
    approx(layer.transform(1_250_000).position_x, 0.1 - ClipAnimation::SLIDE_DISTANCE / 8.0, 1e-9);
    approx(layer.transform(1_500_000).position_x, 0.1, 1e-9);
    assert_eq!(layer.transform(2_000_000), layer.transform(1_500_000), "nothing runs mid-clip");
    approx(layer.transform(2_000_000).scale_x, 0.8, 1e-9);
    // The Out fades in the clip's last 0.5 s.
    approx(layer.transform(2_750_000).alpha, 1.0 - 0.125, 1e-9);
    approx(layer.transform(2_999_999).alpha, 0.0, 1e-4);
}

#[test]
fn clip_in_and_out_longer_than_the_clip_are_squeezed() {
    let (timeline, _dir) = clip_with(
        r#"{"type": "in", "name": "Fade In", "duration": 3000000},
           {"type": "out", "name": "Zoom Out", "duration": 1000000}"#,
    );
    let spans: Vec<[i64; 2]> = timeline.layers[0].animations.iter().map(|a| [a.start, a.end]).collect();
    assert_eq!(spans, [[0, 1_500_000], [1_500_000, 2_000_000]]);
}

#[test]
fn clip_zoom_and_rotate() {
    let identity = LayerTransform {
        scale_x: 1.0,
        scale_y: 1.0,
        rotation: 0.0,
        position_x: 0.0,
        position_y: 0.0,
        flip_h: false,
        flip_v: false,
        alpha: 1.0,
    };
    let at = |kind, preset, p| ClipAnimation { kind, preset, start: 0, end: 1 }.apply_progress(identity, p);
    use ClipAnimationKind::{In, Out};
    use ClipAnimationPreset::*;
    approx(at(In, ZoomIn, 0.0).scale_x, ClipAnimation::ZOOM_IN_SCALE, 1e-9);
    approx(at(In, ZoomOut, 0.0).scale_x, ClipAnimation::ZOOM_OUT_SCALE, 1e-9);
    approx(at(Out, ZoomIn, 1.0).scale_x, ClipAnimation::ZOOM_OUT_SCALE, 1e-9);
    approx(at(Out, ZoomOut, 1.0).scale_x, ClipAnimation::ZOOM_IN_SCALE, 1e-9);
    approx(at(In, Rotate, 0.0).rotation, -ClipAnimation::ROTATE_DEGREES, 1e-9);
    approx(at(Out, RotateCounterclockwise, 1.0).rotation, -ClipAnimation::ROTATE_DEGREES, 1e-9);
    assert_eq!(at(In, Shake, 0.0), identity);
    assert_eq!(at(In, Fade, 1.0), identity);
}

#[test]
fn clip_animations_recognize_preset_names() {
    use ClipAnimationPreset::*;
    assert_eq!(ClipAnimation::preset("Fade In"), Some(Fade));
    assert_eq!(ClipAnimation::preset("渐隐"), Some(Fade));
    assert_eq!(ClipAnimation::preset("Zoom In 2"), Some(ZoomIn));
    assert_eq!(ClipAnimation::preset("缩小"), Some(ZoomOut));
    assert_eq!(ClipAnimation::preset("向上滑动"), Some(SlideUp));
    assert_eq!(ClipAnimation::preset("Slide Left"), Some(SlideLeft));
    assert_eq!(ClipAnimation::preset("Rotate Counterclockwise"), Some(RotateCounterclockwise));
    assert_eq!(ClipAnimation::preset("Spin"), Some(Rotate));
    assert_eq!(ClipAnimation::preset("Shake 1"), Some(Shake));
    assert_eq!(ClipAnimation::preset("Swing"), None);
    assert_eq!(ClipAnimation::preset("Slide"), None, "a slide without a direction isn't guessed");
}

// MARK: - TextAnimationTests

fn text_draft(animations: &str) -> String {
    format!(
        r#"{{"tracks": [{{"type": "text", "segments": [
           {{"id": "S1", "material_id": "X1", "target_timerange": {{"start": 0, "duration": 2000000}},
            "extra_material_refs": ["M1"]}}]}}],
         "materials": {{
           "texts": [{{"id": "X1", "content": "{{\"text\":\"Hello\",\"styles\":[{{\"range\":[0,5]}}]}}"}}],
           "material_animations": [{{"id": "M1", "type": "sticker_animation", "animations": [{animations}]}}]}}}}"#
    )
}

#[test]
fn text_animations_parse_and_report_the_rest_by_name() {
    let _english = language("en");
    let timeline = build(&text_draft(
        r#"{"type": "in", "name": "Typewriter", "duration": 500000, "resource_id": "1"},
           {"type": "out", "name": "渐隐", "duration": 400000},
           {"type": "loop", "name": "Swing", "duration": 600000}"#,
    ));
    let VisualContent::Text(spec) = &timeline.layers[0].content else { panic!("no text layer") };
    assert_eq!(
        spec.animations.intro,
        Some(TextAnimation {
            kind: TextAnimationKind::Intro,
            effect: TextAnimationEffect::Typewriter,
            duration: 500_000
        })
    );
    assert_eq!(
        spec.animations.outro,
        Some(TextAnimation { kind: TextAnimationKind::Outro, effect: TextAnimationEffect::Fade, duration: 400_000 })
    );
    assert!(spec.animations.r#loop.is_none());
    assert_eq!(report_set(&timeline), set(&["Animation: Swing"]));
}

#[test]
fn text_animations_recognize_preset_names() {
    use TextAnimationEffect as E;
    use TextAnimationKind::*;
    let d = TextAnimations::SLIDE_DISTANCE;
    assert_eq!(E::recognize("Fade In", Intro), Some(E::Fade));
    assert_eq!(E::recognize("Slide Left", Intro), Some(E::Slide { dx: -d, dy: 0.0 }));
    assert_eq!(E::recognize("向上滑动", Outro), Some(E::Slide { dx: 0.0, dy: d }));
    assert_eq!(E::recognize("Zoom In", Intro), Some(E::Zoom { scale: 0.0, fades: false }));
    assert_eq!(E::recognize("Zoom Out", Outro), Some(E::Zoom { scale: 0.0, fades: false }));
    assert_eq!(E::recognize("Pop Up", Intro), Some(E::Pop));
    assert_eq!(E::recognize("Blink", Loop), Some(E::Blink));
    assert_eq!(E::recognize("Blink", Intro), None);
    assert_eq!(E::recognize("Spin", Intro), None);
}

#[test]
fn text_animation_state_over_time() {
    let animations = TextAnimations {
        intro: Some(TextAnimation {
            kind: TextAnimationKind::Intro,
            effect: TextAnimationEffect::Slide { dx: 0.2, dy: 0.0 },
            duration: 500_000,
        }),
        outro: Some(TextAnimation {
            kind: TextAnimationKind::Outro,
            effect: TextAnimationEffect::Fade,
            duration: 500_000,
        }),
        r#loop: Some(TextAnimation {
            kind: TextAnimationKind::Loop,
            effect: TextAnimationEffect::Pulse,
            duration: 400_000,
        }),
    };
    let start = animations.state(0, 2_000_000);
    approx(start.offset_x, -0.2, 1e-9);
    approx(start.alpha, 0.0, 1e-9);
    // Loop between intro and outro: a cycle starts at rest and peaks halfway.
    approx(animations.state(500_000, 2_000_000).scale, 1.0, 1e-9);
    approx(animations.state(700_000, 2_000_000).scale, 1.0 + TextAnimations::PULSE_SCALE, 1e-9);
    approx(animations.state(1_750_000, 2_000_000).alpha, 0.5, 1e-9);
    // Intro and outro longer than the clip are squeezed into it.
    approx(animations.state(250_000, 500_000).alpha, 1.0, 1e-9);
    approx(animations.state(375_000, 500_000).alpha, 0.5, 1e-9);
}

#[test]
fn typewriter_reveals_whole_characters() {
    let typewriter = TextAnimations {
        intro: Some(TextAnimation {
            kind: TextAnimationKind::Intro,
            effect: TextAnimationEffect::Typewriter,
            duration: 1_000_000,
        }),
        ..TextAnimations::default()
    };
    approx(typewriter.state(500_000, 2_000_000).reveal, 0.5, 1e-9);
    assert_eq!(TextAnimationState::visible_length("Hi 👋🏽!", 0.5), 2);
    assert_eq!(TextAnimationState::visible_length("Hi 👋🏽!", 0.8), 7, "an emoji is one character");
    assert_eq!(TextAnimationState::visible_length("Hi 👋🏽!", 1.0), 8);
    assert_eq!(TextAnimationState::visible_length("Hi", 0.0), 0);
}

// MARK: - VideoEffectTests

#[test]
fn clip_effects_are_rendered_others_reported() {
    let _english = language("en");
    let dir = TempDir::new();
    let photo = touch(dir.path(), "p.png");
    let json = format!(
        r#"{{"tracks": [
      {{"type": "video", "segments": [{{"id": "S", "material_id": "V", "extra_material_refs": ["E1", "E2", "E3"],
                                      "target_timerange": {{"start": 0, "duration": 2000000}}}}]}},
      {{"type": "effect", "segments": [{{"id": "T", "material_id": "E4",
                                       "target_timerange": {{"start": 0, "duration": 1000000}}}}]}}],
     "materials": {{"videos": [{{"id": "V", "type": "photo", "path": "{}"}}],
      "video_effects": [
        {{"id": "E1", "name": "Pudding Bounce", "effect_id": "7534579654615649553", "apply_target_type": 0}},
        {{"id": "E2", "name": "Hazy Rotation", "effect_id": "7486402432189091127", "apply_target_type": 0,
         "adjust_params": [{{"name": "effects_adjust_speed", "value": 0.6666666666}}]}},
        {{"id": "E3", "name": "Card Slash", "effect_id": "7474875426633026821", "apply_target_type": 0}},
        {{"id": "E4", "name": "Hazy Rotation", "effect_id": "7486402432189091127", "apply_target_type": 2}}]}}}}"#,
        crate::testing::slash(&photo)
    );
    let timeline = build_in(&json, dir.path());
    let layer = layer(&timeline, "S");
    let kinds: Vec<VideoEffectKind> = layer.effects.iter().map(|e| e.kind).collect();
    assert_eq!(kinds, [VideoEffectKind::PuddingBounce, VideoEffectKind::HazyRotation]);
    approx(layer.effects[1].speed, 2.0, 1e-6);
    assert_eq!(
        report_set(&timeline),
        set(&["Video effect: Card Slash", "Video effect: Hazy Rotation"]),
        "unknown effects and effect-track effects are still reported"
    );
}

#[test]
fn video_effects_open_the_clip_and_settle() {
    let pudding = VideoEffect { kind: VideoEffectKind::PuddingBounce, start: 0, speed: 1.0 };
    let first = pudding.state(0);
    assert!(first.zoom > 1.3 && first.flash > 0.5 && first.blur > 0.0);
    assert!(pudding.state(1_000_000).is_identity());
    let hazy = VideoEffect { kind: VideoEffectKind::HazyRotation, start: 0, speed: 1.0 };
    approx(hazy.state(0).zoom, 2.0, 1e-9);
    assert!(hazy.state(0).rotation < -10.0);
    assert!(hazy.state(400_000).is_identity());
    // Twice the speed: the same look at half the time.
    let fast = VideoEffect { speed: 2.0, ..hazy };
    assert_eq!(fast.state(100_000), hazy.state(200_000));
    assert!(VideoEffect { start: 500_000, ..hazy }.state(100_000).is_identity());
}

// MARK: - MaskTests (the parts outside the rasterizer)

#[test]
fn mask_keyframes_are_on_the_source_time_axis() {
    let mut keyframes = std::collections::BTreeMap::new();
    keyframes.insert(MaskProperty::PositionX, KeyframeTrack::new(vec![10_000_000, 12_000_000], vec![0.0, 1.0]));
    let mask = ClipMask {
        kind: ClipMaskKind::Shape(MaskShape::Rectangle),
        geometry: MaskGeometry { center_x: 0.3, ..MaskGeometry::default() },
        inverted: false,
        keyframes,
    };
    approx(mask.geometry_at(11_000_000).center_x, 0.5, 1e-9);
    assert_eq!(mask.geometry_at(0).center_x, 0.0);
    assert_eq!(MaskProperty::from_property_type("KFTypeCommonMaskSizeWidth"), Some(MaskProperty::Width));
    assert_eq!(MaskProperty::from_property_type("KFTypeMaskRoundCorner"), Some(MaskProperty::RoundCorner));
    assert_eq!(MaskProperty::from_property_type("KFTypeCommonMaskTwist"), None);
    assert_eq!(ClipMask::shape("geometric_shape", "Heart"), Some(MaskShape::Heart));
    assert_eq!(ClipMask::shape("line", "Split"), Some(MaskShape::Line));
    assert_eq!(ClipMask::shape("custom", "Brush"), None);
}

#[test]
fn timeline_builds_masks() {
    let _english = language("en");
    let dir = TempDir::new();
    let d = dir.path();
    let strokes = d.join("adjust_mask/B1");
    std::fs::create_dir_all(&strokes).unwrap();
    std::fs::write(strokes.join("2000000"), [0u8, 255]).unwrap();
    std::fs::write(strokes.join("data.json"), r#"{"data": [{"width": 2, "height": 1, "pts": 2000000}]}"#).unwrap();
    let photo = touch(d, "p.png");
    let root = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##/adjust_mask/";
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [
       {{"id": "S0", "material_id": "V", "target_timerange": {{"start": 0, "duration": 1000000}},
        "extra_material_refs": ["M1", "M2", "M3", "M4"],
        "common_keyframes": [
          {{"property_type": "KFTypeCommonMaskPositionX", "material_id": "C1",
           "keyframe_list": [{{"time_offset": 0, "values": [0.1]}}, {{"time_offset": 1000000, "values": [0.3]}}]}},
          {{"property_type": "KFTypeCommonMaskPositionX", "material_id": "C2",
           "keyframe_list": [{{"time_offset": 0, "values": [0.9]}}]}}]}}]}}],
     "materials": {{
       "videos": [{{"id": "V", "type": "photo", "path": "{}"}}],
       "common_mask": [
         {{"id": "M1", "constant_material_id": "C1", "name": "Rectangle", "resource_type": "rectangle",
          "config": {{"width": 0.2, "height": 0.4, "centerX": 0, "centerY": 0.5, "rotation": 30, "feather": 0.1,
                     "roundCorner": 0.2, "invert": true}}}},
         {{"id": "M2", "constant_material_id": "C9", "name": "Brush", "resource_type": "custom",
          "loader_work_space": "{root}B1", "config": {{}}}},
         {{"id": "M3", "name": "Brush", "resource_type": "custom", "loader_work_space": "", "config": {{}}}},
         {{"id": "M4", "name": "Wave", "resource_type": "wave", "config": {{"expansion": 0.2}}}}]}}}}"#,
        crate::testing::slash(&photo)
    );
    let timeline = build_in(&json, d);
    let masks = &timeline.layers[0].masks;
    assert_eq!(masks.len(), 2);
    assert_eq!(masks[0].kind, ClipMaskKind::Shape(MaskShape::Rectangle));
    assert!(masks[0].inverted);
    assert_eq!(
        masks[0].geometry,
        MaskGeometry {
            center_x: 0.0,
            center_y: 0.5,
            width: 0.2,
            height: 0.4,
            rotation: 30.0,
            feather: 0.1,
            round_corner: 0.2
        }
    );
    approx(masks[0].geometry_at(500_000).center_x, 0.2, 1e-9);
    assert_eq!(masks[1].kind, ClipMaskKind::Brush(strokes));
    assert!(masks[1].keyframes.is_empty());
    assert_eq!(
        report_set(&timeline),
        set(&["Mask: brush strokes not saved in the project", "Mask: expansion", "Mask: Wave"])
    );
}

// MARK: - BlendModeTests

#[test]
fn mix_mode_material_becomes_the_layers_blend_mode() {
    let _english = language("en");
    let dir = TempDir::new();
    let photo = touch(dir.path(), "p.png");
    let segments: Vec<String> = (0..6)
        .map(|i| {
            let refs = if i < 5 { format!(r#", "extra_material_refs": ["M{i}"]"#) } else { String::new() };
            format!(
                r#"{{"id": "S{i}", "material_id": "V"{refs}, "target_timerange": {{"start": 0, "duration": 1000000}}}}"#
            )
        })
        .collect();
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [{}]}}],
     "materials": {{"videos": [{{"id": "V", "type": "photo", "path": "{}"}}],
      "effects": [
        {{"id": "M0", "type": "mix_mode", "name": "Screen", "effect_id": "871339", "resource_id": "6758325170760323597"}},
        {{"id": "M1", "type": "mix_mode", "name": "", "resource_id": "6758325895519277582"}},
        {{"id": "M2", "type": "mix_mode", "name": "Soft light"}},
        {{"id": "M3", "type": "mix_mode", "name": "Normal"}},
        {{"id": "M4", "type": "mix_mode", "name": "Vivid", "effect_id": "1", "resource_id": "2"}}]}}}}"#,
        segments.join(","),
        crate::testing::slash(&photo)
    );
    let timeline = build_in(&json, dir.path());
    let mode = |id: &str| layer(&timeline, id).blend_mode;
    assert_eq!(mode("S0"), Some(BlendMode::Screen));
    assert_eq!(mode("S1"), Some(BlendMode::Multiply));
    assert_eq!(mode("S2"), Some(BlendMode::SoftLight));
    assert_eq!(mode("S3"), Some(BlendMode::Normal));
    assert_eq!(mode("S4"), None);
    assert_eq!(mode("S5"), None);
    assert_eq!(report_set(&timeline), set(&["Blend mode: Vivid"]), "only the unknown mode is reported");
}

// MARK: - ChromaKeyTests

#[test]
fn chroma_material_becomes_the_layers_key() {
    let dir = TempDir::new();
    let photo = touch(dir.path(), "p.png");
    let json = format!(
        r##"{{"tracks": [{{"type": "video", "segments": [
        {{"id": "S0", "material_id": "V", "extra_material_refs": ["C0"], "target_timerange": {{"start": 0, "duration": 1000000}}}},
        {{"id": "S1", "material_id": "V", "extra_material_refs": ["C1"], "target_timerange": {{"start": 0, "duration": 1000000}}}},
        {{"id": "S2", "material_id": "V", "extra_material_refs": ["C2"], "target_timerange": {{"start": 0, "duration": 1000000}}}}]}}],
     "materials": {{"videos": [{{"id": "V", "type": "photo", "path": "{}"}}],
      "chromas": [
        {{"id": "C0", "type": "chroma", "color": "#FF00FF00", "intensity_value": 0.4, "shadow_value": 0.2,
         "edge_smooth_value": 0.3, "spill_value": 1.5}},
        {{"id": "C1", "type": "chroma", "color": "", "intensity_value": 0.4}},
        {{"id": "C2", "type": "chroma", "color": "#00FF00", "intensity_value": 0}}]}}}}"##,
        crate::testing::slash(&photo)
    );
    let timeline = build_in(&json, dir.path());
    assert_eq!(
        layer(&timeline, "S0").chroma_key,
        Some(ChromaKey { red: 0.0, green: 1.0, blue: 0.0, intensity: 0.4, shadow: 0.2, edge_smooth: 0.3, spill: 1.0 })
    );
    assert!(layer(&timeline, "S1").chroma_key.is_none(), "no colour picked: the clip is shown as it is");
    assert!(layer(&timeline, "S2").chroma_key.is_none(), "intensity 0: the clip is shown as it is");
    assert!(timeline.report.unsupported.is_empty());
    assert_eq!(ChromaKey::rgb(" #0080FF "), Some((0.0, 128.0 / 255.0, 1.0)));
    assert_eq!(ChromaKey::rgb("#12345"), None);
    assert_eq!(ChromaKey::rgb("#GG0000"), None);
}

// MARK: - StickerTests

#[test]
fn clock_hands_loop_from_the_rest_pose() {
    let start = ClockSticker::hand_angles(0);
    approx(start.0, 0.0, 0.001);
    approx(start.1, 90.0, 0.001);
    // Halfway between the first two samples (1/30 s).
    let early = ClockSticker::hand_angles(33_333);
    approx(early.0, 18.5, 0.1);
    approx(early.1, 96.5, 0.1);
    // The hands rest at the end of the loop, then it starts over.
    let rest = ClockSticker::hand_angles(ClockSticker::PERIOD - 50_000);
    approx(rest.0, 0.0, 0.001);
    approx(rest.1, 90.0, 0.001);
    let again = ClockSticker::hand_angles(ClockSticker::PERIOD + 33_333);
    approx(again.0, early.0, 0.001);
    approx(again.1, early.1, 0.001);
    assert_eq!(ClockSticker::hand_angles(-ClockSticker::PERIOD + 33_333), early);
}

#[test]
fn clock_time_stickers_become_layers_other_stickers_are_reported() {
    let _english = language("en");
    let json = r#"{"tracks": [{"type": "sticker", "segments": [
        {"id": "S0", "material_id": "M0", "render_index": 14000, "target_timerange": {"start": 0, "duration": 1000000},
         "clip": {"scale": {"x": 0.25, "y": 0.25}, "transform": {"x": 0.5, "y": 0.1}}},
        {"id": "S1", "material_id": "M1", "target_timerange": {"start": 0, "duration": 1000000}},
        {"id": "S2", "material_id": "M2", "target_timerange": {"start": 0, "duration": 1000000}}]}],
     "materials": {"stickers": [
        {"id": "M0", "type": "sticker", "name": "Clock Time EN White Outline Progress Bar", "resource_id": "7528282080199904565"},
        {"id": "M1", "type": "sticker", "name": "Clock Time EN Blue White Minimalist", "resource_id": "7528280675909209405"},
        {"id": "M2", "type": "sticker", "name": "Pet Meme", "resource_id": "7616938154540469509"}]}}"#;
    let timeline = build(json);
    let styles: Vec<(String, ClockStyle)> = timeline
        .layers
        .iter()
        .filter_map(|l| match &l.content {
            VisualContent::Sticker(spec) => Some((l.id.clone(), spec.clock.style)),
            _ => None,
        })
        .collect();
    assert_eq!(styles, [("S1".to_string(), ClockStyle::BlueWhite), ("S0".to_string(), ClockStyle::WhiteOutline)]);
    let s0 = layer(&timeline, "S0");
    assert_eq!(s0.render_index, 14000);
    approx(s0.transform(0).position_x, 0.5, 1e-9);
    assert_eq!(report_set(&timeline), set(&["Sticker: Pet Meme"]));
}

// MARK: - ColorFilterTests (the cache and layout parts; the lookup itself runs in the renderers)

/// A file with a PNG header of `width`×`height` (all the lookup search reads).
fn write_png_header(path: &std::path::Path, width: u32, height: u32) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    let mut bytes = vec![0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    bytes.extend(width.to_be_bytes());
    bytes.extend(height.to_be_bytes());
    std::fs::write(path, bytes).unwrap();
}

#[test]
fn lookup_image_layouts() {
    assert_eq!(ColorFilter::layout(512, 512), Some(LutLayout::Grid { size: 64, tiles_per_row: 8 }));
    assert_eq!(ColorFilter::layout(64, 64), Some(LutLayout::Grid { size: 16, tiles_per_row: 4 }));
    assert_eq!(ColorFilter::layout(1024, 32), Some(LutLayout::Row { size: 32 }));
    assert_eq!(ColorFilter::layout(16, 256), Some(LutLayout::Column { size: 16 }));
    assert_eq!(ColorFilter::layout(500, 500), None);
    assert_eq!(ColorFilter::layout(1080, 1920), None);
    assert_eq!(ColorFilter::layout(4096, 4096), None, "N = 256 is over the limit");
    let grid = LutLayout::Grid { size: 64, tiles_per_row: 8 };
    assert_eq!(grid.pixel(5, 7, 9), (64 + 5, 64 + 7));
    assert_eq!(LutLayout::Row { size: 32 }.pixel(1, 2, 3).0, 97);
}

#[test]
fn image_size_from_png_and_jpeg_headers() {
    // "Film" ships a JPEG named filter.png: APP0 segment, then the frame header (SOF0: precision, height, width).
    let jpeg = [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x00, 0x02, 0x00, 0x03,
        0x01, 0x22, 0x00,
    ];
    assert_eq!(ColorFilter::image_size(&jpeg), Some((512, 256)));
    let png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 2, 0, 0, 0, 2, 0];
    assert_eq!(ColorFilter::image_size(&png).map(|s| s.0), Some(512));
    assert_eq!(ColorFilter::image_size(&[0x47, 0x49, 0x46, 0x38]), None);
}

#[test]
fn cube_sizes() {
    let dir = TempDir::new();
    let cube = dir.path().join("a.cube");
    std::fs::write(&cube, "# comment\nTITLE \"x\"\r\nLUT_3D_SIZE 33\n0 0 0\n").unwrap();
    assert_eq!(ColorFilter::cube_size(&cube), Some(33));
    std::fs::write(&cube, "LUT_3D_SIZE 65\n").unwrap();
    assert_eq!(ColorFilter::cube_size(&cube), None);
    std::fs::write(&cube, "LUT_1D_SIZE 16\n").unwrap();
    assert_eq!(ColorFilter::cube_size(&cube), None);
}

#[test]
fn filters_from_the_draft() {
    let _english = language("en");
    let dir = TempDir::new();
    let d = dir.path();
    // A cached package with its lookup image somewhere inside, next to a texture that isn't one.
    let package = d.join("cache/111/abc");
    write_png_header(&package.join("res/filter.png"), 1024, 32);
    write_png_header(&package.join("grain.png"), 100, 60);
    let photo = touch(d, "p.png");
    let json = format!(
        r#"{{"tracks": [
       {{"type": "video", "segments": [{{"id": "S", "material_id": "V", "render_index": 0,
         "extra_material_refs": ["F2"], "target_timerange": {{"start": 0, "duration": 1000000}}}}]}},
       {{"type": "filter", "segments": [
         {{"id": "T1", "material_id": "F2", "render_index": 10001, "target_timerange": {{"start": 0, "duration": 500000}}}},
         {{"id": "T2", "material_id": "F3", "render_index": 10000, "target_timerange": {{"start": 0, "duration": 500000}}}}]}}],
     "materials": {{
       "videos": [{{"id": "V", "type": "photo", "path": "{}"}}],
       "effects": [
         {{"id": "F1", "type": "filter", "name": "Film", "value": 0.4, "resource_id": "111",
          "path": "/somewhere/else/Cache/effect/111/abc"}},
         {{"id": "F2", "type": "filter", "name": "Clip filter", "value": 1, "resource_id": "111", "path": "{}"}},
         {{"id": "F3", "type": "filter", "name": "Not cached", "value": 1, "resource_id": "222", "path": "/nope"}}]}}}}"#,
        crate::testing::slash(&photo),
        crate::testing::slash(&package)
    );
    let names = |luts: &[PathBuf]| -> Vec<String> {
        luts.iter().map(|p| p.file_name().unwrap().to_string_lossy().into_owned()).collect()
    };
    assert_eq!(
        names(&ColorFilter::locate(Some(Path::new("/somewhere/else")), "111", &[d.join("cache")])),
        ["filter.png"]
    );
    let timeline = build_in(&json, d);
    let filters = &timeline.layers[0].filters;
    assert_eq!(filters.iter().map(|f| f.name.as_str()).collect::<Vec<_>>(), ["Clip filter"]);
    assert_eq!(names(&filters[0].luts), ["filter.png"]);
    let found = report_set(&timeline);
    assert!(found.contains("Filter: Not cached"));
    assert!(!found.contains("Filter: Clip filter"));
    assert_eq!(
        timeline.filters.iter().map(|f| f.id.as_str()).collect::<Vec<_>>(),
        ["T1"],
        "the uncached filter is left out"
    );
    let filter_layer = FilterLayer {
        id: "T".into(),
        target: TimeRange::new(0, 10),
        track_index: 1,
        render_index: 10000,
        filter: ColorFilter { name: "x".into(), luts: vec![photo], intensity: 1.0 },
    };
    assert!(filter_layer.covers(timeline.layers[0].render_index, timeline.layers[0].track_index));
    assert!(filter_layer.is_active(5));
    assert!(!filter_layer.is_active(10));
}

/// A multi-part package (like "Shimmering B&W"): parts applied by `zorder`, each with the table its materials name;
/// tables no material uses don't count.
#[test]
fn package_parts_use_the_tables_their_materials_name() {
    let dir = TempDir::new();
    let package = dir.path().join("package");
    for part in ["A/material", "B/material", "C/image"] {
        std::fs::create_dir_all(package.join(part)).unwrap();
    }
    for path in ["A/image/filter.png", "B/image/filter_bg.png", "B/image/filter_skin.png", "C/image/filter.png"] {
        write_png_header(&package.join(path), 64, 64);
    }
    // Binary materials name their textures; A's table is unused.
    std::fs::write(package.join("A/material/stars.material"), "\u{1}image/kira-1.png\u{0}").unwrap();
    std::fs::write(package.join("B/material/SkinSeg.material"), "\u{1}image/filter_skin.png\u{0}image/filter_bg.png")
        .unwrap();
    std::fs::write(
        package.join("config.json"),
        r#"{"effect": {"Link": [{"path": "C/", "zorder": 8003}, {"path": "B/", "zorder": 8002}, {"path": "A/", "zorder": 8001}]}}"#,
    )
    .unwrap();
    let tail = |p: &PathBuf| p.strip_prefix(&package).unwrap().to_string_lossy().replace('\\', "/").replace("//", "/");
    let luts: Vec<String> = ColorFilter::lut_files(&package).iter().map(tail).collect();
    assert_eq!(luts, ["B/image/filter_bg.png"], "C has no materials, A's table is unused");
    // Without the parts list, the first table of the package.
    std::fs::remove_file(package.join("config.json")).unwrap();
    let luts: Vec<String> = ColorFilter::lut_files(&package).iter().map(tail).collect();
    assert_eq!(luts, ["A/image/filter.png"]);
}

// MARK: - QualityEnhanceTests, RetouchTests, HDRModeTests, ColorAdjustmentTests (the builder parts)

#[test]
fn enhance_quality_is_rendered_and_other_ai_processing_is_reported() {
    let _english = language("en");
    let dir = TempDir::new();
    let photo = touch(dir.path(), "p.png");
    let json = format!(
        r###"{{"tracks": [{{"type": "video", "segments": [
        {{"id": "S0", "material_id": "V0", "target_timerange": {{"start": 0, "duration": 1000000}}}},
        {{"id": "S1", "material_id": "V1", "target_timerange": {{"start": 1000000, "duration": 1000000}}}}]}}],
     "materials": {{"videos": [
        {{"id": "V0", "type": "photo", "path": "{p}", "video_algorithm": {{"algorithms": [{{"type": "QualityEnhance"}}],
         "path": "##_draftpath_placeholder_X_##/Resources/videoAlg/a_0_1000000.mp4", "quality_enhance": {{"level": 0}}}}}},
        {{"id": "V1", "type": "photo", "path": "{p}", "video_algorithm": {{"algorithms": [{{"type": "GenAiSmartEdit"}}]}}}}]}}}}"###,
        p = crate::testing::slash(&photo)
    );
    let timeline = build_in(&json, dir.path());
    assert!(layer(&timeline, "S0").enhances_quality);
    assert!(!layer(&timeline, "S1").enhances_quality);
    let found: Vec<String> =
        timeline.report.unsupported.iter().map(|i| format!("{}: {} ×{}", i.category, i.name, i.count)).collect();
    assert_eq!(found, ["AI processing: GenAiSmartEdit ×1"]);
}

#[test]
fn skin_retouch_is_rendered_and_makeup_looks_are_reported() {
    let _english = language("en");
    let dir = TempDir::new();
    let photo = touch(dir.path(), "p.png");
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [
        {{"id": "S0", "material_id": "V", "extra_material_refs": ["E0", "E1", "R"], "target_timerange": {{"start": 0, "duration": 1000000}}}},
        {{"id": "S1", "material_id": "V", "extra_material_refs": ["E2", "R", "D"], "target_timerange": {{"start": 1000000, "duration": 1000000}}}}]}}],
     "materials": {{"videos": [{{"id": "V", "type": "photo", "path": "{}"}}],
      "effects": [
        {{"id": "E0", "type": "figure", "sub_type": "auto_beauty", "name": "Sparkly Skin"}},
        {{"id": "E1", "type": "figure", "sub_type": "auto_beauty", "name": "", "intensity_key": "face_adjust_fuling"}},
        {{"id": "E2", "type": "figure", "sub_type": "exclusion_face", "name": "Cherry Cola"}},
        {{"id": "R", "type": "makeup_root", "sub_type": "auto_beauty", "name": "makeup-root"}}],
      "manual_deformations": [{{"id": "D", "type": "manual_deformation"}}]}}}}"#,
        crate::testing::slash(&photo)
    );
    let timeline = build_in(&json, dir.path());
    assert!(layer(&timeline, "S0").retouches_skin);
    assert!(!layer(&timeline, "S1").retouches_skin, "a makeup look alone is not a skin retouch");
    let found: std::collections::BTreeSet<String> =
        timeline.report.unsupported.iter().map(|i| format!("{}: {} ×{}", i.category, i.name, i.count)).collect();
    assert_eq!(found, set(&["Beauty / makeup: Cherry Cola ×1", "Manual deformation: body/face reshape ×1"]));
}

#[test]
fn hdr_mode_zero_turns_tone_mapping_off() {
    let dir = TempDir::new();
    let photo = touch(dir.path(), "hdr.png");
    let segment = |id: &str, settings: &str| {
        format!(
            r#"{{"id": "{id}", "material_id": "V1", "target_timerange": {{"start": 0, "duration": 1000000}}{settings}}}"#
        )
    };
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [{}, {}, {}]}}],
          "materials": {{"videos": [{{"id": "V1", "type": "photo", "path": "{}"}}]}}}}"#,
        segment("Off", r#", "hdr_settings": {"mode": 0, "intensity": 1.0, "nits": 1000}"#),
        segment("On", r#", "hdr_settings": {"mode": 4, "intensity": 1.0, "nits": 1000}"#),
        segment("None", ""),
        crate::testing::slash(&photo)
    );
    let timeline = build(&json);
    assert!(!layer(&timeline, "Off").tone_maps_hdr);
    assert!(layer(&timeline, "On").tone_maps_hdr);
    assert!(layer(&timeline, "None").tone_maps_hdr);
}

fn hsl_report(hsl: &[(f64, f64, f64)]) -> std::collections::BTreeSet<String> {
    let refs: Vec<String> = (0..hsl.len()).map(|i| format!("\"H{i}\"")).collect();
    let materials: Vec<String> = hsl
        .iter()
        .enumerate()
        .map(|(i, v)| {
            format!(
                r#"{{"id": "H{i}", "type": "hsl", "hsl_color_type": 1, "hue": {}, "saturation": {}, "lightness": {}}}"#,
                v.0, v.1, v.2
            )
        })
        .collect();
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [
        {{"id": "S1", "material_id": "V1", "target_timerange": {{"start": 0, "duration": 1000000}},
         "extra_material_refs": [{}]}}]}}],
      "materials": {{"videos": [{{"id": "V1", "type": "video", "path": "/nonexistent/clip.mov"}}], "hsl": [{}]}}}}"#,
        refs.join(","),
        materials.join(",")
    );
    report_set(&build(&json))
}

#[test]
fn neutral_hsl_is_not_reported_and_changed_hsl_is() {
    let _english = language("en");
    assert!(!hsl_report(&[(0.0, 0.0, 0.0)]).contains("HSL color: color adjustment"));
    assert!(hsl_report(&[(0.0, 0.0, 0.0), (0.0, 25.0, 0.0)]).contains("HSL color: color adjustment"));
}

// MARK: - StabilizationTests (the builder part)

#[test]
fn stabilized_video_clips_are_not_reported() {
    let _english = language("en");
    let dir = TempDir::new();
    let video = touch(dir.path(), "v.mov");
    let photo = touch(dir.path(), "p.png");
    let json = format!(
        r#"{{"tracks": [{{"type": "video", "segments": [
       {{"id": "S1", "material_id": "V1", "target_timerange": {{"start": 0, "duration": 1000000}}}},
       {{"id": "S2", "material_id": "V2", "target_timerange": {{"start": 1000000, "duration": 1000000}}}},
       {{"id": "S3", "material_id": "V3", "target_timerange": {{"start": 2000000, "duration": 1000000}}}}]}}],
     "materials": {{"videos": [
       {{"id": "V1", "type": "video", "path": "{v}", "stable": {{"stable_level": 3, "matrix_path": ""}}}},
       {{"id": "V2", "type": "video", "path": "{v}"}},
       {{"id": "V3", "type": "photo", "path": "{p}", "stable": {{"stable_level": 2}}}}]}}}}"#,
        v = crate::testing::slash(&video),
        p = crate::testing::slash(&photo)
    );
    let timeline = build_in(&json, dir.path());
    assert_eq!(timeline.layers.iter().map(|l| l.stabilization).collect::<Vec<_>>(), [3, 0, 0]);
    assert_eq!(report_set(&timeline), set(&["Stabilization: level 2"]));
}

// MARK: - VoiceTests (VoiceEffectsParsingTests)

#[test]
fn voice_toggles_are_parsed_leniently_and_carried_on_clips() {
    let _english = language("en");
    let dir = TempDir::new();
    let media = touch(dir.path(), "a.caf");
    let json = format!(
        r#"{{
      "duration": 3000000,
      "materials": {{
        "audios": [{{"id": "A", "path": "{}", "type": "record"}}],
        "vocal_separations": [
          {{"id": "VS-KEEP", "type": "vocal_separation", "choice": 2}},
          {{"id": "VS-REMOVE", "type": "vocal_separation", "choice": "1"}},
          {{"id": "VS-OFF", "type": "vocal_separation", "choice": 0}},
          {{"id": "VS-ODD", "type": "vocal_separation", "choice": 7}}
        ],
        "realtime_denoises": [
          {{"id": "RD-ON", "is_denoise": true, "denoise_rate": 0.85}},
          {{"id": "RD-OFF", "is_denoise": false, "denoise_rate": 0.85}},
          {{"id": "RD-NORATE", "is_denoise": 1}}
        ],
        "vocal_beautifys": [
          {{"id": "VB-ON", "type": "vocal_beautify", "enable": true, "ambient_sound_level": 75}},
          {{"id": "VB-100", "enable": 1, "ambient_sound_level": "100"}},
          {{"id": "VB-OFF", "enable": false, "ambient_sound_level": 75}},
          {{"id": "VB-NOLEVEL", "enable": true, "ambient_sound_level": null}}
        ]
      }},
      "tracks": [{{"type": "audio", "segments": [
        {{"material_id": "A", "extra_material_refs": ["VS-KEEP", "RD-ON", "VB-ON"],
         "target_timerange": {{"start": 0, "duration": 1000000}}, "source_timerange": {{"start": 0, "duration": 1000000}}}},
        {{"material_id": "A", "extra_material_refs": ["VS-REMOVE", "RD-OFF", "VB-OFF"],
         "target_timerange": {{"start": 1000000, "duration": 1000000}}}},
        {{"material_id": "A", "extra_material_refs": ["VS-OFF", "RD-NORATE", "VB-100"],
         "target_timerange": {{"start": 2000000, "duration": 500000}}}},
        {{"material_id": "A", "extra_material_refs": ["VS-ODD", "VB-NOLEVEL"],
         "target_timerange": {{"start": 2500000, "duration": 500000}}}}
      ]}}]
    }}"#,
        crate::testing::slash(&media)
    );
    let draft = crate::draft::Draft::parse(&json).unwrap();
    assert_eq!(draft.materials.vocal_separations.iter().map(|v| v.choice).collect::<Vec<_>>(), [2, 1, 0, 7]);
    assert_eq!(draft.materials.vocal_beautifys.iter().map(|v| v.enable).collect::<Vec<_>>(), [true, true, false, true]);
    let timeline = build_in(&json, dir.path());
    let mut clips = timeline.audio_clips.clone();
    clips.sort_by_key(|c| c.target.start);
    let voices: Vec<VoiceEffects> = clips.into_iter().map(|c| c.voice).collect();
    assert_eq!(
        voices,
        [
            VoiceEffects {
                separation: VoiceSeparation::KeepVoice,
                denoise: Some(0.85),
                enhance: Some(0.75),
                filter: None
            },
            VoiceEffects { separation: VoiceSeparation::RemoveVoice, ..VoiceEffects::default() },
            VoiceEffects { denoise: Some(0.85), enhance: Some(1.0), ..VoiceEffects::default() },
            VoiceEffects { enhance: Some(0.75), ..VoiceEffects::default() },
        ]
    );
    // Supported effects are no longer listed; an unknown separation mode is.
    let audio: Vec<&str> =
        timeline.report.unsupported.iter().filter(|i| i.category == "Audio").map(|i| i.name.as_str()).collect();
    assert_eq!(audio, ["vocal separation mode 7"]);
}

// MARK: - VoiceFilterTests (presets and the builder)

fn write_preset(folder: &Path, processor: &str) {
    std::fs::create_dir_all(folder).unwrap();
    let preset = serde_json::json!({
        "nodes": [{"node_id": "p", "processor_name": processor, "processor_state": {"parameters": {"Gain dB": -6.0}}}],
        "connections": [],
        "ports": [{"internal_node_id": "p", "port_direction": "input", "port_type": "audio"},
                  {"internal_node_id": "p", "port_direction": "output", "port_type": "audio"}],
    });
    std::fs::write(folder.join("mdsp_preset.json"), preset.to_string()).unwrap();
}

#[test]
fn unknown_processors_and_broken_presets_are_rejected() {
    let dir = TempDir::new();
    let d = dir.path();
    write_preset(&d.join("good"), "Gain");
    write_preset(&d.join("bad"), "PitchShifter");
    assert!(VoiceFilter::preset_is_usable(&d.join("good")));
    assert!(!VoiceFilter::preset_is_usable(&d.join("bad")));
    assert!(!VoiceFilter::preset_is_usable(&d.join("missing")));
    std::fs::create_dir_all(d.join("odd")).unwrap();
    // No audio output; then a nodes list with a non-object (Swift's cast fails, so no nodes and no ports count).
    std::fs::write(
        d.join("odd/mdsp_preset.json"),
        r#"{"nodes": [{"node_id": "p", "processor_name": "Gain"}], "ports": [{"internal_node_id": "p", "port_direction": "input"}]}"#,
    )
    .unwrap();
    assert!(!VoiceFilter::preset_is_usable(&d.join("odd")));
    std::fs::write(
        d.join("odd/mdsp_preset.json"),
        r#"{"nodes": [{"node_id": "p", "processor_name": "Gain"}, 5], "ports": [
            {"internal_node_id": "p", "port_direction": "input"}, {"internal_node_id": "p", "port_direction": "output"}]}"#,
    )
    .unwrap();
    assert!(!VoiceFilter::preset_is_usable(&d.join("odd")));
}

#[test]
fn audio_effects_become_filters_or_are_reported() {
    let _english = language("en");
    let dir = TempDir::new();
    let d = dir.path();
    let media = touch(d, "a.caf");
    // A cache with one usable preset (found by resource id) and one using an unknown processor.
    let cache = d.join("cache");
    let good = cache.join("111/hash");
    let bad = cache.join("222/hash");
    write_preset(&good, "Gain");
    write_preset(&bad, "PitchShifter");
    let roots = [cache.clone()];
    assert_eq!(VoiceFilter::locate(None, "111", &roots), Some(good.clone()));
    assert_eq!(VoiceFilter::locate(Some(&cache.join("111")), "", &[]), Some(good.clone()));
    assert_eq!(VoiceFilter::locate(None, "333", &roots), None);

    let json = format!(
        r#"{{
      "materials": {{
        "audios": [{{"id": "A", "path": "{}", "type": "record"}}],
        "audio_effects": [
          {{"id": "FX-GOOD", "type": "audio_effect", "name": "Booming", "path": "{}", "resource_id": "111",
           "category_name": "Voice filters",
           "audio_adjust_params": [{{"name": "strength", "value": "0.5", "default_value": 1.0}}]}},
          {{"id": "FX-BAD", "name": "Robot", "path": "{}", "resource_id": "222"}},
          {{"id": "FX-MISSING", "name": "Old Hollywood", "path": "/nonexistent/effect", "resource_id": null}}
        ]
      }},
      "tracks": [{{"type": "audio", "segments": [
        {{"material_id": "A", "extra_material_refs": ["FX-GOOD"], "target_timerange": {{"start": 0, "duration": 100000}}}},
        {{"material_id": "A", "extra_material_refs": ["FX-BAD"], "target_timerange": {{"start": 100000, "duration": 100000}}}},
        {{"material_id": "A", "extra_material_refs": ["FX-MISSING"], "target_timerange": {{"start": 200000, "duration": 100000}}}}
      ]}}]
    }}"#,
        crate::testing::slash(&media),
        crate::testing::slash(&good),
        crate::testing::slash(&bad)
    );
    let draft = crate::draft::Draft::parse(&json).unwrap();
    assert_eq!(draft.materials.audio_effects.iter().map(|e| e.strength).collect::<Vec<_>>(), [0.5, 1.0, 1.0]);
    assert_eq!(
        draft.materials.audio_effects.iter().map(|e| e.resource_id.as_str()).collect::<Vec<_>>(),
        ["111", "222", ""]
    );
    let timeline = build_in(&json, d);
    let mut clips = timeline.audio_clips.clone();
    clips.sort_by_key(|c| c.target.start);
    let filters: Vec<Option<VoiceFilter>> = clips.into_iter().map(|c| c.voice.filter).collect();
    assert_eq!(
        filters.iter().map(|f| f.as_ref().map(|f| f.name.as_str())).collect::<Vec<_>>(),
        [Some("Booming"), None, None]
    );
    assert_eq!(filters[0].as_ref().unwrap().strength, 0.5);
    assert_eq!(filters[0].as_ref().unwrap().directory, good);
    let mut reported: Vec<&str> =
        timeline.report.unsupported.iter().filter(|i| i.category == "Audio effect").map(|i| i.name.as_str()).collect();
    reported.sort();
    assert_eq!(reported, ["Old Hollywood", "Robot"]);
}
