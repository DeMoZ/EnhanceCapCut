//! Writing into CapCut drafts: captions (`subtitles`), voice-over (`voice_over`) and dubbed timelines (`dub`). Drafts
//! are edited in place with `JsonScanner` / `JsonEdits`, so everything we don't touch stays byte for byte. Ids and
//! the clock are inputs (`IdSource`, `now_us`) so the outputs are deterministic under test (design decision 1).

pub mod dub;
pub mod subtitles;
pub mod voice_over;

use std::path::{Path, PathBuf};

use serde_json::Value;

use crate::draft::json::ScanError;
use crate::project::{CapCutProject, ProjectTimeline};
use crate::tr;

/// Where new ids come from: random UUIDs in the apps, a counter in tests and golden dumps.
pub trait IdSource {
    /// A new id shaped like `UUID().uuidString` (36 characters, upper-case hex).
    fn next_id(&mut self) -> String;
}

/// Random version-4 UUIDs, upper case like Swift's `UUID().uuidString`.
#[derive(Debug, Default)]
pub struct RandomIds;

impl IdSource for RandomIds {
    fn next_id(&mut self) -> String {
        uuid::Uuid::new_v4().to_string().to_uppercase()
    }
}

/// `00000000-0000-4000-8000-000000000001`, `…002`, … for deterministic output.
#[derive(Debug, Default)]
pub struct CountingIds(pub u64);

impl IdSource for CountingIds {
    fn next_id(&mut self) -> String {
        self.0 += 1;
        format!("00000000-0000-4000-8000-{:012X}", self.0)
    }
}

/// Microseconds since 1970 now.
pub fn now_us() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_micros() as i64)
}

#[derive(Debug)]
pub enum WriteError {
    /// The project is open in CapCut, which would overwrite our changes when it saves.
    ProjectOpen(String),
    /// The draft has no `tracks` list.
    NoTracks,
    /// The project keeps a single timeline (an older CapCut): it has no `Timelines/project.json`.
    NoTimelineList(String),
    Malformed(ScanError),
    Io(std::io::Error),
}

impl std::fmt::Display for WriteError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            WriteError::ProjectOpen(name) => f.write_str(&tr!(
                "{0} is open in CapCut. Close the project (back to CapCut's home screen) and try again — otherwise CapCut overwrites the subtitles when it saves.",
                name
            )),
            WriteError::NoTracks => f.write_str("The draft has no tracks list."),
            WriteError::NoTimelineList(name) => f.write_str(&tr!(
                "{0} is from an older CapCut that keeps a single timeline. Open it in CapCut once, close it, and try again.",
                name
            )),
            WriteError::Malformed(e) => e.fmt(f),
            WriteError::Io(e) => e.fmt(f),
        }
    }
}

impl std::error::Error for WriteError {}

impl From<ScanError> for WriteError {
    fn from(e: ScanError) -> Self {
        WriteError::Malformed(e)
    }
}

impl From<std::io::Error> for WriteError {
    fn from(e: std::io::Error) -> Self {
        WriteError::Io(e)
    }
}

// MARK: - Reading values the way Foundation bridges `JSONSerialization` objects

/// `value as? NSNumber`'s `int64Value`: numbers truncated toward zero, booleans as 0 / 1.
pub(crate) fn ns_int64(v: Option<&Value>) -> Option<i64> {
    match v? {
        Value::Number(n) => n.as_i64().or_else(|| n.as_f64().map(|d| d as i64)),
        Value::Bool(b) => Some(i64::from(*b)),
        _ => None,
    }
}

/// `value as? NSNumber`'s `doubleValue`.
pub(crate) fn ns_double(v: Option<&Value>) -> Option<f64> {
    match v? {
        Value::Number(n) => n.as_f64(),
        Value::Bool(b) => Some(f64::from(u8::from(*b))),
        _ => None,
    }
}

/// `value as? Int` / `as? Int64`: an exactly integral number.
pub(crate) fn bridged_int(v: Option<&Value>) -> Option<i64> {
    v.and_then(crate::lenient::as_int)
}

/// `value as? Bool`: a boolean, or the number 0 or 1.
pub(crate) fn bridged_bool(v: Option<&Value>) -> Option<bool> {
    match v? {
        Value::Bool(b) => Some(*b),
        other => match crate::lenient::as_int(other) {
            Some(0) => Some(false),
            Some(1) => Some(true),
            _ => None,
        },
    }
}

/// `value as? [String]`: an array of strings only.
pub(crate) fn string_array(v: Option<&Value>) -> Option<Vec<String>> {
    v?.as_array()?.iter().map(|x| x.as_str().map(str::to_string)).collect()
}

/// `value as? [[String: Any]]`: an array of objects only.
pub(crate) fn object_array(v: Option<&Value>) -> Option<&Vec<Value>> {
    v?.as_array().filter(|items| items.iter().all(Value::is_object))
}

/// A JSON default parsed once (the writers' field sets).
pub(crate) fn parse(json: &str) -> Value {
    serde_json::from_str(json).expect("valid default JSON")
}

/// Microseconds of `seconds`, rounded, never negative (`Int64((max(0, s) * 1_000_000).rounded())`).
pub(crate) fn us(seconds: f64) -> i64 {
    (seconds.max(0.0) * 1_000_000.0).round() as i64
}

// MARK: - Files

/// `ISO8601DateFormatter` in UTC with `:` replaced by `-`: `2026-11-23T08-05-09Z`.
pub fn backup_stamp(now_us: i64) -> String {
    let secs = now_us.div_euclid(1_000_000);
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    // Days to civil date (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!("{year:04}-{month:02}-{day:02}T{:02}-{:02}-{:02}Z", rem / 3600, rem / 60 % 60, rem % 60)
}

/// Writes `data` to `path` through a temporary file in the same folder (`.atomic`).
pub fn atomic_write(path: &Path, data: &[u8]) -> std::io::Result<()> {
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let temp = path.with_file_name(format!(".{name}.ecc-{}.tmp", std::process::id()));
    std::fs::write(&temp, data)?;
    std::fs::rename(&temp, path).inspect_err(|_| {
        let _ = std::fs::remove_file(&temp);
    })
}

/// What a write changed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WriteResult {
    /// Every draft file that was rewritten.
    pub files: Vec<PathBuf>,
    /// The originals, saved before writing.
    pub backup_folder: PathBuf,
    pub cue_count: usize,
}

/// CapCut keeps a `.locked` file in an open project's folder, but a crash leaves stale ones, so the lock only counts
/// while CapCut is running (which the app checks natively and passes in).
pub fn is_open_in_capcut(project: &CapCutProject, capcut_running: bool) -> bool {
    capcut_running && project.path.join(".locked").exists()
}

/// Rewrites the timeline's draft with `transform`, after a backup into `backup_root/<project>/<stamp>`. Copies of the
/// draft that CapCut keeps (an identical `template-2.tmp` next to it and, for the main timeline, the root draft and
/// its `template-2.tmp`) are rewritten too. Refused while the project is open in CapCut.
pub fn write_draft(
    project: &CapCutProject,
    timeline: &ProjectTimeline,
    cue_count: usize,
    backup_root: &Path,
    capcut_running: bool,
    now_us: i64,
    transform: impl FnOnce(&[u8]) -> Result<Vec<u8>, WriteError>,
) -> Result<WriteResult, WriteError> {
    if is_open_in_capcut(project, capcut_running) {
        return Err(WriteError::ProjectOpen(project.name()));
    }
    let original = std::fs::read(&timeline.draft_path)?;
    let updated = transform(&original)?;

    let folder = timeline.draft_path.parent().unwrap_or(Path::new(""));
    let mut candidates = vec![timeline.draft_path.clone(), folder.join("template-2.tmp")];
    if timeline.is_main {
        candidates.push(project.draft_path());
        candidates.push(project.path.join("template-2.tmp"));
    }
    let mut files: Vec<PathBuf> = Vec::new();
    for path in candidates {
        if files.contains(&path) {
            continue;
        }
        if path == timeline.draft_path || std::fs::read(&path).ok().as_deref() == Some(original.as_slice()) {
            files.push(path);
        }
    }

    let backup = backup_root.join(project.name()).join(backup_stamp(now_us));
    std::fs::create_dir_all(&backup)?;
    for path in &files {
        let relative = path.strip_prefix(&project.path).unwrap_or(path);
        let dest = backup.join(relative);
        if let Some(parent) = dest.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::copy(path, &dest)?;
    }
    for path in &files {
        atomic_write(path, &updated)?;
    }
    Ok(WriteResult { files, backup_folder: backup, cue_count })
}

/// CapCut's caption language for one of our language codes: a language and region, like `ru-RU` or `en-US` (the
/// region Foundation's likely-subtags maximization gives; a code that has a region keeps it).
pub fn capcut_language(code: &str) -> String {
    let parts: Vec<&str> = code.split(['-', '_']).collect();
    let language = parts[0].to_lowercase();
    // An explicit region (`pt-BR`, `en-GB`): two letters or three digits.
    if let Some(region) = parts[1..].iter().find(|p| p.len() == 2 && p.chars().all(|c| c.is_ascii_alphabetic())) {
        return format!("{language}-{}", region.to_uppercase());
    }
    let script = parts[1..].iter().find(|p| p.len() == 4).map(|s| s.to_lowercase());
    let region = match (language.as_str(), script.as_deref()) {
        ("zh", Some("hant")) => "TW",
        ("zh", _) => "CN",
        ("ar", _) => "EG",
        ("bg", _) => "BG",
        ("bn", _) => "BD",
        ("ca", _) => "ES",
        ("cs", _) => "CZ",
        ("da", _) => "DK",
        ("de", _) => "DE",
        ("el", _) => "GR",
        ("en", _) => "US",
        ("es", _) => "ES",
        ("fa", _) => "IR",
        ("fi", _) => "FI",
        ("fil", _) => "PH",
        ("fr", _) => "FR",
        ("he", _) => "IL",
        ("hi", _) => "IN",
        ("hr", _) => "HR",
        ("hu", _) => "HU",
        ("id", _) => "ID",
        ("it", _) => "IT",
        ("ja", _) => "JP",
        ("km", _) => "KH",
        ("ko", _) => "KR",
        ("lo", _) => "LA",
        ("ms", _) => "MY",
        ("my", _) => "MM",
        ("nb" | "no", _) => "NO",
        ("nl", _) => "NL",
        ("pl", _) => "PL",
        ("pt", _) => "BR",
        ("ro", _) => "RO",
        ("ru", _) => "RU",
        ("sk", _) => "SK",
        ("sr", _) => "RS",
        ("sv", _) => "SE",
        ("ta", _) => "IN",
        ("th", _) => "TH",
        ("tr", _) => "TR",
        ("uk", _) => "UA",
        ("ur", _) => "PK",
        ("vi", _) => "VN",
        _ => return code.to_string(),
    };
    format!("{language}-{region}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn caption_languages_match_foundations_maximization() {
        // What Swift's `Locale.Language(identifier:).maximalIdentifier` gives (macOS 27).
        let expected =
            "ar ar-EG cs cs-CZ de de-DE en en-US es es-ES fr fr-FR he he-IL hi hi-IN id id-ID it it-IT ja ja-JP \
            ko ko-KR ms ms-MY nl nl-NL pl pl-PL pt-BR pt-BR pt pt-BR pt-PT pt-PT ru ru-RU sv sv-SE th th-TH tr tr-TR \
            uk uk-UA vi vi-VN zh-Hans zh-CN zh-Hant zh-TW zh zh-CN en-GB en-GB fa fa-IR bn bn-BD da da-DK fi fi-FI \
            el el-GR hu hu-HU ro ro-RO sk sk-SK bg bg-BG hr hr-HR sr sr-RS ca ca-ES nb nb-NO no no-NO fil fil-PH \
            ta ta-IN ur ur-PK lo lo-LA km km-KH my my-MM xx xx";
        let words: Vec<&str> = expected.split_whitespace().collect();
        for pair in words.chunks(2) {
            assert_eq!(capcut_language(pair[0]), pair[1], "{}", pair[0]);
        }
    }

    #[test]
    fn backup_stamps_are_utc_iso8601() {
        assert_eq!(backup_stamp(0), "1970-01-01T00-00-00Z");
        assert_eq!(backup_stamp(1_795_420_800_123_456), "2026-11-23T08-00-00Z");
        assert_eq!(backup_stamp(951_782_400_000_000), "2000-02-29T00-00-00Z");
    }

    #[test]
    fn writes_the_draft_and_its_copies_after_a_backup() {
        let dir = crate::testing::TempDir::new();
        let root = dir.path().join("Trip");
        let folder = root.join("Timelines/MAIN");
        std::fs::create_dir_all(&folder).unwrap();
        for path in [folder.join("draft_info.json"), folder.join("template-2.tmp"), root.join("draft_info.json")] {
            std::fs::write(path, "{\"a\":1}").unwrap();
        }
        std::fs::write(root.join("template-2.tmp"), "{\"old\":true}").unwrap(); // not a copy: left alone
        std::fs::write(
            root.join("Timelines/project.json"),
            r#"{"main_timeline_id":"MAIN","timelines":[{"id":"MAIN","name":"Main"}]}"#,
        )
        .unwrap();
        std::fs::write(root.join(".locked"), "").unwrap();
        let project = CapCutProject::new(&root);
        let timeline = project.timelines()[0].clone();
        let backups = dir.path().join("Backups");
        let transform = |d: &[u8]| Ok(String::from_utf8_lossy(d).replace('1', "2").into_bytes());
        let refused = write_draft(&project, &timeline, 3, &backups, true, 0, transform);
        assert!(matches!(refused, Err(WriteError::ProjectOpen(name)) if name == "Trip"));
        let result = write_draft(&project, &timeline, 3, &backups, false, 1_795_420_800_000_000, transform).unwrap();
        assert_eq!(
            result.files,
            [folder.join("draft_info.json"), folder.join("template-2.tmp"), root.join("draft_info.json")]
        );
        assert_eq!(result.backup_folder, backups.join("Trip/2026-11-23T08-00-00Z"));
        assert_eq!(result.cue_count, 3);
        for file in &result.files {
            assert_eq!(std::fs::read_to_string(file).unwrap(), "{\"a\":2}");
        }
        assert_eq!(std::fs::read_to_string(root.join("template-2.tmp")).unwrap(), "{\"old\":true}");
        assert_eq!(
            std::fs::read_to_string(result.backup_folder.join("Timelines/MAIN/draft_info.json")).unwrap(),
            "{\"a\":1}"
        );
        assert!(result.backup_folder.join("draft_info.json").exists());
    }

    #[test]
    fn foundation_bridging() {
        use serde_json::json;
        let v = json!({"i": 3, "f": 2.9, "b": true, "s": "x", "one": 1.0, "two": 2});
        assert_eq!(ns_int64(v.get("f")), Some(2));
        assert_eq!(ns_int64(v.get("b")), Some(1));
        assert_eq!(ns_int64(v.get("s")), None);
        assert_eq!(ns_double(v.get("i")), Some(3.0));
        assert_eq!(bridged_int(v.get("f")), None);
        assert_eq!(bridged_bool(v.get("one")), Some(true));
        assert_eq!(bridged_bool(v.get("two")), None);
        assert_eq!(string_array(Some(&json!(["a", 1]))), None);
        assert!(object_array(Some(&json!([{}, 1]))).is_none());
        assert_eq!(CountingIds(0).next_id(), "00000000-0000-4000-8000-000000000001");
        assert_eq!(RandomIds.next_id().len(), 36);
    }
}
