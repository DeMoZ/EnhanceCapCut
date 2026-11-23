//! What the native renderers and caption code ask the core besides the timeline: the platform's word breaks for
//! no-space scripts, the typewriter's visible text, lookup-table layouts and the flashback transition's look.

use std::sync::Arc;

use ecc::timeline as t;

use crate::convert::Ffi;

/// A word in a text: UTF-8 byte offsets, the end excluded.
#[derive(Debug, Clone, Copy, PartialEq, Eq, uniffi::Record)]
pub struct WordRange {
    pub start: u64,
    pub end: u64,
}

/// The platform's word breaks for scripts without spaces (Thai, Lao, Khmer, Burmese, Chinese, Japanese): the macOS app
/// implements it with `NLTokenizer` (design decision 7, option b).
#[uniffi::export(with_foreign)]
pub trait WordBreaker: Send + Sync {
    /// The words of `text` in `language` (our code: `th`, `zh-Hans`…), in order; `None` to let the core use ICU4X.
    fn words(&self, text: String, language: String) -> Option<Vec<WordRange>>;
}

/// Sets the word breaker caption wrapping uses for no-space scripts, or with `None` goes back to ICU4X (the default).
#[uniffi::export]
pub fn set_word_breaker(breaker: Option<Arc<dyn WordBreaker>>) {
    ecc::subtitles::segmenter::set_word_breaker(breaker.map(|b| {
        let f: Arc<ecc::subtitles::segmenter::WordBreaker> = Arc::new(move |text: &str, language: &str| {
            b.words(text.to_string(), language.to_string())
                .map(|words| words.into_iter().map(|w| (w.start as usize, w.end as usize)).collect())
        });
        f
    }));
}

/// UTF-16 length of the part of `text` a typewriter animation shows at `reveal` (whole characters, rounded down).
#[uniffi::export]
pub fn visible_text_length(text: String, reveal: f64) -> u64 {
    t::TextAnimationState::visible_length(&text, reveal) as u64
}

/// How a lookup-table image holds its N×N×N entries.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, uniffi::Enum)]
pub enum LutLayout {
    /// √N × √N tiles of N×N pixels, blue tile by tile, row by row (512×512 for N = 64).
    Grid { size: u64, tiles_per_row: u64 },
    /// N tiles of N×N side by side (N² × N).
    Row { size: u64 },
    /// N tiles of N×N stacked (N × N²).
    Column { size: u64 },
}

impl Ffi for t::LutLayout {
    type Out = LutLayout;
    fn ffi(self) -> LutLayout {
        match self {
            t::LutLayout::Grid { size, tiles_per_row } => {
                LutLayout::Grid { size: size as u64, tiles_per_row: tiles_per_row as u64 }
            }
            t::LutLayout::Row { size } => LutLayout::Row { size: size as u64 },
            t::LutLayout::Column { size } => LutLayout::Column { size: size as u64 },
        }
    }
}

/// The layout of a lookup image of this size, or `None` when it isn't one.
#[uniffi::export]
pub fn lut_layout(width: u64, height: u64) -> Option<LutLayout> {
    t::ColorFilter::layout(width as usize, height as usize).ffi()
}

/// One blur copy of the flashback transition: scale and clockwise rotation (degrees) about the frame centre.
#[derive(Debug, Clone, Copy, PartialEq, uniffi::Record)]
pub struct FlashbackCopy {
    pub scale: f64,
    pub rotation: f64,
}

/// The flashback transition at one moment: how much of the next clip shows, the effect's strength (0 at the edges,
/// 1 at the cut) and the blur copies averaged over the dissolved picture.
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct FlashbackFrame {
    pub mix: f64,
    pub strength: f64,
    pub copies: Vec<FlashbackCopy>,
}

/// `progress` 0…1 over the transition.
#[uniffi::export]
pub fn flashback_frame(progress: f64) -> FlashbackFrame {
    FlashbackFrame {
        mix: t::FlashbackLook::mix(progress),
        strength: t::FlashbackLook::strength(progress),
        copies: (0..t::FlashbackLook::BLUR_COPIES)
            .map(|i| {
                let (scale, rotation) = t::FlashbackLook::copy(i, progress);
                FlashbackCopy { scale, rotation }
            })
            .collect(),
    }
}
