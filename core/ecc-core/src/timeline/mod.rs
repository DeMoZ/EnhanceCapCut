//! A draft resolved into what the renderers need, the port of `Timeline.swift`: visual layers in paint order, audio
//! clips, filter layers and the report of what isn't rendered. Built by `builder::TimelineBuilder`; evaluated over
//! time by the methods here and by `frame` (one call per video frame).

pub mod animation;
pub mod builder;
pub mod cache;
pub mod frame;
pub mod looks;
pub mod time_map;

#[cfg(test)]
mod tests;
#[cfg(test)]
mod tests_features;

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;

pub use crate::draft::{CanvasMaterial, Clip, Crop, TextContent, TextMaterial, TimeRange};
pub use animation::{
    ClipAnimation, ClipAnimationKind, ClipAnimationPreset, TextAnimation, TextAnimationEffect, TextAnimationKind,
    TextAnimationState, TextAnimations,
};
pub use builder::TimelineBuilder;
pub use cache::{ColorFilter, FilterLayer, LutLayout, VoiceFilter};
pub use frame::{FrameState, LayerFrame, TransitionFrame};
pub use looks::{
    BlendMode, ChromaKey, ClipMask, ClipMaskKind, ClockSticker, ClockStyle, FlashbackLook, LayerTransition,
    MaskGeometry, MaskProperty, MaskShape, StickerSpec, TextEffectLook, TextGlow, TransitionKind, VideoEffect,
    VideoEffectKind, VideoEffectState,
};
pub use time_map::{bezier_value, KeyframeHandles, KeyframeTrack, TimeMap};

/// A draft resolved into what the renderer needs: visual layers in paint order and audio clips.
#[derive(Debug, Clone, PartialEq)]
pub struct Timeline {
    pub name: String,
    /// The draft's canvas in pixels. Layout is relative, so any output size can be rendered.
    pub canvas_width: f64,
    pub canvas_height: f64,
    pub duration: i64,
    pub fps: f64,
    /// Bottom to top.
    pub layers: Vec<VisualLayer>,
    pub audio_clips: Vec<AudioClip>,
    pub report: RenderReport,
    /// Filters on filter tracks (each colours the layers painted below it).
    pub filters: Vec<FilterLayer>,
}

impl Timeline {
    /// The same timeline without caption (auto-subtitle) clips.
    pub fn without_subtitles(&self) -> Timeline {
        let keep: Vec<bool> = self.layers.iter().map(|l| !l.is_subtitle()).collect();
        // Transitions point at layers by index: renumber them for the kept layers.
        let mut new_index = vec![usize::MAX; self.layers.len()];
        let mut n = 0;
        for (i, k) in keep.iter().enumerate() {
            if *k {
                new_index[i] = n;
                n += 1;
            }
        }
        let layers = self
            .layers
            .iter()
            .zip(&keep)
            .filter(|(_, k)| **k)
            .map(|(l, _)| {
                let mut l = l.clone();
                l.transition_out = l.transition_out.and_then(|mut t| {
                    t.next = *new_index.get(t.next).filter(|&&i| i != usize::MAX)?;
                    Some(t)
                });
                l
            })
            .collect();
        Timeline { layers, ..self.clone() }
    }

    pub fn has_subtitles(&self) -> bool {
        self.layers.iter().any(VisualLayer::is_subtitle)
    }

    /// For partial exports: the audio clips that play in `range`, cut to it and shifted so the range starts at 0
    /// (fades are dropped at a cut).
    pub fn audio_clips_in(&self, range: TimeRange) -> Vec<AudioClip> {
        let mut clips = Vec::new();
        for clip in &self.audio_clips {
            let start = clip.target.start.max(range.start);
            let end = clip.target.end().min(range.end());
            if end <= start {
                continue;
            }
            let mut copy = clip.clone();
            copy.target = TimeRange::new(start - range.start, end - start);
            copy.time_map = clip.time_map.slice(start - clip.target.start, end - clip.target.start);
            copy.volume_envelope = clip.volume_envelope.as_ref().map(|e| e.shifted(start - clip.target.start));
            if start != clip.target.start {
                copy.fade_in = 0;
            }
            if end != clip.target.end() {
                copy.fade_out = 0;
            }
            clips.push(copy);
        }
        clips
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum VisualContent {
    Video(PathBuf),
    Photo(PathBuf),
    Text(Box<TextLayerSpec>),
    Compound(Arc<Timeline>),
    Sticker(StickerSpec),
}

#[derive(Debug, Clone, PartialEq)]
pub struct TextLayerSpec {
    pub material: TextMaterial,
    pub content: TextContent,
    /// Intro / outro / loop animations.
    pub animations: TextAnimations,
    /// The approximated look of the text's CapCut text effect, if any (`TextEffectLook`).
    pub glow: Option<TextGlow>,
}

/// Layer properties that can be keyframed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum LayerProperty {
    PositionX,
    PositionY,
    ScaleX,
    ScaleY,
    Rotation,
    Alpha,
    Volume,
}

impl LayerProperty {
    pub const ALL: [LayerProperty; 7] = [
        LayerProperty::PositionX,
        LayerProperty::PositionY,
        LayerProperty::ScaleX,
        LayerProperty::ScaleY,
        LayerProperty::Rotation,
        LayerProperty::Alpha,
        LayerProperty::Volume,
    ];

    pub fn raw_value(&self) -> &'static str {
        match self {
            LayerProperty::PositionX => "KFTypePositionX",
            LayerProperty::PositionY => "KFTypePositionY",
            LayerProperty::ScaleX => "KFTypeScaleX",
            LayerProperty::ScaleY => "KFTypeScaleY",
            LayerProperty::Rotation => "KFTypeRotation",
            LayerProperty::Alpha => "KFTypeAlpha",
            LayerProperty::Volume => "KFTypeVolume",
        }
    }

    pub fn parse(raw: &str) -> Option<LayerProperty> {
        Self::ALL.into_iter().find(|p| p.raw_value() == raw)
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LayerTransform {
    pub scale_x: f64,
    pub scale_y: f64,
    /// Degrees, clockwise.
    pub rotation: f64,
    /// Half-canvas units, +y up.
    pub position_x: f64,
    pub position_y: f64,
    pub flip_h: bool,
    pub flip_v: bool,
    pub alpha: f64,
}

#[derive(Debug, Clone, PartialEq)]
pub struct VisualLayer {
    pub id: String,
    pub content: VisualContent,
    pub track_index: i64,
    pub render_index: i64,
    /// The first video track: its clips get the canvas background.
    pub is_main_track: bool,
    pub target: TimeRange,
    pub time_map: TimeMap,
    pub crop: Crop,
    pub clip: Clip,
    pub uniform_scale: bool,
    pub keyframes: BTreeMap<LayerProperty, KeyframeTrack>,
    pub canvas: Option<CanvasMaterial>,
    /// "Remove background": CapCut's mask frames, applied as the clip's alpha.
    pub cutout: Option<Cutout>,
    /// The clip's own filters, in order.
    pub filters: Vec<ColorFilter>,
    /// False when CapCut shows the clip's HDR signal as is (`hdr_settings.mode` 0) instead of tone-mapping it.
    pub tone_maps_hdr: bool,
    /// "Chroma key", applied to the clip's frames before the cutout.
    pub chroma_key: Option<ChromaKey>,
    /// CapCut's automatic skin retouch ("Sparkly Skin", smoothing) is on: approximated by `SkinRetouch`.
    pub retouches_skin: bool,
    /// CapCut's AI "Enhance quality" is on: approximated by `QualityEnhance`.
    pub enhances_quality: bool,
    /// Video effects on the clip that we render, in order.
    pub effects: Vec<VideoEffect>,
    /// The transition into the next clip on this track.
    pub transition_out: Option<LayerTransition>,
    /// Clip masks (shapes, brush); what any of them keeps is shown.
    pub masks: Vec<ClipMask>,
    /// Video clip In / Out animations, applied on top of the transform.
    pub animations: Vec<ClipAnimation>,
    /// The overlay's blend mode (`mix_mode`); `None` = normal.
    pub blend_mode: Option<BlendMode>,
    /// "Stabilize" level (`stable.stable_level`, 1…3), 0 = off; done by `Stabilizer` on video clips.
    pub stabilization: i64,
}

impl VisualLayer {
    /// A layer with everything but its content, place and timing at the defaults (no keyframes, effects, masks…).
    pub fn new(id: &str, content: VisualContent, target: TimeRange, time_map: TimeMap) -> VisualLayer {
        VisualLayer {
            id: id.to_string(),
            content,
            track_index: 0,
            render_index: 0,
            is_main_track: false,
            target,
            time_map,
            crop: Crop::default(),
            clip: Clip::default(),
            uniform_scale: true,
            keyframes: BTreeMap::new(),
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
        }
    }

    pub fn is_active(&self, t: i64) -> bool {
        t >= self.target.start && t < self.target.end()
    }

    fn is_subtitle(&self) -> bool {
        matches!(&self.content, VisualContent::Text(spec) if spec.material.r#type == "subtitle")
    }

    /// Clip transform at timeline time `t`, with keyframes and clip animations applied.
    pub fn transform(&self, t: i64) -> LayerTransform {
        let offset = t - self.target.start;
        // Clip keyframes are on the source time axis, like mask keyframes (docs/FORMAT.md).
        let key_time = self.time_map.source_time(offset);
        let kf = |p: LayerProperty| self.keyframes.get(&p).map(|k| k.value(key_time));
        let mut sx = kf(LayerProperty::ScaleX).unwrap_or(self.clip.scale.x);
        let mut sy = kf(LayerProperty::ScaleY).unwrap_or(self.clip.scale.y);
        if self.uniform_scale {
            if self.keyframes.contains_key(&LayerProperty::ScaleX) {
                sy = sx;
            } else if self.keyframes.contains_key(&LayerProperty::ScaleY) {
                sx = sy;
            }
        }
        ClipAnimation::apply_all(
            &self.animations,
            LayerTransform {
                scale_x: sx,
                scale_y: sy,
                rotation: kf(LayerProperty::Rotation).unwrap_or(self.clip.rotation),
                position_x: kf(LayerProperty::PositionX).unwrap_or(self.clip.transform.x),
                position_y: kf(LayerProperty::PositionY).unwrap_or(self.clip.transform.y),
                flip_h: self.clip.flip_horizontal,
                flip_v: self.clip.flip_vertical,
                alpha: kf(LayerProperty::Alpha).unwrap_or(self.clip.alpha),
            },
            offset,
        )
    }
}

/// CapCut's "Remove background" result for a clip: a folder of mask frames.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct Cutout {
    /// The `mask` folder: one raw 8-bit frame per file, named by its source time in µs.
    pub directory: PathBuf,
    /// "Invert": keep the background, remove the subject.
    pub inverted: bool,
}

impl Cutout {
    /// The mask folder in a matting result folder: `<folder>/<custom_id>/<n>/mask` for a custom cutout,
    /// `<folder>/<n>/mask` for the auto cutout (`n` = 5 and 2 so far). Only folders with frames count.
    pub fn locate(folder: &Path, custom_id: Option<&str>) -> Option<PathBuf> {
        let masks = |base: &Path| -> Option<PathBuf> {
            let mut entries: Vec<PathBuf> =
                std::fs::read_dir(base).map(|it| it.flatten().map(|e| e.path()).collect()).unwrap_or_default();
            entries.sort_by(|a, b| a.to_string_lossy().cmp(&b.to_string_lossy()));
            entries
                .into_iter()
                .map(|e| e.join("mask"))
                .find(|m| std::fs::read_dir(m).map(|mut it| it.next().is_some()).unwrap_or(false))
        };
        match custom_id {
            None => masks(folder),
            Some("") => None,
            Some(id) => masks(&folder.join(id)),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct AudioClip {
    pub path: PathBuf,
    pub target: TimeRange,
    pub time_map: TimeMap,
    /// Linear gain; CapCut allows values above 1.
    pub volume: f64,
    pub fade_in: i64,
    pub fade_out: i64,
    pub preserve_pitch: bool,
    /// Volume keyframes as a multiplier of `volume`, offsets from the clip start (`None` = constant).
    pub volume_envelope: Option<KeyframeTrack>,
    /// "Isolate voice" / "Reduce noise" / "Enhance voice", applied to the source before the mix.
    pub voice: VoiceEffects,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash)]
pub enum VoiceSeparation {
    #[default]
    None,
    /// "Remove vocal": keep only the background (music, ambience).
    RemoveVoice,
    /// "Keep vocal": keep only the voice.
    KeepVoice,
}

impl VoiceSeparation {
    pub fn from_choice(choice: i64) -> Option<VoiceSeparation> {
        match choice {
            0 => Some(VoiceSeparation::None),
            1 => Some(VoiceSeparation::RemoveVoice),
            2 => Some(VoiceSeparation::KeepVoice),
            _ => None,
        }
    }
}

/// CapCut's per-clip voice processing.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct VoiceEffects {
    pub separation: VoiceSeparation,
    /// "Reduce noise" strength 0...1, `None` when off.
    pub denoise: Option<f64>,
    /// "Enhance voice" ambient sound level 0...1 (`ambient_sound_level` / 100), `None` when off.
    pub enhance: Option<f64>,
    /// A voice filter ("Booming", "Old Hollywood"…), applied after the other stages.
    pub filter: Option<VoiceFilter>,
}

impl VoiceEffects {
    pub fn is_empty(&self) -> bool {
        self.separation == VoiceSeparation::None
            && self.denoise.is_none()
            && self.enhance.is_none()
            && self.filter.is_none()
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct ReportItem {
    pub category: String,
    pub name: String,
    pub count: i64,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RenderReport {
    /// Effects, transitions, stickers, filters... present in the project but not rendered.
    pub unsupported: Vec<ReportItem>,
    /// Media files referenced by the project that don't exist on disk (rendered as empty).
    pub missing_files: Vec<String>,
}

impl RenderReport {
    pub fn is_empty(&self) -> bool {
        self.unsupported.is_empty() && self.missing_files.is_empty()
    }
}
