//! Projects, their timelines, the folders the core uses, `ecc info` and the core's language.

use std::path::{Path, PathBuf};

use ecc::project as p;

use crate::convert::{path_string, Ffi};
use crate::{guarded, CoreError};

/// The folders the core reads and writes (design decision 1); `default_environment()` gives this OS's defaults.
#[derive(Debug, Clone, PartialEq, Eq, uniffi::Record)]
pub struct CoreEnvironment {
    /// CapCut's drafts folder: `…/User Data/Projects/com.lveditor.draft`.
    pub projects_root: String,
    /// Roots of CapCut's effect cache (`…/User Data/Cache/effect`), searched in order.
    pub capcut_cache_roots: Vec<String>,
    /// Where the draft writers keep backups of the drafts they change.
    pub backup_root: String,
}

impl From<CoreEnvironment> for ecc::CoreEnvironment {
    fn from(e: CoreEnvironment) -> Self {
        ecc::CoreEnvironment {
            projects_root: e.projects_root.into(),
            capcut_cache_roots: e.capcut_cache_roots.into_iter().map(PathBuf::from).collect(),
            backup_root: e.backup_root.into(),
        }
    }
}

impl Ffi for ecc::CoreEnvironment {
    type Out = CoreEnvironment;
    fn ffi(self) -> CoreEnvironment {
        CoreEnvironment {
            projects_root: self.projects_root.ffi(),
            capcut_cache_roots: self.capcut_cache_roots.ffi(),
            backup_root: self.backup_root.ffi(),
        }
    }
}

/// A CapCut project folder.
#[derive(Debug, Clone, PartialEq, Eq, Hash, uniffi::Record)]
pub struct CapCutProject {
    pub path: String,
    /// The folder's name.
    pub name: String,
    /// When the draft (or the folder) last changed, Unix ms.
    pub modified_ms: Option<i64>,
    /// CapCut's `.locked` file is there (it only counts while CapCut runs, see `is_open_in_capcut`).
    pub is_open: bool,
}

impl Ffi for p::CapCutProject {
    type Out = CapCutProject;
    fn ffi(self) -> CapCutProject {
        CapCutProject {
            name: self.name(),
            path: path_string(&self.path),
            modified_ms: self.modified_ms,
            is_open: self.is_open,
        }
    }
}

/// One timeline of a project.
#[derive(Debug, Clone, PartialEq, Eq, Hash, uniffi::Record)]
pub struct ProjectTimeline {
    /// The timeline's UUID; empty for projects without `Timelines/project.json`.
    pub id: String,
    pub name: String,
    pub draft_path: String,
    pub is_main: bool,
}

impl Ffi for p::ProjectTimeline {
    type Out = ProjectTimeline;
    fn ffi(self) -> ProjectTimeline {
        ProjectTimeline {
            id: self.id,
            name: self.name,
            draft_path: path_string(&self.draft_path),
            is_main: self.is_main,
        }
    }
}

impl From<ProjectTimeline> for p::ProjectTimeline {
    fn from(t: ProjectTimeline) -> Self {
        p::ProjectTimeline { id: t.id, name: t.name, draft_path: t.draft_path.into(), is_main: t.is_main }
    }
}

/// This OS's default folders.
#[uniffi::export]
pub fn default_environment() -> CoreEnvironment {
    ecc::CoreEnvironment::default().ffi()
}

/// The project in `path` (read fresh: modification time, lock file).
#[uniffi::export]
pub fn project_at(path: String) -> CapCutProject {
    p::CapCutProject::new(path).ffi()
}

/// All projects under `root` that have a draft, most recently modified first.
#[uniffi::export]
pub fn all_projects(root: String) -> Vec<CapCutProject> {
    p::ProjectLocator::all_projects(Path::new(&root)).ffi()
}

/// The project CapCut has open, if any.
#[uniffi::export]
pub fn open_project(root: String) -> Option<CapCutProject> {
    p::ProjectLocator::open_project(Path::new(&root)).ffi()
}

/// A project by path (`~` allowed, a draft file's path too) or by folder name under `root`.
#[uniffi::export]
pub fn resolve_project(name_or_path: String, root: String) -> Option<CapCutProject> {
    p::ProjectLocator::resolve(&name_or_path, Path::new(&root)).ffi()
}

/// The project's timelines: the list in `Timelines/project.json`, or its root draft alone.
#[uniffi::export]
pub fn project_timelines(project_path: String) -> Vec<ProjectTimeline> {
    p::CapCutProject::new(project_path).timelines().ffi()
}

/// A timeline by name (any case), id or 1-based number, as `ecc --timeline` picks it; the main one for `None`.
#[uniffi::export]
pub fn project_timeline(project_path: String, name_or_number: Option<String>) -> Option<ProjectTimeline> {
    p::CapCutProject::new(project_path).timeline(name_or_number.as_deref()).ffi()
}

/// A path from a draft with the project-folder placeholder replaced by `project_path`; `None` for an empty path.
#[uniffi::export]
pub fn resolve_draft_path(path: String, project_path: String) -> Option<String> {
    p::DraftLoader::resolve_path(&path, Path::new(&project_path)).ffi()
}

/// The whole `ecc info` text, for the CLI's arguments: a project path or name (the one open in CapCut when `None`)
/// and a timeline name, id or number (the main one when `None`). Both CLIs print it as is.
#[uniffi::export]
pub fn format_info(
    project: Option<String>,
    timeline: Option<String>,
    env: CoreEnvironment,
) -> Result<String, CoreError> {
    guarded(|| Ok(ecc::info::info(project.as_deref(), timeline.as_deref(), &env.into())?))
}

/// The language of the core's texts (`ru`, `en`…), set by the apps next to their own interface language; until
/// then English.
#[uniffi::export]
pub fn set_core_language(code: String) {
    ecc::set_core_language(&code)
}
