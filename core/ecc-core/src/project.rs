//! CapCut projects on disk, the port of `Project.swift`: `CapCutProject`, `ProjectTimeline`, `DraftFile`,
//! `ProjectLocator` and `DraftLoader`.

use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;

use serde_json::Value;

use crate::draft::Draft;
use crate::env::home_dir;
use crate::lenient::Lenient;
use crate::tr;

/// The file a CapCut draft is stored in: `draft_info.json` in CapCut for macOS, `draft_content.json` in CapCut for
/// Windows (seen with new_version 189.0.0). Both are plain JSON of the same format, in the same places: the project
/// folder and `Timelines/<id>/`.
pub struct DraftFile;

impl DraftFile {
    pub const NAMES: [&'static str; 2] = ["draft_info.json", "draft_content.json"];

    /// The platform's own file name, used when a folder has no draft yet.
    pub const DEFAULT_NAME: &'static str = if cfg!(windows) { "draft_content.json" } else { "draft_info.json" };

    pub fn is_name(file_name: &str) -> bool {
        Self::NAMES.contains(&file_name)
    }

    /// The draft in `folder`. When there is none, the path with the platform's own file name, so that callers have
    /// something to report.
    pub fn find(folder: &Path) -> PathBuf {
        Self::NAMES
            .iter()
            .map(|n| folder.join(n))
            .find(|p| p.is_file())
            .unwrap_or_else(|| folder.join(Self::DEFAULT_NAME))
    }

    pub fn exists(folder: &Path) -> bool {
        Self::find(folder).is_file()
    }
}

/// Modification time in milliseconds since 1970, if the file system has one.
fn modified_ms(path: &Path) -> Option<i64> {
    let time = std::fs::metadata(path).ok()?.modified().ok()?;
    Some(match time.duration_since(UNIX_EPOCH) {
        Ok(d) => d.as_millis() as i64,
        Err(e) => -(e.duration().as_millis() as i64),
    })
}

/// A CapCut project folder on disk.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct CapCutProject {
    pub path: PathBuf,
    /// The draft's (or, without one, the folder's) modification time in ms since 1970; `None` when unknown.
    pub modified_ms: Option<i64>,
    /// CapCut keeps a `.locked` file in the folder of every project that is open in the editor.
    pub is_open: bool,
}

impl CapCutProject {
    pub fn new(path: impl Into<PathBuf>) -> CapCutProject {
        let path = path.into();
        let draft = DraftFile::find(&path);
        let modified_ms = modified_ms(&draft).or_else(|| modified_ms(&path));
        let is_open = path.join(".locked").exists();
        CapCutProject { path, modified_ms, is_open }
    }

    /// The folder's name.
    pub fn name(&self) -> String {
        self.path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
    }

    pub fn draft_path(&self) -> PathBuf {
        DraftFile::find(&self.path)
    }

    /// CapCut 9 projects can hold several timelines (`Timelines/project.json`), each with its own draft in
    /// `Timelines/<id>/` (see `DraftFile`); the root draft is a copy of the main one. Projects without that file
    /// have a single timeline: the root draft.
    pub fn timelines(&self) -> Vec<ProjectTimeline> {
        let root = self.path.join("Timelines");
        let single = vec![ProjectTimeline {
            id: String::new(),
            name: "Timeline 01".into(),
            draft_path: self.draft_path(),
            is_main: true,
        }];
        let Some(list) = std::fs::read(root.join("project.json"))
            .ok()
            .and_then(|data| serde_json::from_slice::<Value>(&data).ok())
            .filter(Value::is_object)
        else {
            return single;
        };
        let main_id = list.string("main_timeline_id");
        let entries = list.list("timelines", |e| {
            e.is_object().then(|| (e.string("id"), e.string("name"), e.bool("is_marked_delete").unwrap_or(false)))
        });
        let found: Vec<ProjectTimeline> = entries
            .into_iter()
            .filter(|(id, _, deleted)| !id.is_empty() && !deleted)
            .filter_map(|(id, name, _)| {
                let is_main = id == main_id;
                let mut draft = DraftFile::find(&root.join(&id));
                if !draft.is_file() {
                    if !is_main {
                        return None;
                    }
                    draft = self.draft_path();
                }
                let name = if name.is_empty() { id.clone() } else { name };
                Some(ProjectTimeline { id, name, draft_path: draft, is_main })
            })
            .collect();
        if found.is_empty() {
            single
        } else {
            found
        }
    }

    /// Picks a timeline by name (any case), id or 1-based number, as `ecc --timeline` does; `None` means the main
    /// one.
    pub fn timeline(&self, name_or_number: Option<&str>) -> Option<ProjectTimeline> {
        let timelines = self.timelines();
        match name_or_number {
            None => timelines.iter().find(|t| t.is_main).or(timelines.first()).cloned(),
            Some(arg) => {
                let lower = arg.to_lowercase();
                if let Some(t) = timelines.iter().find(|t| t.name.to_lowercase() == lower || t.id == arg) {
                    return Some(t.clone());
                }
                let n: usize = arg.parse().ok()?;
                timelines.get(n.checked_sub(1)?).cloned()
            }
        }
    }

    pub fn load_draft(&self, timeline: Option<&ProjectTimeline>) -> Result<Draft, LoadError> {
        DraftLoader::load(&timeline.map(|t| t.draft_path.clone()).unwrap_or_else(|| self.draft_path()))
    }
}

/// One timeline of a project.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct ProjectTimeline {
    /// The timeline's UUID; empty for projects without `Timelines/project.json`.
    pub id: String,
    pub name: String,
    pub draft_path: PathBuf,
    pub is_main: bool,
}

pub struct ProjectLocator;

impl ProjectLocator {
    /// All projects under `root` that have a draft, most recently modified first.
    pub fn all_projects(root: &Path) -> Vec<CapCutProject> {
        let Ok(entries) = std::fs::read_dir(root) else {
            return Vec::new();
        };
        let mut projects: Vec<CapCutProject> = entries
            .flatten()
            .map(|e| e.path())
            .filter(|p| !p.file_name().is_some_and(|n| n.to_string_lossy().starts_with('.')))
            .filter(|p| DraftFile::exists(p))
            .map(CapCutProject::new)
            .collect();
        projects.sort_by_key(|p| std::cmp::Reverse(p.modified_ms));
        projects
    }

    /// The project currently open in CapCut. A crashed CapCut can leave stale `.locked` files, so when several
    /// projects look open the most recently modified one wins.
    pub fn open_project(root: &Path) -> Option<CapCutProject> {
        Self::all_projects(root).into_iter().find(|p| p.is_open)
    }

    /// Resolves a CLI argument: a path to a project folder or draft file, or a project name under `root`.
    pub fn resolve(name_or_path: &str, root: &Path) -> Option<CapCutProject> {
        let mut path = expand_tilde(name_or_path);
        if path.file_name().is_some_and(|n| DraftFile::is_name(&n.to_string_lossy())) {
            path.pop();
        }
        if DraftFile::exists(&path) {
            return Some(CapCutProject::new(std::path::absolute(&path).unwrap_or(path)));
        }
        let named = root.join(name_or_path);
        DraftFile::exists(&named).then(|| CapCutProject::new(named))
    }
}

fn expand_tilde(path: &str) -> PathBuf {
    match path.strip_prefix('~') {
        Some(rest) if rest.is_empty() || rest.starts_with(['/', '\\']) => {
            home_dir().join(rest.trim_start_matches(['/', '\\']))
        }
        _ => PathBuf::from(path),
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LoadError {
    Unreadable { path: PathBuf, reason: String },
}

impl std::fmt::Display for LoadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            LoadError::Unreadable { path, reason } => {
                f.write_str(&tr!("Cannot read {0}: {1}", path.display().to_string(), reason))
            }
        }
    }
}

impl std::error::Error for LoadError {}

pub struct DraftLoader;

impl DraftLoader {
    pub fn load_project(project: &Path) -> Result<Draft, LoadError> {
        Self::load(&DraftFile::find(project))
    }

    pub fn load(draft_path: &Path) -> Result<Draft, LoadError> {
        let unreadable = |reason: String| LoadError::Unreadable { path: draft_path.to_path_buf(), reason };
        let text = std::fs::read_to_string(draft_path).map_err(|e| unreadable(e.to_string()))?;
        Draft::parse(&text).map_err(unreadable)
    }

    /// Paths inside a draft may contain a placeholder for the project folder
    /// (`##_draftpath_placeholder_<anything but #>_##`); every one is replaced by `project`.
    pub fn resolve_path(path: &str, project: &Path) -> Option<PathBuf> {
        if path.is_empty() {
            return None;
        }
        const START: &str = "##_draftpath_placeholder_";
        let folder = project.to_string_lossy();
        let mut out = String::with_capacity(path.len());
        let mut rest = path;
        while let Some(i) = rest.find(START) {
            let after = &rest[i + START.len()..];
            // `[^#]*_##`: the shortest run without '#' that ends in "_##".
            let tail_len = after.find('#').filter(|&j| after[..j].ends_with('_') && after[j..].starts_with("##"));
            match tail_len {
                Some(j) => {
                    out.push_str(&rest[..i]);
                    out.push_str(&folder);
                    rest = &after[j + 2..];
                }
                None => {
                    out.push_str(&rest[..i + START.len()]);
                    rest = after;
                }
            }
        }
        out.push_str(rest);
        Some(PathBuf::from(out))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::TempDir;

    const DRAFT: &str = r#"{"duration": 1000000, "tracks": []}"#;

    #[test]
    fn finds_draft_info_or_draft_content() {
        let dir = TempDir::new();
        let p = dir.path();
        assert_eq!(DraftFile::find(p), p.join(DraftFile::DEFAULT_NAME));
        assert!(!DraftFile::exists(p));
        std::fs::write(p.join("draft_content.json"), DRAFT).unwrap();
        assert_eq!(DraftFile::find(p), p.join("draft_content.json"));
        std::fs::write(p.join("draft_info.json"), DRAFT).unwrap();
        assert_eq!(DraftFile::find(p), p.join("draft_info.json"));
        let project = CapCutProject::new(p);
        assert!(project.modified_ms.is_some());
        assert!(!project.is_open);
        assert_eq!(project.load_draft(None).unwrap().duration, 1_000_000);
    }

    #[test]
    fn single_timeline_without_project_json() {
        let dir = TempDir::new();
        std::fs::write(dir.path().join("draft_content.json"), DRAFT).unwrap();
        let timelines = CapCutProject::new(dir.path()).timelines();
        assert_eq!(timelines.len(), 1);
        assert_eq!(timelines[0].name, "Timeline 01");
        assert!(timelines[0].is_main);
        assert_eq!(timelines[0].draft_path, dir.path().join("draft_content.json"));
    }

    #[test]
    fn several_timelines_and_selection() {
        let dir = TempDir::new();
        let p = dir.path();
        std::fs::write(p.join("draft_info.json"), DRAFT).unwrap();
        for id in ["A", "B"] {
            std::fs::create_dir_all(p.join("Timelines").join(id)).unwrap();
        }
        std::fs::write(p.join("Timelines/B/draft_content.json"), DRAFT).unwrap();
        std::fs::write(
            p.join("Timelines/project.json"),
            r#"{"main_timeline_id": "A", "timelines": [
                {"id": "A", "name": "Main cut"}, {"id": "B", "name": ""}, {"id": "C", "name": "No draft"},
                {"id": "D", "is_marked_delete": 1}, {"id": ""}, "junk"]}"#,
        )
        .unwrap();
        let project = CapCutProject::new(p);
        let timelines = project.timelines();
        let names: Vec<_> = timelines.iter().map(|t| t.name.as_str()).collect();
        assert_eq!(names, ["Main cut", "B"]);
        // The main timeline without its own draft uses the root draft.
        assert_eq!(timelines[0].draft_path, p.join("draft_info.json"));
        assert!(timelines[0].is_main && !timelines[1].is_main);
        assert_eq!(timelines[1].draft_path, p.join("Timelines/B/draft_content.json"));
        assert_eq!(project.timeline(None).unwrap().id, "A");
        assert_eq!(project.timeline(Some("B")).unwrap().id, "B");
        assert_eq!(project.timeline(Some("main CUT")).unwrap().id, "A");
        assert_eq!(project.timeline(Some("2")).unwrap().id, "B");
        assert_eq!(project.timeline(Some("1")).unwrap().id, "A");
        assert!(project.timeline(Some("3")).is_none());
        assert!(project.timeline(Some("0")).is_none());
        assert!(project.timeline(Some("Nope")).is_none());
    }

    #[test]
    fn malformed_project_json_means_one_timeline() {
        let dir = TempDir::new();
        let p = dir.path();
        std::fs::write(p.join("draft_info.json"), DRAFT).unwrap();
        std::fs::create_dir_all(p.join("Timelines")).unwrap();
        std::fs::write(p.join("Timelines/project.json"), "[1, 2]").unwrap();
        assert_eq!(CapCutProject::new(p).timelines().len(), 1);
    }

    #[test]
    fn locator_lists_resolves_and_finds_the_open_project() {
        let dir = TempDir::new();
        let root = dir.path();
        for name in ["One", "Two", ".hidden", "Empty"] {
            std::fs::create_dir_all(root.join(name)).unwrap();
        }
        std::fs::write(root.join("One/draft_info.json"), DRAFT).unwrap();
        std::fs::write(root.join("Two/draft_content.json"), DRAFT).unwrap();
        std::fs::write(root.join(".hidden/draft_info.json"), DRAFT).unwrap();
        std::fs::write(root.join("Two/.locked"), "").unwrap();
        let mut names: Vec<_> = ProjectLocator::all_projects(root).iter().map(|p| p.name()).collect();
        names.sort();
        assert_eq!(names, ["One", "Two"]);
        assert_eq!(ProjectLocator::open_project(root).unwrap().name(), "Two");
        assert_eq!(ProjectLocator::resolve("One", root).unwrap().name(), "One");
        let by_file = root.join("Two/draft_content.json");
        assert_eq!(ProjectLocator::resolve(&by_file.to_string_lossy(), root).unwrap().name(), "Two");
        assert!(ProjectLocator::resolve("Empty", root).is_none());
        assert!(ProjectLocator::all_projects(&root.join("missing")).is_empty());
    }

    #[test]
    fn unreadable_drafts_are_errors() {
        let _english = crate::testing::language("en");
        let dir = TempDir::new();
        let missing = DraftLoader::load(&dir.path().join("draft_info.json")).unwrap_err();
        assert!(missing.to_string().starts_with("Cannot read "), "{missing}");
        std::fs::write(dir.path().join("draft_info.json"), "{not json").unwrap();
        assert!(DraftLoader::load_project(dir.path()).is_err());
        std::fs::write(dir.path().join("draft_info.json"), "[]").unwrap();
        assert!(DraftLoader::load_project(dir.path()).is_err());
    }

    #[test]
    fn resolves_the_project_placeholder() {
        let project = Path::new("/p/My Project");
        let resolve = |s: &str| DraftLoader::resolve_path(s, project).map(|p| p.to_string_lossy().into_owned());
        assert_eq!(resolve("##_draftpath_placeholder_0E685133-18CE_##/matting/x").unwrap(), "/p/My Project/matting/x");
        assert_eq!(resolve("/abs/clip.mp4").unwrap(), "/abs/clip.mp4");
        assert_eq!(resolve("##_draftpath_placeholder__##").unwrap(), "/p/My Project");
        assert_eq!(resolve("##_draftpath_placeholder_x#y_##/a").unwrap(), "##_draftpath_placeholder_x#y_##/a");
        assert_eq!(resolve(""), None);
    }
}
