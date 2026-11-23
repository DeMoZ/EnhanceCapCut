//! The platform's word breaker is global, so it is set in a test binary of its own.

use std::sync::Arc;

use ecc_core::*;

/// Splits at every listed word, like `NLTokenizer` keeps Thai compounds whole.
struct Dictionary(Vec<&'static str>);

impl WordBreaker for Dictionary {
    fn words(&self, text: String, language: String) -> Option<Vec<WordRange>> {
        if !language.starts_with("th") {
            return None;
        }
        let mut words: Vec<WordRange> = self
            .0
            .iter()
            .flat_map(|w| text.match_indices(w).map(|(i, _)| WordRange { start: i as u64, end: (i + w.len()) as u64 }))
            .collect();
        words.sort_by_key(|w| w.start);
        Some(words)
    }
}

#[test]
fn captions_wrap_at_the_platforms_words() {
    let text = "ออกกำลังกาย ทุกวัน ".repeat(3);
    set_word_breaker(Some(Arc::new(Dictionary(vec!["ออกกำลังกาย", "ทุก", "วัน"]))));
    let wrapped = wrap_caption(text.clone(), Some("th".into())).unwrap();
    assert!(wrapped.contains('\n') && !wrapped.contains("ออกกำลัง\n"), "{wrapped}");
    set_word_breaker(None);
    // Back on ICU4X: still wrapped, nothing lost but the spaces at line ends.
    let plain = |s: &str| s.chars().filter(|c| !c.is_whitespace()).collect::<String>();
    let again = wrap_caption(text.clone(), Some("th".into())).unwrap();
    assert!(again.contains('\n'));
    assert_eq!(plain(&again), plain(&text));
}
