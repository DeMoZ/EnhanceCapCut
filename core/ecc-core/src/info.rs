//! The `ecc info` report, byte for byte as `Sources/ecc/main.swift` prints it (design decision 4: both CLIs print
//! this text, so their outputs are identical by construction).

use std::path::Path;

use unicode_segmentation::UnicodeSegmentation;

use crate::draft::json::swift_double;
use crate::env::CoreEnvironment;
use crate::project::{CapCutProject, LoadError, ProjectLocator, ProjectTimeline};
use crate::timeline::{Timeline, TimelineBuilder, VisualContent, VoiceSeparation};

/// `String(format: "%.2fs", Double(us) / 1_000_000)`.
pub fn seconds(us: i64) -> String {
    format!("{:.2}s", us as f64 / 1_000_000.0)
}

fn last_path_component(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| path.display().to_string())
}

/// The report for a built `timeline` of `project`; `selected` is the timeline it was built from.
pub fn format_info(project: &CapCutProject, selected: &ProjectTimeline, timeline: &Timeline) -> String {
    let mut out = String::new();
    let timelines = project.timelines();
    if timelines.len() > 1 {
        let names: Vec<String> = timelines
            .iter()
            .map(|t| {
                format!("{}{}{}", if t == selected { "*" } else { "" }, t.name, if t.is_main { " (main)" } else { "" })
            })
            .collect();
        out += &format!("Timelines: {}\n", names.join(", "));
    }
    out += &format!(
        "Project: {}  canvas {}x{}  {} fps  duration {}\n",
        project.name(),
        timeline.canvas_width as i64,
        timeline.canvas_height as i64,
        swift_double(timeline.fps),
        seconds(timeline.duration)
    );
    out += &format!("Visual layers: {}, audio clips: {}\n", timeline.layers.len(), timeline.audio_clips.len());
    for layer in &timeline.layers {
        let kind = match &layer.content {
            VisualContent::Video(path) => format!("video {}", last_path_component(path)),
            VisualContent::Photo(path) => format!("photo {}", last_path_component(path)),
            VisualContent::Text(spec) => {
                let prefix: String = spec.content.text.graphemes(true).take(30).collect();
                format!("text \"{}\"", prefix.replace('\n', " "))
            }
            VisualContent::Compound(inner) => format!("compound ({} layers)", inner.layers.len()),
            VisualContent::Sticker(spec) => format!("sticker {}", spec.name),
        };
        let speed = if layer.time_map.is_constant() {
            format!("x{:.2}", layer.time_map.average_speed())
        } else {
            "curve".to_string()
        };
        out += &format!(
            "  [{}/t{}] {}+{} {} {}\n",
            layer.render_index,
            layer.track_index,
            seconds(layer.target.start),
            seconds(layer.target.duration),
            speed,
            kind
        );
    }
    for clip in &timeline.audio_clips {
        let mut fx = String::new();
        match clip.voice.separation {
            VoiceSeparation::KeepVoice => fx += " [keep voice]",
            VoiceSeparation::RemoveVoice => fx += " [remove voice]",
            VoiceSeparation::None => {}
        }
        if let Some(d) = clip.voice.denoise {
            fx += &format!(" [denoise {d:.2}]");
        }
        if let Some(e) = clip.voice.enhance {
            fx += &format!(" [enhance {:.0}]", e * 100.0);
        }
        if let Some(f) = &clip.voice.filter {
            let strength = if f.strength < 1.0 { format!(" {:.0}%", f.strength * 100.0) } else { String::new() };
            fx += &format!(" [{}{}]", f.name, strength);
        }
        out += &format!(
            "  audio {}+{} vol {:.2} {}{}\n",
            seconds(clip.target.start),
            seconds(clip.target.duration),
            clip.volume,
            last_path_component(&clip.path),
            fx
        );
    }
    if !timeline.report.unsupported.is_empty() {
        out += "Not supported yet:\n";
        for item in &timeline.report.unsupported {
            out += &format!("  {}: {} ×{}\n", item.category, item.name, item.count);
        }
    }
    if !timeline.report.missing_files.is_empty() {
        out += "Missing media:\n";
        for file in &timeline.report.missing_files {
            out += &format!("  {file}\n");
        }
    }
    out
}

/// Why `info` couldn't produce a report. The texts stay English, like all CLI output.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InfoError {
    ProjectNotFound(String),
    NoOpenProject,
    NoTimeline { arg: String, project: String, names: Vec<String> },
    Load(LoadError),
}

impl std::fmt::Display for InfoError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            InfoError::ProjectNotFound(arg) => write!(f, "Project not found: {arg}"),
            InfoError::NoOpenProject => write!(f, "No project is open in CapCut; pass a name"),
            InfoError::NoTimeline { arg, project, names } => {
                write!(f, "No timeline {arg} in {project}: {}", names.join(", "))
            }
            InfoError::Load(e) => e.fmt(f),
        }
    }
}

impl std::error::Error for InfoError {}

/// Resolves the CLI arguments like `ecc info [project] [--timeline name|number]`: the project by path or name (the
/// one open in CapCut when `None`), the timeline by name, id or 1-based number (the main one when `None`).
pub fn resolve(
    project_arg: Option<&str>,
    timeline_arg: Option<&str>,
    env: &CoreEnvironment,
) -> Result<(CapCutProject, ProjectTimeline), InfoError> {
    let project = match project_arg {
        Some(arg) => {
            ProjectLocator::resolve(arg, &env.projects_root).ok_or_else(|| InfoError::ProjectNotFound(arg.into()))?
        }
        None => ProjectLocator::open_project(&env.projects_root).ok_or(InfoError::NoOpenProject)?,
    };
    let timeline = project.timeline(timeline_arg).ok_or_else(|| InfoError::NoTimeline {
        arg: timeline_arg.unwrap_or_default().into(),
        project: project.name(),
        names: project.timelines().into_iter().map(|t| t.name).collect(),
    })?;
    Ok((project, timeline))
}

/// The whole `ecc info` text for a project argument and an optional timeline argument.
pub fn info(project_arg: Option<&str>, timeline_arg: Option<&str>, env: &CoreEnvironment) -> Result<String, InfoError> {
    let (project, selected) = resolve(project_arg, timeline_arg, env)?;
    let timeline = TimelineBuilder::build_project(&project, Some(&selected), env).map_err(InfoError::Load)?;
    Ok(format_info(&project, &selected, &timeline))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::{language, TempDir};

    /// A project with every kind of line `ecc info` prints. `{root}` is the project folder.
    const DRAFT: &str = r###"{"name": "Info", "duration": 9000000, "fps": 29.97, "canvas_config": {"width": 1080.6, "height": 1920},
 "tracks": [
  {"type": "video", "segments": [
    {"id": "V", "material_id": "MV", "render_index": 0, "extra_material_refs": ["SP"], "target_timerange": {"start": 0, "duration": 3000000}},
    {"id": "P", "material_id": "MP", "render_index": 0, "speed": 0.5, "target_timerange": {"start": 3000000, "duration": 2000000}},
    {"id": "C", "material_id": "MC", "extra_material_refs": ["COMBO"], "target_timerange": {"start": 5000000, "duration": 1000000}},
    {"id": "G", "material_id": "GONE", "target_timerange": {"start": 6000000, "duration": 1000000}}]},
  {"type": "text", "segments": [{"id": "X", "material_id": "TXT", "render_index": 11000, "target_timerange": {"start": 0, "duration": 2000000}}]},
  {"type": "sticker", "segments": [{"id": "S", "material_id": "CLOCK", "render_index": 14000, "target_timerange": {"start": 0, "duration": 1000000}},
                                   {"id": "S2", "material_id": "MEME", "target_timerange": {"start": 0, "duration": 1000000}}]},
  {"type": "audio", "segments": [
    {"material_id": "A", "volume": 0.7, "extra_material_refs": ["KEEP", "DN", "EN", "FX"], "target_timerange": {"start": 0, "duration": 4000000}},
    {"material_id": "A", "extra_material_refs": ["REMOVE"], "target_timerange": {"start": 4000000, "duration": 1000000}},
    {"material_id": "MISSING", "target_timerange": {"start": 0, "duration": 1000000}}]}],
 "materials": {
  "videos": [{"id": "MV", "type": "video", "path": "{root}/media/clip.mov", "has_audio": false},
             {"id": "MP", "type": "photo", "path": "##_draftpath_placeholder_ABC_##/media/photo.png"},
             {"id": "MC", "type": "video", "path": ""},
             {"id": "GONE", "type": "video", "path": "/nowhere/gone.mov", "material_name": "gone.mov"}],
  "speeds": [{"id": "SP", "mode": 1, "speed": 1.5, "curve_speed": {"speed_points": [{"x": 0, "y": 1}, {"x": 1, "y": 2}]}}],
  "texts": [{"id": "TXT", "content": "{\"text\":\"Первая строка\\nвторая строка, длинная 👋🏽 очень\"}"}],
  "stickers": [{"id": "CLOCK", "name": "Clock Time EN Blue White Minimalist", "resource_id": "7528280675909209405"},
               {"id": "MEME", "name": "Pet Meme"}],
  "drafts": [{"id": "COMBO", "draft": {"tracks": [{"type": "video", "segments": [{"id": "IN", "material_id": "IP", "target_timerange": {"start": 0, "duration": 1000000}}]}],
                                       "materials": {"videos": [{"id": "IP", "type": "photo", "path": "{root}/media/photo.png"}]}}}],
  "audios": [{"id": "A", "path": "{root}/media/voice.m4a"}, {"id": "MISSING", "path": "", "name": "lost.mp3"}],
  "vocal_separations": [{"id": "KEEP", "choice": 2}, {"id": "REMOVE", "choice": 1}],
  "realtime_denoises": [{"id": "DN", "is_denoise": true, "denoise_rate": 0.855}],
  "vocal_beautifys": [{"id": "EN", "enable": true, "ambient_sound_level": 62.5}],
  "audio_effects": [{"id": "FX", "name": "Booming", "path": "{root}/cache/555/h", "audio_adjust_params": [{"name": "strength", "value": 0.375}]}],
  "transitions": [{"id": "UNUSED", "name": "Pull In"}]
 }}
"###;

    /// What the Swift `ecc info --timeline 2` printed for this project (Swift 6.4, before the switch).
    const EXPECTED: &str = r#"Timelines: Main (main), *Short cut
Project: Info Project  canvas 1080x1920  29.97 fps  duration 9.00s
Visual layers: 5, audio clips: 2
  [0/t0] 0.00s+3.00s curve video clip.mov
  [0/t0] 3.00s+2.00s x0.50 photo photo.png
  [0/t0] 5.00s+1.00s x1.00 compound (1 layers)
  [11000/t1] 0.00s+2.00s x1.00 text "Первая строка вторая строка, д"
  [14000/t2] 0.00s+1.00s x1.00 sticker Clock Time EN Blue White Minimalist
  audio 0.00s+4.00s vol 0.70 voice.m4a [keep voice] [denoise 0.85] [enhance 62] [Booming 38%]
  audio 4.00s+1.00s vol 1.00 voice.m4a [remove voice]
Not supported yet:
  Sticker: Pet Meme ×1
Missing media:
  /nowhere/gone.mov
  lost.mp3
"#;

    #[test]
    fn formats_like_the_swift_cli() {
        let _english = language("en");
        let dir = TempDir::new();
        let root = dir.path().join("Info Project");
        for folder in ["media", "Timelines/T1", "Timelines/T2", "cache/555/h"] {
            std::fs::create_dir_all(root.join(folder)).unwrap();
        }
        for file in ["media/clip.mov", "media/photo.png", "media/voice.m4a"] {
            std::fs::write(root.join(file), "x").unwrap();
        }
        std::fs::write(
            root.join("cache/555/h/mdsp_preset.json"),
            r#"{"nodes": [{"node_id": "p", "processor_name": "Gain"}], "ports": [{"internal_node_id": "p", "port_direction": "input"}, {"internal_node_id": "p", "port_direction": "output"}]}"#,
        )
        .unwrap();
        // JSON strings need forward slashes (Windows paths would need escaping).
        let draft = DRAFT.replace("{root}", &root.to_string_lossy().replace('\\', "/"));
        std::fs::write(root.join("draft_info.json"), &draft).unwrap();
        std::fs::write(root.join("Timelines/T2/draft_info.json"), &draft).unwrap();
        std::fs::write(
            root.join("Timelines/project.json"),
            r#"{"main_timeline_id": "T1", "timelines": [{"id": "T1", "name": "Main"}, {"id": "T2", "name": "Short cut"}]}"#,
        )
        .unwrap();
        let env = CoreEnvironment { projects_root: dir.path().to_path_buf(), ..CoreEnvironment::default() };
        assert_eq!(info(Some("Info Project"), Some("2"), &env).unwrap(), EXPECTED);
        // The main timeline, picked by name in another case.
        let main = info(Some(&root.to_string_lossy()), Some("main"), &env).unwrap();
        assert!(main.starts_with("Timelines: *Main (main), Short cut\n"), "{main}");
    }

    #[test]
    fn errors_name_what_is_missing() {
        let dir = TempDir::new();
        let env = CoreEnvironment { projects_root: dir.path().to_path_buf(), ..CoreEnvironment::default() };
        assert_eq!(info(Some("Nope"), None, &env).unwrap_err().to_string(), "Project not found: Nope");
        assert_eq!(info(None, None, &env).unwrap_err(), InfoError::NoOpenProject);
        std::fs::create_dir_all(dir.path().join("P")).unwrap();
        std::fs::write(dir.path().join("P/draft_info.json"), "{}").unwrap();
        assert_eq!(info(Some("P"), Some("3"), &env).unwrap_err().to_string(), "No timeline 3 in P: Timeline 01");
        assert_eq!(seconds(1_234_567), "1.23s");
        assert_eq!(seconds(-5_000), "-0.01s");
    }
}
