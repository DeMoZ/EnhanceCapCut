//! Batched per-frame evaluation (design decision 4): everything the compositors ask a timeline at one moment, in one
//! call. It applies the same rules as `FrameCompositor.frame(at:)`: a layer is on screen while it's active, and both
//! clips of a transition stay on screen for the transition's whole range.

use super::{
    LayerTransform, MaskGeometry, TextAnimationState, Timeline, TransitionKind, VideoEffectState, VisualContent,
};
use crate::timeline::looks::ClockSticker;

/// A transition the layer is in at this moment, into `next`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TransitionFrame {
    /// The incoming layer's index in the timeline's `layers`.
    pub next: usize,
    pub kind: TransitionKind,
    /// 0 at the start, 0.5 at the cut, 1 at the end.
    pub progress: f64,
}

/// One on-screen layer at one moment.
#[derive(Debug, Clone, PartialEq)]
pub struct LayerFrame {
    /// The layer's index in the timeline's `layers` (paint order).
    pub index: usize,
    /// µs since the layer's start (negative or past its end while it's held for a transition).
    pub offset: i64,
    /// The source media time shown (µs): video frame, mask and brush time, sticker animation time.
    pub source_time: i64,
    /// The clip transform with keyframes and clip animations.
    pub transform: LayerTransform,
    /// Text layers: the text animations' state.
    pub text: Option<TextAnimationState>,
    /// Layers with video effects: their combined state.
    pub effect: Option<VideoEffectState>,
    /// The masks' geometry at `source_time`, one per mask.
    pub masks: Vec<MaskGeometry>,
    /// The transition out of this layer, when it's playing.
    pub transition: Option<TransitionFrame>,
    /// Clock stickers: the hands' angles (minute, hour), degrees clockwise from 12 o'clock.
    pub clock_hands: Option<(f64, f64)>,
}

/// The whole timeline at one moment.
#[derive(Debug, Clone, PartialEq)]
pub struct FrameState {
    pub time: i64,
    /// On-screen layers, bottom to top.
    pub layers: Vec<LayerFrame>,
    /// Filter-track filters active now: indices into the timeline's `filters`.
    pub filters: Vec<usize>,
}

impl Timeline {
    /// Every on-screen layer's state at timeline time `t` (µs).
    pub fn frame(&self, t: i64) -> FrameState {
        let mut mixing = vec![false; self.layers.len()];
        for (i, layer) in self.layers.iter().enumerate() {
            if let Some(transition) = layer.transition_out.as_ref().filter(|tr| tr.contains(t)) {
                mixing[i] = true;
                if let Some(m) = mixing.get_mut(transition.next) {
                    *m = true;
                }
            }
        }
        let layers = self
            .layers
            .iter()
            .enumerate()
            .filter(|(i, l)| l.is_active(t) || mixing[*i])
            .map(|(index, layer)| {
                let offset = t - layer.target.start;
                let source_time = layer.time_map.source_time(offset);
                LayerFrame {
                    index,
                    offset,
                    source_time,
                    transform: layer.transform(t),
                    text: match &layer.content {
                        VisualContent::Text(spec) => Some(spec.animations.state(offset, layer.target.duration)),
                        _ => None,
                    },
                    effect: (!layer.effects.is_empty())
                        .then(|| VideoEffectState::combined(layer.effects.iter().map(|e| e.state(offset)))),
                    masks: layer.masks.iter().map(|m| m.geometry_at(source_time)).collect(),
                    transition: layer.transition_out.as_ref().filter(|tr| tr.contains(t)).map(|tr| TransitionFrame {
                        next: tr.next,
                        kind: tr.kind,
                        progress: tr.progress(t),
                    }),
                    clock_hands: match &layer.content {
                        VisualContent::Sticker(_) => Some(ClockSticker::hand_angles(source_time)),
                        _ => None,
                    },
                }
            })
            .collect();
        let filters = self.filters.iter().enumerate().filter(|(_, f)| f.is_active(t)).map(|(i, _)| i).collect();
        FrameState { time: t, layers, filters }
    }
}
