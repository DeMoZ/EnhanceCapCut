//! Synthetic fixture projects (`core/fixtures/<name>/`), materialized into real project folders.
//!
//! A fixture holds `draft.fixture.json` (the draft; `.gitignore` blocks the real draft names), optionally
//! `timelines.fixture.json` (`Timelines/project.json`; every timeline it lists gets a copy of the draft) and
//! `media.txt` (files to create, one path per line relative to the project; their content doesn't matter unless the
//! line says `path = content`, where `PNG <w> <h>` writes a PNG header of that size and `PRESET <processor>` a voice
//! filter preset with one node of that processor; any other content is written as text). Paths in
//! the draft use the placeholder `##_draftpath_placeholder_FIXTURE_##` for the project folder. Expected outputs live
//! in `expected/`.

use std::path::{Path, PathBuf};

use ecc::project::{CapCutProject, ProjectLocator};
use ecc::{CoreEnvironment, TimelineBuilder};

/// The draft file name a project is materialized with: macOS's, or Windows's in the second pass.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DraftName {
    Info,
    Content,
}

impl DraftName {
    pub fn file_name(self) -> &'static str {
        match self {
            DraftName::Info => "draft_info.json",
            DraftName::Content => "draft_content.json",
        }
    }
}

/// The fixtures under `root`, by name.
pub fn list(root: &Path) -> Vec<PathBuf> {
    let mut all: Vec<PathBuf> = std::fs::read_dir(root)
        .map(|it| it.flatten().map(|e| e.path()).filter(|p| p.join("draft.fixture.json").is_file()).collect())
        .unwrap_or_default();
    all.sort();
    all
}

/// Writes the fixture in `fixture` as a project folder `out/<fixture name>` and returns its path.
pub fn materialize(fixture: &Path, out: &Path, name: DraftName) -> std::io::Result<PathBuf> {
    let project = out.join(fixture.file_name().unwrap_or_default());
    if project.exists() {
        std::fs::remove_dir_all(&project)?;
    }
    std::fs::create_dir_all(&project)?;
    let draft = std::fs::read(fixture.join("draft.fixture.json"))?;
    std::fs::write(project.join(name.file_name()), &draft)?;
    if let Ok(list) = std::fs::read(fixture.join("timelines.fixture.json")) {
        let timelines = project.join("Timelines");
        std::fs::create_dir_all(&timelines)?;
        std::fs::write(timelines.join("project.json"), &list)?;
        let parsed: serde_json::Value = serde_json::from_slice(&list).unwrap_or_default();
        for entry in parsed.get("timelines").and_then(|t| t.as_array()).into_iter().flatten() {
            if let Some(id) = entry.get("id").and_then(|i| i.as_str()) {
                std::fs::create_dir_all(timelines.join(id))?;
                std::fs::write(timelines.join(id).join(name.file_name()), &draft)?;
            }
        }
    }
    if let Ok(media) = std::fs::read_to_string(fixture.join("media.txt")) {
        for line in media.lines().map(str::trim).filter(|l| !l.is_empty() && !l.starts_with('#')) {
            let (path, content) = line.split_once(" = ").unwrap_or((line, "x"));
            let file = project.join(path);
            std::fs::create_dir_all(file.parent().unwrap_or(&project))?;
            std::fs::write(file, media_content(content))?;
        }
    }
    Ok(project)
}

fn media_content(content: &str) -> Vec<u8> {
    let words: Vec<&str> = content.split_whitespace().collect();
    match words.as_slice() {
        ["PNG", w, h] => {
            let mut bytes = vec![0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
            bytes.extend(w.parse::<u32>().unwrap_or(0).to_be_bytes());
            bytes.extend(h.parse::<u32>().unwrap_or(0).to_be_bytes());
            bytes
        }
        ["PRESET", processor] => format!(
            r#"{{"nodes":[{{"node_id":"p","processor_name":"{processor}","processor_state":{{"parameters":{{"Gain dB":-6.0}}}}}}],"connections":[],"ports":[{{"internal_node_id":"p","port_direction":"input","port_type":"audio"}},{{"internal_node_id":"p","port_direction":"output","port_type":"audio"}}]}}"#
        )
        .into_bytes(),
        _ => content.as_bytes().to_vec(),
    }
}

/// The environment the fixtures are built in: effect caches inside the project, so nothing on the machine counts.
pub fn environment(project: &Path) -> CoreEnvironment {
    CoreEnvironment {
        projects_root: project.parent().unwrap_or(project).to_path_buf(),
        capcut_cache_roots: vec![project.join("cache")],
        backup_root: project.join("backups"),
    }
}

/// The three outputs of a materialized fixture: `info.txt`, `timeline.json` (every timeline, one after another) and
/// `writers.txt` (the main timeline).
pub fn outputs(project: &Path) -> Result<[(&'static str, String); 3], String> {
    let env = environment(project);
    let found = ProjectLocator::resolve(&project.to_string_lossy(), &env.projects_root)
        .ok_or_else(|| format!("not a project: {}", project.display()))?;
    let mut info = String::new();
    let mut dumps = String::new();
    for timeline in found.timelines() {
        let built = TimelineBuilder::build_project(&found, Some(&timeline), &env).map_err(|e| e.to_string())?;
        info += &ecc::info::format_info(&found, &timeline, &built);
        dumps += &crate::dump::dump_timeline(&built, &found.path);
    }
    let main = found.timeline(None).ok_or("no timeline")?;
    let writers = crate::dump::dump_writers(&CapCutProject::new(&found.path), &main)?;
    Ok([("info.txt", info), ("timeline.json", dumps), ("writers.txt", writers)])
}
