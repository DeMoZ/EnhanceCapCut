//! Captions, voice-over and dubbed timelines into CapCut drafts, and the caption segmentation they come from. The
//! apps pass "is CapCut running" (`capcut_running`), the clock (`now_us`) and where ids come from (`IdSource`).

use std::cell::RefCell;
use std::path::Path;
use std::rc::Rc;

use ecc::project::CapCutProject;
use ecc::subtitles as s;
use ecc::writers::{self as w, dub, subtitles, voice_over};

use crate::convert::{record, simple_enum, Ffi};
use crate::project::ProjectTimeline;
use crate::{guarded, CoreError};

/// Where the ids of new draft objects come from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, uniffi::Enum)]
pub enum IdSource {
    /// Random UUIDs (the apps).
    Random,
    /// `00000000-0000-4000-8000-<n>` from `after + 1` on, for deterministic output in tests.
    Counting { after: u64 },
}

/// One id source shared by the writer and the transform it runs (a dubbed timeline takes an id, then the draft
/// edits take theirs from the same sequence).
#[derive(Clone)]
struct SharedIds(Rc<RefCell<Box<dyn w::IdSource>>>);

impl SharedIds {
    fn new(source: IdSource) -> SharedIds {
        let ids: Box<dyn w::IdSource> = match source {
            IdSource::Random => Box::new(w::RandomIds),
            IdSource::Counting { after } => Box::new(w::CountingIds(after)),
        };
        SharedIds(Rc::new(RefCell::new(ids)))
    }
}

impl w::IdSource for SharedIds {
    fn next_id(&mut self) -> String {
        self.0.borrow_mut().next_id()
    }
}

record! {
    /// A caption line, seconds from the timeline start.
    #[derive(PartialEq)]
    CaptionCue = s::CaptionCue { start: f64, end: f64, text: String }
}

impl From<CaptionCue> for s::CaptionCue {
    fn from(c: CaptionCue) -> Self {
        s::CaptionCue { start: c.start, end: c.end, text: c.text }
    }
}

/// A recognized word, seconds from the timeline start.
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct TimedWord {
    pub text: String,
    pub start: f64,
    pub end: f64,
}

/// One language of generated subtitles, added to a draft as a CapCut caption track.
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct SubtitleTrack {
    /// Our language code: `ru`, `en`, `pt-BR`, `zh-Hans`…
    pub language: String,
    pub cues: Vec<CaptionCue>,
    /// The spoken language (CapCut's recognized captions) rather than a translation.
    pub is_original: bool,
    /// Hidden tracks can be shown in CapCut with the track's eye button.
    pub is_visible: bool,
}

impl From<SubtitleTrack> for subtitles::SubtitleTrack {
    fn from(t: SubtitleTrack) -> Self {
        subtitles::SubtitleTrack {
            language: t.language,
            cues: t.cues.into_iter().map(Into::into).collect(),
            is_original: t.is_original,
            is_visible: t.is_visible,
        }
    }
}

/// Seconds, start to end.
#[derive(Debug, Clone, Copy, PartialEq, uniffi::Record)]
pub struct LineRange {
    pub start: f64,
    pub end: f64,
}

/// µs, the end excluded.
#[derive(Debug, Clone, Copy, PartialEq, Eq, uniffi::Record)]
pub struct TimeSpan {
    pub start: i64,
    pub end: i64,
}

/// One language of generated voice-over, added to a draft as an audio track.
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct VoiceOverTrack {
    pub language: String,
    /// The spoken track as one file aligned to the timeline (file time 0 = timeline start).
    pub path: String,
    /// Length of the file, seconds.
    pub file_duration: f64,
    /// Where each line was placed; every line becomes its own clip on the track.
    pub lines: Vec<LineRange>,
    /// Muted tracks can be unmuted in CapCut with the track's speaker button.
    pub is_audible: bool,
    /// The project's sound without its voice, played while the original is muted under the voice-over
    /// (`OriginalAudio.Replace`); `lines` are the stretches it covers.
    pub is_background: bool,
}

impl From<VoiceOverTrack> for voice_over::VoiceOverTrack {
    fn from(t: VoiceOverTrack) -> Self {
        voice_over::VoiceOverTrack {
            language: t.language,
            path: t.path,
            file_duration: t.file_duration,
            lines: t.lines.into_iter().map(|l| (l.start, l.end)).collect(),
            is_audible: t.is_audible,
            is_background: t.is_background,
        }
    }
}

simple_enum! {
    /// What happens to the project's own sound while the voice-over speaks.
    OriginalAudio = voice_over::OriginalAudio { Keep, Lower, Mute, Replace, RemoveVocal }
}

/// What a subtitles run puts into a draft: caption tracks, then voice-over tracks.
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct DraftEdits {
    pub captions: Vec<SubtitleTrack>,
    pub voice_over: Vec<VoiceOverTrack>,
    pub original: OriginalAudio,
    /// Replace what an earlier run added instead of adding to it.
    pub replace_previous: bool,
}

impl From<DraftEdits> for w::DraftEdits {
    fn from(e: DraftEdits) -> Self {
        w::DraftEdits {
            captions: e.captions.into_iter().map(Into::into).collect(),
            voice_over: e.voice_over.into_iter().map(Into::into).collect(),
            original: e.original.into(),
            replace_previous: e.replace_previous,
        }
    }
}

/// What a write changed.
#[derive(Debug, Clone, PartialEq, Eq, uniffi::Record)]
pub struct WriteResult {
    /// Every draft file that was rewritten.
    pub files: Vec<String>,
    /// The originals, saved before writing.
    pub backup_folder: String,
    pub cue_count: u64,
}

impl Ffi for w::WriteResult {
    type Out = WriteResult;
    fn ffi(self) -> WriteResult {
        WriteResult {
            files: self.files.ffi(),
            backup_folder: self.backup_folder.ffi(),
            cue_count: self.cue_count.ffi(),
        }
    }
}

/// A new dubbed timeline and what writing it changed.
#[derive(Debug, Clone, PartialEq, Eq, uniffi::Record)]
pub struct DubResult {
    pub timeline: ProjectTimeline,
    pub result: WriteResult,
}

fn optional_path(path: &Option<String>) -> Option<&Path> {
    path.as_deref().map(Path::new)
}

/// The draft with `edits` applied: captions, then voice-over. `project_path` lets voice-over paths inside the project
/// use the draft's project-folder placeholder; `now_us` stamps the caption groups.
#[uniffi::export]
pub fn apply_edits(
    edits: DraftEdits,
    draft: Vec<u8>,
    project_path: Option<String>,
    ids: IdSource,
    now_us: i64,
) -> Result<Vec<u8>, CoreError> {
    guarded(|| {
        let edits = w::DraftEdits::from(edits);
        Ok(edits.apply(&draft, optional_path(&project_path), &mut SharedIds::new(ids), now_us / 1000)?)
    })
}

/// The draft without the voice-over, ducking and vocal removal of earlier runs: what speech recognition should hear.
#[uniffi::export]
pub fn without_voice_over(draft: Vec<u8>, ids: IdSource) -> Result<Vec<u8>, CoreError> {
    guarded(|| Ok(voice_over::without_voice_over(&draft, &mut SharedIds::new(ids))?))
}

/// CapCut keeps a `.locked` file in an open project's folder, but a crash leaves stale ones, so the lock only counts
/// while CapCut is running (which the app checks itself).
#[uniffi::export]
pub fn is_open_in_capcut(project_path: String, capcut_running: bool) -> bool {
    w::is_open_in_capcut(&CapCutProject::new(project_path), capcut_running)
}

/// Writes `edits` into the timeline's draft and the copies CapCut keeps of it, after a backup into
/// `backup_root/<project>/<stamp>`. Refused (`CoreError.ProjectOpen`) while the project is open in CapCut.
#[uniffi::export]
pub fn write_timeline(
    project_path: String,
    timeline: ProjectTimeline,
    edits: DraftEdits,
    backup_root: String,
    capcut_running: bool,
    ids: IdSource,
    now_us: i64,
) -> Result<WriteResult, CoreError> {
    guarded(|| {
        let project = CapCutProject::new(&project_path);
        let edits = w::DraftEdits::from(edits);
        let mut ids = SharedIds::new(ids);
        let result = w::write_draft(
            &project,
            &timeline.into(),
            edits.cue_count(),
            Path::new(&backup_root),
            capcut_running,
            now_us,
            |draft| edits.apply(draft, Some(&project.path), &mut ids, now_us / 1000),
        )?;
        Ok(result.ffi())
    })
}

/// Adds a timeline named `name` to the project: `source`'s draft with `edits` applied. `Timelines/project.json` and
/// `timeline_layout.json` are backed up first. Refused while the project is open in CapCut, and for projects that
/// keep a single timeline (`CoreError.NoTimelineList`).
#[uniffi::export]
#[allow(clippy::too_many_arguments)]
pub fn create_dub_timeline(
    project_path: String,
    source: ProjectTimeline,
    name: String,
    edits: DraftEdits,
    backup_root: String,
    capcut_running: bool,
    ids: IdSource,
    now_us: i64,
) -> Result<DubResult, CoreError> {
    guarded(|| {
        let project = CapCutProject::new(&project_path);
        let edits = w::DraftEdits::from(edits);
        let ids = SharedIds::new(ids);
        let mut for_edits = ids.clone();
        let (timeline, result) = dub::create(
            &project,
            &source.into(),
            &name,
            edits.cue_count(),
            Path::new(&backup_root),
            capcut_running,
            now_us,
            &mut ids.clone(),
            |draft| edits.apply(draft, Some(&project.path), &mut for_edits, now_us / 1000),
        )?;
        Ok(DubResult { timeline: timeline.ffi(), result: result.ffi() })
    })
}

/// `<project>_dub_<languages>_<original audio>_<n>`, with the first `n` from 1 that no timeline has.
#[uniffi::export]
pub fn dub_timeline_name(project_path: String, languages: Vec<String>, original: OriginalAudio) -> String {
    dub::name(&CapCutProject::new(project_path), &languages, original.into())
}

/// The stretches the original is ducked over: the voice's lines, merged when the ramps between them would meet.
#[uniffi::export]
pub fn ducked_ranges(lines: Vec<LineRange>) -> Vec<TimeSpan> {
    let lines: Vec<(f64, f64)> = lines.into_iter().map(|l| (l.start, l.end)).collect();
    voice_over::ducked_ranges(&lines).into_iter().map(|r| TimeSpan { start: r.start, end: r.end }).collect()
}

/// The ducking ramps (µs): the original goes down `down` before a line and back up `up` after it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, uniffi::Record)]
pub struct DuckingRamps {
    pub down: i64,
    pub up: i64,
}

#[uniffi::export]
pub fn ducking_ramps() -> DuckingRamps {
    DuckingRamps { down: voice_over::FADE_DOWN, up: voice_over::FADE_UP }
}

/// The background track's gain at `t` (µs) for one ducked range: the complement of the original's ducking.
#[uniffi::export]
pub fn background_gain(t: i64, range: TimeSpan) -> f64 {
    voice_over::background_gain(t, &(range.start..range.end))
}

/// The English name, also the raw value the apps store.
#[uniffi::export]
pub fn original_audio_raw_value(original: OriginalAudio) -> String {
    voice_over::OriginalAudio::from(original).raw_value().to_string()
}

#[uniffi::export]
pub fn original_audio_from_raw_value(raw: String) -> Option<OriginalAudio> {
    voice_over::OriginalAudio::from_raw_value(&raw).ffi()
}

/// The name in the core's language (`set_core_language`).
#[uniffi::export]
pub fn original_audio_title(original: OriginalAudio) -> String {
    voice_over::OriginalAudio::from(original).title()
}

/// CapCut's caption language for one of our language codes, like `ru-RU` or `en-US`.
#[uniffi::export]
pub fn capcut_language(code: String) -> String {
    w::capcut_language(&code)
}

// Captions.

/// Groups recognized words into readable subtitle cues; `language` picks line lengths and word breaks.
#[uniffi::export]
pub fn caption_cues(words: Vec<TimedWord>, language: Option<String>) -> Result<Vec<CaptionCue>, CoreError> {
    guarded(|| {
        let words: Vec<s::TimedWord> =
            words.into_iter().map(|w| s::TimedWord { text: w.text, start: w.start, end: w.end }).collect();
        Ok(s::Segmenter::cues(&words, language.as_deref()).ffi())
    })
}

/// `text` broken into balanced lines that fit CapCut's caption width; lines break only between words.
#[uniffi::export]
pub fn wrap_caption(text: String, language: Option<String>) -> Result<String, CoreError> {
    guarded(|| Ok(s::LineWrapper::wrap(&text, language.as_deref())))
}

/// Languages written without spaces between words (Thai, Chinese, Japanese…).
#[uniffi::export]
pub fn is_no_space_language(language: String) -> bool {
    s::LineWrapper::is_no_space(&language)
}

/// SRT text of `cues`.
#[uniffi::export]
pub fn srt(cues: Vec<CaptionCue>) -> String {
    let cues: Vec<s::CaptionCue> = cues.into_iter().map(Into::into).collect();
    s::srt(&cues)
}
