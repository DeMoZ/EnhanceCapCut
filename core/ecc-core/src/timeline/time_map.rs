//! Time maps and keyframe curves, the port of `TimeMap.swift` and `KeyframeBezier.swift`. The arithmetic keeps
//! Swift's order of operations so both give the same doubles.

use crate::draft::{Keyframe, KeyframeGroup, Point2};

/// Maps a time offset inside a segment's timeline range to a time in its source media. Constant speed is a straight
/// line; a CapCut speed curve is integrated numerically.
#[derive(Debug, Clone, PartialEq)]
pub struct TimeMap {
    pub source_start: i64,
    pub source_duration: i64,
    pub target_duration: i64,
    /// Normalized cumulative source position S(u) sampled at `u = i / (count - 1)`; `None` for constant speed.
    pub curve_table: Option<Vec<f64>>,
}

impl TimeMap {
    pub const TABLE_SIZE: usize = 1024;

    pub fn new(source_start: i64, source_duration: i64, target_duration: i64, curve_points: &[Point2]) -> TimeMap {
        TimeMap {
            source_start,
            source_duration,
            target_duration,
            curve_table: (curve_points.len() >= 2).then(|| Self::integrate(curve_points)),
        }
    }

    pub fn constant(source_start: i64, source_duration: i64, target_duration: i64) -> TimeMap {
        Self::new(source_start, source_duration, target_duration, &[])
    }

    /// A map sampled from a monotonic function giving the source time (µs) at a normalized target position u in
    /// 0...1. Used to slice and compose maps; a straight line collapses to constant speed.
    pub fn sampled(target_duration: i64, source: impl Fn(f64) -> f64) -> TimeMap {
        let n = Self::TABLE_SIZE;
        let mut values: Vec<f64> = (0..=n).map(|i| source(i as f64 / n as f64)).collect();
        for i in 1..=n {
            values[i] = values[i].max(values[i - 1]);
        }
        let start = values[0];
        let span = values[n] - values[0];
        let source_start = start.round() as i64;
        let source_duration = span.round() as i64;
        if span <= 0.0 || span.is_nan() {
            return TimeMap { source_start, source_duration, target_duration, curve_table: None };
        }
        let table: Vec<f64> = values.iter().map(|v| (v - start) / span).collect();
        let is_line = table.iter().enumerate().all(|(i, v)| (v - i as f64 / n as f64).abs() < 1e-4);
        TimeMap { source_start, source_duration, target_duration, curve_table: (!is_line).then_some(table) }
    }

    pub fn is_constant(&self) -> bool {
        self.curve_table.is_none()
    }

    pub fn average_speed(&self) -> f64 {
        if self.target_duration > 0 {
            self.source_duration as f64 / self.target_duration as f64
        } else {
            1.0
        }
    }

    /// Source time (µs) shown at `offset` µs after the segment's timeline start.
    pub fn source_time(&self, offset: i64) -> i64 {
        self.precise_source_time(offset as f64).round() as i64
    }

    /// `source_time` for many offsets at once (the audio renderer samples every 50 ms).
    pub fn source_times(&self, offsets: &[i64]) -> Vec<i64> {
        offsets.iter().map(|&o| self.source_time(o)).collect()
    }

    pub fn precise_source_time(&self, offset: f64) -> f64 {
        if self.target_duration <= 0 {
            return self.source_start as f64;
        }
        let u = (offset / self.target_duration as f64).clamp(0.0, 1.0);
        self.source_start as f64 + self.normalized_source(u) * self.source_duration as f64
    }

    /// Inverse of `source_time`: the offset at which source time `time` is shown (clamped to the segment).
    pub fn offset_at_source_time(&self, time: i64) -> i64 {
        if self.source_duration <= 0 || self.target_duration <= 0 {
            return 0;
        }
        let s = ((time - self.source_start) as f64 / self.source_duration as f64).clamp(0.0, 1.0);
        let mut u = s;
        if !self.is_constant() {
            let (mut lo, mut hi) = (0.0f64, 1.0f64);
            for _ in 0..40 {
                let mid = (lo + hi) / 2.0;
                if self.normalized_source(mid) < s {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            u = hi;
        }
        (u * self.target_duration as f64).round() as i64
    }

    /// The part of this map between two offsets, as a map of its own (offset 0 = `a`).
    pub fn slice(&self, a: i64, b: i64) -> TimeMap {
        if self.is_constant() {
            let s0 = self.source_time(a);
            return TimeMap::constant(s0, self.source_time(b) - s0, b - a);
        }
        TimeMap::sampled(b - a, |u| self.precise_source_time(a as f64 + u * (b - a) as f64))
    }

    /// S(u) in 0...1.
    pub fn normalized_source(&self, u: f64) -> f64 {
        let Some(table) = &self.curve_table else { return u };
        let pos = u * (table.len() - 1) as f64;
        let i = (pos as usize).min(table.len() - 2);
        let f = pos - i as f64;
        table[i] + (table[i + 1] - table[i]) * f
    }

    /// Instantaneous speed multiplier at normalized position u (relative to the average).
    pub fn speed(&self, u: f64) -> f64 {
        let Some(table) = &self.curve_table else { return self.average_speed() };
        let n = (table.len() - 1) as f64;
        let i = ((u * n) as usize).min(table.len() - 2);
        (table[i + 1] - table[i]) * n * self.average_speed()
    }

    /// Integrates the piecewise-linear speed curve so that S(0) = 0 and S(1) = 1.
    fn integrate(raw_points: &[Point2]) -> Vec<f64> {
        let mut points = raw_points.to_vec();
        points.sort_by(|a, b| a.x.partial_cmp(&b.x).unwrap_or(std::cmp::Ordering::Equal));
        let v = |x: f64| -> f64 {
            if x <= points[0].x {
                return points[0].y.max(0.01);
            }
            for k in 1..points.len() {
                if x <= points[k].x {
                    let (a, b) = (points[k - 1], points[k]);
                    let t = if b.x > a.x { (x - a.x) / (b.x - a.x) } else { 1.0 };
                    return (a.y + (b.y - a.y) * t).max(0.01);
                }
            }
            points[points.len() - 1].y.max(0.01)
        };
        let n = Self::TABLE_SIZE;
        let mut table = vec![0.0f64; n + 1];
        let mut acc = 0.0f64;
        let mut prev = v(0.0);
        for (i, slot) in table.iter_mut().enumerate().skip(1) {
            let x = i as f64 / n as f64;
            let cur = v(x);
            acc += (prev + cur) * 0.5 / n as f64;
            *slot = acc;
            prev = cur;
        }
        if acc > 0.0 {
            for value in &mut table {
                *value /= acc;
            }
        }
        table
    }
}

/// A keyframe's easing: CapCut's `curveType` and its bezier handles.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct KeyframeHandles {
    /// `curveType` "Line": a span between two linear keyframes is a straight line whatever the handles say.
    pub linear: bool,
    /// `left_control` (incoming) and `right_control` (outgoing), relative to the keyframe: x in µs, y in the
    /// property's units.
    pub left: Point2,
    pub right: Point2,
}

impl KeyframeHandles {
    pub fn from_keyframe(k: &Keyframe) -> KeyframeHandles {
        KeyframeHandles {
            linear: k.curve_type.is_empty() || k.curve_type == "Line",
            left: k.left_control,
            right: k.right_control,
        }
    }

    pub fn scaled(&self, factor: f64) -> KeyframeHandles {
        KeyframeHandles {
            linear: self.linear,
            left: Point2::new(self.left.x, self.left.y * factor),
            right: Point2::new(self.right.x, self.right.y * factor),
        }
    }
}

/// Interpolation over a segment's keyframes for one property: linear, or a cubic bezier between keyframes that
/// aren't both "Line" (`bezier_value`).
#[derive(Debug, Clone, PartialEq)]
pub struct KeyframeTrack {
    pub times: Vec<i64>,
    pub values: Vec<f64>,
    /// Per-keyframe bezier handles; `None` when every keyframe is linear.
    pub handles: Option<Vec<KeyframeHandles>>,
}

impl KeyframeTrack {
    pub fn new(times: Vec<i64>, values: Vec<f64>) -> KeyframeTrack {
        KeyframeTrack { times, values, handles: None }
    }

    /// The group's keyframes with values, by time; `None` when there are none.
    pub fn from_group(group: &KeyframeGroup) -> Option<KeyframeTrack> {
        let mut frames: Vec<&Keyframe> = group.keyframes.iter().filter(|k| !k.values.is_empty()).collect();
        if frames.is_empty() {
            return None;
        }
        frames.sort_by_key(|k| k.time_offset);
        let handles: Vec<KeyframeHandles> = frames.iter().map(|k| KeyframeHandles::from_keyframe(k)).collect();
        Some(KeyframeTrack {
            times: frames.iter().map(|k| k.time_offset).collect(),
            values: frames.iter().map(|k| k.values[0]).collect(),
            handles: (!handles.iter().all(|h| h.linear)).then_some(handles),
        })
    }

    pub fn value(&self, offset: i64) -> f64 {
        let (Some(&first), Some(&last)) = (self.times.first(), self.times.last()) else { return 0.0 };
        if offset <= first {
            return self.values[0];
        }
        if offset >= last {
            return self.values[self.values.len() - 1];
        }
        let mut i = 1;
        while i < self.times.len() && self.times[i] < offset {
            i += 1;
        }
        let (t0, t1) = (self.times[i - 1], self.times[i]);
        if let Some(h) = &self.handles {
            if self.is_curved(i, h) {
                return bezier_value(
                    (offset - t0) as f64,
                    (t1 - t0) as f64,
                    self.values[i - 1],
                    self.values[i],
                    h[i - 1].right,
                    h[i].left,
                );
            }
        }
        let f = if t1 > t0 { (offset - t0) as f64 / (t1 - t0) as f64 } else { 1.0 };
        self.values[i - 1] + (self.values[i] - self.values[i - 1]) * f
    }

    /// `value` at many offsets at once.
    pub fn values_at(&self, offsets: &[i64]) -> Vec<f64> {
        offsets.iter().map(|&o| self.value(o)).collect()
    }

    fn is_curved(&self, i: usize, h: &[KeyframeHandles]) -> bool {
        h.len() == self.times.len() && !(h[i - 1].linear && h[i].linear)
    }

    /// The keyframe times plus, inside curved spans, a time every `step`: the breaks of a piecewise-linear
    /// approximation (audio volume ramps).
    pub fn breaks(&self, step: i64) -> Vec<i64> {
        let Some(h) = &self.handles else { return self.times.clone() };
        if self.times.len() <= 1 {
            return self.times.clone();
        }
        let step = step.max(1);
        let mut result = vec![self.times[0]];
        for i in 1..self.times.len() {
            if self.is_curved(i, h) {
                let mut t = self.times[i - 1] + step;
                while t < self.times[i] {
                    result.push(t);
                    t += step;
                }
            }
            result.push(self.times[i]);
        }
        result
    }

    /// The same curve with every value multiplied by `factor`.
    pub fn scaled(&self, factor: f64) -> KeyframeTrack {
        KeyframeTrack {
            times: self.times.clone(),
            values: self.values.iter().map(|v| v * factor).collect(),
            handles: self.handles.as_ref().map(|h| h.iter().map(|k| k.scaled(factor)).collect()),
        }
    }

    /// The curve as seen from a clip that starts `offset` later (or earlier, when negative).
    pub fn shifted(&self, offset: i64) -> KeyframeTrack {
        KeyframeTrack {
            times: self.times.iter().map(|t| t - offset).collect(),
            values: self.values.clone(),
            handles: self.handles.clone(),
        }
    }

    /// `curve(u)` sampled every `step` over `0...length`, plus `length` itself.
    pub fn sampled(length: i64, step: i64, curve: impl Fn(i64) -> f64) -> KeyframeTrack {
        let step = step.max(1);
        let mut times = Vec::new();
        let mut t = 0;
        while t < length {
            times.push(t);
            t += step;
        }
        times.push(length);
        let values = times.iter().map(|&t| curve(t)).collect();
        KeyframeTrack::new(times, values)
    }
}

/// A cubic bezier in (time, value) between two keyframes, like a graph editor's curve: P0 = (0, v0), P1 = P0 + the
/// first keyframe's outgoing handle, P2 = P3 + the second one's incoming handle, P3 = (span, v1). Zero handles give a
/// straight line. Handle times are clamped into the span so that time only moves forward.
pub fn bezier_value(u: f64, d: f64, v0: f64, v1: f64, out: Point2, incoming: Point2) -> f64 {
    if d <= 0.0 || d.is_nan() {
        return v1;
    }
    let x1 = out.x.max(0.0).min(d);
    let x2 = (d + incoming.x).max(0.0).min(d);
    let y1 = v0 + out.y;
    let y2 = v1 + incoming.y;
    let s = bezier_parameter(u.max(0.0).min(d), x1, x2, d);
    let r = 1.0 - s;
    r * r * r * v0 + 3.0 * r * r * s * y1 + 3.0 * r * s * s * y2 + s * s * s * v1
}

/// The bezier parameter where x(s) = u: Newton steps from the linear guess, bisection when they stall.
fn bezier_parameter(u: f64, x1: f64, x2: f64, d: f64) -> f64 {
    let x = |s: f64| {
        let r = 1.0 - s;
        3.0 * r * r * s * x1 + 3.0 * r * s * s * x2 + s * s * s * d
    };
    let dx = |s: f64| {
        let r = 1.0 - s;
        3.0 * r * r * x1 + 6.0 * r * s * (x2 - x1) + 3.0 * s * s * (d - x2)
    };
    let tolerance = 1e-6 * d;
    let mut s = u / d;
    for _ in 0..8 {
        let e = x(s) - u;
        if e.abs() < tolerance {
            return s;
        }
        let slope = dx(s);
        if slope.abs() < 1e-9 * d {
            break;
        }
        s = (s - e / slope).clamp(0.0, 1.0);
    }
    let (mut lo, mut hi) = (0.0f64, 1.0f64);
    for _ in 0..60 {
        s = (lo + hi) / 2.0;
        let e = x(s) - u;
        if e.abs() < tolerance {
            break;
        }
        if e < 0.0 {
            lo = s;
        } else {
            hi = s;
        }
    }
    s
}
