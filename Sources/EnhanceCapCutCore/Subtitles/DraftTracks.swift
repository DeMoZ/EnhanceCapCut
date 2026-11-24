import EccCore
import Foundation

// What a subtitles run puts into a CapCut draft, and the native side of writing it: the tracks carry the app's
// `SubtitleLanguage` and file URLs and become the core's records at the boundary; the core does the writing
// (`writeTimeline`, `createDubTimeline`), given whether CapCut runs, the clock and random ids.

/// One language of generated subtitles, to be added to a draft as a CapCut caption track.
public struct SubtitleTrack {
    public var language: SubtitleLanguage
    public var cues: [CaptionCue]
    /// The spoken language (CapCut's recognized captions) rather than a translation.
    public var isOriginal: Bool
    /// Hidden tracks can be shown in CapCut with the track's eye button.
    public var isVisible: Bool

    public init(language: SubtitleLanguage, cues: [CaptionCue], isOriginal: Bool, isVisible: Bool) {
        self.language = language
        self.cues = cues
        self.isOriginal = isOriginal
        self.isVisible = isVisible
    }

    var core: EccCore.SubtitleTrack {
        EccCore.SubtitleTrack(language: language.code, cues: cues, isOriginal: isOriginal, isVisible: isVisible)
    }
}

/// One language of generated voice-over, to be added to a draft as an audio track.
public struct VoiceOverTrack {
    public var language: SubtitleLanguage
    /// The spoken track as one file aligned to the timeline (file time 0 = timeline start).
    public var url: URL
    /// Length of the file, seconds.
    public var fileDuration: Double
    /// Where each line was placed, seconds; every line becomes its own clip on the track.
    public var lines: [ClosedRange<Double>]
    /// Muted tracks can be unmuted in CapCut with the track's speaker button.
    public var isAudible: Bool
    /// The project's sound without its voice (`OriginalAudio.replace`); `lines` are the stretches it covers.
    public var isBackground = false

    public init(language: SubtitleLanguage, url: URL, fileDuration: Double, lines: [ClosedRange<Double>], isAudible: Bool,
                isBackground: Bool = false) {
        self.language = language
        self.url = url
        self.fileDuration = fileDuration
        self.lines = lines
        self.isAudible = isAudible
        self.isBackground = isBackground
    }

    var core: EccCore.VoiceOverTrack {
        EccCore.VoiceOverTrack(language: language.code, path: url.path, fileDuration: fileDuration,
                               lines: lines.map { LineRange(start: $0.lowerBound, end: $0.upperBound) },
                               isAudible: isAudible, isBackground: isBackground)
    }
}

/// Writing a run into a project through the core.
public enum DraftWriter {
    /// Where the originals of rewritten drafts are kept.
    public static var backupRoot: URL { CoreEnvironment.current.backupRoot.fileURL }

    /// The run's tracks as the core's draft edits: captions, then voice-over.
    public static func edits(captions: [SubtitleTrack], voiceOver: [VoiceOverTrack], original: OriginalAudio,
                             replacePrevious: Bool) -> DraftEdits {
        DraftEdits(captions: captions.map(\.core), voiceOver: voiceOver.map(\.core), original: original,
                   replacePrevious: replacePrevious)
    }

    static var nowUs: Int64 { Int64(Date().timeIntervalSince1970 * 1_000_000) }

    /// Writes `edits` into the timeline's draft and the copies CapCut keeps of it, after a backup. Throws
    /// `CoreError.ProjectOpen` while the project is open in CapCut.
    public static func write(_ edits: DraftEdits, project: CapCutProject, timeline: ProjectTimeline) throws -> WriteResult {
        try writeTimeline(projectPath: project.path, timeline: timeline, edits: edits, backupRoot: backupRoot.path,
                          capcutRunning: CapCutApp.isRunning, ids: .random, nowUs: nowUs)
    }

    /// Adds a timeline named `name`: `source`'s draft with `edits` applied.
    public static func createDub(_ edits: DraftEdits, project: CapCutProject, source: ProjectTimeline,
                                 name: String) throws -> DubResult {
        try createDubTimeline(projectPath: project.path, source: source, name: name, edits: edits,
                              backupRoot: backupRoot.path, capcutRunning: CapCutApp.isRunning, ids: .random, nowUs: nowUs)
    }

    /// `<project>_dub_<languages>_<original audio>_<n>`, with the first `n` no timeline has.
    public static func dubName(project: CapCutProject, languages: [String], original: OriginalAudio) -> String {
        dubTimelineName(projectPath: project.path, languages: languages, original: original)
    }
}
