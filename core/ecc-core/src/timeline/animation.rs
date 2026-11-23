//! Clip and text animations, the port of `ClipAnimation.swift` and `TextAnimation.swift`: CapCut's presets are
//! recognised by name and re-implemented as curves on top of the layer's transform. The constants are where to
//! calibrate them (our own approximation, not a copy of CapCut's packages).

use std::f64::consts::PI;

use unicode_segmentation::UnicodeSegmentation;

use super::LayerTransform;
use crate::draft::AnimationMaterial;

fn has(n: &str, words: &[&str]) -> bool {
    words.iter().any(|w| n.contains(w))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ClipAnimationKind {
    In,
    Out,
}

impl ClipAnimationKind {
    fn parse(s: &str) -> Option<ClipAnimationKind> {
        match s {
            "in" => Some(ClipAnimationKind::In),
            "out" => Some(ClipAnimationKind::Out),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ClipAnimationPreset {
    Fade,
    ZoomIn,
    ZoomOut,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Rotate,
    RotateCounterclockwise,
    Shake,
}

/// A video or photo clip's In / Out animation.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ClipAnimation {
    pub kind: ClipAnimationKind,
    pub preset: ClipAnimationPreset,
    /// Start and end in µs from the segment start.
    pub start: i64,
    pub end: i64,
}

impl ClipAnimation {
    /// Scale the clip starts from (In) or ends at (Out) for "Zoom In" / "Zoom Out".
    pub const ZOOM_IN_SCALE: f64 = 0.5;
    pub const ZOOM_OUT_SCALE: f64 = 1.5;
    /// How far a slide travels, in half-canvas units (2 = one canvas width or height: from off-screen).
    pub const SLIDE_DISTANCE: f64 = 2.0;
    /// Full turns of "Rotate".
    pub const ROTATE_DEGREES: f64 = 360.0;
    /// "Shake": peak offset in half-canvas units, and the number of swings over the animation.
    pub const SHAKE_AMPLITUDE: f64 = 0.06;
    pub const SHAKE_SWINGS: f64 = 4.0;

    /// The clip's animations, and the names of the ones the renderer doesn't reproduce (Combo and unknown presets).
    pub fn make(material: Option<&AnimationMaterial>, segment_duration: i64) -> (Vec<ClipAnimation>, Vec<String>) {
        let (mut din, mut dout) = (0i64, 0i64);
        let mut found = Vec::new();
        let mut unsupported = Vec::new();
        for item in material.map(|m| m.animations.as_slice()).unwrap_or_default() {
            let (Some(kind), Some(preset)) = (ClipAnimationKind::parse(&item.r#type), Self::preset(&item.name)) else {
                unsupported.push(if item.name.is_empty() { item.r#type.clone() } else { item.name.clone() });
                continue;
            };
            if item.duration <= 0 {
                unsupported.push(if item.name.is_empty() { item.r#type.clone() } else { item.name.clone() });
                continue;
            }
            if kind == ClipAnimationKind::In {
                din = item.duration;
            } else {
                dout = item.duration;
            }
            found.push((kind, preset));
        }
        // `start` is always 0 in the draft: an Out plays in the clip's last `duration`. An In and Out that don't fit
        // are squeezed into the clip, as for texts.
        let total = segment_duration.max(0);
        if din + dout > total && din + dout > 0 {
            din = (din as f64 * total as f64 / (din + dout) as f64) as i64;
            dout = total - din;
        }
        let animations = found
            .into_iter()
            .map(|(kind, preset)| match kind {
                ClipAnimationKind::In => ClipAnimation { kind, preset, start: 0, end: din },
                ClipAnimationKind::Out => ClipAnimation { kind, preset, start: total - dout, end: total },
            })
            .filter(|a| a.end > a.start)
            .collect();
        (animations, unsupported)
    }

    /// The preset for a CapCut animation name (English, Chinese or Russian UI), or `None` when it isn't reproduced.
    pub fn preset(name: &str) -> Option<ClipAnimationPreset> {
        use ClipAnimationPreset::*;
        let n = name.to_lowercase().replace('-', " ");
        let n = n.as_str();
        if has(n, &["counterclockwise", "anticlockwise", "逆时针", "против час"]) {
            return Some(RotateCounterclockwise);
        }
        if has(n, &["rotat", "spin", "旋转", "вращ", "поворот"]) {
            return Some(Rotate);
        }
        if has(n, &["shake", "抖动", "тряс", "дрож"]) {
            return Some(Shake);
        }
        if has(n, &["slide", "滑动", "сдвиг", "скольж", "слайд"]) {
            if has(n, &["left", "向左", "влево", "налево"]) {
                return Some(SlideLeft);
            }
            if has(n, &["right", "向右", "вправо", "направо"]) {
                return Some(SlideRight);
            }
            if has(n, &["up", "向上", "вверх"]) {
                return Some(SlideUp);
            }
            if has(n, &["down", "向下", "вниз"]) {
                return Some(SlideDown);
            }
            return None;
        }
        if has(n, &["zoom in", "放大", "увелич", "приближ"]) {
            return Some(ZoomIn);
        }
        if has(n, &["zoom out", "缩小", "уменьш", "отдал"]) {
            return Some(ZoomOut);
        }
        if has(n, &["fade", "渐显", "渐隐", "растворен", "проявлен", "затухан", "угасан", "появлен", "исчезн"])
        {
            return Some(Fade);
        }
        None
    }

    /// Applies the animations that are running at `offset` (µs from the segment start).
    pub fn apply_all(animations: &[ClipAnimation], transform: LayerTransform, offset: i64) -> LayerTransform {
        animations.iter().fold(transform, |t, a| a.apply(t, offset))
    }

    pub fn apply(&self, transform: LayerTransform, offset: i64) -> LayerTransform {
        // Before an In the clip is in its far state; after an Out it stays there.
        if offset < self.start {
            return if self.kind == ClipAnimationKind::In { self.apply_progress(transform, 0.0) } else { transform };
        }
        if offset >= self.end {
            return if self.kind == ClipAnimationKind::Out { self.apply_progress(transform, 1.0) } else { transform };
        }
        self.apply_progress(transform, (offset - self.start) as f64 / (self.end - self.start) as f64)
    }

    /// `amount` = 1 at the animation's far state (start of In, end of Out), 0 at the clip's own transform.
    pub fn apply_progress(&self, mut t: LayerTransform, p: f64) -> LayerTransform {
        use ClipAnimationPreset::*;
        let is_in = self.kind == ClipAnimationKind::In;
        // Ease out into the clip (In), ease in away from it (Out).
        let amount = if is_in { (1.0 - p).powf(3.0) } else { p.powf(3.0) };
        match self.preset {
            Fade => t.alpha *= 1.0 - amount,
            ZoomIn | ZoomOut => {
                // "Zoom In" grows: In starts small, Out ends large. "Zoom Out" shrinks: the other way round.
                let grows = self.preset == ZoomIn;
                let far = if is_in == grows { Self::ZOOM_IN_SCALE } else { Self::ZOOM_OUT_SCALE };
                let s = 1.0 + (far - 1.0) * amount;
                t.scale_x *= s;
                t.scale_y *= s;
                t.alpha *= 1.0 - amount;
            }
            SlideLeft | SlideRight | SlideUp | SlideDown => {
                // The direction is the motion: an In "Slide Right" comes from the left, an Out one leaves to the right.
                let (dx, dy) = match self.preset {
                    SlideLeft => (-1.0, 0.0),
                    SlideRight => (1.0, 0.0),
                    SlideUp => (0.0, 1.0),
                    _ => (0.0, -1.0),
                };
                let d = Self::SLIDE_DISTANCE * amount * if is_in { -1.0 } else { 1.0 };
                t.position_x += dx * d;
                t.position_y += dy * d;
            }
            Rotate | RotateCounterclockwise => {
                let sign = if self.preset == Rotate { 1.0 } else { -1.0 };
                t.rotation += sign * Self::ROTATE_DEGREES * amount * if is_in { -1.0 } else { 1.0 };
                t.scale_x *= 1.0 - amount;
                t.scale_y *= 1.0 - amount;
            }
            Shake => {
                // A decaying (In) or growing (Out) side-to-side swing; linear envelope so it's visible throughout.
                let envelope = if is_in { 1.0 - p } else { p };
                t.position_x += Self::SHAKE_AMPLITUDE * envelope * (2.0 * PI * Self::SHAKE_SWINGS * p).sin();
            }
        }
        t
    }
}

// MARK: - Text animations

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TextAnimationKind {
    Intro,
    Outro,
    Loop,
}

impl TextAnimationKind {
    fn parse(s: &str) -> Option<TextAnimationKind> {
        match s {
            "in" => Some(TextAnimationKind::Intro),
            "out" => Some(TextAnimationKind::Outro),
            "loop" => Some(TextAnimationKind::Loop),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum TextAnimationEffect {
    Fade,
    /// Scale from (intro) or to (outro) `scale`, fading when `fades`.
    Zoom {
        scale: f64,
        fades: bool,
    },
    /// Grows from nothing with a small overshoot.
    Pop,
    /// Moves in the direction (dx, dy) (+y up): an intro arrives from the opposite side, an outro leaves.
    Slide {
        dx: f64,
        dy: f64,
    },
    /// Characters appear one by one (outro: disappear from the end).
    Typewriter,
    Blink,
    Pulse,
}

impl TextAnimationEffect {
    /// The preset for a CapCut animation name, or `None` when it isn't reproduced.
    pub fn recognize(name: &str, kind: TextAnimationKind) -> Option<TextAnimationEffect> {
        use TextAnimationEffect::*;
        let n = name.to_lowercase();
        let n = n.as_str();
        if kind == TextAnimationKind::Loop {
            if has(n, &["blink", "flicker", "flash", "闪烁", "мерца", "мига"]) {
                return Some(Blink);
            }
            if has(n, &["pulse", "heartbeat", "breath", "心跳", "呼吸", "пульс", "сердцеби", "дыхан"])
            {
                return Some(Pulse);
            }
            return None;
        }
        if has(n, &["typewriter", "typing", "打字机", "печатн", "машинк"]) {
            return Some(Typewriter);
        }
        if has(n, &["fade", "dissolve", "渐显", "渐隐", "растворен", "проявлен", "затухан", "угасан"])
        {
            return Some(Fade);
        }
        if has(n, &["pop", "弹入", "弹出", "всплыт"]) {
            return Some(Pop);
        }
        if has(n, &["slide", "move", "滑动", "移动", "сдвиг", "скольж", "слайд"]) {
            let d = TextAnimations::SLIDE_DISTANCE;
            if has(n, &["left", "左", "влево", "налево"]) {
                return Some(Slide { dx: -d, dy: 0.0 });
            }
            if has(n, &["right", "右", "вправо", "направо"]) {
                return Some(Slide { dx: d, dy: 0.0 });
            }
            if has(n, &["up", "上", "вверх"]) {
                return Some(Slide { dx: 0.0, dy: d });
            }
            if has(n, &["down", "下", "вниз"]) {
                return Some(Slide { dx: 0.0, dy: -d });
            }
            return None;
        }
        let large = TextAnimations::ZOOM_LARGE_SCALE;
        if has(n, &["zoom in", "放大", "увеличен"]) {
            // Grows: an intro from nothing, an outro beyond its size while fading.
            return Some(if kind == TextAnimationKind::Intro {
                Zoom { scale: 0.0, fades: false }
            } else {
                Zoom { scale: large, fades: true }
            });
        }
        if has(n, &["zoom out", "缩小", "уменьшен"]) {
            // Shrinks: an intro from larger while appearing, an outro to nothing.
            return Some(if kind == TextAnimationKind::Intro {
                Zoom { scale: large, fades: true }
            } else {
                Zoom { scale: 0.0, fades: false }
            });
        }
        None
    }

    /// The state at `progress` (0…1) of an intro or outro, or at the phase of a loop cycle.
    pub fn state(&self, kind: TextAnimationKind, progress: f64) -> TextAnimationState {
        use TextAnimationEffect::*;
        let p = progress.clamp(0.0, 1.0);
        let mut s = TextAnimationState::default();
        if kind == TextAnimationKind::Loop {
            let wave = 0.5 - 0.5 * (2.0 * PI * p).cos(); // 0 → 1 → 0 over a cycle
            match self {
                Blink => s.alpha = 1.0 - (1.0 - TextAnimations::BLINK_MINIMUM_ALPHA) * wave,
                Pulse => s.scale = 1.0 + TextAnimations::PULSE_SCALE * wave,
                _ => {}
            }
            return s;
        }
        let intro = kind == TextAnimationKind::Intro;
        // How far from the resting state: 1 at the start of an intro and at the end of an outro.
        let away = match self {
            Fade | Typewriter => {
                if intro {
                    1.0 - p
                } else {
                    p
                }
            }
            Pop => {
                if intro {
                    1.0 - back_out(p)
                } else {
                    p * p * p
                }
            }
            _ => {
                if intro {
                    (1.0 - p).powf(3.0)
                } else {
                    p * p * p
                }
            }
        };
        match *self {
            Fade => s.alpha = 1.0 - away,
            Zoom { scale, fades } => {
                s.scale = 1.0 + (scale - 1.0) * away;
                if fades {
                    s.alpha = 1.0 - away;
                }
            }
            Pop => s.scale = 1.0 - away,
            Slide { dx, dy } => {
                let sign = if intro { -1.0 } else { 1.0 };
                s.offset_x = sign * dx * away;
                s.offset_y = sign * dy * away;
                s.alpha = 1.0 - away;
            }
            Typewriter => s.reveal = 1.0 - away,
            Blink | Pulse => {}
        }
        s
    }
}

/// Ease-out with a small overshoot (≈ +10 %).
fn back_out(p: f64) -> f64 {
    let c = 1.70158;
    let q = p - 1.0;
    1.0 + (c + 1.0) * q * q * q + c * q * q
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TextAnimation {
    pub kind: TextAnimationKind,
    pub effect: TextAnimationEffect,
    /// µs; for a loop, one cycle.
    pub duration: i64,
}

/// CapCut's text intro / outro / loop animations.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct TextAnimations {
    pub intro: Option<TextAnimation>,
    pub outro: Option<TextAnimation>,
    pub r#loop: Option<TextAnimation>,
}

impl TextAnimations {
    /// Slide distance in half-canvas units (0.2 = a tenth of the canvas).
    pub const SLIDE_DISTANCE: f64 = 0.2;
    /// Scale a "zoom out" intro starts from (and a "zoom in" outro ends at).
    pub const ZOOM_LARGE_SCALE: f64 = 1.5;
    /// Opacity swing of "blink" and scale swing of "pulse" loops.
    pub const BLINK_MINIMUM_ALPHA: f64 = 0.2;
    pub const PULSE_SCALE: f64 = 0.1;
    /// Shortest loop cycle; CapCut's loop `duration` is the length of one cycle.
    pub const MINIMUM_LOOP_CYCLE: i64 = 100_000;

    pub fn is_empty(&self) -> bool {
        self.intro.is_none() && self.outro.is_none() && self.r#loop.is_none()
    }

    /// The animations of a clip, and the names of the ones the renderer doesn't reproduce.
    pub fn make(material: Option<&AnimationMaterial>) -> (TextAnimations, Vec<String>) {
        let mut result = TextAnimations::default();
        let mut unsupported = Vec::new();
        for item in material.map(|m| m.animations.as_slice()).unwrap_or_default() {
            let Some((kind, effect)) = TextAnimationKind::parse(&item.r#type)
                .and_then(|kind| TextAnimationEffect::recognize(&item.name, kind).map(|e| (kind, e)))
            else {
                unsupported.push(if item.name.is_empty() { item.r#type.clone() } else { item.name.clone() });
                continue;
            };
            let animation = TextAnimation { kind, effect, duration: item.duration.max(0) };
            match kind {
                TextAnimationKind::Intro => result.intro = Some(animation),
                TextAnimationKind::Outro => result.outro = Some(animation),
                TextAnimationKind::Loop => result.r#loop = Some(animation),
            }
        }
        (result, unsupported)
    }

    /// The largest scale the animations reach (text is rasterized at it so it stays sharp).
    pub fn max_scale(&self) -> f64 {
        [self.intro, self.outro, self.r#loop]
            .iter()
            .flatten()
            .map(|a| match a.effect {
                TextAnimationEffect::Zoom { scale, .. } => scale.max(1.0),
                TextAnimationEffect::Pop => 1.1,
                TextAnimationEffect::Pulse => 1.0 + Self::PULSE_SCALE,
                _ => 1.0,
            })
            .fold(None, |m: Option<f64>, v| Some(m.map_or(v, |m| m.max(v))))
            .unwrap_or(1.0)
    }

    /// The animation state at `offset` µs into a clip of `duration` µs.
    pub fn state(&self, offset: i64, duration: i64) -> TextAnimationState {
        if duration <= 0 {
            return TextAnimationState::default();
        }
        let mut din = self.intro.map_or(0, |a| a.duration);
        let mut dout = self.outro.map_or(0, |a| a.duration);
        // CapCut squeezes an intro and outro that don't fit into the clip.
        if din + dout > duration {
            let k = duration as f64 / (din + dout) as f64;
            din = (din as f64 * k) as i64;
            dout = duration - din;
        }
        if let Some(intro) = self.intro {
            if din > 0 && offset < din {
                return intro.effect.state(TextAnimationKind::Intro, offset as f64 / din as f64);
            }
        }
        if let Some(outro) = self.outro {
            if dout > 0 && offset >= duration - dout {
                return outro.effect.state(TextAnimationKind::Outro, (offset - (duration - dout)) as f64 / dout as f64);
            }
        }
        if let Some(lp) = self.r#loop {
            let cycle = lp.duration.max(Self::MINIMUM_LOOP_CYCLE);
            // Swift's `%` keeps the dividend's sign, like Rust's.
            let phase = ((offset - din) % cycle) as f64 / cycle as f64;
            return lp.effect.state(TextAnimationKind::Loop, phase);
        }
        TextAnimationState::default()
    }
}

/// What the animations do to a text clip at one moment, on top of its own transform.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TextAnimationState {
    pub alpha: f64,
    pub scale: f64,
    /// Half-canvas units, +y up.
    pub offset_x: f64,
    pub offset_y: f64,
    /// The fraction of characters shown (typewriter).
    pub reveal: f64,
}

impl Default for TextAnimationState {
    fn default() -> Self {
        TextAnimationState { alpha: 1.0, scale: 1.0, offset_x: 0.0, offset_y: 0.0, reveal: 1.0 }
    }
}

impl TextAnimationState {
    /// UTF-16 length of the part of `text` shown at `reveal`: whole characters (grapheme clusters, spaces and line
    /// breaks included), rounded down.
    pub fn visible_length(text: &str, reveal: f64) -> usize {
        if reveal >= 1.0 {
            return text.encode_utf16().count();
        }
        let count = text.graphemes(true).count();
        let shown = (reveal.max(0.0) * count as f64).floor() as usize;
        text.graphemes(true).take(shown).map(|g| g.encode_utf16().count()).sum()
    }
}
