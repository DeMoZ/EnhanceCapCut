//! Caption cues from recognised words, line wrapping and SRT output: the port of `Segmenter.swift` (`Segmenter`,
//! `LineWrapper`, `SRTWriter`). Lengths count grapheme clusters, like Swift's `String.count`.
//!
//! Scripts written without spaces are split into word units by the ICU4X word segmenter with LSTM data for Thai, Lao,
//! Khmer and Burmese, and by grapheme cluster for Chinese and Japanese (design decision 7). This replaces
//! `NLTokenizer` on macOS and the grapheme split on Windows, so both platforms break these lines the same way.

use std::sync::{Arc, OnceLock, RwLock};

use icu_segmenter::options::WordBreakInvariantOptions;
use icu_segmenter::{WordSegmenter, WordSegmenterBorrowed};
use unicode_segmentation::UnicodeSegmentation;

/// A word with its time, seconds from the timeline start.
#[derive(Debug, Clone, PartialEq)]
pub struct TimedWord {
    pub text: String,
    pub start: f64,
    pub end: f64,
}

impl TimedWord {
    pub fn new(text: &str, start: f64, end: f64) -> TimedWord {
        TimedWord { text: text.to_string(), start, end }
    }
}

/// A subtitle line on the timeline, in seconds from the timeline start.
#[derive(Debug, Clone, PartialEq)]
pub struct CaptionCue {
    pub start: f64,
    pub end: f64,
    pub text: String,
}

impl CaptionCue {
    pub fn new(start: f64, end: f64, text: &str) -> CaptionCue {
        CaptionCue { start, end, text: text.to_string() }
    }
}

/// Swift's `String.count`: grapheme clusters.
pub(crate) fn count(s: &str) -> usize {
    s.graphemes(true).count()
}

fn last_grapheme(s: &str) -> Option<&str> {
    s.graphemes(true).next_back()
}

/// `.whitespaces` (spaces and tabs, not line breaks) trimmed from both ends.
pub(crate) fn trim_spaces(s: &str) -> &str {
    s.trim_matches(|c: char| {
        c.is_whitespace() && !matches!(c, '\n' | '\r' | '\u{0B}' | '\u{0C}' | '\u{85}' | '\u{2028}' | '\u{2029}')
    })
}

fn first_char_is(g: &str, test: impl Fn(char) -> bool) -> bool {
    g.chars().next().is_some_and(test)
}

/// The two-letter language of a code like `th`, `zh-Hans`, `pt-BR`.
fn base(language: &str) -> &str {
    language.get(..2).unwrap_or(language)
}

/// Groups recognised words into readable subtitle cues (ported from SubDub, the Final Cut Pro version).
pub struct Segmenter;

impl Segmenter {
    pub const MAX_CHARS_PER_LINE: usize = 42;
    pub const MAX_LINES: usize = 2;
    pub const MAX_DURATION: f64 = 6.0;
    pub const MIN_DURATION: f64 = 1.0;
    pub const PAUSE_BREAK: f64 = 0.8;
    /// Some on-device models (e.g. Russian dictation) return no punctuation at all. In that case sentences end at long
    /// pauses so cues and translations read naturally.
    pub const SENTENCE_PAUSE: f64 = 0.45;

    pub fn cues(raw_words: &[TimedWord], language: Option<&str>) -> Vec<CaptionCue> {
        let words = Self::punctuate_if_needed(Self::explode(raw_words), language);
        let separator = if language.is_some_and(LineWrapper::is_no_space) { "" } else { " " };
        let mut cues: Vec<CaptionCue> = Vec::new();
        let mut current: Vec<TimedWord> = Vec::new();

        let emit = |group: &[TimedWord], cues: &mut Vec<CaptionCue>| {
            let (Some(first), Some(last)) = (group.first(), group.last()) else { return };
            let text = group.iter().map(|w| w.text.as_str()).collect::<Vec<_>>().join(separator);
            cues.push(CaptionCue { start: first.start, end: last.end.max(first.start + 0.3), text });
        };

        let line_limit = LineWrapper::line_limit(language);
        let max_chars = line_limit * Self::MAX_LINES;
        for word in words {
            if let Some(last) = current.last() {
                let length: usize = current.iter().map(|w| count(&w.text) + 1).sum::<usize>() + count(&word.text);
                let gap = word.start - last.end;
                let last_text = last_grapheme(&last.text);
                let sentence_end = last_text.is_some_and(|g| [".", "?", "!", "…", "。", "？", "！"].contains(&g));
                let clause_end = last_text.is_some_and(|g| [",", ";", ":", "—", "，", "、"].contains(&g));
                let cur_len = length - count(&word.text);

                if gap > Self::PAUSE_BREAK || (sentence_end && cur_len >= 15) || (clause_end && cur_len >= line_limit) {
                    emit(&current, &mut cues);
                    current.clear();
                } else if length > max_chars || word.end - current[0].start > Self::MAX_DURATION {
                    // The cue is full mid-phrase: cut at the longest pause in its second part (later cut wins ties)
                    // and carry the rest over into the next cue.
                    if current.len() < 3 {
                        emit(&current, &mut cues);
                        current.clear();
                    } else {
                        let mut best = current.len();
                        let mut best_gap = f64::NEG_INFINITY;
                        for k in (current.len() / 3).max(1)..current.len() {
                            let gap = current[k].start - current[k - 1].end;
                            if gap >= best_gap {
                                best_gap = gap;
                                best = k;
                            }
                        }
                        emit(&current[..best], &mut cues);
                        current.drain(..best);
                    }
                }
            }
            current.push(word);
        }
        emit(&current, &mut cues);

        // Give short cues enough reading time without overlapping the next one.
        for i in 0..cues.len() {
            let next_start = if i + 1 < cues.len() { cues[i + 1].start } else { f64::INFINITY };
            if cues[i].end - cues[i].start < Self::MIN_DURATION {
                cues[i].end = (cues[i].start + Self::MIN_DURATION).min(next_start);
            }
            // Close tiny gaps so captions don't flicker.
            if next_start - cues[i].end < 0.25 && next_start > cues[i].end {
                cues[i].end = next_start;
            }
            cues[i].end = cues[i].end.min(next_start);
        }
        cues.into_iter().map(|c| CaptionCue { text: LineWrapper::wrap(&c.text, language), ..c }).collect()
    }

    fn punctuate_if_needed(words: Vec<TimedWord>, language: Option<&str>) -> Vec<TimedWord> {
        let terminators = [".", "?", "!", "…", "。", "？", "！"];
        if words.len() <= 3 || words.iter().any(|w| last_grapheme(&w.text).is_some_and(|g| terminators.contains(&g))) {
            return words;
        }
        if language.is_some_and(LineWrapper::is_no_space) {
            return words;
        }
        let mut out = words;
        let mut capitalize_next = true;
        for i in 0..out.len() {
            if capitalize_next {
                if let Some(first) = out[i].text.graphemes(true).next() {
                    // Swift's `Character.isLowercase`: changed by uppercasing, unchanged by lowercasing.
                    if first.to_uppercase() != first && first.to_lowercase() == first {
                        let rest = out[i].text[first.len()..].to_string();
                        out[i].text = first.to_uppercase() + &rest;
                    }
                }
            }
            let is_last = i == out.len() - 1;
            capitalize_next = is_last || out[i + 1].start - out[i].end >= Self::SENTENCE_PAUSE;
            if capitalize_next {
                if let Some(last) = last_grapheme(&out[i].text) {
                    if first_char_is(last, |c| c.is_alphabetic() || c.is_numeric()) {
                        out[i].text.push('.');
                    }
                }
            }
        }
        out
    }

    /// Tokens that contain several words get split with interpolated timing.
    fn explode(words: &[TimedWord]) -> Vec<TimedWord> {
        let mut out = Vec::new();
        for w in words {
            let parts: Vec<&str> = w.text.split(char::is_whitespace).filter(|p| !p.is_empty()).collect();
            if parts.len() <= 1 {
                out.push(w.clone());
                continue;
            }
            let total: usize = parts.iter().map(|p| count(p)).sum();
            let mut t = w.start;
            for p in parts {
                let d = (w.end - w.start) * count(p) as f64 / total as f64;
                out.push(TimedWord { text: p.to_string(), start: t, end: t + d });
                t += d;
            }
        }
        out
    }
}

/// Breaks subtitle text into balanced lines: two when the text fits them, more only when it doesn't, so a line never
/// runs past CapCut's width (CapCut would then wrap it itself, cutting through words of Thai and other scripts). Lines
/// break only between word units.
pub struct LineWrapper;

impl LineWrapper {
    /// Languages written without spaces between words.
    pub const NO_SPACE_LANGUAGES: [&'static str; 6] = ["th", "zh", "ja", "lo", "km", "my"];

    pub fn is_no_space(language: &str) -> bool {
        Self::NO_SPACE_LANGUAGES.contains(&base(language))
    }

    pub fn line_limit(language: Option<&str>) -> usize {
        match language.map(base) {
            Some("zh" | "ja") => 18,
            Some("ko") => 24,
            // Thai captions are written twice as large (`size_factor`), so half of the 35 that fit at the normal size.
            Some("th") => 17,
            _ => Segmenter::MAX_CHARS_PER_LINE,
        }
    }

    pub fn wrap(text: &str, language: Option<&str>) -> String {
        let replaced = text.replace('\n', " ");
        let flat = trim_spaces(&replaced);
        let limit = Self::line_limit(language);
        if count(flat) <= limit {
            return flat.to_string();
        }
        let no_spaces = language.is_some_and(Self::is_no_space);
        let tokens: Vec<String> = if no_spaces {
            Self::keep_numbers_with_next(word_units(flat, language.unwrap_or_default()))
        } else {
            flat.split(' ').filter(|s| !s.is_empty()).map(|s| format!("{s} ")).collect()
        };

        let total: usize = tokens.iter().map(|t| count(t)).sum();
        if total > 2 * limit {
            return Self::lines(&tokens, total.div_ceil(limit));
        }

        // Find the split point that balances both lines best. Without spaces between words, a break at a space (where
        // Thai separates phrases) is preferred over one between word units when it's nearly as balanced.
        let mut best = (usize::MAX, tokens.len() / 2);
        let mut left = 0;
        for i in 1..tokens.len().max(2) {
            if i >= tokens.len() {
                continue;
            }
            left += count(&tokens[i - 1]);
            let mut score = total.abs_diff(2 * left);
            if no_spaces && !tokens[i - 1].ends_with(char::is_whitespace) {
                score += Self::UNIT_BREAK_PENALTY;
            }
            if score < best.0 {
                best = (score, i);
            }
        }
        let line1 = trim_spaces(&tokens[..best.1].concat()).to_string();
        let line2 = trim_spaces(&tokens[best.1..].concat()).to_string();
        if line2.is_empty() {
            line1
        } else {
            format!("{line1}\n{line2}")
        }
    }

    /// How much less balanced (in characters, counted twice) a break at a space may be than one between word units.
    pub const UNIT_BREAK_PENALTY: usize = 10;

    /// A number stays on the line of the word after it ("20 นาที", 20 minutes).
    pub fn keep_numbers_with_next(tokens: Vec<String>) -> Vec<String> {
        let mut out: Vec<String> = Vec::new();
        for token in tokens {
            match out.last_mut() {
                Some(last)
                    if !trim_spaces(last).is_empty()
                        && trim_spaces(last).graphemes(true).all(|g| first_char_is(g, char::is_numeric)) =>
                {
                    last.push_str(&token)
                }
                _ => out.push(token),
            }
        }
        out
    }

    /// `tokens` in `count` lines as even as possible: the narrowest width that still needs no more lines.
    fn lines(tokens: &[String], lines: usize) -> String {
        let fill = |width: usize| -> Vec<String> {
            let mut out = Vec::new();
            let mut current = String::new();
            for token in tokens {
                if !current.is_empty() && count(trim_spaces(&format!("{current}{token}"))) > width {
                    out.push(std::mem::take(&mut current));
                }
                current.push_str(token);
            }
            if !current.is_empty() {
                out.push(current);
            }
            out
        };
        let total: usize = tokens.iter().map(|t| count(t)).sum();
        let mut width = tokens.iter().map(|t| count(t)).max().unwrap_or(1).max(total / lines);
        while fill(width).len() > lines {
            width += 1;
        }
        fill(width).iter().map(|l| trim_spaces(l)).collect::<Vec<_>>().join("\n")
    }
}

fn segmenter() -> &'static WordSegmenterBorrowed<'static> {
    static SEGMENTER: OnceLock<WordSegmenterBorrowed<'static>> = OnceLock::new();
    SEGMENTER.get_or_init(|| WordSegmenter::new_lstm(WordBreakInvariantOptions::default()))
}

/// Characters that don't start a line in Chinese and Japanese (closing punctuation, prolonged sound marks): kept with
/// the unit before them.
const NO_LINE_START: &str =
    "、。，．,.！？!?）)」』】〉》〕］}”’…‥ー〜～・：；:;％%ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ";
/// Characters that don't end a line (opening brackets): kept with the unit after them.
const NO_LINE_END: &str = "（(「『【〈《〔［{“‘";

/// The platform's own word breaks for scripts without spaces (design decision 7, option b): the word ranges of a text in
/// a language, as UTF-8 byte offsets, or `None` to use ICU4X. The macOS app sets `NLTokenizer`; without one (Windows, the
/// CLIs' tests, the golden runner) ICU4X's LSTM breaks are used.
pub type WordBreaker = dyn Fn(&str, &str) -> Option<Vec<(usize, usize)>> + Send + Sync;

fn word_breaker() -> &'static RwLock<Option<Arc<WordBreaker>>> {
    static BREAKER: OnceLock<RwLock<Option<Arc<WordBreaker>>>> = OnceLock::new();
    BREAKER.get_or_init(|| RwLock::new(None))
}

/// Sets (or, with `None`, removes) the platform's word breaker used by `word_units`.
pub fn set_word_breaker(breaker: Option<Arc<WordBreaker>>) {
    *word_breaker().write().unwrap_or_else(|e| e.into_inner()) = breaker;
}

/// Word units from the platform's word ranges, as the macOS app made them before the core: characters between words
/// (spaces, punctuation) go with the word before them, or form the first unit. `None` when the ranges don't fit `text`
/// (out of order, out of bounds or not on character boundaries).
fn native_units(text: &str, ranges: &[(usize, usize)]) -> Option<Vec<String>> {
    let mut units: Vec<String> = Vec::new();
    let mut cursor = 0;
    for &(start, end) in ranges {
        if start < cursor
            || end < start
            || end > text.len()
            || !text.is_char_boundary(start)
            || !text.is_char_boundary(end)
        {
            return None;
        }
        if cursor < start {
            match units.last_mut() {
                Some(last) => last.push_str(&text[cursor..start]),
                None => units.push(text[cursor..start].to_string()),
            }
        }
        if end > start {
            units.push(text[start..end].to_string());
        }
        cursor = end;
    }
    if cursor < text.len() {
        match units.last_mut() {
            Some(last) => last.push_str(&text[cursor..]),
            None => units.push(text[cursor..].to_string()),
        }
    }
    Some(units)
}

/// Word units for scripts without spaces, keeping every original character (spaces too), so they join back to `text`.
pub fn word_units(text: &str, language: &str) -> Vec<String> {
    let breaker = word_breaker().read().unwrap_or_else(|e| e.into_inner()).clone();
    if let Some(units) = breaker.and_then(|b| b(text, language)).and_then(|r| native_units(text, &r)) {
        return units;
    }
    #[derive(PartialEq)]
    enum Kind {
        /// Spaces and punctuation: kept with the unit before.
        Skipped,
        Number,
        Word,
    }
    let mut units: Vec<(String, Kind)> = Vec::new();
    if matches!(base(language), "zh" | "ja") {
        // Grapheme clusters (line breaks between any two are allowed typographically), with the punctuation rules.
        let mut carry = String::new();
        for g in text.graphemes(true) {
            if NO_LINE_END.contains(g) {
                carry.push_str(g);
                continue;
            }
            let kind = if g.chars().all(char::is_whitespace) || NO_LINE_START.contains(g) {
                Kind::Skipped
            } else if g.chars().all(char::is_numeric) {
                Kind::Number
            } else {
                Kind::Word
            };
            units.push((std::mem::take(&mut carry) + g, kind));
        }
        if !carry.is_empty() {
            units.push((carry, Kind::Word));
        }
    } else {
        let mut start = 0;
        for (end, kind) in segmenter().segment_str(text).iter_with_word_type() {
            if end > start {
                let kind = match kind {
                    icu_segmenter::options::WordType::None => Kind::Skipped,
                    icu_segmenter::options::WordType::Number => Kind::Number,
                    _ => Kind::Word,
                };
                units.push((text[start..end].to_string(), kind));
            }
            start = end;
        }
    }
    // Skipped characters (spaces, punctuation) go with the unit before them; a word unit of a single letter (the LSTM
    // model sometimes splits one off a Thai word: ดีมาก → ดี|มา|ก) goes with its neighbour. Numbers stay apart: they
    // are kept with the word after them (`keep_numbers_with_next`).
    let joins_letters = !matches!(base(language), "zh" | "ja");
    let mut out: Vec<String> = Vec::new();
    let mut starts_with_single_letter = false;
    for (unit, kind) in units {
        let single = joins_letters && kind == Kind::Word && count(&unit) == 1;
        match out.last_mut() {
            Some(last) if kind == Kind::Skipped || single => last.push_str(&unit),
            _ => {
                starts_with_single_letter = out.is_empty() && single;
                out.push(unit)
            }
        }
    }
    // A single letter at the very start joins the unit after it.
    if out.len() > 1 && starts_with_single_letter {
        let first = out.remove(0);
        out[0].insert_str(0, &first);
    }
    out
}

/// SRT text of `cues`.
pub fn srt(cues: &[CaptionCue]) -> String {
    let stamp = |t: f64| {
        let ms = (t.max(0.0) * 1000.0).round() as i64;
        format!("{:02}:{:02}:{:02},{:03}", ms / 3_600_000, (ms / 60_000) % 60, (ms / 1000) % 60, ms % 1000)
    };
    let mut out = String::new();
    for (i, cue) in cues.iter().enumerate() {
        out += &format!("{}\n{} --> {}\n{}\n\n", i + 1, stamp(cue.start), stamp(cue.end), cue.text);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Port of `SubtitleWriterTests.testSegmenterSplitsAtPauses`.
    #[test]
    fn segmenter_splits_at_pauses() {
        let words: Vec<TimedWord> = [("Привет", 0.0, 0.4), ("мир", 0.45, 0.8), ("как", 2.0, 2.2), ("дела", 2.25, 2.6)]
            .iter()
            .map(|w| TimedWord::new(w.0, w.1, w.2))
            .collect();
        let cues = Segmenter::cues(&words, Some("ru"));
        assert_eq!(cues.iter().map(|c| c.text.as_str()).collect::<Vec<_>>(), ["Привет мир.", "Как дела."]);
        assert_eq!(cues[0].start, 0.0);
    }

    #[test]
    fn segmenter_explodes_multi_word_tokens_and_keeps_punctuation() {
        let words = [TimedWord::new("Hello there", 0.0, 1.0), TimedWord::new("friend.", 1.1, 1.5)];
        let cues = Segmenter::cues(&words, Some("en"));
        assert_eq!(cues.len(), 1);
        assert_eq!(cues[0].text, "Hello there friend.");
        assert_eq!(cues[0].end, 1.5);
        // Short cues get a second unless the next one starts first; tiny gaps close.
        let words = [TimedWord::new("One.", 0.0, 0.2), TimedWord::new("Two", 1.2, 1.3)];
        let cues = Segmenter::cues(&words, Some("en"));
        assert_eq!(cues.iter().map(|c| (c.start, c.end)).collect::<Vec<_>>(), [(0.0, 1.2), (1.2, 2.2)]);
    }

    #[test]
    fn long_runs_split_at_the_best_pause() {
        // 20 words without pauses over the line budget: cut where the gap is widest in the second part.
        let mut words = Vec::new();
        let mut t = 0.0;
        for i in 0..20 {
            let gap = if i == 12 { 0.5 } else { 0.05 };
            t += gap;
            words.push(TimedWord::new("word", t, t + 0.2));
            t += 0.2;
        }
        let cues = Segmenter::cues(&words, Some("en"));
        assert!(cues.len() >= 2);
        assert!(cues.iter().all(|c| c.end > c.start));
    }

    #[test]
    fn wraps_long_text_into_more_lines_only_when_needed() {
        let two = LineWrapper::wrap("They add a little sugar and form such sausages for the market.", Some("en"));
        assert_eq!(two.split('\n').count(), 2);
        let long = "word ".repeat(30);
        let lines: Vec<&str> = LineWrapper::wrap(long.trim(), Some("en")).leak().split('\n').collect();
        assert_eq!(lines.len(), 4);
        assert!(lines.iter().all(|l| count(l) <= Segmenter::MAX_CHARS_PER_LINE));
        assert_eq!(LineWrapper::wrap(" short \n text ", Some("en")), "short   text");
    }

    #[test]
    fn wraps_thai_at_words_within_the_line_limit() {
        let text = "ผมได้ยินมาว่าปลาหมึกกล้วยและปลาหมึกกระดองต้องย่างแค่หนึ่งนาทีหรือยี่สิบนาทีเท่านั้น";
        let wrapped = LineWrapper::wrap(text, Some("th"));
        let lines: Vec<&str> = wrapped.split('\n').collect();
        assert!(lines.len() > 2, "{wrapped}");
        assert_eq!(lines.concat(), text, "nothing lost or added");
        for line in &lines {
            assert!(count(line) <= LineWrapper::line_limit(Some("th")) + 4, "{line}");
        }
        for word in ["ปลาหมึก", "หนึ่ง", "ยี่สิบ", "เท่านั้น"] {
            assert!(lines.iter().any(|l| l.contains(word)), "{word} cut in {wrapped}");
        }
    }

    #[test]
    fn thai_numbers_stay_with_their_word() {
        let tokens = vec!["ปรุงเป็นเวลา ".to_string(), "1 ".to_string(), "นาที".to_string()];
        assert_eq!(LineWrapper::keep_numbers_with_next(tokens), ["ปรุงเป็นเวลา ", "1 นาที"]);
        let wrapped = LineWrapper::wrap("ปรุงเป็นเวลา 1 นาทีหรือ 20 นาทีเท่านั้นนะครับ", Some("th"));
        assert!(!wrapped.contains("1\n") && !wrapped.contains("20\n"), "{wrapped}");
    }

    #[test]
    fn a_native_word_breaker_gives_the_units() {
        // Answers only for its own language code, so the other tests (running at the same time) keep ICU4X.
        set_word_breaker(Some(Arc::new(|text: &str, language: &str| {
            (language == "th-x-native").then(|| {
                let words = ["ออกกำลังกาย", "ทุก", "วัน"];
                let mut ranges = Vec::new();
                for w in words {
                    if let Some(i) = text.find(w) {
                        ranges.push((i, i + w.len()));
                    }
                }
                ranges
            })
        })));
        // Spaces and punctuation go with the word before; a leading gap is its own unit.
        assert_eq!(word_units(" ออกกำลังกาย ทุกวัน!", "th-x-native"), [" ", "ออกกำลังกาย ", "ทุก", "วัน!"]);
        let wrapped = LineWrapper::wrap(&"ออกกำลังกาย ทุกวัน ".repeat(3), Some("th-x-native"));
        assert!(wrapped.contains('\n') && !wrapped.contains("ออกกำลัง\n"), "{wrapped}");
        // Ranges that don't fit the text fall back to ICU4X.
        assert_eq!(native_units("abc", &[(0, 2), (1, 3)]), None);
        assert_eq!(native_units("aé", &[(0, 2)]), None);
        // Other languages still use ICU4X.
        assert_eq!(word_units("วันนี้อากาศดีมาก", "th").concat(), "วันนี้อากาศดีมาก");
    }

    #[test]
    fn thai_units_never_end_on_a_split_off_character() {
        // The LSTM model alone splits ดีมาก as ดี|มา|ก.
        let units = word_units("วันนี้อากาศดีมาก", "th");
        assert_eq!(units.concat(), "วันนี้อากาศดีมาก");
        assert!(units.iter().all(|u| count(u) > 1), "{units:?}");
        assert!(units.iter().any(|u| u.ends_with("มาก")), "{units:?}");
    }

    #[test]
    fn chinese_and_japanese_break_between_characters_but_not_before_closing_punctuation() {
        let units = word_units("我们今天去北京。（真的）好！", "zh");
        assert_eq!(units.concat(), "我们今天去北京。（真的）好！");
        assert!(units.contains(&"京。".to_string()), "{units:?}");
        assert!(units.contains(&"（真".to_string()), "{units:?}");
        assert!(units.contains(&"的）".to_string()), "{units:?}");
        let wrapped = LineWrapper::wrap("今日はとても良い天気ですね。明日も晴れるといいですね。", Some("ja"));
        assert_eq!(wrapped.replace('\n', ""), "今日はとても良い天気ですね。明日も晴れるといいですね。");
        for line in wrapped.split('\n') {
            assert!(count(line) <= LineWrapper::line_limit(Some("ja")), "{wrapped}");
            assert!(!line.starts_with(['。', '、']), "{wrapped}");
        }
        // Numbers stay with what follows them.
        let units = LineWrapper::keep_numbers_with_next(word_units("等了20分钟", "zh"));
        assert!(units.contains(&"20分".to_string()), "{units:?}");
    }

    #[test]
    fn lao_khmer_and_burmese_split_into_words() {
        for (text, language) in [("ສະບາຍດີເພື່ອນໆທຸກຄົນ", "lo"), ("ជំរាបសួរមិត្តភក្តិទាំងអស់គ្នា", "km"), ("မင်္ဂလာပါသူငယ်ချင်းများ", "my")]
        {
            let units = word_units(text, language);
            assert_eq!(units.concat(), text);
            assert!(units.len() > 1, "{language}: {units:?}");
        }
    }

    #[test]
    fn srt_stamps() {
        let cues = [CaptionCue::new(0.0, 1.5, "Hi"), CaptionCue::new(3661.0004, 3662.9995, "Two\nlines")];
        assert_eq!(
            srt(&cues),
            "1\n00:00:00,000 --> 00:00:01,500\nHi\n\n2\n01:01:01,000 --> 01:01:03,000\nTwo\nlines\n\n"
        );
        assert_eq!(srt(&[CaptionCue::new(-1.0, 0.0005, "x")]), "1\n00:00:00,000 --> 00:00:00,001\nx\n\n");
    }
}
