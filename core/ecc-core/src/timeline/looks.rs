//! Small timeline features, each the port of a Swift file of the same name: `ClipMask`, `Transition`
//! (`LayerTransition`, `FlashbackLook`), `VideoEffect`, `BlendMode`, `ChromaKey`, `ClockSticker` and `TextEffect`
//! (`TextGlow`, `TextEffectLook`).

use std::collections::BTreeMap;
use std::f64::consts::PI;
use std::path::{Path, PathBuf};

use super::{KeyframeTrack, TimeRange};
use crate::draft::{ChromaMaterial, GenericMaterial, VideoEffectMaterial};

// MARK: - Masks

/// The shapes of CapCut's clip masks (see FORMAT.md › Masks).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MaskShape {
    /// "Split": a straight edge; the part above the line (before rotation) is kept.
    Line,
    /// "Mirror": a band `height` tall.
    Mirror,
    Circle,
    Rectangle,
    Heart,
    Star,
}

impl MaskShape {
    pub fn parse(raw: &str) -> Option<MaskShape> {
        Some(match raw {
            "line" => MaskShape::Line,
            "mirror" => MaskShape::Mirror,
            "circle" => MaskShape::Circle,
            "rectangle" => MaskShape::Rectangle,
            "heart" => MaskShape::Heart,
            "star" => MaskShape::Star,
            _ => return None,
        })
    }

    pub fn name(&self) -> &'static str {
        match self {
            MaskShape::Line => "line",
            MaskShape::Mirror => "mirror",
            MaskShape::Circle => "circle",
            MaskShape::Rectangle => "rectangle",
            MaskShape::Heart => "heart",
            MaskShape::Star => "star",
        }
    }
}

/// A mask's placement, in the clip's own frame (before the clip transform): center in half-frame units (+y up),
/// size as fractions of the frame's width and height.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MaskGeometry {
    pub center_x: f64,
    pub center_y: f64,
    pub width: f64,
    pub height: f64,
    /// Degrees, clockwise.
    pub rotation: f64,
    /// 0...1.
    pub feather: f64,
    /// 0...1 of half the shorter side.
    pub round_corner: f64,
}

impl Default for MaskGeometry {
    fn default() -> Self {
        MaskGeometry {
            center_x: 0.0,
            center_y: 0.0,
            width: 0.5,
            height: 0.5,
            rotation: 0.0,
            feather: 0.0,
            round_corner: 0.0,
        }
    }
}

/// Mask properties that can be keyframed: `KFTypeCommonMask<raw value>` (older drafts: `KFTypeMask<raw value>`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum MaskProperty {
    PositionX,
    PositionY,
    Width,
    Height,
    Rotation,
    Feather,
    RoundCorner,
    /// Read so it isn't reported as unknown; the builder reports a non-zero expansion instead.
    Expansion,
}

impl MaskProperty {
    pub const ALL: [MaskProperty; 8] = [
        MaskProperty::PositionX,
        MaskProperty::PositionY,
        MaskProperty::Width,
        MaskProperty::Height,
        MaskProperty::Rotation,
        MaskProperty::Feather,
        MaskProperty::RoundCorner,
        MaskProperty::Expansion,
    ];

    pub fn raw_value(&self) -> &'static str {
        match self {
            MaskProperty::PositionX => "PositionX",
            MaskProperty::PositionY => "PositionY",
            MaskProperty::Width => "SizeWidth",
            MaskProperty::Height => "SizeHeight",
            MaskProperty::Rotation => "Rotation",
            MaskProperty::Feather => "Feather",
            MaskProperty::RoundCorner => "RoundCorner",
            MaskProperty::Expansion => "Expansion",
        }
    }

    /// The property of a keyframe group's `property_type`, `None` when it isn't a known mask property.
    pub fn from_property_type(property_type: &str) -> Option<MaskProperty> {
        for prefix in ["KFTypeCommonMask", "KFTypeMask"] {
            if let Some(rest) = property_type.strip_prefix(prefix) {
                return Self::ALL.into_iter().find(|p| p.raw_value() == rest);
            }
        }
        None
    }

    pub fn is_mask(property_type: &str) -> bool {
        property_type.starts_with("KFTypeCommonMask") || property_type.starts_with("KFTypeMask")
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum ClipMaskKind {
    Shape(MaskShape),
    /// "Brush": CapCut's painted strokes, one raw 8-bit frame per file named by its source time in µs.
    Brush(PathBuf),
}

/// One of a clip's masks. A clip can have several; what any of them keeps is shown.
#[derive(Debug, Clone, PartialEq)]
pub struct ClipMask {
    pub kind: ClipMaskKind,
    pub geometry: MaskGeometry,
    pub inverted: bool,
    /// Keyframes on the clip's source time axis (µs), like CapCut stores them.
    pub keyframes: BTreeMap<MaskProperty, KeyframeTrack>,
}

impl ClipMask {
    /// The geometry at source time `t` (µs), with keyframes applied.
    pub fn geometry_at(&self, t: i64) -> MaskGeometry {
        let kf = |p: MaskProperty| self.keyframes.get(&p).map(|k| k.value(t));
        let g = self.geometry;
        MaskGeometry {
            center_x: kf(MaskProperty::PositionX).unwrap_or(g.center_x),
            center_y: kf(MaskProperty::PositionY).unwrap_or(g.center_y),
            width: kf(MaskProperty::Width).unwrap_or(g.width),
            height: kf(MaskProperty::Height).unwrap_or(g.height),
            rotation: kf(MaskProperty::Rotation).unwrap_or(g.rotation),
            feather: kf(MaskProperty::Feather).unwrap_or(g.feather),
            round_corner: kf(MaskProperty::RoundCorner).unwrap_or(g.round_corner),
        }
    }

    /// The shape of a mask material's `resource_type` (and name, for the geometric shapes).
    pub fn shape(resource_type: &str, name: &str) -> Option<MaskShape> {
        if let Some(shape) = MaskShape::parse(resource_type) {
            return Some(shape);
        }
        let n = name.to_lowercase();
        match resource_type {
            "geometric_shape" | "heart" | "star" | "love" | "pentagram" => {
                if n.contains("heart") || resource_type == "heart" || resource_type == "love" {
                    return Some(MaskShape::Heart);
                }
                if n.contains("star") || resource_type == "star" || resource_type == "pentagram" {
                    return Some(MaskShape::Star);
                }
                None
            }
            _ => None,
        }
    }
}

/// The source times (µs) of a brush mask's frames: the files in `folder` named by an integer, in order.
pub fn brush_frame_times(folder: &Path) -> Vec<i64> {
    let mut times: Vec<i64> = std::fs::read_dir(folder)
        .map(|entries| entries.flatten().filter_map(|e| e.file_name().to_str()?.parse::<i64>().ok()).collect())
        .unwrap_or_default();
    times.sort();
    times
}

// MARK: - Transitions

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TransitionKind {
    /// CapCut's "Flashback": a zoom-and-spin blur that peaks at the cut, dissolving into the next clip.
    Flashback,
    /// Every other transition, approximated by a cross-fade.
    CrossFade,
}

/// A transition from a clip into the next clip on its track (`materials.transitions`, referenced from the outgoing
/// clip's `extra_material_refs`). CapCut centres it on the cut: the outgoing clip keeps playing until the cut and the
/// incoming one starts there, each holding its edge frame for the other half (see FORMAT.md).
#[derive(Debug, Clone, PartialEq)]
pub struct LayerTransition {
    /// CapCut's name for it.
    pub name: String,
    pub kind: TransitionKind,
    /// The incoming clip: its index in the timeline's `layers`.
    pub next: usize,
    /// The timeline range it plays over.
    pub range: TimeRange,
}

impl LayerTransition {
    /// "Flashback"'s effect id in CapCut's library.
    pub const FLASHBACK_ID: &'static str = "7252586720078598658";
    /// Clips closer than this (µs) count as adjacent.
    pub const ADJACENCY: i64 = 40_000;

    pub fn contains(&self, t: i64) -> bool {
        t >= self.range.start && t < self.range.end()
    }

    /// 0 at the start, 0.5 at the cut, 1 at the end.
    pub fn progress(&self, t: i64) -> f64 {
        if self.range.duration <= 0 {
            return 1.0;
        }
        ((t - self.range.start) as f64 / self.range.duration as f64).clamp(0.0, 1.0)
    }

    pub fn kind(name: &str, resource_id: &str) -> TransitionKind {
        if name == "Flashback" || resource_id == Self::FLASHBACK_ID {
            TransitionKind::Flashback
        } else {
            TransitionKind::CrossFade
        }
    }

    /// The transition from a clip over `from` into one over `into` (index `next`), or `None` when they don't touch.
    /// CapCut keeps a transition within half of each clip; so do we.
    pub fn make(
        name: &str,
        resource_id: &str,
        duration: i64,
        from: TimeRange,
        into: TimeRange,
        next: usize,
    ) -> Option<LayerTransition> {
        let cut = from.end();
        if (into.start - cut).abs() > Self::ADJACENCY {
            return None;
        }
        let half = (duration / 2).min(from.duration / 2).min(into.duration / 2);
        if half <= 0 {
            return None;
        }
        Some(LayerTransition {
            name: name.to_string(),
            kind: Self::kind(name, resource_id),
            next,
            range: TimeRange::new(cut - half, 2 * half),
        })
    }
}

/// Our own "Flashback" look, calibrated by eye against CapCut's exports (ATV): both clips zoom in and turn a little
/// while a zoom-and-spin blur builds up to the cut and fades out after it; the picture dissolves around the cut.
pub struct FlashbackLook;

impl FlashbackLook {
    /// Copies averaged for the blur.
    pub const BLUR_COPIES: usize = 16;
    /// Zoom at the peak (fraction of the frame) and the extra zoom the blur spreads over.
    pub const ZOOM: f64 = 0.2;
    pub const BLUR_ZOOM: f64 = 0.25;
    /// Rotation at the peak and the extra rotation the blur spreads over, degrees clockwise.
    pub const ROTATION: f64 = 6.0;
    pub const BLUR_ROTATION: f64 = 14.0;
    /// The dissolve into the next clip runs over this part of the transition, centred on the cut.
    pub const DISSOLVE_START: f64 = 0.35;
    pub const DISSOLVE_END: f64 = 0.65;

    /// Strength of the effect: 0 at the edges, 1 at the cut.
    pub fn strength(p: f64) -> f64 {
        (PI * p.clamp(0.0, 1.0)).sin()
    }

    /// How much of the next clip shows.
    pub fn mix(p: f64) -> f64 {
        let x = ((p - Self::DISSOLVE_START) / (Self::DISSOLVE_END - Self::DISSOLVE_START)).clamp(0.0, 1.0);
        x * x * (3.0 - 2.0 * x)
    }

    /// Scale and clockwise rotation (degrees) about the frame centre of blur copy `i` at progress `p`.
    pub fn copy(i: usize, p: f64) -> (f64, f64) {
        let s = Self::strength(p);
        let f = if Self::BLUR_COPIES > 1 { i as f64 / (Self::BLUR_COPIES - 1) as f64 } else { 0.0 };
        (1.0 + (Self::ZOOM + Self::BLUR_ZOOM * f) * s, (Self::ROTATION + Self::BLUR_ROTATION * f) * s)
    }
}

// MARK: - Video effects

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum VideoEffectKind {
    /// "Pudding Bounce": the clip opens on a white flash, zoomed in and blurred, and bounces back to rest.
    PuddingBounce,
    /// "Hazy Rotation": the clip opens zoomed in, tilted and out of focus, then turns and settles.
    HazyRotation,
}

/// A video effect on a clip, re-implemented in our code: its look is a function of time (`state`) that the
/// compositors apply to the clip's picture inside its own frame (see FORMAT.md, "Video effects").
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VideoEffect {
    pub kind: VideoEffectKind,
    /// Where the effect starts, relative to the clip start (µs).
    pub start: i64,
    /// Playback rate of the effect: 1 at CapCut's default "Speed" slider.
    pub speed: f64,
}

impl VideoEffect {
    /// CapCut's default "Speed" slider value (`effects_adjust_speed`).
    pub const DEFAULT_SPEED_SLIDER: f64 = 1.0 / 3.0;

    /// CapCut's effect ids of the effects we render.
    pub fn kind_for(resource_id: &str) -> Option<VideoEffectKind> {
        match resource_id {
            "7534579654615649553" => Some(VideoEffectKind::PuddingBounce),
            "7486402432189091127" => Some(VideoEffectKind::HazyRotation),
            _ => None,
        }
    }

    /// The effect of a material applied to a clip, or `None` when it isn't one we render.
    pub fn from_material(material: &VideoEffectMaterial) -> Option<VideoEffect> {
        if material.apply_target_type != 0 {
            return None;
        }
        let kind = Self::kind_for(&material.resource_id)?;
        let slider = material.params.get("effects_adjust_speed").copied().unwrap_or(Self::DEFAULT_SPEED_SLIDER);
        Some(VideoEffect { kind, start: 0, speed: slider.max(0.01) / Self::DEFAULT_SPEED_SLIDER })
    }

    /// The effect's look `offset` µs after the clip start.
    pub fn state(&self, offset: i64) -> VideoEffectState {
        if offset < self.start {
            return VideoEffectState::IDENTITY;
        }
        let t = (offset - self.start) as f64 / 1_000_000.0 * self.speed;
        match self.kind {
            VideoEffectKind::PuddingBounce => Self::pudding_bounce(t),
            VideoEffectKind::HazyRotation => Self::hazy_rotation(t),
        }
    }

    // Calibration against the author's CapCut export ("Мокрая прогулка", 148.9 s and 31.5 s; 30 fps frame grabs).

    /// White mix at the first frame (mean luma 146 → 204 of 255) and its decay time: halves every 2 frames.
    pub const PUDDING_FLASH: f64 = 0.53;
    pub const PUDDING_FLASH_DECAY: f64 = 0.097;
    /// Zoom at the first frame (≈1.45×), its decay and the bounce period (a slight undershoot at ≈0.3 s).
    pub const PUDDING_ZOOM: f64 = 0.45;
    pub const PUDDING_ZOOM_DECAY: f64 = 0.09;
    pub const PUDDING_BOUNCE_PERIOD: f64 = 0.6;
    /// Blur at the first frame (fraction of the picture's shorter side) and when it is gone (sharp again at 0.4 s).
    pub const PUDDING_BLUR: f64 = 0.008;
    pub const PUDDING_BLUR_END: f64 = 0.4;
    pub const PUDDING_END: f64 = 0.8;

    pub fn pudding_bounce(t: f64) -> VideoEffectState {
        if t >= Self::PUDDING_END || t.is_nan() {
            return VideoEffectState::IDENTITY;
        }
        let blur_left = (1.0 - t / Self::PUDDING_BLUR_END).max(0.0);
        VideoEffectState {
            zoom: 1.0
                + Self::PUDDING_ZOOM
                    * (-t / Self::PUDDING_ZOOM_DECAY).exp()
                    * (2.0 * PI * t / Self::PUDDING_BOUNCE_PERIOD).cos(),
            rotation: 0.0,
            blur: Self::PUDDING_BLUR * blur_left * blur_left,
            flash: if t < 0.35 { Self::PUDDING_FLASH * (-t / Self::PUDDING_FLASH_DECAY).exp() } else { 0.0 },
        }
    }

    /// The opening hold (zoomed in ≈2×, tilted, heavy blur) and the settle that follows (gone at ≈0.36 s).
    pub const HAZY_HOLD: f64 = 0.13;
    pub const HAZY_SETTLE: f64 = 0.23;
    pub const HAZY_ZOOM: f64 = 1.0;
    pub const HAZY_TILT: f64 = -15.0;
    pub const HAZY_BLUR: f64 = 0.012;

    pub fn hazy_rotation(t: f64) -> VideoEffectState {
        if t >= Self::HAZY_HOLD + Self::HAZY_SETTLE || t.is_nan() {
            return VideoEffectState::IDENTITY;
        }
        let u = (t - Self::HAZY_HOLD).max(0.0) / Self::HAZY_SETTLE;
        let left = (1.0 - u) * (1.0 - u);
        VideoEffectState {
            zoom: 1.0 + Self::HAZY_ZOOM * left,
            rotation: Self::HAZY_TILT * left,
            blur: Self::HAZY_BLUR * left,
            flash: 0.0,
        }
    }
}

/// What a video effect does to a clip's picture at one moment, applied in this order inside the picture's own frame:
/// blur, then zoom and rotation about the centre (edges clamped), then a mix towards white.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VideoEffectState {
    pub zoom: f64,
    /// Degrees, clockwise on screen.
    pub rotation: f64,
    /// Gaussian blur radius as a fraction of the picture's shorter side.
    pub blur: f64,
    /// Mix towards white, 0...1 (alpha kept).
    pub flash: f64,
}

impl VideoEffectState {
    pub const IDENTITY: VideoEffectState = VideoEffectState { zoom: 1.0, rotation: 0.0, blur: 0.0, flash: 0.0 };

    pub fn is_identity(&self) -> bool {
        (self.zoom - 1.0).abs() < 0.0005 && self.rotation.abs() < 0.01 && self.blur < 0.0002 && self.flash < 0.002
    }

    /// Several effects on one clip: zooms multiply, the rest add up.
    pub fn combined(states: impl IntoIterator<Item = VideoEffectState>) -> VideoEffectState {
        states.into_iter().fold(Self::IDENTITY, |a, b| VideoEffectState {
            zoom: a.zoom * b.zoom,
            rotation: a.rotation + b.rotation,
            blur: a.blur + b.blur,
            flash: (a.flash + b.flash).min(1.0),
        })
    }
}

// MARK: - Blend modes

/// An overlay clip's blend mode (CapCut's "Blend" panel), a `mix_mode` material in `materials.effects` (see
/// FORMAT.md). The modes use the standard separable blend formulas applied to gamma-encoded sRGB values.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum BlendMode {
    Normal,
    Screen,
    Darken,
    Lighten,
    Overlay,
    HardLight,
    SoftLight,
    LinearBurn,
    ColorBurn,
    ColorDodge,
    Multiply,
}

impl BlendMode {
    pub const ALL: [BlendMode; 11] = [
        BlendMode::Normal,
        BlendMode::Screen,
        BlendMode::Darken,
        BlendMode::Lighten,
        BlendMode::Overlay,
        BlendMode::HardLight,
        BlendMode::SoftLight,
        BlendMode::LinearBurn,
        BlendMode::ColorBurn,
        BlendMode::ColorDodge,
        BlendMode::Multiply,
    ];

    /// The Swift case name (`hardLight`), used in dumps.
    pub fn name(&self) -> &'static str {
        match self {
            BlendMode::Normal => "normal",
            BlendMode::Screen => "screen",
            BlendMode::Darken => "darken",
            BlendMode::Lighten => "lighten",
            BlendMode::Overlay => "overlay",
            BlendMode::HardLight => "hardLight",
            BlendMode::SoftLight => "softLight",
            BlendMode::LinearBurn => "linearBurn",
            BlendMode::ColorBurn => "colorBurn",
            BlendMode::ColorDodge => "colorDodge",
            BlendMode::Multiply => "multiply",
        }
    }

    /// The ids CapCut uses for each mode: its effect id, resource id, internal name and English UI name.
    pub fn capcut_ids(&self) -> &'static [&'static str] {
        match self {
            BlendMode::Normal => &["normal"],
            BlendMode::Screen => &["871339", "6758325170760323597", "color_filter", "screen"],
            BlendMode::Darken => &["871342", "6758324839670354445", "dark_en", "darken"],
            BlendMode::Lighten => &["871341", "6758324919789949453", "bright_en", "brighten", "lighten"],
            BlendMode::Overlay => &["871340", "6758324989931295240", "over_lay", "overlay"],
            BlendMode::HardLight => &["871338", "6758325264670790152", "glare_pc", "hard light"],
            BlendMode::SoftLight => &["871337", "6758325439212556814", "soft_light", "soft light"],
            BlendMode::LinearBurn => &["871336", "6758325619253056013", "linear_deepening", "linear burn"],
            BlendMode::ColorBurn => &["871335", "6758325724848853518", "darken_color", "color burn"],
            BlendMode::ColorDodge => &["871334", "6758325800031752712", "color_dodge", "color dodge"],
            BlendMode::Multiply => &["871333", "6758325895519277582", "multiply_blend_mode", "multiply"],
        }
    }

    fn find(key: &str) -> Option<BlendMode> {
        Self::ALL.into_iter().find(|mode| mode.capcut_ids().contains(&key))
    }

    /// The mode of a `mix_mode` material, `None` when it isn't one of the known modes. The draft's `effect_id` /
    /// `resource_id` are matched first, then `name`.
    pub fn from_material(material: &GenericMaterial) -> Option<BlendMode> {
        let name = material.name.to_lowercase();
        let keys = [material.effect_id.as_str(), material.resource_id.as_str(), name.as_str()];
        for key in keys.into_iter().filter(|k| !k.is_empty()) {
            if let Some(mode) = Self::find(key) {
                return Some(mode);
            }
        }
        None
    }
}

// MARK: - Chroma key

/// "Chroma key" on a clip: the picked colour and CapCut's sliders (0…1). See FORMAT.md › Chroma key.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ChromaKey {
    /// The picked colour, sRGB 0…1.
    pub red: f64,
    pub green: f64,
    pub blue: f64,
    pub intensity: f64,
    pub shadow: f64,
    pub edge_smooth: f64,
    pub spill: f64,
}

impl ChromaKey {
    /// `None` when CapCut leaves the clip as it is: no colour picked or "Intensity" at 0.
    pub fn from_material(m: &ChromaMaterial) -> Option<ChromaKey> {
        let (r, g, b) = Self::rgb(&m.color)?;
        if m.intensity <= 0.0 || m.intensity.is_nan() {
            return None;
        }
        let unit = |v: f64| v.clamp(0.0, 1.0);
        Some(ChromaKey {
            red: r,
            green: g,
            blue: b,
            intensity: unit(m.intensity),
            shadow: unit(m.shadow),
            edge_smooth: unit(m.edge_smooth),
            spill: unit(m.spill),
        })
    }

    /// "#RRGGBB" or "#AARRGGBB" (the alpha is ignored).
    pub fn rgb(hex: &str) -> Option<(f64, f64, f64)> {
        // `.whitespaces`: spaces and tabs, not line breaks.
        let s = hex.trim_matches(|c: char| {
            c.is_whitespace() && !matches!(c, '\n' | '\r' | '\u{0B}' | '\u{0C}' | '\u{85}' | '\u{2028}' | '\u{2029}')
        });
        let s = s.strip_prefix('#').unwrap_or(s);
        if s.chars().count() != 6 && s.chars().count() != 8 {
            return None;
        }
        let v = u64::from_str_radix(s, 16).ok()?;
        Some((((v >> 16) & 0xFF) as f64 / 255.0, ((v >> 8) & 0xFF) as f64 / 255.0, (v & 0xFF) as f64 / 255.0))
    }
}

// MARK: - Clock stickers

/// CapCut's "Clock Time" stickers, re-drawn natively: a round clock whose two hands spin in a loop.
///
/// Calibrated on the author's CapCut exports (Pullups3Wide, Pullups5Wide, 1920×1080, 30 fps): the clock is centered on
/// the clip position, its outer diameter is `diameter` × the canvas's shorter side × the clip scale, and the animation
/// starts over at the segment start every `PERIOD`. Sizes below are fractions of the outer radius.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ClockStyle {
    /// "Clock Time EN White Outline Progress Bar": a white ring and white hands, no face.
    WhiteOutline,
    /// "Clock Time EN Blue White Minimalist": a grey ring, a white face and blue hands.
    BlueWhite,
}

impl ClockStyle {
    pub fn name(&self) -> &'static str {
        match self {
            ClockStyle::WhiteOutline => "whiteOutline",
            ClockStyle::BlueWhite => "blueWhite",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ClockSticker {
    pub style: ClockStyle,
}

impl ClockSticker {
    /// The sticker resources this draws, by `materials.stickers[].resource_id`.
    pub fn style(resource_id: &str) -> Option<ClockStyle> {
        match resource_id {
            "7528282080199904565" => Some(ClockStyle::WhiteOutline),
            "7528280675909209405" => Some(ClockStyle::BlueWhite),
            _ => None,
        }
    }

    fn white(&self) -> bool {
        self.style == ClockStyle::WhiteOutline
    }

    /// Outer diameter at clip scale 1, as a fraction of the canvas's shorter side (102 px at scale 0.214 and 136 px
    /// at scale 0.281 on a 1080 px tall canvas).
    pub fn diameter(&self) -> f64 {
        if self.white() {
            0.441
        } else {
            0.448
        }
    }
    /// Ring (outline) width.
    pub fn ring_width(&self) -> f64 {
        if self.white() {
            0.157
        } else {
            0.191
        }
    }
    /// Hand lengths from the center to the tip of the rounded end, and the hands' width.
    pub fn minute_hand(&self) -> f64 {
        if self.white() {
            0.686
        } else {
            0.674
        }
    }
    pub fn hour_hand(&self) -> f64 {
        if self.white() {
            0.451
        } else {
            0.465
        }
    }
    pub fn hand_width(&self) -> f64 {
        if self.white() {
            0.137
        } else {
            0.132
        }
    }
    /// sRGB colours, 0…1; `face` `None` = transparent.
    pub fn ring_color(&self) -> [f64; 3] {
        if self.white() {
            [1.0, 1.0, 1.0]
        } else {
            [0.325, 0.325, 0.325]
        }
    }
    pub fn face_color(&self) -> Option<[f64; 3]> {
        (!self.white()).then_some([0.976, 0.976, 0.976])
    }
    pub fn hand_color(&self) -> [f64; 3] {
        if self.white() {
            [1.0, 1.0, 1.0]
        } else {
            [0.0, 0.471, 0.824]
        }
    }

    /// One loop of the animation: 82 frames at 30 fps.
    pub const PERIOD: i64 = 2_733_333;
    /// Measured hand angles every 1/15 s of the loop, in degrees clockwise from 12 o'clock, relative to the rest pose
    /// (minute hand at 12, hour hand at 3). The minute hand turns twice, the hour hand once, both slowing down; the
    /// last ~0.13 s of the loop they rest.
    const MINUTE_TABLE: [f64; 40] = [
        0.0, 37.0, 73.0, 107.0, 141.0, 188.0, 217.0, 245.0, 270.0, 303.0, 321.0, 336.0, 347.0, 354.0, 370.0, 389.0,
        408.0, 425.0, 452.0, 469.0, 486.0, 503.0, 519.0, 543.0, 558.0, 574.0, 588.0, 609.0, 621.0, 633.0, 645.0, 657.0,
        674.0, 680.0, 689.0, 699.0, 706.0, 711.0, 715.0, 720.0,
    ];
    const HOUR_TABLE: [f64; 40] = [
        0.0, 13.0, 29.0, 47.0, 57.0, 67.0, 84.0, 96.0, 109.0, 126.0, 138.0, 150.0, 162.0, 172.0, 189.0, 199.0, 208.0,
        218.0, 232.0, 241.0, 249.0, 258.0, 267.0, 277.0, 285.0, 292.0, 300.0, 309.0, 315.0, 320.0, 325.0, 330.0, 337.0,
        342.0, 346.0, 349.0, 355.0, 356.0, 357.0, 360.0,
    ];
    const TABLE_STEP: f64 = 1_000_000.0 / 15.0;

    /// The hands' angles (degrees clockwise from 12 o'clock) `offset` µs after the segment start: (minute, hour).
    pub fn hand_angles(offset: i64) -> (f64, f64) {
        let mut t = offset % Self::PERIOD;
        if t < 0 {
            t += Self::PERIOD;
        }
        let x = t as f64 / Self::TABLE_STEP;
        let sample = |table: &[f64; 40]| {
            let i = x as usize;
            if i + 1 >= table.len() {
                return table[table.len() - 1];
            }
            table[i] + (table[i + 1] - table[i]) * (x - i as f64)
        };
        // `truncatingRemainder` is Rust's `%` on floats.
        (sample(&Self::MINUTE_TABLE) % 360.0, (90.0 + sample(&Self::HOUR_TABLE)) % 360.0)
    }
}

/// A sticker clip the renderer draws itself.
#[derive(Debug, Clone, PartialEq)]
pub struct StickerSpec {
    /// The sticker's name in CapCut.
    pub name: String,
    pub clock: ClockSticker,
}

// MARK: - Text effects

/// A soft halo drawn around the glyphs (and their outline): our approximation of CapCut's "… Glow" text effects.
#[derive(Debug, Clone, PartialEq)]
pub struct TextGlow {
    /// RGB in 0...1.
    pub color: Vec<f64>,
    /// Blur radius relative to the font size.
    pub radius: f64,
}

/// CapCut text effects ("word art", `materials.effects` of type `text_effect`) are packages of shaders and textures
/// downloaded into CapCut's cache; we don't run them. A text uses one only when its content style has an
/// `effectStyle`; for effects that are in use, the common looks are approximated from the effect's name.
pub struct TextEffectLook;

impl TextEffectLook {
    /// Measured on the "ART Artistic EN Yellow Glow" export: the bright rim fades out about an eighth of the font size
    /// beyond the outline.
    pub const GLOW_RADIUS: f64 = 0.12;

    /// Colour words found in effect names, in the order they're checked.
    const COLOR_WORDS: [(&'static str, [f64; 3]); 11] = [
        ("yellow", [1.0, 0.86, 0.1]),
        ("gold", [1.0, 0.78, 0.2]),
        ("orange", [1.0, 0.55, 0.1]),
        ("red", [1.0, 0.15, 0.15]),
        ("pink", [1.0, 0.4, 0.75]),
        ("purple", [0.65, 0.3, 1.0]),
        ("violet", [0.65, 0.3, 1.0]),
        ("blue", [0.2, 0.5, 1.0]),
        ("cyan", [0.1, 0.9, 1.0]),
        ("green", [0.2, 1.0, 0.35]),
        ("white", [1.0, 1.0, 1.0]),
    ];

    /// The glow for an effect named like "ART Artistic EN Yellow Glow"; `None` when the name isn't a glow. Without a
    /// colour word the glow takes `fill`, the text's own colour.
    pub fn glow(name: &str, fill: Option<&[f64]>) -> Option<TextGlow> {
        let lower = name.to_lowercase();
        let words: Vec<&str> = lower.split(|c: char| !c.is_alphanumeric()).filter(|w| !w.is_empty()).collect();
        if !words.contains(&"glow") {
            return None;
        }
        let color = Self::COLOR_WORDS
            .iter()
            .find(|(w, _)| words.contains(w))
            .map(|(_, c)| c.to_vec())
            .or_else(|| fill.map(<[f64]>::to_vec))
            .unwrap_or_else(|| vec![1.0, 1.0, 1.0]);
        Some(TextGlow { color, radius: Self::GLOW_RADIUS })
    }
}
