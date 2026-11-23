//! The canonical dumps the golden-parity runner compares (design decision 11). The same format is produced by the
//! hidden `ecc dump-timeline` / `ecc dump-writers` of the Swift and C# CLIs; see docs/DEVELOPMENT.md › Golden parity.
//!
//! One JSON object per line, fields in a fixed order; doubles as `%.6f`, integers as they are, paths inside the
//! project folder as `<project>/…`.

use std::fmt::Write as _;
use std::path::Path;

use ecc::project::{CapCutProject, ProjectTimeline};
use ecc::subtitles::CaptionCue;
use ecc::timeline::*;
use ecc::writers::subtitles::{self, SubtitleTrack};
use ecc::writers::voice_over::{self, OriginalAudio, VoiceOverTrack};
use ecc::writers::RandomIds;

/// A JSON value written by hand, so all three implementations print it the same way.
pub enum J {
    Null,
    Bool(bool),
    Int(i64),
    Num(f64),
    Str(String),
    Arr(Vec<J>),
}

impl J {
    fn write(&self, out: &mut String) {
        match self {
            J::Null => out.push_str("null"),
            J::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
            J::Int(i) => {
                let _ = write!(out, "{i}");
            }
            J::Num(d) => {
                let _ = write!(out, "{d:.6}");
            }
            J::Str(s) => out.push_str(&ecc::draft::json::json_string(s)),
            J::Arr(items) => {
                out.push('[');
                for (i, item) in items.iter().enumerate() {
                    if i > 0 {
                        out.push(',');
                    }
                    item.write(out);
                }
                out.push(']');
            }
        }
    }
}

fn s(v: &str) -> J {
    J::Str(v.to_string())
}

fn n(v: f64) -> J {
    J::Num(v)
}

fn i(v: i64) -> J {
    J::Int(v)
}

fn opt<T>(v: Option<T>, f: impl Fn(T) -> J) -> J {
    v.map_or(J::Null, f)
}

fn nums(v: &[f64]) -> J {
    J::Arr(v.iter().map(|x| n(*x)).collect())
}

fn ints(v: &[i64]) -> J {
    J::Arr(v.iter().map(|x| i(*x)).collect())
}

struct Dump<'a> {
    out: String,
    project: &'a str,
}

impl Dump<'_> {
    fn line(&mut self, fields: Vec<(&str, J)>) {
        self.out.push('{');
        for (k, (key, value)) in fields.into_iter().enumerate() {
            if k > 0 {
                self.out.push(',');
            }
            self.out.push_str(&ecc::draft::json::json_string(key));
            self.out.push(':');
            value.write(&mut self.out);
        }
        self.out.push_str("}\n");
    }

    fn path(&self, p: &Path) -> J {
        let text = p.to_string_lossy().replace('\\', "/");
        let prefix = format!("{}/", self.project);
        J::Str(match text.strip_prefix(&prefix) {
            Some(rest) => format!("<project>/{rest}"),
            None => text,
        })
    }
}

fn map(m: &TimeMap) -> J {
    J::Arr(vec![i(m.source_start), i(m.source_duration), i(m.target_duration), J::Bool(m.is_constant())])
}

fn range(r: TimeRange) -> J {
    ints(&[r.start, r.duration])
}

fn transform(t: &LayerTransform) -> J {
    J::Arr(vec![
        n(t.scale_x),
        n(t.scale_y),
        n(t.rotation),
        n(t.position_x),
        n(t.position_y),
        J::Bool(t.flip_h),
        J::Bool(t.flip_v),
        n(t.alpha),
    ])
}

fn geometry(g: &MaskGeometry) -> J {
    nums(&[g.center_x, g.center_y, g.width, g.height, g.rotation, g.feather, g.round_corner])
}

fn preset(p: ClipAnimationPreset) -> &'static str {
    use ClipAnimationPreset::*;
    match p {
        Fade => "fade",
        ZoomIn => "zoomIn",
        ZoomOut => "zoomOut",
        SlideLeft => "slideLeft",
        SlideRight => "slideRight",
        SlideUp => "slideUp",
        SlideDown => "slideDown",
        Rotate => "rotate",
        RotateCounterclockwise => "rotateCounterclockwise",
        Shake => "shake",
    }
}

fn text_animation(a: Option<&TextAnimation>) -> J {
    opt(a, |a| {
        let effect = match a.effect {
            TextAnimationEffect::Fade => "fade".to_string(),
            TextAnimationEffect::Zoom { scale, fades } => format!("zoom {scale:.6} {fades}"),
            TextAnimationEffect::Pop => "pop".into(),
            TextAnimationEffect::Slide { dx, dy } => format!("slide {dx:.6} {dy:.6}"),
            TextAnimationEffect::Typewriter => "typewriter".into(),
            TextAnimationEffect::Blink => "blink".into(),
            TextAnimationEffect::Pulse => "pulse".into(),
        };
        J::Arr(vec![J::Str(effect), i(a.duration)])
    })
}

fn transition_kind(k: TransitionKind) -> &'static str {
    match k {
        TransitionKind::Flashback => "flashback",
        TransitionKind::CrossFade => "crossFade",
    }
}

/// The frame times: 0, every clip edge ±1 µs, every keyframe ±1 µs (on the timeline), each transition's start,
/// cut and last microsecond, and 16 even steps; within 0…duration.
pub fn sample_times(t: &Timeline) -> Vec<i64> {
    let mut times = vec![0];
    for l in &t.layers {
        for edge in [l.target.start, l.target.end()] {
            times.extend([edge - 1, edge, edge + 1]);
        }
        for track in l.keyframes.values() {
            for &time in &track.times {
                let at = l.target.start + l.time_map.offset_at_source_time(time);
                times.extend([at - 1, at, at + 1]);
            }
        }
        if let Some(tr) = &l.transition_out {
            times.extend([tr.range.start, tr.range.start + tr.range.duration / 2, tr.range.end() - 1]);
        }
    }
    times.extend((0..=16).map(|k| t.duration * k / 16));
    times.retain(|&x| x >= 0 && x <= t.duration);
    times.sort_unstable();
    times.dedup();
    times
}

/// `dump-timeline`: the built timeline, its static data and its state over time.
pub fn dump_timeline(timeline: &Timeline, project: &Path) -> String {
    let folder = project.to_string_lossy().replace('\\', "/");
    let mut d = Dump { out: String::new(), project: folder.trim_end_matches('/') };
    write_timeline(&mut d, timeline);
    d.out
}

fn write_timeline(d: &mut Dump, t: &Timeline) {
    d.line(vec![
        ("timeline", s(&t.name)),
        ("canvas", ints(&[t.canvas_width as i64, t.canvas_height as i64])),
        ("fps", n(t.fps)),
        ("duration", i(t.duration)),
    ]);
    for item in &t.report.unsupported {
        d.line(vec![("unsupported", J::Arr(vec![s(&item.category), s(&item.name), i(item.count)]))]);
    }
    for file in &t.report.missing_files {
        d.line(vec![("missing", s(file))]);
    }
    for (index, l) in t.layers.iter().enumerate() {
        let li = i(index as i64);
        let content = match &l.content {
            VisualContent::Video(p) => J::Arr(vec![s("video"), d.path(p)]),
            VisualContent::Photo(p) => J::Arr(vec![s("photo"), d.path(p)]),
            VisualContent::Text(_) => J::Arr(vec![s("text")]),
            VisualContent::Compound(inner) => J::Arr(vec![s("compound"), i(inner.layers.len() as i64)]),
            VisualContent::Sticker(spec) => J::Arr(vec![s("sticker"), s(&spec.name), s(spec.clock.style.name())]),
        };
        let c = &l.clip;
        d.line(vec![
            ("layer", li),
            ("id", s(&l.id)),
            ("content", content),
            ("track", i(l.track_index)),
            ("render", i(l.render_index)),
            ("main", J::Bool(l.is_main_track)),
            ("target", range(l.target)),
            ("map", map(&l.time_map)),
            ("crop", nums(&[l.crop.upper_left.x, l.crop.upper_left.y, l.crop.lower_right.x, l.crop.lower_right.y])),
            (
                "clip",
                J::Arr(vec![
                    n(c.scale.x),
                    n(c.scale.y),
                    n(c.rotation),
                    n(c.transform.x),
                    n(c.transform.y),
                    J::Bool(c.flip_horizontal),
                    J::Bool(c.flip_vertical),
                    n(c.alpha),
                ]),
            ),
            ("uniform", J::Bool(l.uniform_scale)),
            ("tone_maps_hdr", J::Bool(l.tone_maps_hdr)),
            ("retouch", J::Bool(l.retouches_skin)),
            ("enhance", J::Bool(l.enhances_quality)),
            ("stabilization", i(l.stabilization)),
            ("blend", opt(l.blend_mode, |b| s(b.name()))),
            ("canvas", opt(l.canvas.as_ref(), |c| J::Arr(vec![s(&c.r#type), s(&c.color), n(c.blur), s(&c.image)]))),
            ("cutout", opt(l.cutout.as_ref(), |c| J::Arr(vec![d.path(&c.directory), J::Bool(c.inverted)]))),
            (
                "chroma",
                opt(l.chroma_key.as_ref(), |k| {
                    nums(&[k.red, k.green, k.blue, k.intensity, k.shadow, k.edge_smooth, k.spill])
                }),
            ),
        ]);
        for property in LayerProperty::ALL {
            if let Some(track) = l.keyframes.get(&property) {
                d.line(vec![
                    ("keyframes", i(index as i64)),
                    ("property", s(property.raw_value())),
                    ("times", ints(&track.times)),
                    ("values", nums(&track.values)),
                    ("curved", J::Bool(track.handles.is_some())),
                ]);
            }
        }
        for f in &l.filters {
            let luts = J::Arr(f.luts.iter().map(|p| d.path(p)).collect());
            d.line(vec![
                ("layer_filter", i(index as i64)),
                ("name", s(&f.name)),
                ("luts", luts),
                ("intensity", n(f.intensity)),
            ]);
        }
        for e in &l.effects {
            let kind = match e.kind {
                VideoEffectKind::PuddingBounce => "puddingBounce",
                VideoEffectKind::HazyRotation => "hazyRotation",
            };
            d.line(vec![("effect", i(index as i64)), ("kind", s(kind)), ("start", i(e.start)), ("speed", n(e.speed))]);
        }
        if let Some(tr) = &l.transition_out {
            d.line(vec![
                ("transition", i(index as i64)),
                ("name", s(&tr.name)),
                ("kind", s(transition_kind(tr.kind))),
                ("next", i(tr.next as i64)),
                ("range", range(tr.range)),
            ]);
        }
        for m in &l.masks {
            let (kind, brush) = match &m.kind {
                ClipMaskKind::Shape(shape) => (shape.name(), J::Null),
                ClipMaskKind::Brush(dir) => ("brush", d.path(dir)),
            };
            let keyframed =
                MaskProperty::ALL.iter().filter(|p| m.keyframes.contains_key(p)).map(|p| s(p.raw_value())).collect();
            d.line(vec![
                ("mask", i(index as i64)),
                ("kind", s(kind)),
                ("brush", brush),
                ("inverted", J::Bool(m.inverted)),
                ("geometry", geometry(&m.geometry)),
                ("keyframed", J::Arr(keyframed)),
            ]);
        }
        for a in &l.animations {
            let kind = if a.kind == ClipAnimationKind::In { "in" } else { "out" };
            d.line(vec![
                ("animation", i(index as i64)),
                ("kind", s(kind)),
                ("preset", s(preset(a.preset))),
                ("range", ints(&[a.start, a.end])),
            ]);
        }
        if let VisualContent::Text(spec) = &l.content {
            let m = &spec.material;
            let styles = spec
                .content
                .styles
                .iter()
                .map(|st| {
                    let r = spec.content.utf16_range(Some(st));
                    J::Arr(vec![
                        i(r.start as i64),
                        i(r.end as i64),
                        opt(st.size, n),
                        opt(st.font_path.as_deref(), s),
                        opt(st.fill_color.as_deref(), nums),
                        opt(st.fill_alpha, n),
                        i(st.strokes.len() as i64),
                        J::Bool(st.bold),
                        J::Bool(st.italic),
                        J::Bool(st.underline),
                    ])
                })
                .collect();
            d.line(vec![
                ("text", i(index as i64)),
                ("type", s(&m.r#type)),
                ("content", s(&spec.content.text)),
                ("font", s(&m.font_path)),
                ("size", n(m.font_size)),
                ("color", s(&m.text_color)),
                ("alignment", i(m.alignment)),
                ("spacing", nums(&[m.line_spacing, m.letter_spacing, m.line_max_width])),
                ("border", J::Arr(vec![s(&m.border_color), n(m.border_width)])),
                ("background", J::Arr(vec![s(&m.background_color), n(m.background_alpha)])),
                ("shadow", J::Bool(m.has_shadow)),
                ("typesetting", i(m.typesetting)),
                ("styles", J::Arr(styles)),
                ("intro", text_animation(spec.animations.intro.as_ref())),
                ("outro", text_animation(spec.animations.outro.as_ref())),
                ("loop", text_animation(spec.animations.r#loop.as_ref())),
                (
                    "glow",
                    opt(spec.glow.as_ref(), |g| {
                        let mut v: Vec<J> = g.color.iter().map(|c| n(*c)).collect();
                        v.push(n(g.radius));
                        J::Arr(v)
                    }),
                ),
            ]);
        }
        if let VisualContent::Compound(inner) = &l.content {
            d.line(vec![("compound", i(index as i64))]);
            write_timeline(d, inner);
            d.line(vec![("end", i(index as i64))]);
        }
    }
    for (k, c) in t.audio_clips.iter().enumerate() {
        let v = &c.voice;
        let separation = match v.separation {
            VoiceSeparation::None => "none",
            VoiceSeparation::RemoveVoice => "removeVoice",
            VoiceSeparation::KeepVoice => "keepVoice",
        };
        d.line(vec![
            ("audio", i(k as i64)),
            ("path", d.path(&c.path)),
            ("target", range(c.target)),
            ("map", map(&c.time_map)),
            ("volume", n(c.volume)),
            ("fade", ints(&[c.fade_in, c.fade_out])),
            ("pitch", J::Bool(c.preserve_pitch)),
            (
                "voice",
                J::Arr(vec![
                    s(separation),
                    opt(v.denoise, n),
                    opt(v.enhance, n),
                    opt(v.filter.as_ref(), |f| s(&f.name)),
                    opt(v.filter.as_ref(), |f| d.path(&f.directory)),
                    opt(v.filter.as_ref(), |f| n(f.strength)),
                ]),
            ),
        ]);
        if let Some(e) = &c.volume_envelope {
            d.line(vec![
                ("envelope", i(k as i64)),
                ("times", ints(&e.times)),
                ("values", nums(&e.values)),
                ("curved", J::Bool(e.handles.is_some())),
            ]);
        }
        // The audio renderer samples the time map every 50 ms; at most 200 samples per clip here.
        let duration = c.target.duration.max(0);
        let step = 50_000i64.max((duration + 199) / 200);
        let offsets: Vec<i64> = (0..).map(|k| k * step).take_while(|o| *o <= duration).collect();
        d.line(vec![("samples", i(k as i64)), ("step", i(step)), ("source", ints(&c.time_map.source_times(&offsets)))]);
    }
    for (j, f) in t.filters.iter().enumerate() {
        let luts = J::Arr(f.filter.luts.iter().map(|p| d.path(p)).collect());
        d.line(vec![
            ("filter", i(j as i64)),
            ("id", s(&f.id)),
            ("target", range(f.target)),
            ("track", i(f.track_index)),
            ("render", i(f.render_index)),
            ("name", s(&f.filter.name)),
            ("luts", luts),
            ("intensity", n(f.filter.intensity)),
        ]);
    }
    for time in sample_times(t) {
        let frame = t.frame(time);
        d.line(vec![("t", i(time)), ("filters", J::Arr(frame.filters.iter().map(|x| i(*x as i64)).collect()))]);
        for f in &frame.layers {
            d.line(vec![
                ("f", i(f.index as i64)),
                ("offset", i(f.offset)),
                ("source", i(f.source_time)),
                ("tr", transform(&f.transform)),
                ("text", opt(f.text, |x| nums(&[x.alpha, x.scale, x.offset_x, x.offset_y, x.reveal]))),
                ("fx", opt(f.effect, |x| nums(&[x.zoom, x.rotation, x.blur, x.flash]))),
                ("masks", J::Arr(f.masks.iter().map(geometry).collect())),
                (
                    "tx",
                    opt(f.transition, |x| J::Arr(vec![i(x.next as i64), s(transition_kind(x.kind)), n(x.progress)])),
                ),
                ("clock", opt(f.clock_hands, |(m, h)| nums(&[m, h]))),
            ]);
        }
    }
}

// MARK: - Writers

/// The fixed tracks every writer dump writes.
pub fn caption_tracks() -> Vec<SubtitleTrack> {
    let track = |language: &str, cues: Vec<CaptionCue>, is_original: bool, is_visible: bool| SubtitleTrack {
        language: language.into(),
        cues,
        is_original,
        is_visible,
    };
    vec![
        track(
            "ru",
            vec![CaptionCue::new(0.5, 2.0, "Привет, мир"), CaptionCue::new(2.0, 3.01, "Вторая\nстрока 😳")],
            true,
            false,
        ),
        track("en", vec![CaptionCue::new(0.5, 2.0, "Hello, world")], false, true),
        track("th", vec![CaptionCue::new(1.234, 4.5, "สวัสดีครับ ทุกคน")], false, true),
        track("zh-Hans", vec![CaptionCue::new(5.0, 6.0, "你好")], false, false),
    ]
}

pub fn voice_tracks(project: &Path) -> Vec<VoiceOverTrack> {
    let folder = project.to_string_lossy();
    let track = |language: &str, path: String, lines: Vec<(f64, f64)>, is_audible: bool, is_background: bool| {
        VoiceOverTrack { language: language.into(), path, file_duration: 10.0, lines, is_audible, is_background }
    };
    vec![
        track(
            "en",
            format!("{folder}/EnhanceCapCut/voiceover 1/en.wav"),
            vec![(1.0, 2.5), (3.0, 4.0), (7.0, 9.25)],
            true,
            false,
        ),
        track("th", "/elsewhere/th.wav".into(), vec![(1.0, 2.0)], false, false),
        track("en", format!("{folder}/bg.wav"), vec![(0.75, 4.4)], true, true),
    ]
}

/// The fixed `now` of the caption writer: 1_700_000_000.5 s.
pub const NOW_MS: i64 = 1_700_000_000_500;

fn is_uuid(b: &[u8]) -> bool {
    b.len() == 36
        && b.iter().enumerate().all(|(k, c)| {
            if [8, 13, 18, 23].contains(&k) {
                *c == b'-'
            } else {
                c.is_ascii_digit() || (b'A'..=b'F').contains(c)
            }
        })
}

/// UUIDs (upper case, as the writers make them) replaced by `<id1>`, `<id2>`… in order of first appearance; ids in
/// `keep` (those already in the input draft) stay. The project folder becomes `<project>`.
pub fn normalize(output: &[u8], keep: &[Vec<u8>], project: &str) -> String {
    let mut seen: Vec<Vec<u8>> = Vec::new();
    let mut out: Vec<u8> = Vec::with_capacity(output.len());
    let mut k = 0;
    while k < output.len() {
        if k + 36 <= output.len() && is_uuid(&output[k..k + 36]) {
            let id = output[k..k + 36].to_vec();
            if keep.contains(&id) {
                out.extend_from_slice(&id);
            } else {
                let n = match seen.iter().position(|x| *x == id) {
                    Some(p) => p + 1,
                    None => {
                        seen.push(id);
                        seen.len()
                    }
                };
                out.extend_from_slice(format!("<id{n}>").as_bytes());
            }
            k += 36;
        } else {
            out.push(output[k]);
            k += 1;
        }
    }
    String::from_utf8_lossy(&out).replace(project, "<project>")
}

/// The UUIDs already in a draft.
pub fn uuids_in(bytes: &[u8]) -> Vec<Vec<u8>> {
    let mut ids = Vec::new();
    let mut k = 0;
    while k + 36 <= bytes.len() {
        if is_uuid(&bytes[k..k + 36]) {
            ids.push(bytes[k..k + 36].to_vec());
            k += 36;
        } else {
            k += 1;
        }
    }
    ids
}

/// `dump-writers`: the caption and voice-over writers run on the timeline's draft with the fixed tracks.
pub fn dump_writers(project: &CapCutProject, timeline: &ProjectTimeline) -> Result<String, String> {
    let draft = std::fs::read(&timeline.draft_path).map_err(|e| e.to_string())?;
    let keep = uuids_in(&draft);
    let folder = project.path.to_string_lossy().replace('\\', "/");
    let ids = &mut RandomIds;
    let mut out = String::new();
    // In a JSON string a Windows folder's backslashes are escaped.
    let escaped = project.path.to_string_lossy().replace('\\', "\\\\");
    let mut section = |title: &str, bytes: &[u8]| {
        let text = normalize(bytes, &keep, &folder).replace(&escaped, "<project>");
        out += &format!("== {title}\n{text}\n");
    };
    let captions = caption_tracks();
    let once = subtitles::apply(&captions, &draft, true, ids, NOW_MS).map_err(|e| e.to_string())?;
    section("captions", &once);
    let twice = subtitles::apply(&captions, &once, true, ids, NOW_MS).map_err(|e| e.to_string())?;
    section("captions again", &twice);
    let voice = voice_tracks(&project.path);
    let mut last = Vec::new();
    for mode in OriginalAudio::ALL {
        let written =
            voice_over::apply(&voice, &draft, mode, true, Some(&project.path), ids).map_err(|e| e.to_string())?;
        section(&format!("voice-over {}", mode.name_token()), &written);
        last = written;
    }
    let without = voice_over::without_voice_over(&last, ids).map_err(|e| e.to_string())?;
    section("without voice-over", &without);
    Ok(out)
}
