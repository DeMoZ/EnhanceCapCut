//! The committed golden outputs (task 5.6): every fixture in `core/fixtures`, written as `draft_info.json` and as
//! `draft_content.json`, must give exactly `expected/info.txt`, `expected/timeline.json` and `expected/writers.txt`.
//! `UPDATE_GOLDEN=1 cargo test -p ecc-golden` rewrites them; review the diff before committing.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, Ordering};

use ecc_golden::fixtures::{self, DraftName};

fn fixtures_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures")
}

/// A fresh folder for materialized projects, removed afterwards.
struct Scratch(PathBuf);

impl Scratch {
    fn new() -> Scratch {
        static N: AtomicU32 = AtomicU32::new(0);
        let path = std::env::temp_dir().join(format!(
            "ecc-golden-{}-{}",
            std::process::id(),
            N.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = std::fs::remove_dir_all(&path);
        std::fs::create_dir_all(&path).unwrap();
        Scratch(path)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// The files of `expected/` that differ from the fixture's outputs (rewritten instead when `update`).
fn mismatches(fixture: &Path, name: DraftName, update: bool) -> Vec<String> {
    let scratch = Scratch::new();
    let project = fixtures::materialize(fixture, &scratch.0, name).unwrap();
    let outputs = fixtures::outputs(&project).unwrap_or_else(|e| panic!("{}: {e}", fixture.display()));
    let expected = fixture.join("expected");
    let mut wrong = Vec::new();
    for (file, text) in outputs {
        let path = expected.join(file);
        if update {
            std::fs::create_dir_all(&expected).unwrap();
            std::fs::write(&path, &text).unwrap();
        } else if std::fs::read_to_string(&path).ok().as_deref() != Some(text.as_str()) {
            wrong.push(format!("{} ({:?})", path.display(), name));
        }
    }
    wrong
}

#[test]
fn fixtures_match_their_expected_outputs() {
    let update = std::env::var_os("UPDATE_GOLDEN").is_some_and(|v| v == "1");
    let all = fixtures::list(&fixtures_root());
    assert!(all.len() >= 9, "fixtures not found under {}", fixtures_root().display());
    let mut wrong = Vec::new();
    for fixture in &all {
        // Written once, checked against both draft file names.
        wrong.extend(mismatches(fixture, DraftName::Info, update));
        wrong.extend(mismatches(fixture, DraftName::Content, false));
    }
    assert!(
        wrong.is_empty(),
        "outputs differ from the committed expected files (UPDATE_GOLDEN=1 rewrites them):\n{}",
        wrong.join("\n")
    );
}

#[test]
fn a_changed_expected_file_fails() {
    let scratch = Scratch::new();
    let fixture = scratch.0.join("timelines");
    let source = fixtures_root().join("timelines");
    std::fs::create_dir_all(fixture.join("expected")).unwrap();
    for file in ["draft.fixture.json", "timelines.fixture.json", "media.txt"] {
        std::fs::copy(source.join(file), fixture.join(file)).unwrap();
    }
    for file in ["info.txt", "timeline.json", "writers.txt"] {
        std::fs::copy(source.join("expected").join(file), fixture.join("expected").join(file)).unwrap();
    }
    assert!(mismatches(&fixture, DraftName::Info, false).is_empty());
    let info = fixture.join("expected/info.txt");
    let changed = std::fs::read_to_string(&info).unwrap().replace("1.00s", "1.01s");
    std::fs::write(&info, changed).unwrap();
    assert_eq!(mismatches(&fixture, DraftName::Info, false).len(), 1);
}

#[test]
fn materialized_projects_have_both_draft_names_and_their_timelines() {
    let scratch = Scratch::new();
    let fixture = fixtures_root().join("timelines");
    let a = fixtures::materialize(&fixture, &scratch.0, DraftName::Info).unwrap();
    assert!(a.join("draft_info.json").is_file());
    assert!(a.join("Timelines/A0000000-0000-4000-8000-000000000001/draft_info.json").is_file());
    assert!(a.join("media/p.png").is_file());
    let b = fixtures::materialize(&fixture, &scratch.0, DraftName::Content).unwrap();
    assert!(b.join("draft_content.json").is_file() && !b.join("draft_info.json").exists());
    let looks =
        fixtures::materialize(&fixtures_root().join("keyframes-and-motion"), &scratch.0, DraftName::Info).unwrap();
    assert_eq!(std::fs::read(looks.join("filters/warm/lut.png")).unwrap().len(), 24);
}
