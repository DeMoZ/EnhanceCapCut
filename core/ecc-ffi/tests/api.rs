//! The exported API, called the way the bindings call it (task 6.1): on every fixture project, what crosses the
//! boundary must be what the core computed, and the errors must arrive as `CoreError` cases.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;

use ecc_core::*;
use ecc_golden::fixtures::{self, DraftName};

fn fixtures_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures")
}

/// A fresh folder, removed afterwards.
struct Scratch(PathBuf);

impl Scratch {
    fn new() -> Scratch {
        static N: AtomicU32 = AtomicU32::new(0);
        let path =
            std::env::temp_dir().join(format!("ecc-ffi-{}-{}", std::process::id(), N.fetch_add(1, Ordering::Relaxed)));
        let _ = std::fs::remove_dir_all(&path);
        std::fs::create_dir_all(&path).unwrap();
        Scratch(path)
    }

    fn project(&self, fixture: &str, name: DraftName) -> String {
        let path = fixtures::materialize(&fixtures_root().join(fixture), &self.0, name).unwrap();
        path.to_string_lossy().into_owned()
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn env(project: &str) -> CoreEnvironment {
    let core = fixtures::environment(Path::new(project));
    CoreEnvironment {
        projects_root: core.projects_root.to_string_lossy().into_owned(),
        capcut_cache_roots: core.capcut_cache_roots.iter().map(|p| p.to_string_lossy().into_owned()).collect(),
        backup_root: core.backup_root.to_string_lossy().into_owned(),
    }
}

fn fixture_names() -> Vec<String> {
    let all = fixtures::list(&fixtures_root());
    assert!(all.len() >= 9, "fixtures not found under {}", fixtures_root().display());
    all.iter().map(|p| p.file_name().unwrap().to_string_lossy().into_owned()).collect()
}

fn content_kind(c: &VisualContent) -> &'static str {
    match c {
        VisualContent::Video { .. } => "video",
        VisualContent::Photo { .. } => "photo",
        VisualContent::Text { .. } => "text",
        VisualContent::Compound { .. } => "compound",
        VisualContent::Sticker { .. } => "sticker",
    }
}

fn core_kind(c: &ecc::timeline::VisualContent) -> &'static str {
    use ecc::timeline::VisualContent as C;
    match c {
        C::Video(_) => "video",
        C::Photo(_) => "photo",
        C::Text(_) => "text",
        C::Compound(_) => "compound",
        C::Sticker(_) => "sticker",
    }
}

/// The FFI timeline against the core's, recursively into compound clips.
fn check_timeline(ffi: &Timeline, context: &str) {
    let core = ffi.core();
    assert_eq!(ffi.duration(), core.duration, "{context}");
    assert_eq!(ffi.canvas().width, core.canvas_width, "{context}");
    let layers = ffi.layers();
    assert_eq!(layers.len(), core.layers.len(), "{context}");
    for (l, c) in layers.iter().zip(&core.layers) {
        let context = format!("{context} layer {}", c.id);
        assert_eq!(l.id, c.id);
        assert_eq!((l.render_index, l.track_index), (c.render_index, c.track_index), "{context}");
        assert_eq!((l.target.start, l.target.duration), (c.target.start, c.target.duration), "{context}");
        assert_eq!(content_kind(&l.content), core_kind(&c.content), "{context}");
        assert_eq!(l.keyframes.len(), c.keyframes.len(), "{context}");
        assert_eq!(l.masks.len(), c.masks.len(), "{context}");
        assert_eq!(
            l.transition_out.as_ref().map(|t| t.next),
            c.transition_out.as_ref().map(|t| t.next as u64),
            "{context}"
        );
        let offsets: Vec<i64> = (0..=8).map(|i| c.target.duration * i / 8).collect();
        assert_eq!(l.time_map.source_times(offsets.clone()), c.time_map.source_times(&offsets), "{context}");
        for (property, track) in &l.keyframes {
            let core_track = &c.keyframes[&ecc::timeline::LayerProperty::from(*property)];
            assert_eq!(track.values_at(offsets.clone()), core_track.values_at(&offsets), "{context}");
        }
        if let VisualContent::Compound { timeline } = &l.content {
            check_timeline(timeline, &context);
        }
    }
    assert_eq!(ffi.audio_clips().len(), core.audio_clips.len(), "{context}");
    assert_eq!(ffi.filters().len(), core.filters.len(), "{context}");
    let report = ffi.report();
    assert_eq!(report.unsupported.len(), core.report.unsupported.len(), "{context}");
    assert_eq!(report.missing_files, core.report.missing_files, "{context}");

    let times = ecc_golden::dump::sample_times(core);
    let frames = ffi.frames(times.clone()).unwrap();
    for (frame, &t) in frames.iter().zip(&times) {
        let expected = core.frame(t);
        assert_eq!(frame.time, t);
        assert_eq!(frame.layers.len(), expected.layers.len(), "{context} at {t}");
        for (a, b) in frame.layers.iter().zip(&expected.layers) {
            assert_eq!(
                (a.index, a.offset, a.source_time),
                (b.index as u64, b.offset, b.source_time),
                "{context} at {t}"
            );
            assert_eq!(a.transform.position_x, b.transform.position_x, "{context} at {t}");
            assert_eq!(a.transform.alpha, b.transform.alpha, "{context} at {t}");
            assert_eq!(a.masks.len(), b.masks.len(), "{context} at {t}");
            assert_eq!(a.text.as_ref().map(|s| s.reveal), b.text.map(|s| s.reveal), "{context} at {t}");
            assert_eq!(a.clock_hands.map(|h| (h.minute, h.hour)), b.clock_hands, "{context} at {t}");
        }
        assert_eq!(frame.filters, expected.filters.iter().map(|&i| i as u64).collect::<Vec<_>>(), "{context} at {t}");
        assert_eq!(frame.layers.len(), ffi.frame(t).unwrap().layers.len());
    }
}

#[test]
fn every_fixture_crosses_the_boundary_unchanged() {
    for fixture in fixture_names() {
        for name in [DraftName::Info, DraftName::Content] {
            let scratch = Scratch::new();
            let project = scratch.project(&fixture, name);
            let env = env(&project);
            let mut info = String::new();
            for timeline in project_timelines(project.clone()) {
                let built = Timeline::build(project.clone(), Some(timeline.clone()), env.clone()).unwrap();
                check_timeline(&built, &format!("{fixture} ({name:?}) {}", timeline.name));
                let number = Some(timeline.id.clone()).filter(|id| !id.is_empty());
                info += &format_info(Some(project.clone()), number, env.clone()).unwrap();
            }
            let expected = std::fs::read_to_string(fixtures_root().join(&fixture).join("expected/info.txt")).unwrap();
            assert_eq!(info, expected, "{fixture} ({name:?}) info");
        }
    }
}

#[test]
fn timelines_from_bytes_and_identity() {
    let scratch = Scratch::new();
    let project = scratch.project("compound-clips", DraftName::Info);
    let env = env(&project);
    let main = project_timeline(project.clone(), None).unwrap();
    let built = Timeline::build(project.clone(), Some(main.clone()), env.clone()).unwrap();
    let draft = std::fs::read(&main.draft_path).unwrap();
    let from_bytes = Timeline::from_draft(draft, project.clone(), env.clone()).unwrap();
    assert_eq!(from_bytes.core(), built.core());
    // Equality is identity: same built timeline, not same content.
    assert!(*built == *built.clone());
    assert!(*built != *from_bytes);
    let inner: Vec<Arc<Timeline>> = built
        .layers()
        .into_iter()
        .filter_map(|l| match l.content {
            VisualContent::Compound { timeline } => Some(timeline),
            _ => None,
        })
        .collect();
    assert!(!inner.is_empty(), "compound-clips has no compound layer");
    let again: Vec<Arc<Timeline>> = built
        .layers()
        .into_iter()
        .filter_map(|l| match l.content {
            VisualContent::Compound { timeline } => Some(timeline),
            _ => None,
        })
        .collect();
    assert!(inner.iter().zip(&again).all(|(a, b)| a == b), "an inner timeline is shared, not copied");
}

#[test]
fn subtitles_can_be_left_out() {
    let scratch = Scratch::new();
    let project = scratch.project("captions", DraftName::Info);
    let built = Timeline::build(project.clone(), None, env(&project)).unwrap();
    assert!(built.has_subtitles());
    let without = built.without_subtitles();
    assert!(!without.has_subtitles());
    assert!(without.layers().len() < built.layers().len());
}

#[test]
fn curves_answer_like_the_core() {
    let map = TimeMap::constant(1_000, 2_000_000, 1_000_000);
    assert!(map.is_constant());
    assert_eq!(map.source_times(vec![0, 500_000, 1_000_000]), vec![1_000, 1_001_000, 2_001_000]);
    assert_eq!(map.average_speed(), 2.0);
    assert_eq!(map.slice(500_000, 1_000_000).source_time(0), 1_001_000);
    let track = KeyframeTrack::linear(vec![0, 1_000], vec![0.0, 1.0]);
    assert_eq!(track.values_at(vec![-10, 0, 250, 1_000, 2_000]), vec![0.0, 0.0, 0.25, 1.0, 1.0]);
    assert_eq!(track.times(), vec![0, 1_000]);
}

fn edits(project: &str) -> DraftEdits {
    let core_captions = ecc_golden::dump::caption_tracks();
    let core_voice = ecc_golden::dump::voice_tracks(Path::new(project));
    DraftEdits {
        captions: core_captions
            .into_iter()
            .map(|t| SubtitleTrack {
                language: t.language,
                cues: t.cues.into_iter().map(|c| CaptionCue { start: c.start, end: c.end, text: c.text }).collect(),
                is_original: t.is_original,
                is_visible: t.is_visible,
            })
            .collect(),
        voice_over: core_voice
            .into_iter()
            .map(|t| VoiceOverTrack {
                language: t.language,
                path: t.path,
                file_duration: t.file_duration,
                lines: t.lines.into_iter().map(|(start, end)| LineRange { start, end }).collect(),
                is_audible: t.is_audible,
                is_background: t.is_background,
            })
            .collect(),
        original: OriginalAudio::Lower,
        replace_previous: true,
    }
}

#[test]
fn edits_are_the_cores_edits() {
    let scratch = Scratch::new();
    let project = scratch.project("voice", DraftName::Info);
    let main = project_timeline(project.clone(), None).unwrap();
    let draft = std::fs::read(&main.draft_path).unwrap();
    let now_us = ecc_golden::dump::NOW_MS * 1000;
    let ours =
        apply_edits(edits(&project), draft.clone(), Some(project.clone()), IdSource::Counting { after: 0 }, now_us)
            .unwrap();

    let core = ecc::writers::DraftEdits {
        captions: ecc_golden::dump::caption_tracks(),
        voice_over: ecc_golden::dump::voice_tracks(Path::new(&project)),
        original: ecc::writers::voice_over::OriginalAudio::Lower,
        replace_previous: true,
    };
    let mut ids = ecc::writers::CountingIds(0);
    let expected = core.apply(&draft, Some(Path::new(&project)), &mut ids, ecc_golden::dump::NOW_MS).unwrap();
    assert_eq!(ours, expected);

    // Without the voice-over again, the draft is back to what speech recognition should hear.
    let cleaned = without_voice_over(ours, IdSource::Counting { after: 100 }).unwrap();
    let speech = Timeline::from_draft(cleaned, project.clone(), env(&project)).unwrap();
    assert!(speech.audio_clips().iter().all(|c| !c.path.contains("voiceover")));
}

#[test]
fn writing_a_timeline_backs_it_up_and_respects_capcut() {
    let scratch = Scratch::new();
    let project = scratch.project("voice", DraftName::Info);
    let main = project_timeline(project.clone(), None).unwrap();
    let before = std::fs::read(&main.draft_path).unwrap();
    let backups = scratch.0.join("backups").to_string_lossy().into_owned();
    let edits = DraftEdits { voice_over: Vec::new(), ..edits(&project) };

    let result = write_timeline(
        project.clone(),
        main.clone(),
        edits.clone(),
        backups.clone(),
        false,
        IdSource::Random,
        1_700_000_000_000_000,
    )
    .unwrap();
    assert_eq!(result.cue_count, 5);
    assert!(result.files.contains(&main.draft_path));
    assert_ne!(std::fs::read(&main.draft_path).unwrap(), before);
    let saved = Path::new(&result.backup_folder).join(Path::new(&main.draft_path).strip_prefix(&project).unwrap());
    assert_eq!(std::fs::read(saved).unwrap(), before);

    // Locked while CapCut runs: refused, nothing written.
    std::fs::write(Path::new(&project).join(".locked"), "").unwrap();
    assert!(is_open_in_capcut(project.clone(), true));
    assert!(!is_open_in_capcut(project.clone(), false));
    let written = std::fs::read(&main.draft_path).unwrap();
    let refused = write_timeline(project.clone(), main.clone(), edits, backups, true, IdSource::Random, 0);
    assert!(matches!(refused, Err(CoreError::ProjectOpen(_))), "{refused:?}");
    assert_eq!(std::fs::read(&main.draft_path).unwrap(), written);
}

#[test]
fn dubbed_timelines_are_added_to_the_list() {
    let scratch = Scratch::new();
    let project = scratch.project("timelines", DraftName::Info);
    let backups = scratch.0.join("backups").to_string_lossy().into_owned();
    let main = project_timeline(project.clone(), None).unwrap();
    let before = project_timelines(project.clone()).len();
    let name = dub_timeline_name(project.clone(), vec!["en".into(), "ru".into()], OriginalAudio::Replace);
    assert!(name.ends_with("_dub_en-ru_replacevoice_1"), "{name}");
    let dub = create_dub_timeline(
        project.clone(),
        main,
        name.clone(),
        edits(&project),
        backups.clone(),
        false,
        IdSource::Counting { after: 0 },
        1_700_000_000_000_000,
    )
    .unwrap();
    assert_eq!(dub.timeline.id, "00000000-0000-4000-8000-000000000001", "the timeline's id comes first");
    assert_eq!(dub.timeline.name, name);
    let timelines = project_timelines(project.clone());
    assert_eq!(timelines.len(), before + 1);
    assert!(timelines.contains(&dub.timeline));
    assert!(Timeline::build(project.clone(), Some(dub.timeline.clone()), env(&project)).unwrap().has_subtitles());

    // A project with a single timeline can't take one.
    let single = scratch.project("captions", DraftName::Info);
    let main = project_timeline(single.clone(), None).unwrap();
    let refused = create_dub_timeline(single, main, "x".into(), edits(&project), backups, false, IdSource::Random, 0);
    assert!(matches!(refused, Err(CoreError::NoTimelineList(_))), "{refused:?}");
}

#[test]
fn errors_arrive_as_core_errors() {
    let missing = std::env::temp_dir().join("ecc-ffi-no-such-project").to_string_lossy().into_owned();
    let built = Timeline::build(missing.clone(), None, default_environment());
    assert!(matches!(built, Err(CoreError::Unreadable(_))), "{built:?}");
    let info = format_info(Some(missing), None, default_environment());
    assert!(matches!(info, Err(CoreError::NotFound(_))), "{info:?}");
    assert!(matches!(
        Timeline::from_draft(vec![0xFF], ".".into(), default_environment()),
        Err(CoreError::Malformed(_))
    ));
    let broken = apply_edits(edits("/p"), b"{\"no\":1}".to_vec(), None, IdSource::Random, 0);
    assert!(matches!(broken, Err(CoreError::Malformed(_))), "{broken:?}");
    let panic = debug_panic("boom".into());
    assert_eq!(panic, Err(CoreError::Internal("boom".into())));
}

#[test]
fn captions_and_names() {
    let words = vec![
        TimedWord { text: "Hello".into(), start: 0.0, end: 0.4 },
        TimedWord { text: "world.".into(), start: 0.4, end: 0.9 },
        TimedWord { text: "Again".into(), start: 3.0, end: 3.5 },
    ];
    let cues = caption_cues(words, Some("en".into())).unwrap();
    assert_eq!(cues.len(), 2);
    assert_eq!(cues[0].text, "Hello world.");
    assert!(srt(cues).starts_with("1\n00:00:00,000 --> "));
    assert!(wrap_caption("a ".repeat(40), Some("en".into())).unwrap().contains('\n'));
    assert!(is_no_space_language("th".into()));
    assert_eq!(capcut_language("ru".into()), "ru-RU");
    assert_eq!(original_audio_raw_value(OriginalAudio::Mute), "Mute all");
    assert_eq!(original_audio_from_raw_value("Remove vocal".into()), Some(OriginalAudio::RemoveVocal));
    assert_eq!(
        ducked_ranges(vec![LineRange { start: 1.0, end: 2.0 }, LineRange { start: 2.1, end: 3.0 }]),
        vec![TimeSpan { start: 1_000_000, end: 3_000_000 }]
    );
    assert_eq!(background_gain(1_500_000, TimeSpan { start: 1_000_000, end: 2_000_000 }), 1.0);
    let default = default_environment();
    assert!(!default.projects_root.is_empty() && !default.capcut_cache_roots.is_empty());
}
