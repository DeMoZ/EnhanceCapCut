//! The core's interface texts in English and Russian (design decision 6), over the same `Localization/ru.json` the
//! apps use (English text → translation). Until a host calls `set_core_language`, everything stays English, so the
//! CLIs' output doesn't depend on the system.
//!
//! Texts are written as `tr!("English {0} text", arg)`; the key must be a single string literal so the tests (and
//! the apps' localization tests) can find every key in the code.

use std::collections::HashMap;
use std::sync::{OnceLock, RwLock};

use crate::draft::json::swift_double;

const RU_JSON: &str = include_str!("../../../Localization/ru.json");

fn russian() -> &'static HashMap<String, String> {
    static TABLE: OnceLock<HashMap<String, String>> = OnceLock::new();
    TABLE.get_or_init(|| serde_json::from_str(RU_JSON).unwrap_or_default())
}

/// The language codes the core knows.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Language {
    English,
    Russian,
}

static LANGUAGE: RwLock<Language> = RwLock::new(Language::English);

/// Switches the core's texts to `code` ("ru", "en"; anything else is English). The apps call it next to
/// `Localization.use`.
pub fn set_core_language(code: &str) {
    let language = if code.to_lowercase().starts_with("ru") { Language::Russian } else { Language::English };
    *LANGUAGE.write().unwrap_or_else(|e| e.into_inner()) = language;
}

pub fn core_language() -> Language {
    *LANGUAGE.read().unwrap_or_else(|e| e.into_inner())
}

/// The translation of `english` in the current language, or `english` itself.
pub fn translate(english: &str) -> String {
    match core_language() {
        Language::English => english.to_string(),
        Language::Russian => russian().get(english).cloned().unwrap_or_else(|| english.to_string()),
    }
}

/// `english` translated, with `{0}`, `{1}`… replaced by `args` one after another (like Swift's `L`).
pub fn format(english: &str, args: &[String]) -> String {
    let mut text = translate(english);
    for (i, arg) in args.iter().enumerate() {
        text = text.replace(&format!("{{{i}}}"), arg);
    }
    text
}

/// How an argument of `tr!` is printed: like Swift's `description` (`Double` as `1.0`, `0.1`, `1e-05`).
pub trait TrArg {
    fn tr_arg(&self) -> String;
}

impl TrArg for str {
    fn tr_arg(&self) -> String {
        self.to_string()
    }
}

impl TrArg for String {
    fn tr_arg(&self) -> String {
        self.clone()
    }
}

impl TrArg for f64 {
    fn tr_arg(&self) -> String {
        swift_double(*self)
    }
}

macro_rules! tr_arg_display {
    ($($t:ty),*) => { $(impl TrArg for $t { fn tr_arg(&self) -> String { self.to_string() } })* };
}
tr_arg_display!(i32, i64, u32, u64, usize, char);

impl<T: TrArg + ?Sized> TrArg for &T {
    fn tr_arg(&self) -> String {
        (**self).tr_arg()
    }
}

/// `tr!("English {0}", arg, …)`: the text in the core's language with the arguments filled in.
#[macro_export]
macro_rules! tr {
    ($key:literal $(,)?) => {
        $crate::l10n::translate($key)
    };
    ($key:literal, $($arg:expr),+ $(,)?) => {
        $crate::l10n::format($key, &[$($crate::l10n::TrArg::tr_arg(&$arg)),+])
    };
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::testing::language;
    use std::path::Path;

    /// Every `tr!("…")` key in `dir`, unescaped.
    pub(crate) fn keys_in(dir: &Path, out: &mut Vec<(String, String)>) {
        for entry in std::fs::read_dir(dir).unwrap().flatten() {
            let path = entry.path();
            if path.is_dir() {
                keys_in(&path, out);
            } else if path.extension().is_some_and(|e| e == "rs") {
                let text = std::fs::read_to_string(&path).unwrap();
                let mut rest = text.as_str();
                while let Some(i) = rest.find("tr!(") {
                    // A call in code: not the end of `include_str!(`, and not quoted in a string or `docs`.
                    let own = !rest[..i].ends_with(|c: char| c.is_alphanumeric() || matches!(c, '_' | '"' | '`'));
                    rest = rest[i + 4..].trim_start();
                    if !own {
                        continue;
                    }
                    let Some(body) = rest.strip_prefix('"') else {
                        continue;
                    };
                    let mut key = String::new();
                    let mut chars = body.char_indices();
                    let mut end = body.len();
                    while let Some((j, c)) = chars.next() {
                        match c {
                            '\\' => match chars.next().map(|(_, e)| e) {
                                Some('n') => key.push('\n'),
                                Some('t') => key.push('\t'),
                                Some(e) => key.push(e),
                                None => {}
                            },
                            '"' => {
                                end = j + 1;
                                break;
                            }
                            c => key.push(c),
                        }
                    }
                    out.push((key, path.display().to_string()));
                    rest = &body[end..];
                }
            }
        }
    }

    #[test]
    fn every_key_has_a_russian_entry() {
        let mut keys = Vec::new();
        keys_in(Path::new(concat!(env!("CARGO_MANIFEST_DIR"), "/src")), &mut keys);
        let missing: Vec<_> = keys.iter().filter(|(k, _)| !russian().contains_key(k)).collect();
        assert!(missing.is_empty(), "keys without a Russian entry in Localization/ru.json: {missing:#?}");
        assert!(keys.iter().any(|(k, _)| k == "Cannot read {0}: {1}"), "the scan found no keys: {keys:?}");
    }

    #[test]
    fn english_by_default_and_russian_after_switching() {
        let _guard = language("en");
        assert_eq!(core_language(), Language::English);
        assert_eq!(tr!("Cannot read {0}: {1}", "/a", "b"), "Cannot read /a: b");
        set_core_language("ru");
        assert_eq!(tr!("Cannot read {0}: {1}", "/a", "b"), format("Cannot read {0}: {1}", &["/a".into(), "b".into()]));
        assert_ne!(tr!("Cannot read {0}: {1}", "/a", "b"), "Cannot read /a: b");
        assert_eq!(translate("no such key"), "no such key");
        set_core_language("en");
        assert_eq!(format("{0} and {1}", &[1.0.tr_arg(), 2.tr_arg()]), "1.0 and 2");
    }
}
