//! A dubbed copy of a timeline, added to the project as a timeline of its own (the source timeline stays as it was):
//! the port of `DubTimeline.swift`.
//!
//! A new timeline is an entry in `Timelines/project.json`, a folder `Timelines/<id>/` with the draft (whose root `id`
//! is the timeline's) and its `template-2.tmp` copy, and a tab in `timeline_layout.json`'s first dock (see
//! docs/FORMAT.md). The source's plain-JSON attachments are copied along; `draft.extra` (binary) is left to CapCut.

use std::path::Path;

use serde_json::json;

use super::voice_over::OriginalAudio;
use super::{atomic_write, backup_stamp, is_open_in_capcut, IdSource, WriteError, WriteResult};
use crate::draft::json::{json_string, json_text, JsonEdits, JsonScanner};
use crate::project::{CapCutProject, ProjectTimeline};

/// Files and folders next to a timeline's draft that are copied into the new timeline's folder.
pub const ATTACHMENTS: [&str; 3] = ["attachment_editing.json", "attachment_pc_common.json", "common_attachment"];

/// `<project>_dub_<languages>_<original audio>_<n>`, with the first `n` from 1 that no timeline has.
pub fn name(project: &CapCutProject, languages: &[String], original: OriginalAudio) -> String {
    let taken: Vec<String> = project.timelines().into_iter().map(|t| t.name).collect();
    let base = format!("{}_dub_{}_{}", project.name(), languages.join("-"), original.name_token());
    let mut n = 1;
    while taken.contains(&format!("{base}_{n}")) {
        n += 1;
    }
    format!("{base}_{n}")
}

/// Adds a timeline named `name` to the project: `source`'s draft run through `transform`. `project.json` and
/// `timeline_layout.json` are backed up to `backup_root` first. Refused while the project is open in CapCut, and for
/// projects that keep a single timeline (no `Timelines/project.json`).
#[allow(clippy::too_many_arguments)]
pub fn create(
    project: &CapCutProject,
    source: &ProjectTimeline,
    name: &str,
    cue_count: usize,
    backup_root: &Path,
    capcut_running: bool,
    now_us: i64,
    ids: &mut dyn IdSource,
    transform: impl FnOnce(&[u8]) -> Result<Vec<u8>, WriteError>,
) -> Result<(ProjectTimeline, WriteResult), WriteError> {
    if is_open_in_capcut(project, capcut_running) {
        return Err(WriteError::ProjectOpen(project.name()));
    }
    let timelines = project.path.join("Timelines");
    let list_path = timelines.join("project.json");
    let list = std::fs::read(&list_path).map_err(|_| WriteError::NoTimelineList(project.name()))?;
    let layout_path = project.path.join("timeline_layout.json");
    let layout = std::fs::read(&layout_path).ok();

    let id = ids.next_id();
    let draft = with_root_id(&transform(&std::fs::read(&source.draft_path)?)?, &id)?;
    let new_list = add_entry(&list, &id, name, now_us)?.ok_or_else(|| WriteError::NoTimelineList(project.name()))?;
    let new_layout = match &layout {
        Some(l) => Some(add_tab(l, &id, name)?),
        None => None,
    };

    let backup = backup_root.join(project.name()).join(backup_stamp(now_us));
    std::fs::create_dir_all(backup.join("Timelines"))?;
    std::fs::copy(&list_path, backup.join("Timelines/project.json"))?;
    if layout.is_some() {
        std::fs::copy(&layout_path, backup.join("timeline_layout.json"))?;
    }

    let folder = timelines.join(&id);
    std::fs::create_dir_all(&folder)?;
    let result = (|| -> Result<(ProjectTimeline, WriteResult), WriteError> {
        let source_folder = source.draft_path.parent().unwrap_or(Path::new(""));
        for item in ATTACHMENTS {
            let from = source_folder.join(item);
            if from.exists() {
                copy_recursively(&from, &folder.join(item))?;
            }
        }
        let draft_name = source.draft_path.file_name().unwrap_or_default();
        let files = vec![folder.join(draft_name), folder.join("template-2.tmp")];
        for path in &files {
            atomic_write(path, &draft)?;
        }
        // The list last: until then CapCut doesn't know the folder.
        atomic_write(&list_path, &new_list)?;
        if let Some(new_layout) = &new_layout {
            atomic_write(&layout_path, new_layout)?;
        }
        let timeline =
            ProjectTimeline { id: id.clone(), name: name.to_string(), draft_path: files[0].clone(), is_main: false };
        Ok((timeline, WriteResult { files, backup_folder: backup.clone(), cue_count }))
    })();
    if result.is_err() {
        let _ = std::fs::remove_dir_all(&folder);
    }
    result
}

fn copy_recursively(from: &Path, to: &Path) -> std::io::Result<()> {
    if from.is_dir() {
        std::fs::create_dir_all(to)?;
        for entry in std::fs::read_dir(from)?.flatten() {
            copy_recursively(&entry.path(), &to.join(entry.file_name()))?;
        }
        Ok(())
    } else {
        std::fs::copy(from, to).map(|_| ())
    }
}

/// The draft with its root `id` (the timeline's id) replaced.
pub fn with_root_id(draft: &[u8], id: &str) -> Result<Vec<u8>, WriteError> {
    let scanner = JsonScanner::new(draft);
    let root = scanner.root()?;
    let mut edits = JsonEdits::new();
    match scanner.value("id", root.clone())? {
        Some(range) => edits.replace(range, &json_string(id)),
        None => {
            let sep = if scanner.members(root.clone())?.is_empty() { "" } else { "," };
            edits.replace(root.start + 1..root.start + 1, &format!("\"id\":{}{sep}", json_string(id)));
        }
    }
    Ok(edits.applied(scanner.bytes))
}

/// `project.json` with an entry for the new timeline appended to `timelines`; `None` without that list.
pub fn add_entry(list: &[u8], id: &str, name: &str, now: i64) -> Result<Option<Vec<u8>>, WriteError> {
    let scanner = JsonScanner::new(list);
    let root = scanner.root()?;
    let entry = json_text(&json!({
        "create_time": now, "id": id, "is_marked_delete": false, "name": name, "update_time": now,
    }));
    let Some(range) = scanner.value("timelines", root)?.filter(|r| scanner.bytes[r.start] == b'[') else {
        return Ok(None);
    };
    let mut edits = JsonEdits::new();
    let close = range.end - 1;
    let sep = if scanner.elements(range)?.is_empty() { "" } else { "," };
    edits.replace(close..close, &format!("{sep}{entry}"));
    Ok(Some(edits.applied(scanner.bytes)))
}

/// `timeline_layout.json` with the timeline added as a tab of the first dock; unchanged when it has no dock.
pub fn add_tab(layout: &[u8], id: &str, name: &str) -> Result<Vec<u8>, WriteError> {
    let scanner = JsonScanner::new(layout);
    let is_array = |r: &std::ops::Range<usize>| scanner.bytes[r.start] == b'[';
    let Some(docks) = scanner.value("dockItems", scanner.root()?)?.filter(is_array) else { return Ok(layout.to_vec()) };
    let Some(dock) = scanner.elements(docks)?.into_iter().next() else { return Ok(layout.to_vec()) };
    if scanner.bytes[dock.start] != b'{' {
        return Ok(layout.to_vec());
    }
    let (Some(ids), Some(names)) = (
        scanner.value("timelineIds", dock.clone())?.filter(is_array),
        scanner.value("timelineNames", dock)?.filter(is_array),
    ) else {
        return Ok(layout.to_vec());
    };
    let mut edits = JsonEdits::new();
    for (range, value) in [(ids, id), (names, name)] {
        let close = range.end - 1;
        let sep = if scanner.elements(range)?.is_empty() { "" } else { "," };
        edits.replace(close..close, &format!("{sep}{}", json_string(value)));
    }
    Ok(edits.applied(scanner.bytes))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::TempDir;
    use crate::writers::CountingIds;

    const MAIN_ID: &str = "40332EEA-CA6F-4C3A-A039-B145ECAED041";

    /// A project with one timeline in `Timelines/`, its root draft copy and a dock with that timeline.
    fn setup() -> (TempDir, std::path::PathBuf) {
        let dir = TempDir::new();
        let root = dir.path().join("Trip");
        let folder = root.join("Timelines").join(MAIN_ID);
        std::fs::create_dir_all(folder.join("common_attachment")).unwrap();
        let draft = format!(r#"{{"id":"{MAIN_ID}","duration":1000000,"tracks":[]}}"#);
        for path in [folder.join("draft_info.json"), folder.join("template-2.tmp"), root.join("draft_info.json")] {
            std::fs::write(path, &draft).unwrap();
        }
        std::fs::write(folder.join("attachment_editing.json"), "{}").unwrap();
        std::fs::write(folder.join("common_attachment/attachment_pc_timeline.json"), "{}").unwrap();
        std::fs::write(folder.join("draft.extra"), "x").unwrap();
        std::fs::write(
            root.join("Timelines/project.json"),
            format!(
                r#"{{"config":{{"color_space":-1}},"id":"P","main_timeline_id":"{MAIN_ID}","timelines":[{{"create_time":1,"id":"{MAIN_ID}","is_marked_delete":false,"name":"Timeline 01","update_time":1}}],"version":0}}"#
            ),
        )
        .unwrap();
        std::fs::write(
            root.join("timeline_layout.json"),
            format!(
                r#"{{"dockItems":[{{"dockIndex":0,"ratio":1,"timelineIds":["{MAIN_ID}"],"timelineNames":["Timeline 01"]}}],"layoutOrientation":1}}"#
            ),
        )
        .unwrap();
        (dir, root)
    }

    /// Port of `DubTimelineTests.testCreatesANumberedDubTimeline`.
    #[test]
    fn creates_a_numbered_dub_timeline() {
        let (dir, root) = setup();
        let project = CapCutProject::new(&root);
        let source = project.timelines()[0].clone();
        let languages = vec!["en".to_string()];
        let name = name(&project, &languages, OriginalAudio::Mute);
        assert_eq!(name, "Trip_dub_en_muteall_1");
        let backups = dir.path().join("Backups");
        let (timeline, result) = create(
            &project,
            &source,
            &name,
            0,
            &backups,
            false,
            1_700_000_000_000_000,
            &mut CountingIds::default(),
            |d| Ok(String::from_utf8_lossy(d).replace("\"tracks\":[]", "\"tracks\":[1]").into_bytes()),
        )
        .unwrap();
        assert!(!timeline.is_main);
        // The project lists it, with the transformed draft under its own id; the source is untouched.
        let timelines = project.timelines();
        assert_eq!(timelines.iter().map(|t| t.name.as_str()).collect::<Vec<_>>(), ["Timeline 01", name.as_str()]);
        let draft: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&timelines[1].draft_path).unwrap()).unwrap();
        assert_eq!(draft["id"], timeline.id.as_str());
        assert_eq!(draft["tracks"], json!([1]));
        let folder = timelines[1].draft_path.parent().unwrap();
        assert_eq!(
            std::fs::read(folder.join("template-2.tmp")).unwrap(),
            std::fs::read(&timelines[1].draft_path).unwrap()
        );
        assert!(folder.join("attachment_editing.json").exists());
        assert!(folder.join("common_attachment/attachment_pc_timeline.json").exists());
        assert!(!folder.join("draft.extra").exists());
        assert!(String::from_utf8(std::fs::read(&source.draft_path).unwrap()).unwrap().contains("\"tracks\":[]"));
        // A tab in the dock; project.json keeps its other fields.
        let layout = String::from_utf8(std::fs::read(root.join("timeline_layout.json")).unwrap()).unwrap();
        assert!(layout.contains(&format!(
            r#""timelineIds":["{MAIN_ID}","{}"],"timelineNames":["Timeline 01","{name}"]"#,
            timeline.id
        )));
        let list = String::from_utf8(std::fs::read(root.join("Timelines/project.json")).unwrap()).unwrap();
        assert!(list.starts_with(r#"{"config":{"color_space":-1},"id":"P""#));
        assert!(list.contains(r#""create_time":1700000000000000"#));
        assert!(result.backup_folder.join("Timelines/project.json").exists());
        assert!(result.backup_folder.starts_with(backups.join("Trip")));
        // The next one with the same settings gets the next number.
        assert_eq!(super::name(&project, &languages, OriginalAudio::Mute), "Trip_dub_en_muteall_2");
        let two = vec!["en".to_string(), "th".to_string()];
        assert_eq!(super::name(&project, &two, OriginalAudio::RemoveVocal), "Trip_dub_en-th_removevocal_1");
    }

    /// Port of `DubTimelineTests.testOlderSingleTimelineProjectsAreRefused`.
    #[test]
    fn older_single_timeline_projects_are_refused() {
        let (dir, root) = setup();
        std::fs::remove_file(root.join("Timelines/project.json")).unwrap();
        let project = CapCutProject::new(&root);
        let source = project.timelines()[0].clone();
        let result =
            create(&project, &source, "x", 0, &dir.path().join("B"), false, 0, &mut CountingIds::default(), |d| {
                Ok(d.to_vec())
            });
        assert!(matches!(result, Err(WriteError::NoTimelineList(_))));
    }

    #[test]
    fn open_projects_are_refused_only_while_capcut_runs() {
        let (dir, root) = setup();
        std::fs::write(root.join(".locked"), "").unwrap();
        let project = CapCutProject::new(&root);
        let source = project.timelines()[0].clone();
        let backups = dir.path().join("B");
        let run = |running: bool| {
            create(&project, &source, "x", 0, &backups, running, 0, &mut CountingIds::default(), |d| Ok(d.to_vec()))
        };
        assert!(matches!(run(true), Err(WriteError::ProjectOpen(_))));
        assert!(run(false).is_ok(), "a stale lock without CapCut running doesn't count");
    }

    #[test]
    fn root_ids_entries_and_tabs() {
        assert_eq!(with_root_id(br#"{"a":1}"#, "X").unwrap(), br#"{"id":"X","a":1}"#);
        assert_eq!(with_root_id(br#"{}"#, "X").unwrap(), br#"{"id":"X"}"#);
        assert_eq!(with_root_id(br#"{"id": "old", "b": 2}"#, "X").unwrap(), br#"{"id": "X", "b": 2}"#);
        assert_eq!(
            add_entry(br#"{"timelines":[]}"#, "I", "N", 5).unwrap().unwrap(),
            br#"{"timelines":[{"create_time":5,"id":"I","is_marked_delete":false,"name":"N","update_time":5}]}"#
        );
        assert_eq!(add_entry(br#"{"x":[]}"#, "I", "N", 5).unwrap(), None);
        assert_eq!(add_tab(br#"{"dockItems":[]}"#, "I", "N").unwrap(), br#"{"dockItems":[]}"#);
    }
}
