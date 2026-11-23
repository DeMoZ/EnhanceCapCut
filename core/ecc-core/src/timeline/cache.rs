//! Lookups in CapCut's effect cache: colour filters' lookup tables (the port of `ColorFilter.swift`) and voice
//! filters' presets (`VoiceFilter.locate` and the loadability check of `VoiceFilterGraph.load`). Only the user's own
//! cache is read, at build time; nothing from it is bundled.

use std::path::{Path, PathBuf};

use serde_json::Value;

use super::TimeRange;

/// Entries of a folder (hidden ones included), sorted by path; empty when it can't be listed.
fn sorted_entries(folder: &Path) -> Vec<PathBuf> {
    let mut entries: Vec<PathBuf> =
        std::fs::read_dir(folder).map(|it| it.flatten().map(|e| e.path()).collect()).unwrap_or_default();
    entries.sort_by(|a, b| a.to_string_lossy().cmp(&b.to_string_lossy()));
    entries
}

fn extension_lowercased(path: &Path) -> String {
    path.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default()
}

/// One of CapCut's "Filters" (`materials.effects`, `type` "filter"): colour lookup tables read from the filter's
/// package in CapCut's effect cache, applied in order at the filter's intensity.
#[derive(Debug, Clone, PartialEq)]
pub struct ColorFilter {
    pub name: String,
    /// The lookup tables, in the order the package applies them: `.cube` files or PNG lookup images.
    pub luts: Vec<PathBuf>,
    /// The "intensity" slider (`value`), 0...1.
    pub intensity: f64,
}

/// How a PNG lookup image holds its N×N×N table. In every layout red grows to the right and green downwards inside a
/// tile, and blue picks the tile.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LutLayout {
    /// √N × √N tiles of N×N pixels, blue tile by tile, row by row (512×512 for N = 64).
    Grid { size: usize, tiles_per_row: usize },
    /// N tiles of N×N side by side (N² × N).
    Row { size: usize },
    /// N tiles of N×N stacked (N × N²).
    Column { size: usize },
}

impl LutLayout {
    pub fn size(&self) -> usize {
        match *self {
            LutLayout::Grid { size, .. } | LutLayout::Row { size } | LutLayout::Column { size } => size,
        }
    }

    /// The pixel holding entry (r, g, b).
    pub fn pixel(&self, r: usize, g: usize, b: usize) -> (usize, usize) {
        match *self {
            LutLayout::Grid { size: n, tiles_per_row: k } => ((b % k) * n + r, (b / k) * n + g),
            LutLayout::Row { size: n } => (b * n + r, g),
            LutLayout::Column { size: n } => (r, b * n + g),
        }
    }
}

impl ColorFilter {
    /// The largest lookup table we read (entries per channel).
    pub const MAX_SIZE: usize = 64;

    /// The layout of a lookup image of this size, or `None` when it isn't one.
    pub fn layout(width: usize, height: usize) -> Option<LutLayout> {
        let valid = |n: usize| (2..=Self::MAX_SIZE).contains(&n);
        if width == height {
            // N = k², side = N·k = k³.
            return (2..=8)
                .find(|&k| k * k * k == width && valid(k * k))
                .map(|k| LutLayout::Grid { size: k * k, tiles_per_row: k });
        }
        if height.checked_mul(height) == Some(width) && valid(height) {
            return Some(LutLayout::Row { size: height });
        }
        if width.checked_mul(width) == Some(height) && valid(width) {
            return Some(LutLayout::Column { size: width });
        }
        None
    }

    /// The width and height of a PNG or JPEG file, from its header, whatever its extension says (some packages ship a
    /// JPEG named `filter.png`); `None` for anything else.
    pub fn image_size_of_file(path: &Path) -> Option<(usize, usize)> {
        use std::io::Read;
        let mut file = std::fs::File::open(path).ok()?;
        let mut data = Vec::new();
        file.by_ref().take(65536).read_to_end(&mut data).ok()?;
        Self::image_size(&data)
    }

    /// The width and height in the first bytes of a PNG or JPEG file.
    pub fn image_size(b: &[u8]) -> Option<(usize, usize)> {
        let be = |at: usize, n: usize| b[at..at + n].iter().fold(0usize, |acc, &x| acc << 8 | x as usize);
        if b.len() >= 24 && b[..8] == [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] {
            return Some((be(16, 4), be(20, 4)));
        }
        if b.len() < 4 || b[0] != 0xFF || b[1] != 0xD8 {
            return None;
        }
        // JPEG: walk the segments to the frame header (SOFn: C0–CF except C4 DHT, C8 JPG, CC DAC).
        let mut i = 2;
        while i + 9 < b.len() {
            if b[i] != 0xFF {
                return None;
            }
            let marker = b[i + 1];
            if marker == 0xFF {
                i += 1;
                continue;
            }
            if (0xC0..=0xCF).contains(&marker) && ![0xC4, 0xC8, 0xCC].contains(&marker) {
                return Some((be(i + 7, 2), be(i + 5, 2)));
            }
            if marker == 0xD9 || marker == 0xDA {
                return None;
            }
            i += 2 + be(i + 2, 2);
        }
        None
    }

    /// The `LUT_3D_SIZE` of a `.cube` file, or `None` when it has none or it's out of range.
    pub fn cube_size(path: &Path) -> Option<usize> {
        let text = std::fs::read_to_string(path).ok()?;
        for line in text.split(['\n', '\r', '\u{0B}', '\u{0C}', '\u{85}', '\u{2028}', '\u{2029}']) {
            let parts: Vec<&str> = line.split(char::is_whitespace).filter(|p| !p.is_empty()).collect();
            if parts.len() == 2 && parts[0] == "LUT_3D_SIZE" {
                if let Ok(n) = parts[1].parse::<i64>() {
                    if (2..=Self::MAX_SIZE as i64).contains(&n) {
                        return Some(n as usize);
                    }
                }
            }
        }
        None
    }

    /// Every `.cube` or lookup-shaped image in a folder (recursively), those whose name mentions "lut" first, then by
    /// path.
    pub fn lut_files_in(package: &Path) -> Vec<PathBuf> {
        let mut candidates: Vec<(u8, String, PathBuf)> = Vec::new();
        let base = package.to_string_lossy().trim_end_matches('/').to_string();
        let mut stack = vec![package.to_path_buf()];
        while let Some(dir) = stack.pop() {
            let Ok(entries) = std::fs::read_dir(&dir) else { continue };
            for entry in entries.flatten() {
                let path = entry.path();
                let Ok(kind) = entry.file_type() else { continue };
                if kind.is_dir() {
                    stack.push(path);
                    continue;
                }
                let usable = match extension_lowercased(&path).as_str() {
                    "cube" => Self::cube_size(&path).is_some(),
                    "png" | "jpg" | "jpeg" => {
                        Self::image_size_of_file(&path).and_then(|(w, h)| Self::layout(w, h)).is_some()
                    }
                    _ => continue,
                };
                if !usable {
                    continue;
                }
                let name = path.file_name().map(|n| n.to_string_lossy().to_lowercase()).unwrap_or_default();
                let rank = if name.contains("lut") { 0 } else { 1 };
                let full = path.to_string_lossy().to_string();
                let relative = full.strip_prefix(&base).unwrap_or(&full).to_string();
                candidates.push((rank, relative, path));
            }
        }
        candidates.sort_by(|a, b| (a.0, &a.1).cmp(&(b.0, &b.1)));
        candidates.into_iter().map(|c| c.2).collect()
    }

    /// The parts of a package (`config.json` → `effect.Link[]`), in the order they're applied (`zorder`).
    pub fn features(package: &Path) -> Vec<PathBuf> {
        let Some(json) =
            std::fs::read(package.join("config.json")).ok().and_then(|d| serde_json::from_slice::<Value>(&d).ok())
        else {
            return Vec::new();
        };
        // `as? [[String: Any]]`: every element must be an object.
        let Some(links) = json.get("effect").and_then(|e| e.get("Link")).and_then(Value::as_array) else {
            return Vec::new();
        };
        if !links.iter().all(Value::is_object) {
            return Vec::new();
        }
        let mut parts: Vec<(f64, usize, PathBuf)> = links
            .iter()
            .enumerate()
            .filter_map(|(i, link)| {
                let path = link.get("path")?.as_str().filter(|p| !p.is_empty())?;
                // An NSNumber: numbers and booleans.
                let zorder = match link.get("zorder") {
                    Some(Value::Number(n)) => n.as_f64().unwrap_or(0.0),
                    Some(Value::Bool(b)) => f64::from(u8::from(*b)),
                    _ => 0.0,
                };
                Some((zorder, i, package.join(path)))
            })
            .collect();
        parts.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap_or(std::cmp::Ordering::Equal).then(a.1.cmp(&b.1)));
        parts.into_iter().map(|p| p.2).collect()
    }

    /// The lookup table a package part uses: a table in it whose path without the extension (`image/filter`) one of its
    /// `material/*.material` files names. Parts often carry unused tables, so unreferenced ones don't count.
    pub fn referenced_lut_file(feature: &Path) -> Option<PathBuf> {
        let materials: Vec<Vec<u8>> = sorted_entries(&feature.join("material"))
            .into_iter()
            .filter(|p| p.extension().is_some_and(|e| e == "material"))
            .filter_map(|p| std::fs::read(p).ok())
            .collect();
        if materials.is_empty() {
            return None;
        }
        let base = feature.to_string_lossy().trim_end_matches('/').to_string();
        Self::lut_files_in(feature).into_iter().find(|url| {
            let without_extension = url.with_extension("");
            let full = without_extension.to_string_lossy();
            let relative = full.get(base.len() + 1..).unwrap_or("");
            materials.iter().any(|m| contains_bytes(m, relative.as_bytes()))
        })
    }

    /// A package's lookup tables in the order it applies them: the table each part's materials use; for a package
    /// without such references, its first table.
    pub fn lut_files(package: &Path) -> Vec<PathBuf> {
        let referenced: Vec<PathBuf> =
            Self::features(package).iter().filter_map(|f| Self::referenced_lut_file(f)).collect();
        if !referenced.is_empty() {
            return referenced;
        }
        Self::lut_files_in(package).into_iter().take(1).collect()
    }

    /// The lookup tables for a filter: in `path` (the cached package), otherwise in the first
    /// `<cache>/<resource_id>/<hash>` that has any. Empty when none is found.
    pub fn locate(path: Option<&Path>, resource_id: &str, cache_roots: &[PathBuf]) -> Vec<PathBuf> {
        if let Some(path) = path.filter(|p| p.is_dir()) {
            let luts = Self::lut_files(path);
            if !luts.is_empty() {
                return luts;
            }
        }
        if resource_id.is_empty() {
            return Vec::new();
        }
        for root in cache_roots {
            for entry in sorted_entries(&root.join(resource_id)) {
                let luts = Self::lut_files(&entry);
                if !luts.is_empty() {
                    return luts;
                }
            }
        }
        Vec::new()
    }
}

fn contains_bytes(haystack: &[u8], needle: &[u8]) -> bool {
    needle.is_empty() || haystack.windows(needle.len()).any(|w| w == needle)
}

/// A filter on a filter track: it colours everything painted below it (`render_index`) while it's active.
#[derive(Debug, Clone, PartialEq)]
pub struct FilterLayer {
    pub id: String,
    pub target: TimeRange,
    pub track_index: i64,
    pub render_index: i64,
    pub filter: ColorFilter,
}

impl FilterLayer {
    pub fn is_active(&self, t: i64) -> bool {
        t >= self.target.start && t < self.target.end()
    }

    /// True when the filter is painted after a layer at (`render_index`, `track_index`) and so colours it.
    pub fn covers(&self, render_index: i64, track_index: i64) -> bool {
        (self.render_index, self.track_index) > (render_index, track_index)
    }
}

// MARK: - Voice filters

/// One of CapCut's "Voice filters": its cached processor graph and the strength slider.
#[derive(Debug, Clone, PartialEq)]
pub struct VoiceFilter {
    pub name: String,
    /// The cached effect directory holding `mdsp_preset.json` (and any audio it loops).
    pub directory: PathBuf,
    /// 0...1.
    pub strength: f64,
}

impl VoiceFilter {
    /// The processors the native voice filter graphs implement (`VoiceFilterGraph.Kind`).
    pub const PROCESSORS: [&'static str; 10] = [
        "Filter",
        "Equaliser",
        "Distortion",
        "Compressor",
        "StudioReverb",
        "Gain",
        "GainAndPan",
        "Mixer",
        "Source",
        "ParameterChangeTransform",
    ];

    /// The cached directory for an effect: `path` (or a folder in it) when it holds a preset, otherwise the first
    /// `<cache>/<resource_id>/<hash>` that does.
    pub fn locate(path: Option<&Path>, resource_id: &str, cache_roots: &[PathBuf]) -> Option<PathBuf> {
        let has_preset = |dir: &Path| dir.join("mdsp_preset.json").exists();
        let search = |folder: &Path| -> Option<PathBuf> {
            if has_preset(folder) {
                return Some(folder.to_path_buf());
            }
            sorted_entries(folder).into_iter().find(|e| has_preset(e))
        };
        if let Some(dir) = path.and_then(search) {
            return Some(dir);
        }
        if resource_id.is_empty() {
            return None;
        }
        cache_roots.iter().find_map(|root| search(&root.join(resource_id)))
    }

    /// Whether the preset in `directory` loads in the native `VoiceFilterGraph`: readable, every node a processor we
    /// implement, with an audio input and an audio output.
    pub fn preset_is_usable(directory: &Path) -> bool {
        let Some(root) = std::fs::read(directory.join("mdsp_preset.json"))
            .ok()
            .and_then(|d| serde_json::from_slice::<Value>(&d).ok())
            .filter(Value::is_object)
        else {
            return false;
        };
        // `root[key] as? [[String: Any]] ?? []`: an array whose elements are all objects, otherwise empty.
        let objects = |key: &str| -> Vec<&Value> {
            match root.get(key).and_then(Value::as_array) {
                Some(items) if items.iter().all(Value::is_object) => items.iter().collect(),
                _ => Vec::new(),
            }
        };
        let mut nodes = std::collections::HashSet::new();
        for node in objects("nodes") {
            let Some(id) = node.get("node_id").and_then(Value::as_str) else { continue };
            let name = node.get("processor_name").and_then(Value::as_str).unwrap_or("");
            if !Self::PROCESSORS.contains(&name) {
                return false;
            }
            nodes.insert(id.to_string());
        }
        let (mut has_input, mut has_output) = (false, false);
        for port in objects("ports") {
            let Some(id) = port.get("internal_node_id").and_then(Value::as_str) else { continue };
            if !nodes.contains(id) {
                continue;
            }
            let is_audio = port.get("port_type").and_then(Value::as_str) != Some("parameter_change");
            if port.get("port_direction").and_then(Value::as_str) == Some("output") {
                has_output |= is_audio;
            } else if is_audio {
                has_input = true;
            }
        }
        has_input && has_output
    }
}
