//! Adds voice-over to a CapCut draft, the port of `DraftVoiceOverWriter.swift`: one audio track per language with a
//! clip per spoken line (`materials.audios` of type `record`, like CapCut's own voiceovers; see docs/FORMAT.md), and
//! optionally ducks the other audio with `KFTypeVolume` keyframes while the voice speaks or turns on CapCut's "Remove
//! vocal" on it.
//!
//! Like the caption writer, the draft is edited in place: only the arrays we add to, the `common_keyframes` and
//! `extra_material_refs` of the segments we change and the vocal separations we turn on are rewritten.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::ops::Range;
use std::path::Path;
use std::sync::OnceLock;

use serde_json::{json, Map, Value};

use super::{
    bridged_bool, bridged_int, ns_double, ns_int64, object_array, parse, string_array, us, IdSource, WriteError,
};
use crate::draft::json::{json_text, JsonEdits, JsonScanner, ScanError};
use crate::tr;

/// One language of generated voice-over, to be added to a draft as an audio track.
#[derive(Debug, Clone, PartialEq)]
pub struct VoiceOverTrack {
    /// Our language code.
    pub language: String,
    /// The spoken track as one file aligned to the timeline (file time 0 = timeline start).
    pub path: String,
    /// Length of the file, seconds.
    pub file_duration: f64,
    /// Where each line was placed, seconds (start, end). Every line becomes its own clip on the track.
    pub lines: Vec<(f64, f64)>,
    /// Muted tracks can be unmuted in CapCut with the track's speaker button.
    pub is_audible: bool,
    /// The project's sound without its voice, played while the original is muted under the voice-over
    /// (`OriginalAudio::Replace`); `lines` are the stretches it covers.
    pub is_background: bool,
}

/// What happens to the project's own sound while the voice-over speaks.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OriginalAudio {
    Keep,
    Lower,
    Mute,
    /// Muted like `Mute`, with the project's sound minus its voice (a background track) in its place.
    Replace,
    /// CapCut's own "Remove vocal" on the clips the voice-over speaks over.
    RemoveVocal,
}

impl OriginalAudio {
    pub const ALL: [OriginalAudio; 5] = [
        OriginalAudio::Keep,
        OriginalAudio::Lower,
        OriginalAudio::Mute,
        OriginalAudio::Replace,
        OriginalAudio::RemoveVocal,
    ];

    /// The English name, also the raw value the apps store.
    pub fn raw_value(&self) -> &'static str {
        match self {
            OriginalAudio::Keep => "Keep",
            OriginalAudio::Lower => "Lower",
            OriginalAudio::Mute => "Mute all",
            OriginalAudio::Replace => "Replace voice",
            OriginalAudio::RemoveVocal => "Remove vocal",
        }
    }

    pub fn from_raw_value(raw: &str) -> Option<OriginalAudio> {
        Self::ALL.into_iter().find(|o| o.raw_value() == raw)
    }

    /// The name in the interface language.
    pub fn title(&self) -> String {
        match self {
            OriginalAudio::Keep => tr!("Keep"),
            OriginalAudio::Lower => tr!("Lower"),
            OriginalAudio::Mute => tr!("Mute all"),
            OriginalAudio::Replace => tr!("Replace voice"),
            OriginalAudio::RemoveVocal => tr!("Remove vocal"),
        }
    }

    /// In the names of dubbed timelines: `keep`, `lower`, `muteall`, `replacevoice`, `removevocal`.
    pub fn name_token(&self) -> String {
        self.raw_value().to_lowercase().replace(' ', "")
    }

    /// Gain applied while the voice speaks; `None` = untouched.
    fn duck_gain(&self) -> Option<f64> {
        match self {
            OriginalAudio::Keep | OriginalAudio::RemoveVocal => None,
            OriginalAudio::Lower => Some(LOWER_GAIN),
            OriginalAudio::Mute | OriginalAudio::Replace => Some(0.0),
        }
    }
}

/// Name prefix of the tracks we create; a later run replaces tracks with this prefix.
pub const TRACK_NAME_PREFIX: &str = "EnhanceCapCut voice-over";
/// First block of the ids of the volume keyframe groups and vocal separations we add, so a later run can find and
/// undo them.
pub const ID_PREFIX: &str = "ECC0D0CC-";
/// "Lower": −15 dB while the voice speaks, as in SubDub.
pub const LOWER_GAIN: f64 = 0.178;
/// Ducking ramps: down before a line starts, back up after it ends (µs).
pub const FADE_DOWN: i64 = 250_000;
pub const FADE_UP: i64 = 400_000;
/// CapCut's "Remove vocal" refuses sources longer than this (µs).
pub const MAX_VOCAL_REMOVAL_SOURCE: i64 = 600_000_000;

/// Returns the draft with `tracks` appended as audio tracks. With `replace_previous`, tracks, ducking and vocal
/// removal from an earlier run are removed first. Media paths inside `project` are written with the draft's own
/// project-folder placeholder when it has one, so they move with the project.
pub fn apply(
    tracks: &[VoiceOverTrack],
    draft: &[u8],
    original: OriginalAudio,
    replace_previous: bool,
    project: Option<&Path>,
    ids: &mut dyn IdSource,
) -> Result<Vec<u8>, WriteError> {
    let mut data = draft.to_vec();
    if replace_previous {
        data = restore_original_audio(&data)?;
    }
    data = add_tracks(tracks, &data, replace_previous, project, ids)?;
    let Some(spoken) = tracks.iter().find(|t| t.is_audible && !t.is_background) else { return Ok(data) };
    let speech = ducked_ranges(&spoken.lines);
    if let Some(gain) = original.duck_gain() {
        data = duck(&data, &speech, gain, None, ids)?;
    } else if original == OriginalAudio::RemoveVocal {
        let (removed, unsupported) = remove_vocal(&data, &speech, ids)?;
        data = duck(&removed, &speech, 0.0, Some(&unsupported), ids)?;
    }
    Ok(data)
}

/// The stretches the original is ducked over: the voice's lines, merged when the ramps between them would meet.
pub fn ducked_ranges(lines: &[(f64, f64)]) -> Vec<Range<i64>> {
    merge(lines.iter().map(|l| us(l.0)..us(l.1)).collect(), FADE_DOWN + FADE_UP)
}

/// The background's gain at `t` (µs) for one ducked range: the complement of the original's ducking, so the two
/// cross-fade — up during the ramp down before the range, down during the ramp back up after it.
pub fn background_gain(t: i64, r: &Range<i64>) -> f64 {
    if t >= r.start && t < r.end {
        return 1.0;
    }
    if t >= r.start - FADE_DOWN && t < r.start {
        return (t - (r.start - FADE_DOWN)) as f64 / FADE_DOWN as f64;
    }
    if t >= r.end && t < r.end + FADE_UP {
        return 1.0 - (t - r.end) as f64 / FADE_UP as f64;
    }
    0.0
}

/// The draft without the voice-over, ducking and vocal removal of earlier runs: what speech recognition should hear.
pub fn without_voice_over(draft: &[u8], ids: &mut dyn IdSource) -> Result<Vec<u8>, WriteError> {
    let restored = restore_original_audio(draft)?;
    add_tracks(&[], &restored, true, None, ids)
}

fn text(scanner: &JsonScanner, r: &Range<usize>) -> String {
    String::from_utf8_lossy(&scanner.bytes[r.clone()]).into_owned()
}

fn object(scanner: &JsonScanner, r: &Range<usize>) -> Value {
    scanner.object(r.clone()).filter(Value::is_object).unwrap_or_else(|| json!({}))
}

fn id_of(v: &Value) -> &str {
    v.get("id").and_then(Value::as_str).unwrap_or("")
}

// MARK: Tracks

fn add_tracks(
    tracks: &[VoiceOverTrack],
    draft: &[u8],
    replace_previous: bool,
    project: Option<&Path>,
    ids: &mut dyn IdSource,
) -> Result<Vec<u8>, WriteError> {
    let scanner = JsonScanner::new(draft);
    let root = scanner.root()?;
    let tracks_range = scanner.value("tracks", root.clone())?.ok_or(WriteError::NoTracks)?;
    let materials_range = scanner.value("materials", root.clone())?;
    let duration = scanner
        .value("duration", root.clone())?
        .and_then(|r| scanner.object(r))
        .and_then(|v| bridged_int(Some(&v)))
        .unwrap_or(i64::MAX);
    let placeholder = project.and_then(|_| placeholder_prefix(scanner.bytes));

    let mut track_elements = scanner.elements(tracks_range.clone())?;
    let mut removed: HashSet<String> = HashSet::new();
    if replace_previous {
        track_elements.retain(|range| {
            let track = object(&scanner, range);
            if !track.get("name").and_then(Value::as_str).is_some_and(|n| n.starts_with(TRACK_NAME_PREFIX)) {
                return true;
            }
            for segment in object_array(track.get("segments")).into_iter().flatten() {
                if let Some(id) = segment.get("material_id").and_then(Value::as_str) {
                    removed.insert(id.to_string());
                }
                removed.extend(string_array(segment.get("extra_material_refs")).unwrap_or_default());
            }
            false
        });
    }

    let mut new_tracks: Vec<String> = Vec::new();
    let mut added: BTreeMap<String, Vec<String>> = BTreeMap::new();
    for (n, track) in tracks.iter().enumerate() {
        let track_index = (track_elements.len() + n) as i64;
        let attribute = if track.is_audible { 0 } else { 1 };
        let mut path = track.path.clone();
        if let (Some(placeholder), Some(project)) = (&placeholder, project) {
            let folder = project.to_string_lossy();
            if path.starts_with(&format!("{folder}/")) {
                path = format!("{placeholder}{}", &path[folder.len()..]);
            }
        }
        let file_duration = us(track.file_duration);
        let mut segments: Vec<Value> = Vec::new();
        for (i, line) in track.lines.iter().enumerate() {
            let start = us(line.0);
            let end = us(line.1).min(duration).min(file_duration);
            if end <= start {
                continue;
            }
            let material_id = ids.next_id();
            let mut audio = default_audio().as_object().unwrap().clone();
            audio.insert("id".into(), json!(material_id));
            let name = if track.is_background {
                format!("Background {}", i + 1)
            } else {
                format!("Voice-over {} {}", track.language, i + 1)
            };
            audio.insert("name".into(), json!(name));
            audio.insert("path".into(), json!(path));
            audio.insert("duration".into(), json!(file_duration));
            added.entry("audios".into()).or_default().push(json_text(&Value::Object(audio)));

            let mut refs: Vec<String> = Vec::new();
            for (key, template) in extra_materials() {
                let mut m = template.as_object().unwrap().clone();
                let id = ids.next_id();
                m.insert("id".into(), json!(id));
                refs.push(id);
                added.entry((*key).into()).or_default().push(json_text(&Value::Object(m)));
            }
            let mut s = default_segment().as_object().unwrap().clone();
            s.insert("id".into(), json!(ids.next_id()));
            s.insert("material_id".into(), json!(material_id));
            s.insert("extra_material_refs".into(), json!(refs));
            s.insert("source_timerange".into(), json!({"start": start, "duration": end - start}));
            s.insert("target_timerange".into(), json!({"start": start, "duration": end - start}));
            s.insert("track_render_index".into(), json!(track_index));
            s.insert("track_attribute".into(), json!(attribute));
            segments.push(Value::Object(s));
        }
        let suffix = if track.is_background { "background".to_string() } else { track.language.clone() };
        new_tracks.push(json_text(&json!({
            "id": ids.next_id(), "type": "audio", "flag": 0, "attribute": attribute,
            "name": format!("{TRACK_NAME_PREFIX} · {suffix}"), "is_default_name": false,
            "segments": segments,
        })));
    }

    let array = |kept: &[Range<usize>], new: &[String]| -> String {
        let mut items: Vec<String> = kept.iter().map(|r| text(&scanner, r)).collect();
        items.extend(new.iter().cloned());
        format!("[{}]", items.join(","))
    };
    let mut edits = JsonEdits::new();
    edits.replace(tracks_range, &array(&track_elements, &new_tracks));
    if let Some(materials_range) = materials_range {
        let mut missing = added.clone();
        for (key, range) in scanner.members(materials_range.clone())? {
            if scanner.bytes[range.start] != b'[' {
                continue;
            }
            missing.remove(&key);
            let mut elements = scanner.elements(range.clone())?;
            let before = elements.len();
            if !removed.is_empty() {
                elements.retain(|e| !removed.contains(id_of(&object(&scanner, e))));
            }
            let new = added.get(&key).cloned().unwrap_or_default();
            if elements.len() != before || !new.is_empty() {
                edits.replace(range, &array(&elements, &new));
            }
        }
        if !missing.is_empty() {
            let close = materials_range.end - 1;
            let empty = scanner.members(materials_range)?.is_empty();
            let members: Vec<String> = missing.iter().map(|(k, v)| format!("\"{k}\":{}", array(&[], v))).collect();
            edits.replace(close..close, &format!("{}{}", if empty { "" } else { "," }, members.join(",")));
        }
    } else {
        let close = root.end - 1;
        let members: Vec<String> = added.iter().map(|(k, v)| format!("\"{k}\":{}", array(&[], v))).collect();
        edits.replace(close..close, &format!(",\"materials\":{{{}}}", members.join(",")));
    }
    Ok(edits.applied(scanner.bytes))
}

/// `##_draftpath_placeholder_<UUID>_##`, the project folder in CapCut's own media paths: the first one in the draft.
pub fn placeholder_prefix(bytes: &[u8]) -> Option<String> {
    let marker = b"##_draftpath_placeholder_";
    if bytes.len() <= marker.len() {
        return None;
    }
    let start = (0..bytes.len() - marker.len()).find(|&i| &bytes[i..i + marker.len()] == marker)?;
    let mut i = start + marker.len();
    while i + 2 < bytes.len() && i - start < 80 {
        if bytes[i] == b'_' && bytes[i + 1] == b'#' && bytes[i + 2] == b'#' {
            return Some(String::from_utf8_lossy(&bytes[start..i + 3]).into_owned());
        }
        i += 1;
    }
    None
}

// MARK: Ducking

/// Volume keyframes on every audible segment that overlaps `speech` (only those with ids in `only`, when given): down
/// to `gain` × its volume while the voice speaks, back up in between. Segments that already have volume keyframes of
/// their own are left alone.
fn duck(
    draft: &[u8],
    speech: &[Range<i64>],
    gain: f64,
    only: Option<&HashSet<String>>,
    ids: &mut dyn IdSource,
) -> Result<Vec<u8>, WriteError> {
    if speech.is_empty() || only.is_some_and(HashSet::is_empty) {
        return Ok(draft.to_vec());
    }
    let mut edits = JsonEdits::new();
    let scanner = JsonScanner::new(draft);
    for_each_segment(&scanner, |track, segment, members| {
        if !is_original_sound(track) {
            return Ok(());
        }
        let parsed = object(&scanner, &segment);
        if let Some(only) = only {
            if !only.contains(id_of(&parsed)) {
                return Ok(());
            }
        }
        let has_volume = object_array(parsed.get("common_keyframes"))
            .is_some_and(|g| g.iter().any(|g| g.get("property_type").and_then(Value::as_str) == Some("KFTypeVolume")));
        if has_volume {
            return Ok(());
        }
        let Some(range) = parsed.get("target_timerange").filter(|r| r.is_object()) else { return Ok(()) };
        let (Some(start), Some(length)) = (ns_int64(range.get("start")), ns_int64(range.get("duration"))) else {
            return Ok(());
        };
        if length <= 0 {
            return Ok(());
        }
        let volume = ns_double(parsed.get("volume")).unwrap_or(1.0);
        if volume <= 0.0 || volume.is_nan() {
            return Ok(());
        }

        // Timeline points (ducked or not), then clamped into the segment; later points win on a tie.
        let mut points: Vec<(i64, bool)> = Vec::new();
        for r in speech {
            if r.end + FADE_UP > start && r.start - FADE_DOWN < start + length {
                points.extend([(r.start - FADE_DOWN, false), (r.start, true), (r.end, true), (r.end + FADE_UP, false)]);
            }
        }
        if points.is_empty() {
            return Ok(());
        }
        let mut keyframes: Vec<(i64, bool)> = Vec::new();
        for (t, ducked) in points {
            let local = (t - start).max(0).min(length);
            match keyframes.last_mut() {
                Some(last) if last.0 == local => last.1 = ducked,
                _ => keyframes.push((local, ducked)),
            }
        }
        let list: Vec<Value> = keyframes
            .iter()
            .map(|(t, ducked)| {
                json!({
                    "id": ids.next_id(), "curveType": "Line", "time_offset": t,
                    "left_control": {"x": 0.0, "y": 0.0}, "right_control": {"x": 0.0, "y": 0.0},
                    "values": [volume * if *ducked { gain } else { 1.0 }], "string_value": "", "graphID": "",
                })
            })
            .collect();
        let group = json_text(&json!({
            "id": own_id(ids), "material_id": "", "property_type": "KFTypeVolume", "keyframe_list": list,
        }));
        let existing = members.iter().find(|(k, _)| k == "common_keyframes").map(|(_, v)| v.clone());
        match existing.filter(|r| scanner.bytes[r.start] == b'[') {
            Some(existing) => {
                let mut kept: Vec<String> =
                    scanner.elements(existing.clone())?.iter().map(|r| text(&scanner, r)).collect();
                kept.push(group);
                edits.replace(existing, &format!("[{}]", kept.join(",")));
            }
            None => {
                let close = segment.end - 1;
                let sep = if members.is_empty() { "" } else { "," };
                edits.replace(close..close, &format!("{sep}\"common_keyframes\":[{group}]"));
            }
        }
        Ok(())
    })?;
    Ok(edits.applied(scanner.bytes))
}

/// An audible video or audio track of the project itself, not one of ours.
fn is_original_sound(track: &Map<String, Value>) -> bool {
    let kind = track.get("type").and_then(Value::as_str);
    if kind != Some("video") && kind != Some("audio") {
        return false;
    }
    bridged_int(track.get("attribute")).unwrap_or(0) & 1 == 0
        && !track.get("name").and_then(Value::as_str).is_some_and(|n| n.starts_with(TRACK_NAME_PREFIX))
}

fn own_id(ids: &mut dyn IdSource) -> String {
    let id = ids.next_id();
    format!("{ID_PREFIX}{}", id.get(ID_PREFIX.len()..).unwrap_or(""))
}

// MARK: Vocal removal

/// CapCut's "Remove vocal" (`vocal_separations` choice 1) on every audible segment that overlaps `speech`, whole:
/// CapCut separates a clip at a time. Its result is CapCut's to make (we can't write its `production_path`). Returns
/// the ids of the segments CapCut can't do it on (compound clips, its own music, reversed clips, sources over 10
/// minutes, and clips set to "Keep vocal"), to be muted under the voice instead.
fn remove_vocal(
    draft: &[u8],
    speech: &[Range<i64>],
    ids: &mut dyn IdSource,
) -> Result<(Vec<u8>, HashSet<String>), WriteError> {
    let scanner = JsonScanner::new(draft);
    let root = scanner.root()?;
    let mut separations: HashMap<String, (Range<usize>, Value)> = HashMap::new();
    let mut separations_array: Option<Range<usize>> = None;
    let mut combos: HashSet<String> = HashSet::new();
    let mut media: HashMap<String, Value> = HashMap::new();
    let materials = scanner.value("materials", root.clone())?;
    let members = match &materials {
        Some(m) => scanner.members(m.clone())?,
        None => Vec::new(),
    };
    for (key, range) in members {
        if !["vocal_separations", "drafts", "videos", "audios"].contains(&key.as_str())
            || scanner.bytes[range.start] != b'['
        {
            continue;
        }
        if key == "vocal_separations" {
            separations_array = Some(range.clone());
        }
        for element in scanner.elements(range)? {
            let Some(m) = scanner.object(element.clone()).filter(Value::is_object) else { continue };
            let Some(id) = m.get("id").and_then(Value::as_str).map(str::to_string) else { continue };
            match key.as_str() {
                "vocal_separations" => {
                    separations.insert(id, (element, m));
                }
                "drafts" => {
                    combos.insert(id);
                }
                _ => {
                    media.insert(id, m);
                }
            }
        }
    }

    let mut edits = JsonEdits::new();
    let mut turned_on: HashSet<String> = HashSet::new();
    let mut added: Vec<String> = Vec::new();
    let mut unsupported: HashSet<String> = HashSet::new();
    for_each_segment(&scanner, |track, segment, members| {
        if !is_original_sound(track) {
            return Ok(());
        }
        let parsed = object(&scanner, &segment);
        let Some(range) = parsed.get("target_timerange").filter(|r| r.is_object()) else { return Ok(()) };
        let (Some(start), Some(length)) = (ns_int64(range.get("start")), ns_int64(range.get("duration"))) else {
            return Ok(());
        };
        if length <= 0 || ns_double(parsed.get("volume")).unwrap_or(1.0) <= 0.0 {
            return Ok(());
        }
        if !speech.iter().any(|r| r.end > start && r.start < start + length) {
            return Ok(());
        }
        let refs = string_array(parsed.get("extra_material_refs")).unwrap_or_default();
        let separation_id = refs.iter().find(|r| separations.contains_key(*r)).cloned();
        let choice = separation_id.as_ref().and_then(|id| ns_int64(separations[id].1.get("choice"))).unwrap_or(0);
        if choice == 1 {
            return Ok(());
        }
        let is_compound = refs.iter().any(|r| combos.contains(r));
        let empty = json!({});
        let material = media.get(parsed.get("material_id").and_then(Value::as_str).unwrap_or("")).unwrap_or(&empty);
        let kind = material.get("type").and_then(Value::as_str);
        let supported = choice == 0
            && bridged_bool(parsed.get("reverse")) != Some(true)
            && if track.get("type").and_then(Value::as_str) == Some("video") {
                !is_compound && kind == Some("video")
            } else {
                is_compound || kind != Some("music")
            }
            && ns_int64(material.get("duration")).unwrap_or(0) <= MAX_VOCAL_REMOVAL_SOURCE;
        if !supported {
            if let Some(id) = parsed.get("id").and_then(Value::as_str) {
                unsupported.insert(id.to_string());
            }
            return Ok(());
        }

        // One of ours (from an earlier run) is turned on again; anything else is replaced by one of ours, so a later
        // run can find and undo it. The one replaced stays in the materials, unchanged.
        let mut m = separation_id
            .as_ref()
            .map(|id| separations[id].1.clone())
            .unwrap_or_else(|| extra_materials().iter().find(|e| e.0 == "vocal_separations").unwrap().1.clone());
        let map = m.as_object_mut().expect("object");
        map.insert("choice".into(), json!(1));
        map.insert("time_range".into(), parsed.get("source_timerange").cloned().unwrap_or(Value::Null));
        map.insert("final_algorithm".into(), json!("vocal_separate"));
        if let Some(old) = separation_id.as_ref().filter(|id| id.starts_with(ID_PREFIX)) {
            if !turned_on.contains(old) {
                edits.replace(separations[old].0.clone(), &json_text(&m));
            }
            turned_on.insert(old.clone());
            return Ok(());
        }
        let id = own_id(ids);
        map.insert("id".into(), json!(id));
        added.push(json_text(&m));
        let new_refs: Vec<String> = match &separation_id {
            Some(old) => refs.iter().map(|r| if r == old { id.clone() } else { r.clone() }).collect(),
            None => refs.iter().cloned().chain(std::iter::once(id.clone())).collect(),
        };
        match members.iter().find(|(k, _)| k == "extra_material_refs") {
            Some((_, existing)) => edits.replace(existing.clone(), &json_text(&json!(new_refs))),
            None => {
                let close = segment.end - 1;
                let sep = if members.is_empty() { "" } else { "," };
                edits.replace(close..close, &format!("{sep}\"extra_material_refs\":{}", json_text(&json!(new_refs))));
            }
        }
        Ok(())
    })?;

    if !added.is_empty() {
        if let Some(array) = separations_array {
            let mut kept: Vec<String> = scanner.elements(array.clone())?.iter().map(|r| text(&scanner, r)).collect();
            kept.extend(added);
            edits.replace(array, &format!("[{}]", kept.join(",")));
        } else if let Some(materials) = materials {
            let close = materials.end - 1;
            let empty = scanner.members(materials)?.is_empty();
            edits.replace(
                close..close,
                &format!("{}\"vocal_separations\":[{}]", if empty { "" } else { "," }, added.join(",")),
            );
        } else {
            let close = root.end - 1;
            edits.replace(close..close, &format!(",\"materials\":{{\"vocal_separations\":[{}]}}", added.join(",")));
        }
    }
    Ok((edits.applied(scanner.bytes), unsupported))
}

// MARK: Undo

/// Drops the volume keyframe groups an earlier run added and turns its vocal removal back off.
fn restore_original_audio(draft: &[u8]) -> Result<Vec<u8>, WriteError> {
    let mut edits = JsonEdits::new();
    let mut changed = false;
    let scanner = JsonScanner::new(draft);
    if let Some(materials) = scanner.value("materials", scanner.root()?)? {
        if let Some(separations) =
            scanner.value("vocal_separations", materials)?.filter(|r| scanner.bytes[r.start] == b'[')
        {
            for element in scanner.elements(separations)? {
                let Some(mut m) = scanner.object(element.clone()).filter(Value::is_object) else { continue };
                if !id_of(&m).starts_with(ID_PREFIX) || ns_int64(m.get("choice")) == Some(0) {
                    continue;
                }
                m.as_object_mut().unwrap().insert("choice".into(), json!(0));
                changed = true;
                edits.replace(element, &json_text(&m));
            }
        }
    }
    for_each_segment(&scanner, |_, _, members| {
        let Some((_, range)) = members.iter().find(|(k, _)| k == "common_keyframes") else { return Ok(()) };
        if scanner.bytes[range.start] != b'[' {
            return Ok(());
        }
        let groups = scanner.elements(range.clone())?;
        let kept: Vec<&Range<usize>> =
            groups.iter().filter(|g| !id_of(&object(&scanner, g)).starts_with(ID_PREFIX)).collect();
        if kept.len() == groups.len() {
            return Ok(());
        }
        changed = true;
        let items: Vec<String> = kept.iter().map(|r| text(&scanner, r)).collect();
        edits.replace(range.clone(), &format!("[{}]", items.join(",")));
        Ok(())
    })?;
    Ok(if changed { edits.applied(scanner.bytes) } else { draft.to_vec() })
}

type Members = Vec<(String, Range<usize>)>;

/// Calls `body` with each top-level track (parsed, without its segments), segment range and the segment's members.
fn for_each_segment(
    scanner: &JsonScanner,
    mut body: impl FnMut(&Map<String, Value>, Range<usize>, &Members) -> Result<(), ScanError>,
) -> Result<(), ScanError> {
    let root = scanner.root()?;
    let Some(tracks_range) = scanner.value("tracks", root)? else { return Ok(()) };
    for track_range in scanner.elements(tracks_range)? {
        let members = scanner.members(track_range)?;
        let mut track = Map::new();
        for (key, value) in &members {
            if key != "segments" {
                track.insert(key.clone(), scanner.object(value.clone()).unwrap_or(Value::Null));
            }
        }
        let Some((_, segments)) = members.iter().find(|(k, _)| k == "segments") else { continue };
        if scanner.bytes[segments.start] != b'[' {
            continue;
        }
        for segment in scanner.elements(segments.clone())? {
            let segment_members = scanner.members(segment.clone())?;
            body(&track, segment, &segment_members)?;
        }
    }
    Ok(())
}

pub fn merge(ranges: Vec<Range<i64>>, gap: i64) -> Vec<Range<i64>> {
    let mut sorted = ranges;
    sorted.sort_by_key(|r| r.start);
    let mut out: Vec<Range<i64>> = Vec::new();
    for r in sorted {
        match out.last_mut() {
            Some(last) if r.start - last.end < gap => last.end = last.end.max(r.end),
            _ => out.push(r),
        }
    }
    out
}

// MARK: Defaults
//
// Field sets of a voiceover clip as CapCut 9.5 for macOS writes it (see docs/FORMAT.md). Floating-point fields are
// written with a decimal point on purpose.

fn default_audio() -> &'static Value {
    static V: OnceLock<Value> = OnceLock::new();
    V.get_or_init(|| {
        parse(
            r#"{"id":"","unique_id":"","type":"record","name":"","duration":0,"path":"","category_name":"","wave_points":[],
        "music_id":"","app_id":0,"text_id":"","tone_type":"","source_platform":0,"video_id":"","effect_id":"",
        "resource_id":"","third_resource_id":"","category_id":"","intensifies_path":"","formula_id":"","check_flag":1,
        "team_id":"","local_material_id":"","tone_speaker":"","mock_tone_speaker":"","tone_effect_id":"",
        "tone_effect_name":"","tone_platform":"","cloned_model_type":"","tone_category_id":"","tone_category_name":"",
        "tone_second_category_id":"","tone_second_category_name":"","tone_emotion_name_key":"","tone_emotion_style":"",
        "tone_emotion_role":"","tone_emotion_selection":"","tone_emotion_scale":0.0,"moyin_emotion":"","request_id":"",
        "query":"","search_id":"","sound_separate_type":"","is_text_edit_overdub":false,"is_ugc":false,
        "is_ai_clone_tone":false,"is_ai_clone_tone_post":false,"source_from":"","copyright_limit_type":"none",
        "aigc_history_id":"","aigc_item_id":"","music_source":"","pgc_id":"","pgc_name":"",
        "similiar_music_info":{"original_song_id":"","original_song_name":""},"ai_music_type":0,"ai_music_enter_from":"",
        "lyric_type":0,"tts_task_id":"","tts_generate_scene":"","ai_music_generate_scene":0,
        "tts_benefit_info":{"benefit_type":"none","benefit_log_id":"","benefit_log_extra":"","benefit_amount":-1}}"#,
        )
    })
}

/// Per-clip materials CapCut gives a voiceover clip, by `materials` key.
fn extra_materials() -> &'static [(&'static str, Value)] {
    static V: OnceLock<Vec<(&'static str, Value)>> = OnceLock::new();
    V.get_or_init(|| {
        vec![
            ("speeds", parse(r#"{"id":"","type":"speed","mode":0,"speed":1.0,"curve_speed":null}"#)),
            (
                "placeholder_infos",
                parse(
                    r#"{"id":"","type":"placeholder_info","meta_type":"none","res_path":"","res_text":"","error_path":"","error_text":""}"#,
                ),
            ),
            (
                "beats",
                parse(
                    r#"{"id":"","type":"beats","enable_ai_beats":false,"gear":404,"gear_count":0,"mode":404,"user_beats":[],
            "user_delete_ai_beats":null,"ai_beats":{"melody_url":"","melody_path":"","beats_url":"","beats_path":"",
            "melody_percents":[0.0],"beat_speed_infos":[]}}"#,
                ),
            ),
            (
                "sound_channel_mappings",
                parse(r#"{"id":"","type":"","audio_channel_mapping":0,"is_config_open":false}"#),
            ),
            (
                "vocal_separations",
                parse(
                    r#"{"id":"","type":"vocal_separation","choice":0,"removed_sounds":[],"time_range":null,"production_path":"",
            "final_algorithm":"","enter_from":""}"#,
                ),
            ),
        ]
    })
}

fn default_segment() -> &'static Value {
    static V: OnceLock<Value> = OnceLock::new();
    V.get_or_init(|| {
        parse(
            r#"{"id":"","source_timerange":{"start":0,"duration":0},"target_timerange":{"start":0,"duration":0},
        "render_timerange":{"start":0,"duration":0},"desc":"","state":0,"speed":1.0,"is_loop":false,
        "is_tone_modify":false,"reverse":false,"intensifies_audio":false,"cartoon":false,"volume":1.0,
        "last_nonzero_volume":1.0,"clip":null,"uniform_scale":null,"material_id":"","extra_material_refs":[],
        "render_index":0,"keyframe_refs":[],"enable_lut":false,"enable_adjust":false,"enable_hsl":false,"visible":true,
        "group_id":"","enable_color_curves":true,"enable_hsl_curves":true,"track_render_index":0,"hdr_settings":null,
        "enable_color_wheels":true,"track_attribute":0,"is_placeholder":false,"template_id":"",
        "enable_smart_color_adjust":false,"template_scene":"default","common_keyframes":[],"caption_info":null,
        "responsive_layout":{"enable":false,"target_follow":"","size_layout":0,"horizontal_pos_layout":0,
        "vertical_pos_layout":0},"enable_color_match_adjust":false,"enable_color_correct_adjust":false,
        "enable_adjust_mask":false,"raw_segment_id":"","lyric_keyframes":null,"enable_video_mask":true,
        "digital_human_template_group_id":"","color_correct_alg_result":"","source":"segmentsourcenormal",
        "enable_mask_stroke":false,"enable_mask_shadow":false,"enable_color_adjust_pro":false}"#,
        )
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::writers::CountingIds;

    const PROJECT: &str = "/Projects/Demo";
    const PLACEHOLDER: &str = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##";

    /// A video clip over the whole 10 s, a music clip from 6 s, and one with volume keyframes of its own.
    fn draft() -> String {
        format!(
            r#"{{"id":"D","fps":30.0,"duration":10000000,"tracks":[{{"id":"V","type":"video","flag":0,"attribute":0,"segments":[{{"id":"S1","material_id":"M1","target_timerange":{{"start":0,"duration":10000000}},"volume":0.5,"common_keyframes":[]}}]}},{{"id":"A","type":"audio","flag":0,"attribute":0,"segments":[{{"id":"S2","material_id":"M2","target_timerange":{{"start":6000000,"duration":4000000}},"volume":1.0}}]}},{{"id":"B","type":"audio","flag":0,"attribute":0,"segments":[{{"id":"S3","material_id":"M3","target_timerange":{{"start":0,"duration":4000000}},"volume":1.0,"common_keyframes":[{{"id":"K","property_type":"KFTypeVolume","keyframe_list":[{{"time_offset":0,"values":[1.0]}}]}}]}}]}}],"materials":{{"videos":[{{"id":"M1","path":"{PLACEHOLDER}/clip.mp4"}}],"audios":[],"speeds":[]}}}}"#
        )
    }

    fn tracks() -> Vec<VoiceOverTrack> {
        vec![
            VoiceOverTrack {
                language: "en".into(),
                path: format!("{PROJECT}/EnhanceCapCut/voiceover 1/en.wav"),
                file_duration: 10.0,
                lines: vec![(1.0, 2.5), (3.0, 4.0)],
                is_audible: true,
                is_background: false,
            },
            VoiceOverTrack {
                language: "th".into(),
                path: "/elsewhere/th.wav".into(),
                file_duration: 10.0,
                lines: vec![(1.0, 2.0)],
                is_audible: false,
                is_background: false,
            },
        ]
    }

    fn run(tracks: &[VoiceOverTrack], draft: &[u8], original: OriginalAudio) -> Vec<u8> {
        apply(tracks, draft, original, true, Some(Path::new(PROJECT)), &mut CountingIds::default()).unwrap()
    }

    fn parsed(bytes: &[u8]) -> Value {
        serde_json::from_slice(bytes).unwrap()
    }

    fn segments(d: &Value, track: usize) -> Vec<Value> {
        d["tracks"][track]["segments"].as_array().unwrap().clone()
    }

    fn volume_keyframes(segment: &Value) -> Vec<Value> {
        segment["common_keyframes"]
            .as_array()
            .map(|g| g.iter().filter(|g| g["property_type"] == "KFTypeVolume").cloned().collect())
            .unwrap_or_default()
    }

    fn values(group: &Value) -> Vec<f64> {
        group["keyframe_list"].as_array().unwrap().iter().map(|k| k["values"][0].as_f64().unwrap()).collect()
    }

    /// Replace voice: the original is muted like Mute and a background track (always audible) fills in.
    #[test]
    fn replace_adds_an_audible_background_and_mutes_the_original() {
        let mut all = tracks();
        all.push(VoiceOverTrack {
            language: "en".into(),
            path: format!("{PROJECT}/EnhanceCapCut/voiceover 1/background.wav"),
            file_duration: 10.0,
            lines: vec![(0.75, 4.4)],
            is_audible: true,
            is_background: true,
        });
        let out = run(&all, draft().as_bytes(), OriginalAudio::Replace);
        let d = parsed(&out);
        let tracks = d["tracks"].as_array().unwrap();
        assert_eq!(tracks.len(), 6);
        assert_eq!(tracks[5]["name"], "EnhanceCapCut voice-over · background");
        assert_eq!(tracks[5]["attribute"], 0);
        // Ducked to silence under the voice's lines, not the background's.
        assert_eq!(values(&volume_keyframes(&segments(&d, 0)[0])[0]), [0.5, 0.0, 0.0, 0.5]);
        // A later run takes the background away too.
        let cleaned = parsed(&without_voice_over(&out, &mut CountingIds::default()).unwrap());
        assert_eq!(cleaned["tracks"].as_array().unwrap().len(), 3);
    }

    #[test]
    fn background_gain_cross_fades_with_the_ducking() {
        let r = 1_000_000..2_000_000;
        assert_eq!(background_gain(500_000, &r), 0.0);
        assert!((background_gain(875_000, &r) - 0.5).abs() < 1e-9);
        assert_eq!(background_gain(1_500_000, &r), 1.0);
        assert!((background_gain(2_200_000, &r) - 0.5).abs() < 1e-9);
        assert_eq!(background_gain(2_400_000, &r), 0.0);
    }

    #[test]
    fn adds_audio_tracks_with_a_clip_per_line() {
        let out = run(&tracks(), draft().as_bytes(), OriginalAudio::Keep);
        let d = parsed(&out);
        let all = d["tracks"].as_array().unwrap();
        assert_eq!(all.len(), 5);
        assert_eq!(all[3]["type"], "audio");
        assert_eq!(all[3]["attribute"], 0);
        assert_eq!(all[4]["attribute"], 1); // muted
        assert_eq!(all[3]["name"], "EnhanceCapCut voice-over · en");
        let lines = segments(&d, 3);
        assert_eq!(lines.len(), 2);
        assert_eq!(lines[0]["target_timerange"], json!({"start": 1_000_000, "duration": 1_500_000}));
        assert_eq!(lines[0]["source_timerange"], json!({"start": 1_000_000, "duration": 1_500_000}));
        assert_eq!(lines[0]["track_render_index"], 3);
        assert_eq!(lines[0]["extra_material_refs"].as_array().unwrap().len(), 5);
        let audios = d["materials"]["audios"].as_array().unwrap();
        assert_eq!(audios.len(), 3);
        let first = audios.iter().find(|a| a["id"] == lines[0]["material_id"]).unwrap();
        assert_eq!(first["type"], "record");
        assert_eq!(first["duration"], 10_000_000);
        // Inside the project: written with the draft's placeholder; elsewhere: absolute.
        assert_eq!(first["path"], format!("{PLACEHOLDER}/EnhanceCapCut/voiceover 1/en.wav"));
        assert!(audios.iter().any(|a| a["path"] == "/elsewhere/th.wav"));
        assert_eq!(d["materials"]["speeds"].as_array().unwrap().len(), 3);
        assert_eq!(d["materials"]["vocal_separations"].as_array().unwrap().len(), 3);
        // Keep: nothing else changes.
        assert!(volume_keyframes(&segments(&d, 0)[0]).is_empty());
        let text = String::from_utf8(out).unwrap();
        assert!(text.starts_with(r#"{"id":"D","fps":30.0,"duration":10000000,"tracks":[{"id":"V","type":"video""#));
    }

    #[test]
    fn ducks_other_audio_while_the_voice_speaks() {
        let d = parsed(&run(&tracks(), draft().as_bytes(), OriginalAudio::Lower));
        // The two lines (1–2.5 s, 3–4 s) are less than the ramps apart, so they duck as one: 1–4 s.
        let video = volume_keyframes(&segments(&d, 0)[0])[0].clone();
        assert!(video["id"].as_str().unwrap().starts_with(ID_PREFIX));
        let offsets: Vec<i64> =
            video["keyframe_list"].as_array().unwrap().iter().map(|k| k["time_offset"].as_i64().unwrap()).collect();
        assert_eq!(offsets, [750_000, 1_000_000, 4_000_000, 4_400_000]);
        let v = values(&video);
        assert_eq!(v[0], 0.5);
        assert!((v[1] - 0.5 * LOWER_GAIN).abs() < 1e-9);
        assert_eq!(v[3], 0.5);
        // Music from 6 s: no speech there. The clip with its own volume keyframes is left alone.
        assert!(volume_keyframes(&segments(&d, 1)[0]).is_empty());
        let own: Vec<Value> = volume_keyframes(&segments(&d, 2)[0]).iter().map(|g| g["id"].clone()).collect();
        assert_eq!(own, [json!("K")]);
        // Voice-over tracks aren't ducked.
        assert!(volume_keyframes(&segments(&d, 3)[0]).is_empty());
    }

    #[test]
    fn replaces_previous_run() {
        let once = run(&tracks(), draft().as_bytes(), OriginalAudio::Mute);
        let twice = run(&tracks(), &once, OriginalAudio::Mute);
        let d = parsed(&twice);
        assert_eq!(d["tracks"].as_array().unwrap().len(), 5);
        assert_eq!(d["materials"]["audios"].as_array().unwrap().len(), 3);
        assert_eq!(d["materials"]["beats"].as_array().unwrap().len(), 3);
        let video = volume_keyframes(&segments(&d, 0)[0]);
        assert_eq!(video.len(), 1);
        assert_eq!(values(&video[0])[1], 0.0);
        // Keep the original sound this time: the earlier ducking goes too.
        let kept = run(&tracks(), &once, OriginalAudio::Keep);
        assert!(volume_keyframes(&segments(&parsed(&kept), 0)[0]).is_empty());
    }

    /// A video clip with its own (off) vocal separation, a compound clip, CapCut music and an extracted sound, all
    /// under the voice, and a clip after it.
    const VOCAL_DRAFT: &str = r#"{"id":"D","duration":10000000,"tracks":[{"type":"video","attribute":0,"segments":[{"id":"S1","material_id":"M1","extra_material_refs":["VS1"],"target_timerange":{"start":0,"duration":5000000},"source_timerange":{"start":2000000,"duration":5000000},"volume":1.0},{"id":"S2","material_id":"M1","extra_material_refs":[],"target_timerange":{"start":6000000,"duration":4000000},"volume":1.0}]},{"type":"video","attribute":0,"segments":[{"id":"S3","material_id":"M1","extra_material_refs":["C1"],"target_timerange":{"start":0,"duration":5000000},"volume":1.0}]},{"type":"audio","attribute":0,"segments":[{"id":"S4","material_id":"A1","target_timerange":{"start":0,"duration":5000000},"volume":1.0},{"id":"S5","material_id":"A2","target_timerange":{"start":5000000,"duration":1000000},"volume":1.0}]}],"materials":{"videos":[{"id":"M1","type":"video","duration":20000000}],"audios":[{"id":"A1","type":"music","duration":5000000},{"id":"A2","type":"extract_music","duration":1000000}],"drafts":[{"id":"C1"}],"vocal_separations":[{"id":"VS1","type":"vocal_separation","choice":0}]}}"#;

    fn separation(d: &Value, segment: &Value) -> Option<Value> {
        let refs: Vec<&str> = segment["extra_material_refs"].as_array()?.iter().filter_map(Value::as_str).collect();
        d["materials"]["vocal_separations"]
            .as_array()?
            .iter()
            .find(|s| refs.contains(&s["id"].as_str().unwrap_or("")))
            .cloned()
    }

    /// Remove vocal: CapCut's "Remove vocal" on the clips under the voice, muting where CapCut can't separate.
    #[test]
    fn remove_vocal_turns_on_capcuts_separation() {
        let line = vec![VoiceOverTrack { lines: vec![(1.0, 5.5)], ..tracks()[0].clone() }];
        let once = run(&line, VOCAL_DRAFT.as_bytes(), OriginalAudio::RemoveVocal);
        let d = parsed(&once);
        let (s1, s2) = (segments(&d, 0)[0].clone(), segments(&d, 0)[1].clone());
        let own = separation(&d, &s1).unwrap();
        assert!(own["id"].as_str().unwrap().starts_with(ID_PREFIX));
        assert_eq!(own["choice"], 1);
        assert_eq!(own["final_algorithm"], "vocal_separate");
        assert_eq!(own["time_range"]["start"], 2_000_000);
        assert!(volume_keyframes(&s1).is_empty());
        // The clip's own entry stays, unchanged, for whatever else refers to it.
        let all = d["materials"]["vocal_separations"].as_array().unwrap();
        assert_eq!(all.iter().find(|s| s["id"] == "VS1").unwrap()["choice"], 0);
        // No speech over the clip from 6 s.
        assert!(separation(&d, &s2).is_none());
        // Compound clips and CapCut music can't be separated: muted instead.
        for track in [1, 2] {
            let segment = segments(&d, track)[0].clone();
            assert!(separation(&d, &segment).is_none());
            assert_eq!(volume_keyframes(&segment).len(), 1);
        }
        // A segment without separation material gets one.
        let s5 = segments(&d, 2)[1].clone();
        assert_eq!(separation(&d, &s5).unwrap()["choice"], 1);
        assert!(volume_keyframes(&s5).is_empty());
        // Running again reuses our entries; keeping the original sound turns them off.
        let twice = run(&line, &once, OriginalAudio::RemoveVocal);
        let d = parsed(&twice);
        let ours = |d: &Value| -> Vec<Value> {
            d["materials"]["vocal_separations"]
                .as_array()
                .unwrap()
                .iter()
                .filter(|s| s["id"].as_str().unwrap().starts_with(ID_PREFIX))
                .map(|s| s["choice"].clone())
                .collect()
        };
        assert_eq!(ours(&d), [json!(1), json!(1)]);
        assert_eq!(separation(&d, &segments(&d, 0)[0]).unwrap()["id"], own["id"]);
        let kept = parsed(&run(&line, &twice, OriginalAudio::Keep));
        assert_eq!(ours(&kept), [json!(0), json!(0)]);
        assert!(volume_keyframes(&segments(&kept, 1)[0]).is_empty());
        let restored = parsed(&without_voice_over(&twice, &mut CountingIds::default()).unwrap());
        assert_eq!(ours(&restored), [json!(0), json!(0)]);
    }

    #[test]
    fn ducking_reaches_the_timeline() {
        let dir = crate::testing::TempDir::new();
        let media = dir.path().join("a.m4a");
        std::fs::write(&media, [0u8]).unwrap();
        let source = format!(
            r#"{{"duration":10000000,"tracks":[{{"type":"audio","segments":[{{"id":"S","material_id":"A","target_timerange":{{"start":0,"duration":10000000}},"volume":0.5}}]}}],"materials":{{"audios":[{{"id":"A","path":"{}"}}]}}}}"#,
            crate::testing::slash(&media)
        );
        let ducked =
            apply(&tracks()[..1], source.as_bytes(), OriginalAudio::Lower, true, None, &mut CountingIds::default())
                .unwrap();
        let draft = crate::draft::Draft::parse(std::str::from_utf8(&ducked).unwrap()).unwrap();
        let env = crate::env::CoreEnvironment::for_home(dir.path());
        let timeline = crate::timeline::TimelineBuilder::build(&draft, Path::new("/tmp"), &env);
        let clip = timeline.audio_clips.iter().find(|c| c.path == media).unwrap();
        assert_eq!(clip.volume, 0.5);
        let envelope = clip.volume_envelope.as_ref().unwrap();
        assert_eq!(envelope.value(0), 1.0);
        assert!((envelope.value(2_000_000) - LOWER_GAIN).abs() < 1e-9);
        assert!(!timeline.report.unsupported.iter().any(|i| i.category == "Keyframes"));
    }

    #[test]
    fn placeholders_and_names() {
        assert_eq!(placeholder_prefix(draft().as_bytes()).as_deref(), Some(PLACEHOLDER));
        assert_eq!(placeholder_prefix(b"no placeholder here at all"), None);
        assert_eq!(OriginalAudio::Mute.name_token(), "muteall");
        assert_eq!(OriginalAudio::from_raw_value("Replace voice"), Some(OriginalAudio::Replace));
        assert_eq!(merge(vec![5..6, 0..1, 1..3], 1), [0..3, 5..6]);
    }
}
