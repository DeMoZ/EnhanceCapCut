//! Captions: segmentation into cues, line wrapping and SRT (`segmenter`).

pub mod segmenter;

pub use segmenter::{srt, word_units, CaptionCue, LineWrapper, Segmenter, TimedWord};
