//! The timeline's static data as records and enums, with today's Swift type and field names (UniFFI makes them
//! `camelCase` in Swift and `PascalCase` in C#). Documentation of each field is on the core type.

use std::collections::HashMap;
use std::sync::Arc;

use ecc::draft as d;
use ecc::timeline as t;

use crate::convert::{path_string, record, simple_enum, Ffi};
use crate::timeline::{KeyframeTrack, Timeline};

record! {
    /// A time range on the timeline or in a source, µs.
    TimeRange = d::TimeRange { start: i64, duration: i64 }
}

impl From<TimeRange> for d::TimeRange {
    fn from(r: TimeRange) -> Self {
        d::TimeRange::new(r.start, r.duration)
    }
}

record! { Point2 = d::Point2 { x: f64, y: f64 } }

record! {
    Clip = d::Clip {
        scale: Point2,
        rotation: f64,
        transform: Point2,
        flip_horizontal: bool,
        flip_vertical: bool,
        alpha: f64,
    }
}

record! {
    /// Normalized corners, origin at the top-left of the source frame.
    Crop = d::Crop { upper_left: Point2, lower_right: Point2 }
}

record! {
    CanvasMaterial = d::CanvasMaterial { id: String, r#type: String, color: String, blur: f64, image: String }
}

record! {
    TextMaterial = d::TextMaterial {
        id: String,
        r#type: String,
        content: String,
        font_path: String,
        font_size: f64,
        text_color: String,
        alignment: i64,
        line_spacing: f64,
        letter_spacing: f64,
        line_max_width: f64,
        border_color: String,
        border_width: f64,
        background_color: String,
        background_alpha: f64,
        background_round_radius: f64,
        background_width: f64,
        background_height: f64,
        has_shadow: bool,
        shadow_color: String,
        shadow_alpha: f64,
        shadow_smoothing: f64,
        shadow_distance: f64,
        shadow_angle: f64,
        global_alpha: f64,
        bold_width: f64,
        italic_degree: f64,
        underline: bool,
        typesetting: i64,
        curve_enabled: bool,
    }
}

record! {
    #[derive(PartialEq)]
    TextStroke = d::TextStroke { width: f64, color: Vec<f64>, alpha: f64 }
}

/// A style run of a text. `range` is the draft's (code points); `utf16_start`/`utf16_end` are the same run in UTF-16
/// units of `TextContent.text`, clamped to it (an `NSRange` / .NET string range).
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct TextStyle {
    pub range: Vec<i64>,
    pub utf16_start: u64,
    pub utf16_end: u64,
    pub size: Option<f64>,
    pub font_path: Option<String>,
    pub fill_color: Option<Vec<f64>>,
    pub fill_alpha: Option<f64>,
    pub strokes: Vec<TextStroke>,
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
    pub effect_style_path: Option<String>,
}

#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct TextContent {
    pub text: String,
    pub styles: Vec<TextStyle>,
}

impl Ffi for d::TextContent {
    type Out = TextContent;
    fn ffi(self) -> TextContent {
        let styles = self
            .styles
            .iter()
            .map(|style| {
                let run = self.utf16_range(Some(style));
                let style = style.clone();
                TextStyle {
                    range: style.range,
                    utf16_start: run.start as u64,
                    utf16_end: run.end as u64,
                    size: style.size,
                    font_path: style.font_path,
                    fill_color: style.fill_color,
                    fill_alpha: style.fill_alpha,
                    strokes: style.strokes.ffi(),
                    bold: style.bold,
                    italic: style.italic,
                    underline: style.underline,
                    effect_style_path: style.effect_style_path,
                }
            })
            .collect();
        TextContent { text: self.text, styles }
    }
}

// Animations.

simple_enum! { ClipAnimationKind = t::ClipAnimationKind { In, Out } }

simple_enum! {
    ClipAnimationPreset = t::ClipAnimationPreset {
        Fade, ZoomIn, ZoomOut, SlideLeft, SlideRight, SlideUp, SlideDown, Rotate, RotateCounterclockwise, Shake,
    }
}

record! {
    ClipAnimation = t::ClipAnimation { kind: ClipAnimationKind, preset: ClipAnimationPreset, start: i64, end: i64 }
}

simple_enum! { TextAnimationKind = t::TextAnimationKind { Intro, Outro, Loop } }

#[derive(Debug, Clone, Copy, PartialEq, uniffi::Enum)]
pub enum TextAnimationEffect {
    Fade,
    Zoom { scale: f64, fades: bool },
    Pop,
    Slide { dx: f64, dy: f64 },
    Typewriter,
    Blink,
    Pulse,
}

impl Ffi for t::TextAnimationEffect {
    type Out = TextAnimationEffect;
    fn ffi(self) -> TextAnimationEffect {
        use t::TextAnimationEffect as E;
        match self {
            E::Fade => TextAnimationEffect::Fade,
            E::Zoom { scale, fades } => TextAnimationEffect::Zoom { scale, fades },
            E::Pop => TextAnimationEffect::Pop,
            E::Slide { dx, dy } => TextAnimationEffect::Slide { dx, dy },
            E::Typewriter => TextAnimationEffect::Typewriter,
            E::Blink => TextAnimationEffect::Blink,
            E::Pulse => TextAnimationEffect::Pulse,
        }
    }
}

record! {
    TextAnimation = t::TextAnimation { kind: TextAnimationKind, effect: TextAnimationEffect, duration: i64 }
}

record! {
    TextAnimations = t::TextAnimations {
        intro: Option<TextAnimation>,
        outro: Option<TextAnimation>,
        r#loop: Option<TextAnimation>,
    }
}

record! {
    TextAnimationState = t::TextAnimationState { alpha: f64, scale: f64, offset_x: f64, offset_y: f64, reveal: f64 }
}

// Looks.

simple_enum! { MaskShape = t::MaskShape { Line, Mirror, Circle, Rectangle, Heart, Star } }

record! {
    MaskGeometry = t::MaskGeometry {
        center_x: f64,
        center_y: f64,
        width: f64,
        height: f64,
        rotation: f64,
        feather: f64,
        round_corner: f64,
    }
}

simple_enum! {
    MaskProperty = t::MaskProperty { PositionX, PositionY, Width, Height, Rotation, Feather, RoundCorner, Expansion }
}

#[derive(Debug, Clone, PartialEq, uniffi::Enum)]
pub enum ClipMaskKind {
    Shape {
        shape: MaskShape,
    },
    /// A brush mask: a folder of mask frames.
    Brush {
        directory: String,
    },
}

impl Ffi for t::ClipMaskKind {
    type Out = ClipMaskKind;
    fn ffi(self) -> ClipMaskKind {
        match self {
            t::ClipMaskKind::Shape(shape) => ClipMaskKind::Shape { shape: shape.ffi() },
            t::ClipMaskKind::Brush(path) => ClipMaskKind::Brush { directory: path_string(&path) },
        }
    }
}

record! {
    ClipMask = t::ClipMask {
        kind: ClipMaskKind,
        geometry: MaskGeometry,
        inverted: bool,
        keyframes: HashMap<MaskProperty, Arc<KeyframeTrack>>,
    }
}

simple_enum! { TransitionKind = t::TransitionKind { Flashback, CrossFade } }

record! {
    /// `next` is the incoming layer's index in the timeline's `layers`.
    LayerTransition = t::LayerTransition { name: String, kind: TransitionKind, next: u64, range: TimeRange }
}

simple_enum! { VideoEffectKind = t::VideoEffectKind { PuddingBounce, HazyRotation } }

record! { VideoEffect = t::VideoEffect { kind: VideoEffectKind, start: i64, speed: f64 } }

record! { VideoEffectState = t::VideoEffectState { zoom: f64, rotation: f64, blur: f64, flash: f64 } }

simple_enum! {
    BlendMode = t::BlendMode {
        Normal, Screen, Darken, Lighten, Overlay, HardLight, SoftLight, LinearBurn, ColorBurn, ColorDodge, Multiply,
    }
}

record! {
    ChromaKey = t::ChromaKey {
        red: f64,
        green: f64,
        blue: f64,
        intensity: f64,
        shadow: f64,
        edge_smooth: f64,
        spill: f64,
    }
}

simple_enum! { ClockStyle = t::ClockStyle { WhiteOutline, BlueWhite } }

/// A "Clock Time" sticker, drawn natively: its style and the look of that style (sizes as fractions of the radius,
/// except `diameter`: of the canvas's shorter side at clip scale 1; sRGB colours 0…1, `face_color` `None` =
/// transparent). The hands' angles come with each frame (`LayerFrame.clock_hands`).
#[derive(Debug, Clone, uniffi::Record)]
pub struct ClockSticker {
    pub style: ClockStyle,
    pub diameter: f64,
    pub ring_width: f64,
    pub minute_hand: f64,
    pub hour_hand: f64,
    pub hand_width: f64,
    pub ring_color: Vec<f64>,
    pub face_color: Option<Vec<f64>>,
    pub hand_color: Vec<f64>,
}

impl Ffi for t::ClockSticker {
    type Out = ClockSticker;
    fn ffi(self) -> ClockSticker {
        ClockSticker {
            style: self.style.ffi(),
            diameter: self.diameter(),
            ring_width: self.ring_width(),
            minute_hand: self.minute_hand(),
            hour_hand: self.hour_hand(),
            hand_width: self.hand_width(),
            ring_color: self.ring_color().to_vec(),
            face_color: self.face_color().map(|c| c.to_vec()),
            hand_color: self.hand_color().to_vec(),
        }
    }
}

record! { StickerSpec = t::StickerSpec { name: String, clock: ClockSticker } }

record! { TextGlow = t::TextGlow { color: Vec<f64>, radius: f64 } }

// Filters and voice.

record! { ColorFilter = t::ColorFilter { name: String, luts: Vec<String>, intensity: f64 } }

record! {
    FilterLayer = t::FilterLayer {
        id: String,
        target: TimeRange,
        track_index: i64,
        render_index: i64,
        filter: ColorFilter,
    }
}

record! { VoiceFilter = t::VoiceFilter { name: String, directory: String, strength: f64 } }

simple_enum! { VoiceSeparation = t::VoiceSeparation { None, RemoveVoice, KeepVoice } }

record! {
    VoiceEffects = t::VoiceEffects {
        separation: VoiceSeparation,
        denoise: Option<f64>,
        enhance: Option<f64>,
        filter: Option<VoiceFilter>,
    }
}

// Layers.

/// A text clip: its material, text and styles, animations (`animation_max_scale`: the largest scale they reach, so the
/// text can be rasterized sharp at it) and the approximated look of its CapCut text effect (`glow`).
#[derive(Debug, Clone, uniffi::Record)]
pub struct TextLayerSpec {
    pub material: TextMaterial,
    pub content: TextContent,
    pub animations: TextAnimations,
    pub animation_max_scale: f64,
    pub glow: Option<TextGlow>,
}

impl Ffi for t::TextLayerSpec {
    type Out = TextLayerSpec;
    fn ffi(self) -> TextLayerSpec {
        TextLayerSpec {
            animation_max_scale: self.animations.max_scale(),
            material: self.material.ffi(),
            content: self.content.ffi(),
            animations: self.animations.ffi(),
            glow: self.glow.ffi(),
        }
    }
}

/// What a visual layer shows. Compound clips hold their inner timeline as another `Timeline` object.
// Records can't be boxed across the boundary; the enum only lives while it's converted.
#[allow(clippy::large_enum_variant)]
#[derive(Debug, Clone, uniffi::Enum)]
pub enum VisualContent {
    Video { path: String },
    Photo { path: String },
    Text { spec: TextLayerSpec },
    Compound { timeline: Arc<Timeline> },
    Sticker { spec: StickerSpec },
}

impl Ffi for t::VisualContent {
    type Out = VisualContent;
    fn ffi(self) -> VisualContent {
        match self {
            t::VisualContent::Video(path) => VisualContent::Video { path: path_string(&path) },
            t::VisualContent::Photo(path) => VisualContent::Photo { path: path_string(&path) },
            t::VisualContent::Text(spec) => VisualContent::Text { spec: spec.ffi() },
            t::VisualContent::Compound(timeline) => VisualContent::Compound { timeline: Timeline::wrap(timeline) },
            t::VisualContent::Sticker(spec) => VisualContent::Sticker { spec: spec.ffi() },
        }
    }
}

simple_enum! {
    LayerProperty = t::LayerProperty { PositionX, PositionY, ScaleX, ScaleY, Rotation, Alpha, Volume }
}

record! {
    LayerTransform = t::LayerTransform {
        scale_x: f64,
        scale_y: f64,
        rotation: f64,
        position_x: f64,
        position_y: f64,
        flip_h: bool,
        flip_v: bool,
        alpha: f64,
    }
}

record! { Cutout = ecc::timeline::Cutout { directory: String, inverted: bool } }

record! {
    VisualLayer = t::VisualLayer {
        id: String,
        content: VisualContent,
        track_index: i64,
        render_index: i64,
        is_main_track: bool,
        target: TimeRange,
        time_map: Arc<crate::timeline::TimeMap>,
        crop: Crop,
        clip: Clip,
        uniform_scale: bool,
        keyframes: HashMap<LayerProperty, Arc<KeyframeTrack>>,
        canvas: Option<CanvasMaterial>,
        cutout: Option<Cutout>,
        filters: Vec<ColorFilter>,
        tone_maps_hdr: bool,
        chroma_key: Option<ChromaKey>,
        retouches_skin: bool,
        enhances_quality: bool,
        effects: Vec<VideoEffect>,
        transition_out: Option<LayerTransition>,
        masks: Vec<ClipMask>,
        animations: Vec<ClipAnimation>,
        blend_mode: Option<BlendMode>,
        stabilization: i64,
    }
}

record! {
    AudioClip = t::AudioClip {
        path: String,
        target: TimeRange,
        time_map: Arc<crate::timeline::TimeMap>,
        volume: f64,
        fade_in: i64,
        fade_out: i64,
        preserve_pitch: bool,
        volume_envelope: Option<Arc<KeyframeTrack>>,
        voice: VoiceEffects,
    }
}

record! { ReportItem = t::ReportItem { category: String, name: String, count: i64 } }

record! { RenderReport = t::RenderReport { unsupported: Vec<ReportItem>, missing_files: Vec<String> } }

/// The canvas size in pixels.
#[derive(Debug, Clone, Copy, PartialEq, uniffi::Record)]
pub struct Size {
    pub width: f64,
    pub height: f64,
}

// One frame (`Timeline.frame`).

record! {
    /// `next` is the incoming layer's index in the timeline's `layers`; `progress` 0 at the start, 0.5 at the cut.
    TransitionFrame = t::TransitionFrame { next: u64, kind: TransitionKind, progress: f64 }
}

/// A clock sticker's hands, degrees clockwise from 12 o'clock.
#[derive(Debug, Clone, Copy, PartialEq, uniffi::Record)]
pub struct ClockHands {
    pub minute: f64,
    pub hour: f64,
}

impl Ffi for (f64, f64) {
    type Out = ClockHands;
    fn ffi(self) -> ClockHands {
        ClockHands { minute: self.0, hour: self.1 }
    }
}

record! {
    LayerFrame = t::LayerFrame {
        index: u64,
        offset: i64,
        source_time: i64,
        transform: LayerTransform,
        text: Option<TextAnimationState>,
        effect: Option<VideoEffectState>,
        masks: Vec<MaskGeometry>,
        transition: Option<TransitionFrame>,
        clock_hands: Option<ClockHands>,
    }
}

record! {
    /// `filters` are indices into the timeline's `filters`.
    FrameState = t::FrameState { time: i64, layers: Vec<LayerFrame>, filters: Vec<u64> }
}
