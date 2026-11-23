//! Draft → `Timeline`, the port of `TimelineBuilder.swift`, including the report of what isn't rendered.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use super::*;
use crate::draft::{
    AudioEffectMaterial, CombinationMaterial, Draft, GenericMaterial, MaskMaterial, Matting, Segment, SpeedMaterial,
    VideoMaterial,
};
use crate::env::CoreEnvironment;
use crate::project::{CapCutProject, DraftLoader, LoadError, ProjectTimeline};
use crate::tr;

pub struct TimelineBuilder;

impl TimelineBuilder {
    /// Builds `timeline` of the project, or the root draft (the main timeline) when it's `None`.
    pub fn build_project(
        project: &CapCutProject,
        timeline: Option<&ProjectTimeline>,
        env: &CoreEnvironment,
    ) -> Result<Timeline, LoadError> {
        Ok(Self::build(&project.load_draft(timeline)?, &project.path, env))
    }

    pub fn build(draft: &Draft, project: &Path, env: &CoreEnvironment) -> Timeline {
        let mut builder = Builder::new(project, &env.capcut_cache_roots);
        builder.build(draft, 0)
    }
}

/// `effects` "figure" sub-type of CapCut's automatic skin retouch ("Sparkly Skin", smoothing).
const SKIN_RETOUCH_SUB_TYPE: &str = "auto_beauty";
/// `video_algorithm.algorithms[].type` of CapCut's AI "Enhance quality".
const QUALITY_ENHANCE: &str = "QualityEnhance";

const IGNORED: [&str; 22] = [
    "speeds",
    "placeholder_infos",
    "sound_channel_mappings",
    "material_colors",
    "canvases",
    "beats",
    "loudnesses",
    "audio_fades",
    "drafts",
    "videos",
    "audios",
    "texts",
    "ai_translates",
    "smart_crops",
    "realtime_denoises_placeholder",
    // Rendered as clip masks (what can't be drawn is noted in make_masks).
    "common_mask",
    "masks",
    // Rendered by VoiceProcessor (voice filters whose preset can't be run are noted where they're read).
    "vocal_separations",
    "realtime_denoises",
    "vocal_beautifys",
    "audio_effects",
    // Rendered as the clip's chroma key.
    "chromas",
];

/// Foundation's `capitalized`: the first letter of every word upper case, the other letters lower case; a word
/// starts at a letter that doesn't follow a letter.
pub(crate) fn capitalized(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut previous_is_letter = false;
    for c in s.chars() {
        if c.is_alphabetic() {
            if previous_is_letter {
                out.extend(c.to_lowercase());
            } else {
                out.extend(c.to_uppercase());
            }
            previous_is_letter = true;
        } else {
            out.push(c);
            previous_is_letter = false;
        }
    }
    out
}

/// Maps ids to the first material with that id (`Dictionary(…, uniquingKeysWith: { a, _ in a })`).
fn by_id<T>(items: &[T], id: impl Fn(&T) -> &str) -> HashMap<String, &T> {
    let mut map = HashMap::new();
    for item in items {
        map.entry(id(item).to_string()).or_insert(item);
    }
    map
}

/// The first ref that maps to something.
fn first<'a, T>(refs: &[String], map: &HashMap<String, &'a T>) -> Option<&'a T> {
    refs.iter().find_map(|r| map.get(r).copied())
}

fn clamp01(v: f64) -> f64 {
    v.clamp(0.0, 1.0)
}

/// A layer as it's built, before the layers are sorted: its transition still points at the creation order.
struct Built {
    layer: VisualLayer,
    order: usize,
    /// (transition, creation index of the incoming layer).
    transition: Option<(LayerTransition, usize)>,
}

struct Builder<'a> {
    project: &'a Path,
    cache_roots: &'a [PathBuf],
    unsupported: BTreeMap<(String, String), i64>,
    missing: BTreeSet<String>,
    /// Nested drafts of compound clips, built once per combination material.
    nested_timelines: HashMap<String, Arc<Timeline>>,
    combos: HashMap<String, CombinationMaterial>,
    /// `video_effects` materials rendered on the clips that use them (not reported).
    rendered_effects: BTreeSet<String>,
    /// Voice filter preset directories by material path and resource id (`None`: not usable).
    filter_directories: HashMap<String, Option<PathBuf>>,
    /// Colour filters by material id (`None`: lookup table not found).
    color_filters: HashMap<String, Option<ColorFilter>>,
}

impl<'a> Builder<'a> {
    fn new(project: &'a Path, cache_roots: &'a [PathBuf]) -> Builder<'a> {
        Builder {
            project,
            cache_roots,
            unsupported: BTreeMap::new(),
            missing: BTreeSet::new(),
            nested_timelines: HashMap::new(),
            combos: HashMap::new(),
            rendered_effects: BTreeSet::new(),
            filter_directories: HashMap::new(),
            color_filters: HashMap::new(),
        }
    }

    fn resolve(&self, path: &str) -> Option<PathBuf> {
        DraftLoader::resolve_path(path, self.project)
    }

    fn exists(&self, path: &str) -> Option<PathBuf> {
        self.resolve(path).filter(|p| p.exists())
    }

    fn build(&mut self, draft: &Draft, depth: usize) -> Timeline {
        let m = &draft.materials;
        let videos = by_id(&m.videos, |v| &v.id);
        let audios = by_id(&m.audios, |v| &v.id);
        let texts = by_id(&m.texts, |v| &v.id);
        let canvases = by_id(&m.canvases, |v| &v.id);
        let speeds = by_id(&m.speeds, |v| &v.id);
        let fades = by_id(&m.audio_fades, |v| &v.id);
        // Nested drafts may reference combinations stored in an outer draft.
        for combo in &m.drafts {
            self.combos.entry(combo.id.clone()).or_insert_with(|| combo.clone());
        }
        let loudness = by_id(&m.loudnesses, |v| &v.id);
        let separations = by_id(&m.vocal_separations, |v| &v.id);
        let denoises = by_id(&m.realtime_denoises, |v| &v.id);
        let enhancements = by_id(&m.vocal_beautifys, |v| &v.id);
        let audio_effects = by_id(&m.audio_effects, |v| &v.id);
        let chromas = by_id(&m.chromas, |v| &v.id);
        let mut video_effects: HashMap<String, VideoEffect> = HashMap::new();
        for e in &m.video_effects {
            if let Some(effect) = VideoEffect::from_material(e) {
                video_effects.entry(e.id.clone()).or_insert(effect);
            }
        }
        self.rendered_effects.extend(video_effects.keys().cloned());
        let animations = by_id(&m.material_animations, |v| &v.id);
        let mask_materials = by_id(&m.masks, |v| &v.id);
        let mut generic: HashMap<String, (String, GenericMaterial)> = HashMap::new();
        for (key, items) in &m.all {
            for item in items {
                generic.insert(item.id.clone(), (key.clone(), item.clone()));
            }
        }

        let main_track = draft.tracks.iter().position(|t| t.r#type == "video");
        let mut layers: Vec<Built> = Vec::new();
        let mut filter_layers: Vec<FilterLayer> = Vec::new();
        let mut clips: Vec<AudioClip> = Vec::new();
        let mut order = 0;

        for (track_index, track) in draft.tracks.iter().enumerate() {
            // The previous clip on this track (its creation index) and the transition it ends with.
            let mut previous: Option<(usize, Option<GenericMaterial>)> = None;
            for segment in &track.segments {
                order += 1;
                let refs = &segment.extra_material_refs;
                let speed = first(refs, &speeds);
                let fade = first(refs, &fades);
                let gain = 10f64.powf(first(refs, &loudness).map_or(0.0, |l| l.gain_db()) / 20.0);
                let mut time_map = make_time_map(segment, speed);
                let mut voice = VoiceEffects::default();
                if let Some(choice) = first(refs, &separations).map(|s| s.choice) {
                    voice.separation = VoiceSeparation::from_choice(choice).unwrap_or_default();
                    if voice.separation == VoiceSeparation::None && choice != 0 {
                        self.note(&tr!("Audio"), &format!("vocal separation mode {choice}"));
                    }
                }
                if let Some(denoise) = first(refs, &denoises).filter(|d| d.is_denoise) {
                    voice.denoise = Some(clamp01(denoise.rate));
                }
                if let Some(enhance) = first(refs, &enhancements).filter(|e| e.enable) {
                    voice.enhance = Some(clamp01(enhance.ambient_sound_level / 100.0));
                }
                // A voice filter we can't run (preset not cached, unknown processor) is reported instead.
                let mut filter_issue: Option<String> = None;
                if let Some(effect) = first(refs, &audio_effects) {
                    if let Some(directory) = self.voice_filter_directory(effect) {
                        voice.filter = Some(VoiceFilter {
                            name: effect.name.clone(),
                            directory,
                            strength: clamp01(effect.strength),
                        });
                    } else {
                        filter_issue =
                            Some(if effect.name.is_empty() { effect.resource_id.clone() } else { effect.name.clone() });
                    }
                }
                let report_all = |this: &mut Self| {
                    // Text and clip animations are rendered; the ones that aren't are noted where the layer is built.
                    let animated = track.r#type == "text" || track.r#type == "video";
                    let reported: Vec<String> = if animated {
                        refs.iter()
                            .filter(|r| generic.get(*r).map(|g| g.0.as_str()) != Some("material_animations"))
                            .cloned()
                            .collect()
                    } else {
                        refs.clone()
                    };
                    this.report_refs(&reported, &generic);
                    if let Some(issue) = &filter_issue {
                        this.note(&tr!("Audio effect"), issue);
                    }
                };
                if track.r#type != "audio" {
                    report_all(self);
                }

                match track.r#type.as_str() {
                    "video" => {
                        if track.is_hidden() || !segment.visible {
                            continue;
                        }
                        let is_main = Some(track_index) == main_track;
                        let video = videos.get(&segment.material_id).copied();
                        let mut content: Option<VisualContent> = None;
                        let mut audio_path: Option<PathBuf> = None;
                        let mut compound_audio: Vec<AudioClip> = Vec::new();

                        let combo = refs.iter().find_map(|r| self.combos.get(r).cloned());
                        if let Some(timeline) = combo.and_then(|c| self.nested_timeline(&c, depth)) {
                            compound_audio = timeline.audio_clips.clone();
                            content = Some(VisualContent::Compound(timeline));
                        } else if let Some(video) = video {
                            let mut path = video.path.clone();
                            if segment.reverse && video.r#type != "photo" {
                                // CapCut renders a fully reversed copy into the project folder; the segment's source
                                // range is in that copy's time base.
                                if self.exists(&video.reverse_path).is_some() {
                                    path = video.reverse_path.clone();
                                } else {
                                    self.note(
                                        &tr!("Reverse playback"),
                                        &tr!("reversed clip without CapCut's reversed copy (rendered forward)"),
                                    );
                                    time_map = mirrored(time_map, video.duration);
                                }
                            }
                            if let Some(url) = self.exists(&path) {
                                if video.r#type != "photo" && video.has_audio && !video.has_sound_separated {
                                    audio_path = Some(url.clone());
                                }
                                content = Some(if video.r#type == "photo" {
                                    VisualContent::Photo(url)
                                } else {
                                    VisualContent::Video(url)
                                });
                            } else {
                                self.missing.insert(if video.path.is_empty() {
                                    video.name.clone()
                                } else {
                                    video.path.clone()
                                });
                            }
                        }
                        let Some(content) = content else { continue };
                        self.report_keyframes(segment);
                        if segment.is_loop {
                            self.note(&tr!("Playback"), &tr!("loop"));
                        }
                        if let Some(video) = video {
                            self.report_video(video);
                        }
                        // A compound clip's material is its rendered video; a cutout on it uses the compound's time.
                        let cutout = video.filter(|v| v.matting.flag != 0).and_then(|v| self.make_cutout(&v.matting));
                        let mask_list: Vec<&MaskMaterial> =
                            refs.iter().filter_map(|r| mask_materials.get(r).copied()).collect();
                        let masks = self.make_masks(&mask_list, segment);
                        let canvas = first(refs, &canvases);
                        if is_main {
                            if let Some(canvas) = canvas {
                                if !["canvas_color", "canvas_blur", "canvas_image"].contains(&canvas.r#type.as_str()) {
                                    self.note(&tr!("Canvas"), &canvas.r#type);
                                }
                            }
                        }
                        let (clip_animations, unsupported_animations) =
                            ClipAnimation::make(first(refs, &animations), segment.target_timerange.duration);
                        for name in unsupported_animations {
                            self.note(&tr!("Animation"), &name);
                        }
                        let filters: Vec<ColorFilter> = refs
                            .iter()
                            .filter_map(|r| generic.get(r))
                            .filter(|(key, item)| key == "effects" && item.r#type == "filter")
                            .map(|(_, item)| item.clone())
                            .collect::<Vec<_>>()
                            .iter()
                            .filter_map(|item| self.color_filter(item))
                            .collect();
                        let mut stabilization = 0;
                        if let Some(level) = video.map(|v| v.stable_level).filter(|&l| l > 0) {
                            // Stabilized by us on video clips; a compound clip's rendered video isn't analysed yet.
                            if matches!(content, VisualContent::Video(_)) {
                                stabilization = level;
                            } else {
                                self.note(&tr!("Stabilization"), &format!("level {level}"));
                            }
                        }
                        let layer = VisualLayer {
                            id: segment.id.clone(),
                            content,
                            track_index: track_index as i64,
                            render_index: segment.render_index,
                            is_main_track: is_main,
                            target: segment.target_timerange,
                            time_map: time_map.clone(),
                            crop: video.map(|v| v.crop).unwrap_or_default(),
                            clip: segment.clip.clone().unwrap_or_default(),
                            uniform_scale: segment.uniform_scale,
                            keyframes: keyframes(segment),
                            canvas: if is_main { canvas.cloned() } else { None },
                            cutout,
                            filters,
                            tone_maps_hdr: segment.hdr_mode != Some(0),
                            chroma_key: first(refs, &chromas).and_then(ChromaKey::from_material),
                            retouches_skin: refs.iter().any(|r| {
                                generic.get(r).is_some_and(|(key, item)| {
                                    key == "effects"
                                        && item.r#type == "figure"
                                        && item.sub_type == SKIN_RETOUCH_SUB_TYPE
                                })
                            }),
                            enhances_quality: video.is_some_and(|v| v.algorithms.iter().any(|a| a == QUALITY_ENHANCE)),
                            effects: refs.iter().filter_map(|r| video_effects.get(r).copied()).collect(),
                            transition_out: None,
                            masks,
                            animations: clip_animations,
                            blend_mode: blend_mode(refs, &generic),
                            stabilization,
                        };
                        let index = layers.len();
                        if let Some((from, Some(material))) = &previous {
                            let from_target = layers[*from].layer.target;
                            layers[*from].transition = LayerTransition::make(
                                &material.name,
                                &material.resource_id,
                                material.duration,
                                from_target,
                                layer.target,
                                index,
                            )
                            .map(|t| (t, index));
                        }
                        previous = Some((
                            index,
                            refs.iter()
                                .filter_map(|r| generic.get(r))
                                .find(|g| g.0 == "transitions")
                                .map(|g| g.1.clone()),
                        ));
                        layers.push(Built { layer, order, transition: None });

                        let muted = track.is_muted() || (is_main && draft.config.video_mute) || segment.volume <= 0.0;
                        if !muted {
                            if let Some(path) = audio_path {
                                clips.push(AudioClip {
                                    path,
                                    target: segment.target_timerange,
                                    time_map: time_map.clone(),
                                    volume: segment.volume * gain,
                                    fade_in: fade.map_or(0, |f| f.fade_in),
                                    fade_out: fade.map_or(0, |f| f.fade_out),
                                    preserve_pitch: !segment.is_tone_modify,
                                    volume_envelope: volume_envelope(segment),
                                    voice: voice.clone(),
                                });
                            }
                        }
                        // A compound whose sound was detached plays it from the audio track instead.
                        if !muted && !compound_audio.is_empty() && video.map(|v| v.has_sound_separated) != Some(true) {
                            clips.extend(nest_audio(
                                &compound_audio,
                                segment,
                                &time_map,
                                segment.volume * gain,
                                volume_envelope(segment).as_ref(),
                                &voice,
                            ));
                        }
                    }

                    "audio" => {
                        if track.is_muted() || segment.volume <= 0.0 {
                            continue;
                        }
                        report_all(self);
                        self.report_keyframes(segment);
                        let combo = refs.iter().find_map(|r| self.combos.get(r).cloned());
                        if let Some(timeline) = combo.and_then(|c| self.nested_timeline(&c, depth)) {
                            // Audio detached from a compound clip.
                            clips.extend(nest_audio(
                                &timeline.audio_clips,
                                segment,
                                &time_map,
                                segment.volume * gain,
                                volume_envelope(segment).as_ref(),
                                &voice,
                            ));
                            continue;
                        }
                        let Some(audio) = audios.get(&segment.material_id) else { continue };
                        let Some(path) = self.exists(&audio.path) else {
                            self.missing.insert(if audio.path.is_empty() {
                                audio.name.clone()
                            } else {
                                audio.path.clone()
                            });
                            continue;
                        };
                        clips.push(AudioClip {
                            path,
                            target: segment.target_timerange,
                            time_map,
                            volume: segment.volume * gain,
                            fade_in: fade.map_or(0, |f| f.fade_in),
                            fade_out: fade.map_or(0, |f| f.fade_out),
                            preserve_pitch: !segment.is_tone_modify,
                            volume_envelope: volume_envelope(segment),
                            voice,
                        });
                    }

                    "text" => {
                        if track.is_hidden() || !segment.visible {
                            continue;
                        }
                        let Some(text) = texts.get(&segment.material_id) else { continue };
                        let Some(parsed) = TextContent::parse(&text.content) else { continue };
                        if parsed.text.trim().is_empty() {
                            continue;
                        }
                        if text.curve_enabled {
                            self.note(&tr!("Text"), &tr!("curved text (rendered straight)"));
                        }
                        if parsed.styles.iter().any(|s| s.strokes.len() > 1) {
                            self.note(&tr!("Text"), &tr!("multiple outlines (first one rendered)"));
                        }
                        // A text effect applies only while a style carries its `effectStyle` (see `TextEffectLook`).
                        let mut glow = None;
                        if let Some(styled) =
                            parsed.styles.iter().find(|s| !s.effect_style_path.as_deref().unwrap_or("").is_empty())
                        {
                            let effect = refs
                                .iter()
                                .filter_map(|r| generic.get(r))
                                .map(|g| &g.1)
                                .find(|g| g.r#type == "text_effect");
                            if let Some(effect) = effect {
                                glow = TextEffectLook::glow(&effect.name, styled.fill_color.as_deref());
                                if glow.is_none() {
                                    let name =
                                        if effect.name.is_empty() { tr!("word art") } else { effect.name.clone() };
                                    self.note(&tr!("Text effect"), &name);
                                }
                            } else {
                                self.note(&tr!("Text effect"), &tr!("word art"));
                            }
                        }
                        self.report_keyframes(segment);
                        let (text_animations, unsupported_animations) = TextAnimations::make(first(refs, &animations));
                        for name in unsupported_animations {
                            self.note(&tr!("Animation"), &name);
                        }
                        let layer = VisualLayer {
                            id: segment.id.clone(),
                            content: VisualContent::Text(Box::new(TextLayerSpec {
                                material: (*text).clone(),
                                content: parsed,
                                animations: text_animations,
                                glow,
                            })),
                            track_index: track_index as i64,
                            render_index: segment.render_index,
                            is_main_track: false,
                            target: segment.target_timerange,
                            time_map,
                            crop: Crop::default(),
                            clip: segment.clip.clone().unwrap_or_default(),
                            uniform_scale: segment.uniform_scale,
                            keyframes: keyframes(segment),
                            canvas: None,
                            cutout: None,
                            filters: Vec::new(),
                            tone_maps_hdr: true,
                            chroma_key: None,
                            retouches_skin: false,
                            enhances_quality: false,
                            effects: Vec::new(),
                            transition_out: None,
                            masks: Vec::new(),
                            animations: Vec::new(),
                            blend_mode: blend_mode(refs, &generic),
                            stabilization: 0,
                        };
                        layers.push(Built { layer, order, transition: None });
                    }

                    _ => {
                        // Stickers, effects and filters on their own tracks.
                        if track.is_hidden() {
                            continue;
                        }
                        let entry = generic.get(&segment.material_id);
                        // "Clock Time" stickers are drawn natively; other stickers are reported.
                        let clock = entry
                            .filter(|(key, _)| track.r#type == "sticker" && segment.visible && key == "stickers")
                            .and_then(|(_, item)| ClockSticker::style(&item.resource_id).map(|s| (item, s)));
                        if let Some((item, style)) = clock {
                            self.report_keyframes(segment);
                            let layer = VisualLayer {
                                id: segment.id.clone(),
                                content: VisualContent::Sticker(StickerSpec {
                                    name: item.name.clone(),
                                    clock: ClockSticker { style },
                                }),
                                track_index: track_index as i64,
                                render_index: segment.render_index,
                                is_main_track: false,
                                target: segment.target_timerange,
                                time_map,
                                crop: Crop::default(),
                                clip: segment.clip.clone().unwrap_or_default(),
                                uniform_scale: segment.uniform_scale,
                                keyframes: keyframes(segment),
                                canvas: None,
                                cutout: None,
                                filters: Vec::new(),
                                tone_maps_hdr: true,
                                chroma_key: None,
                                retouches_skin: false,
                                enhances_quality: false,
                                effects: Vec::new(),
                                transition_out: None,
                                masks: Vec::new(),
                                animations: Vec::new(),
                                blend_mode: None,
                                stabilization: 0,
                            };
                            layers.push(Built { layer, order, transition: None });
                        } else if let Some((key, item)) = entry {
                            self.report(key, item);
                            if key == "effects" && item.r#type == "filter" && segment.visible {
                                if let Some(filter) = self.color_filter(item) {
                                    filter_layers.push(FilterLayer {
                                        id: segment.id.clone(),
                                        target: segment.target_timerange,
                                        track_index: track_index as i64,
                                        render_index: segment.render_index,
                                        filter,
                                    });
                                }
                            }
                        } else {
                            self.note(&capitalized(&track.r#type), "segment");
                        }
                    }
                }
            }
        }

        // Sort into paint order; transitions follow their layers to the new indices.
        let mut sorted: Vec<usize> = (0..layers.len()).collect();
        sorted.sort_by(|&a, &b| {
            let (a, b) = (&layers[a], &layers[b]);
            (a.layer.render_index, a.layer.track_index, a.order).cmp(&(
                b.layer.render_index,
                b.layer.track_index,
                b.order,
            ))
        });
        let mut position = vec![0usize; layers.len()];
        for (new, &old) in sorted.iter().enumerate() {
            position[old] = new;
        }
        let mut slots: Vec<Option<Built>> = layers.into_iter().map(Some).collect();
        let layers: Vec<VisualLayer> = sorted
            .iter()
            .map(|&old| {
                let built = slots[old].take().expect("each layer once");
                let mut layer = built.layer;
                layer.transition_out = built.transition.map(|(mut t, next)| {
                    t.next = position[next];
                    t
                });
                layer
            })
            .collect();

        let end = draft
            .duration
            .max(layers.iter().map(|l| l.target.end()).max().unwrap_or(0))
            .max(clips.iter().map(|c| c.target.end()).max().unwrap_or(0));
        let report = RenderReport {
            unsupported: self
                .unsupported
                .iter()
                .map(|((category, name), count)| ReportItem {
                    category: category.clone(),
                    name: name.clone(),
                    count: *count,
                })
                .collect(),
            missing_files: self.missing.iter().cloned().collect(),
        };
        Timeline {
            name: draft.name.clone(),
            canvas_width: draft.canvas.width as f64,
            canvas_height: draft.canvas.height as f64,
            duration: end,
            fps: if draft.fps > 0.0 { draft.fps } else { 30.0 },
            layers,
            audio_clips: clips,
            report,
            filters: filter_layers,
        }
    }

    fn nested_timeline(&mut self, combo: &CombinationMaterial, depth: usize) -> Option<Arc<Timeline>> {
        if let Some(cached) = self.nested_timelines.get(&combo.id) {
            return Some(cached.clone());
        }
        let draft = combo.draft.as_ref()?;
        if depth >= 8 {
            return None;
        }
        let timeline = Arc::new(self.build(draft, depth + 1));
        self.nested_timelines.insert(combo.id.clone(), timeline.clone());
        Some(timeline)
    }

    /// The cached preset directory for a voice filter, or `None` when it's missing or uses a processor the native
    /// `VoiceFilterGraph` doesn't implement.
    fn voice_filter_directory(&mut self, effect: &AudioEffectMaterial) -> Option<PathBuf> {
        let key = format!("{}|{}", effect.path, effect.resource_id);
        if let Some(cached) = self.filter_directories.get(&key) {
            return cached.clone();
        }
        let path = self.resolve(&effect.path);
        let directory = VoiceFilter::locate(path.as_deref(), &effect.resource_id, self.cache_roots)
            .filter(|dir| VoiceFilter::preset_is_usable(dir));
        self.filter_directories.insert(key, directory.clone());
        directory
    }

    /// A filter's lookup table from CapCut's effect cache (`None` when the package isn't cached or has none).
    fn color_filter(&mut self, item: &GenericMaterial) -> Option<ColorFilter> {
        if let Some(cached) = self.color_filters.get(&item.id) {
            return cached.clone();
        }
        let path = self.resolve(&item.path);
        let luts = ColorFilter::locate(path.as_deref(), &item.resource_id, self.cache_roots);
        let filter = (!luts.is_empty()).then(|| ColorFilter {
            name: item.name.clone(),
            luts,
            intensity: clamp01(item.value.unwrap_or(1.0)),
        });
        self.color_filters.insert(item.id.clone(), filter.clone());
        filter
    }

    // MARK: Report

    fn note(&mut self, category: &str, name: &str) {
        *self.unsupported.entry((category.to_string(), name.to_string())).or_insert(0) += 1;
    }

    fn report_refs(&mut self, refs: &[String], generic: &HashMap<String, (String, GenericMaterial)>) {
        for r in refs {
            if let Some((key, item)) = generic.get(r) {
                self.report(key, item);
            }
        }
    }

    /// Keyframed properties the renderer doesn't animate. Unknown mask properties are one entry.
    fn report_keyframes(&mut self, segment: &Segment) {
        let mut names = BTreeSet::new();
        for group in segment.keyframes.iter().filter(|g| !g.keyframes.is_empty()) {
            let kind = &group.property_type;
            if LayerProperty::parse(kind).is_some() {
                continue;
            }
            if MaskProperty::is_mask(kind) {
                // Known mask properties are animated with their mask (see make_masks).
                if MaskProperty::from_property_type(kind).is_none() {
                    names.insert("mask animation".to_string());
                }
            } else {
                names.insert(kind.strip_prefix("KFType").unwrap_or(kind).to_string());
            }
        }
        for name in names {
            self.note(&tr!("Keyframes"), &name);
        }
    }

    /// Per-clip processing stored on the video material itself.
    fn report_video(&mut self, video: &VideoMaterial) {
        if video.r#type != "video" && video.r#type != "photo" {
            self.note(&tr!("Media"), &format!("{} clip", video.r#type));
        }
        if video.beauty_mode {
            self.note(&tr!("Beauty / makeup"), &tr!("retouch"));
        }
        // AI-generated images are already baked into the material's file. "Enhance quality" is approximated by
        // QualityEnhance.
        let algorithms: BTreeSet<&String> = video.algorithms.iter().collect();
        for algorithm in algorithms {
            if !algorithm.starts_with("AigcTypeGen") && algorithm != QUALITY_ENHANCE {
                self.note(&tr!("AI processing"), algorithm);
            }
        }
    }

    /// CapCut's cached "Remove background" result; what can't be reproduced is reported.
    fn make_cutout(&mut self, matting: &Matting) -> Option<Cutout> {
        if matting.expansion != 0.0 || matting.feather != 0.0 {
            self.note(&tr!("Remove background"), &tr!("edge expansion or feather"));
        }
        if matting.stroke {
            self.note(&tr!("Remove background"), &tr!("stroke"));
        }
        let Some(folder) = self.exists(&matting.path) else {
            self.note(&tr!("Remove background"), &tr!("result not saved in the project"));
            return None;
        };
        // A custom cutout without its own result (no subject picked yet) shows the whole clip in CapCut.
        let custom = matting.flag == 1;
        let Some(directory) = Cutout::locate(&folder, custom.then_some(matting.custom_id.as_str())) else {
            if !custom {
                self.note(&tr!("Remove background"), &tr!("result not saved in the project"));
            }
            return None;
        };
        Some(Cutout { directory, inverted: matting.reverse })
    }

    /// A video clip's masks; what can't be reproduced is reported.
    fn make_masks(&mut self, materials: &[&MaskMaterial], segment: &Segment) -> Vec<ClipMask> {
        let mut result = Vec::new();
        for mask in materials {
            // Keyframes name their mask by its constant id; older drafts with one mask may leave it out.
            let mut keyframes: BTreeMap<MaskProperty, KeyframeTrack> = BTreeMap::new();
            for group in &segment.keyframes {
                if MaskProperty::is_mask(&group.property_type)
                    && (group.material_id == mask.constant_id || (group.material_id.is_empty() && materials.len() == 1))
                {
                    if let (Some(property), Some(track)) =
                        (MaskProperty::from_property_type(&group.property_type), KeyframeTrack::from_group(group))
                    {
                        keyframes.insert(property, track);
                    }
                }
            }
            let animated = |p: MaskProperty, value: f64| {
                value != 0.0 || keyframes.get(&p).is_some_and(|k| k.values.iter().any(|v| *v != 0.0))
            };
            if animated(MaskProperty::Expansion, mask.expansion) {
                self.note(&tr!("Mask"), &tr!("expansion"));
            }
            let geometry = MaskGeometry {
                center_x: mask.center_x,
                center_y: mask.center_y,
                width: mask.width,
                height: mask.height,
                rotation: mask.rotation,
                feather: mask.feather,
                round_corner: mask.round_corner,
            };
            let kind = if mask.resource_type == "custom" {
                let folder = self.resolve(&mask.loader_work_space).filter(|f| !looks::brush_frame_times(f).is_empty());
                let Some(folder) = folder else {
                    self.note(&tr!("Mask"), &tr!("brush strokes not saved in the project"));
                    continue;
                };
                if animated(MaskProperty::Feather, mask.feather) {
                    self.note(&tr!("Mask"), &tr!("brush feather"));
                }
                ClipMaskKind::Brush(folder)
            } else if let Some(shape) = ClipMask::shape(&mask.resource_type, &mask.name) {
                ClipMaskKind::Shape(shape)
            } else {
                let name = if mask.name.is_empty() { mask.resource_type.clone() } else { mask.name.clone() };
                self.note(&tr!("Mask"), &name);
                continue;
            };
            result.push(ClipMask { kind, geometry, inverted: mask.invert, keyframes });
        }
        result
    }

    fn report(&mut self, key: &str, item: &GenericMaterial) {
        if IGNORED.contains(&key) {
            return;
        }
        let name = if item.name.is_empty() { item.r#type.clone() } else { item.name.clone() };
        match key {
            "transitions" => {
                if LayerTransition::kind(&item.name, &item.resource_id) != TransitionKind::Flashback {
                    self.note(&tr!("Transition"), &tr!("{0} (shown as a cross-fade)", name));
                }
            }
            "video_effects" => {
                if !self.rendered_effects.contains(&item.id) {
                    self.note(&tr!("Video effect"), &name);
                }
            }
            "stickers" => self.note(&tr!("Sticker"), &name),
            // A neutral HSL changes nothing (all of the author's projects have only those).
            "hsl" => {
                if !item.is_neutral_hsl() {
                    self.note(&tr!("HSL color"), &tr!("color adjustment"));
                }
            }
            "manual_deformations" => self.note(&tr!("Manual deformation"), &tr!("body/face reshape")),
            "material_animations" => {
                for animation in &item.animation_names {
                    self.note(&tr!("Animation"), animation);
                }
            }
            "effects" => match item.r#type.as_str() {
                // Rendered from the cached lookup table when there is one.
                "filter" => {
                    if self.color_filter(item).is_none() {
                        self.note(&tr!("Filter"), &name);
                    }
                }
                // Reported with its text, where it's known whether it applies.
                "text_effect" => {}
                "text_shape" => {
                    if !item.name.is_empty() {
                        self.note(&tr!("Text shape"), &item.name);
                    }
                }
                "mix_mode" => {
                    if BlendMode::from_material(item).is_none() {
                        self.note(&tr!("Blend mode"), &name);
                    }
                }
                "color_correct" | "smart_color_adjust" => {
                    self.note(&tr!("Color adjustment"), &item.r#type.replace('_', " "));
                }
                // The container of a clip's beauty effects; it draws nothing itself.
                "makeup_root" => {}
                // Skin retouch ("Sparkly Skin", smoothing): approximated by SkinRetouch.
                "figure" if item.sub_type == SKIN_RETOUCH_SUB_TYPE => {}
                t if t.contains("makeup") || t.contains("beauty") || t == "figure" || t.contains("face") => {
                    self.note(&tr!("Beauty / makeup"), &name);
                }
                _ => self.note(&tr!("Effect"), &name),
            },
            _ => {
                let suffix = if item.r#type.is_empty() { String::new() } else { format!(" ({})", item.r#type) };
                self.note(&tr!("Other"), &format!("{key}{suffix}"));
            }
        }
    }
}

/// The blend mode of the segment's `mix_mode` material, if any (unknown modes are reported in `report`).
fn blend_mode(refs: &[String], generic: &HashMap<String, (String, GenericMaterial)>) -> Option<BlendMode> {
    refs.iter()
        .filter_map(|r| generic.get(r))
        .find(|(key, item)| key == "effects" && item.r#type == "mix_mode")
        .and_then(|(_, item)| BlendMode::from_material(item))
}

fn make_time_map(segment: &Segment, speed: Option<&SpeedMaterial>) -> TimeMap {
    let target = segment.target_timerange;
    let speed_value = speed.map_or(segment.speed, |s| s.speed);
    let source =
        segment.source_timerange.unwrap_or(TimeRange::new(0, (target.duration as f64 * speed_value).round() as i64));
    let curve: &[crate::draft::Point2] = match speed {
        Some(s) if s.mode == 1 => &s.curve_points,
        _ => &[],
    };
    TimeMap::new(source.start, source.duration, target.duration, curve)
}

/// Maps a range given in a reversed file's time base back onto the original media (played forward).
fn mirrored(map: TimeMap, media_duration: i64) -> TimeMap {
    if media_duration <= 0 {
        return map;
    }
    let start = (media_duration - map.source_start - map.source_duration).max(0);
    TimeMap::constant(start, map.source_duration, map.target_duration)
}

fn keyframes(segment: &Segment) -> BTreeMap<LayerProperty, KeyframeTrack> {
    let mut result = BTreeMap::new();
    for group in &segment.keyframes {
        if let (Some(property), Some(track)) =
            (LayerProperty::parse(&group.property_type), KeyframeTrack::from_group(group))
        {
            result.insert(property, track);
        }
    }
    result
}

/// `KFTypeVolume` keyframes hold the segment's volume over time; as a multiplier of `segment.volume`.
fn volume_envelope(segment: &Segment) -> Option<KeyframeTrack> {
    if segment.volume <= 0.0 || segment.volume.is_nan() {
        return None;
    }
    let group = segment.keyframes.iter().find(|g| g.property_type == LayerProperty::Volume.raw_value())?;
    Some(KeyframeTrack::from_group(group)?.scaled(1.0 / segment.volume))
}

/// Re-times a compound clip's audio into the parent timeline, following the parent's speed and curve. `envelope`
/// (the compound segment's volume keyframes) and `voice` (its voice effects) apply on top of the nested clips' own
/// settings.
fn nest_audio(
    nested: &[AudioClip],
    segment: &Segment,
    time_map: &TimeMap,
    volume: f64,
    envelope: Option<&KeyframeTrack>,
    voice: &VoiceEffects,
) -> Vec<AudioClip> {
    let window_start = time_map.source_start;
    let window_end = time_map.source_start + time_map.source_duration;
    let mut result = Vec::new();
    for clip in nested {
        let start = clip.target.start.max(window_start);
        let end = clip.target.end().min(window_end);
        if end <= start {
            continue;
        }
        let o0 = time_map.offset_at_source_time(start);
        let o1 = time_map.offset_at_source_time(end);
        if o1 <= o0 {
            continue;
        }
        let mut copy = clip.clone();
        copy.target = TimeRange::new(segment.target_timerange.start + o0, o1 - o0);
        if time_map.is_constant() && clip.time_map.is_constant() {
            copy.time_map = clip.time_map.slice(start - clip.target.start, end - clip.target.start);
            copy.time_map.target_duration = o1 - o0;
        } else {
            let (outer, inner, clip_start) = (time_map.clone(), clip.time_map.clone(), clip.target.start);
            copy.time_map = TimeMap::sampled(o1 - o0, move |u| {
                let nested_time = outer.precise_source_time(o0 as f64 + u * (o1 - o0) as f64);
                inner.precise_source_time(nested_time - clip_start as f64)
            });
        }
        copy.volume *= volume;
        if let Some(inner) = &clip.volume_envelope {
            // The nested curve follows the parent's speed: sample both every 50 ms.
            copy.volume_envelope = Some(KeyframeTrack::sampled(o1 - o0, 50_000, |u| {
                let nested_time = time_map.source_time(o0 + u);
                inner.value(nested_time - clip.target.start) * envelope.map_or(1.0, |e| e.value(o0 + u))
            }));
        } else {
            copy.volume_envelope = envelope.map(|e| e.shifted(o0));
        }
        if voice.separation != VoiceSeparation::None {
            copy.voice.separation = voice.separation;
        }
        if let Some(denoise) = voice.denoise {
            copy.voice.denoise = Some(denoise);
        }
        if let Some(enhance) = voice.enhance {
            copy.voice.enhance = Some(enhance);
        }
        if let Some(filter) = &voice.filter {
            copy.voice.filter = Some(filter.clone());
        }
        copy.fade_in =
            if start == clip.target.start { time_map.offset_at_source_time(start + clip.fade_in) - o0 } else { 0 };
        copy.fade_out =
            if end == clip.target.end() { o1 - time_map.offset_at_source_time(end - clip.fade_out) } else { 0 };
        result.push(copy);
    }
    result
}
