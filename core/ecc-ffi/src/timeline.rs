//! The objects: a built `Timeline` (immutable, shared), and the `TimeMap` / `KeyframeTrack` curves with vectorized
//! methods for the audio renderer (design decision 4).

use std::sync::Arc;

use ecc::project::CapCutProject;
use ecc::timeline as t;
use ecc::TimelineBuilder;

use crate::convert::Ffi;
use crate::project::{CoreEnvironment, ProjectTimeline};
use crate::types::{AudioClip, FilterLayer, FrameState, RenderReport, Size, VisualLayer};
use crate::{guarded, CoreError};

/// A draft resolved into what the renderers need: visual layers in paint order, audio clips, filter layers and the
/// report of what isn't rendered. Equal when it's the same built timeline (identity).
#[derive(Debug, uniffi::Object)]
#[uniffi::export(Eq, Hash)]
pub struct Timeline {
    inner: Arc<t::Timeline>,
}

impl PartialEq for Timeline {
    fn eq(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.inner, &other.inner)
    }
}

impl Eq for Timeline {}

impl std::hash::Hash for Timeline {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        (Arc::as_ptr(&self.inner) as usize).hash(state)
    }
}

impl Timeline {
    pub(crate) fn wrap(inner: Arc<t::Timeline>) -> Arc<Timeline> {
        Arc::new(Timeline { inner })
    }

    /// The core timeline (for the other modules and the tests).
    pub fn core(&self) -> &t::Timeline {
        &self.inner
    }
}

#[uniffi::export]
impl Timeline {
    /// Builds `timeline` of the project in `project_path`, or its main timeline when `None`.
    #[uniffi::constructor]
    pub fn build(
        project_path: String,
        timeline: Option<ProjectTimeline>,
        env: CoreEnvironment,
    ) -> Result<Arc<Timeline>, CoreError> {
        guarded(|| {
            let project = CapCutProject::new(project_path);
            let timeline = timeline.map(ecc::project::ProjectTimeline::from);
            let built = TimelineBuilder::build_project(&project, timeline.as_ref(), &env.into())?;
            Ok(Timeline::wrap(Arc::new(built)))
        })
    }

    /// Builds a timeline from draft bytes (for example a draft without earlier voice-over); media paths are
    /// resolved against `project_path`.
    #[uniffi::constructor]
    pub fn from_draft(draft: Vec<u8>, project_path: String, env: CoreEnvironment) -> Result<Arc<Timeline>, CoreError> {
        guarded(|| {
            let text = String::from_utf8(draft).map_err(|e| CoreError::Malformed(e.to_string()))?;
            let parsed = ecc::draft::Draft::parse(&text).map_err(CoreError::Malformed)?;
            let built = TimelineBuilder::build(&parsed, std::path::Path::new(&project_path), &env.into());
            Ok(Timeline::wrap(Arc::new(built)))
        })
    }

    pub fn name(&self) -> String {
        self.inner.name.clone()
    }

    /// The draft's canvas in pixels.
    pub fn canvas(&self) -> Size {
        Size { width: self.inner.canvas_width, height: self.inner.canvas_height }
    }

    /// µs.
    pub fn duration(&self) -> i64 {
        self.inner.duration
    }

    pub fn fps(&self) -> f64 {
        self.inner.fps
    }

    /// Bottom to top.
    pub fn layers(&self) -> Vec<VisualLayer> {
        self.inner.layers.clone().ffi()
    }

    pub fn audio_clips(&self) -> Vec<AudioClip> {
        self.inner.audio_clips.clone().ffi()
    }

    /// Filters on filter tracks (each colours the layers painted below it).
    pub fn filters(&self) -> Vec<FilterLayer> {
        self.inner.filters.clone().ffi()
    }

    pub fn report(&self) -> RenderReport {
        self.inner.report.clone().ffi()
    }

    pub fn has_subtitles(&self) -> bool {
        self.inner.has_subtitles()
    }

    /// The same timeline without caption (auto-subtitle) clips.
    pub fn without_subtitles(&self) -> Arc<Timeline> {
        Timeline::wrap(Arc::new(self.inner.without_subtitles()))
    }

    /// Every on-screen layer's state at timeline time `t` (µs): one call per video frame.
    pub fn frame(&self, t: i64) -> Result<FrameState, CoreError> {
        guarded(|| Ok(self.inner.frame(t).ffi()))
    }

    /// `frame` at each of `times`.
    pub fn frames(&self, times: Vec<i64>) -> Result<Vec<FrameState>, CoreError> {
        guarded(|| Ok(times.into_iter().map(|t| self.inner.frame(t).ffi()).collect()))
    }
}

/// Maps a clip's offset on the timeline to the source time shown, with speed and speed curves.
#[derive(Debug, uniffi::Object)]
pub struct TimeMap {
    inner: t::TimeMap,
}

impl Ffi for t::TimeMap {
    type Out = Arc<TimeMap>;
    fn ffi(self) -> Arc<TimeMap> {
        Arc::new(TimeMap { inner: self })
    }
}

impl TimeMap {
    pub fn core(&self) -> &t::TimeMap {
        &self.inner
    }
}

#[uniffi::export]
impl TimeMap {
    /// Constant speed: `source_duration` of source over `target_duration` of timeline.
    #[uniffi::constructor]
    pub fn constant(source_start: i64, source_duration: i64, target_duration: i64) -> Arc<TimeMap> {
        t::TimeMap::constant(source_start, source_duration, target_duration).ffi()
    }

    pub fn source_start(&self) -> i64 {
        self.inner.source_start
    }

    pub fn source_duration(&self) -> i64 {
        self.inner.source_duration
    }

    pub fn target_duration(&self) -> i64 {
        self.inner.target_duration
    }

    /// Normalized cumulative source position S(u) sampled at `u = i / (count - 1)`; `None` at constant speed.
    pub fn curve_table(&self) -> Option<Vec<f64>> {
        self.inner.curve_table.clone()
    }

    pub fn is_constant(&self) -> bool {
        self.inner.is_constant()
    }

    pub fn average_speed(&self) -> f64 {
        self.inner.average_speed()
    }

    /// The source time (µs) at `offset` µs from the clip start.
    pub fn source_time(&self, offset: i64) -> i64 {
        self.inner.source_time(offset)
    }

    /// `source_time` of each offset.
    pub fn source_times(&self, offsets: Vec<i64>) -> Vec<i64> {
        self.inner.source_times(&offsets)
    }

    pub fn precise_source_time(&self, offset: f64) -> f64 {
        self.inner.precise_source_time(offset)
    }

    /// Inverse of `source_time`: the offset at which source time `time` is shown (clamped to the segment).
    pub fn offset_at_source_time(&self, time: i64) -> i64 {
        self.inner.offset_at_source_time(time)
    }

    /// The part of this map between two offsets, as a map of its own (offset 0 = `a`).
    pub fn slice(&self, a: i64, b: i64) -> Arc<TimeMap> {
        self.inner.slice(a, b).ffi()
    }

    /// S(u) in 0...1.
    pub fn normalized_source(&self, u: f64) -> f64 {
        self.inner.normalized_source(u)
    }

    /// Instantaneous speed multiplier at normalized position u (relative to the average).
    pub fn speed(&self, u: f64) -> f64 {
        self.inner.speed(u)
    }
}

/// A keyframed value over time (offsets in µs).
#[derive(Debug, uniffi::Object)]
pub struct KeyframeTrack {
    inner: t::KeyframeTrack,
}

impl Ffi for t::KeyframeTrack {
    type Out = Arc<KeyframeTrack>;
    fn ffi(self) -> Arc<KeyframeTrack> {
        Arc::new(KeyframeTrack { inner: self })
    }
}

impl KeyframeTrack {
    pub fn core(&self) -> &t::KeyframeTrack {
        &self.inner
    }
}

#[uniffi::export]
impl KeyframeTrack {
    /// Linear keyframes.
    #[uniffi::constructor]
    pub fn linear(times: Vec<i64>, values: Vec<f64>) -> Arc<KeyframeTrack> {
        t::KeyframeTrack::new(times, values).ffi()
    }

    pub fn times(&self) -> Vec<i64> {
        self.inner.times.clone()
    }

    pub fn values(&self) -> Vec<f64> {
        self.inner.values.clone()
    }

    /// The value at `offset`.
    pub fn value(&self, offset: i64) -> f64 {
        self.inner.value(offset)
    }

    /// `value` at each offset.
    pub fn values_at(&self, offsets: Vec<i64>) -> Vec<f64> {
        self.inner.values_at(&offsets)
    }

    /// The keyframe times plus, inside curved spans, a time every `step`: the breaks of a piecewise-linear
    /// approximation (audio volume ramps).
    pub fn breaks(&self, step: i64) -> Vec<i64> {
        self.inner.breaks(step)
    }
}
