import Foundation

/// Timeline → audio mix → speech recognition → cues → translations → voice-over. The result goes into
/// the draft with `DraftSubtitleWriter` / `DraftVoiceOverWriter` and into `.srt` files. Ported from
/// SubDub, the Final Cut Pro version.
@available(macOS 26, *)
public final class SubtitlePipeline {
    public struct Options {
        public var sourceLocale: Locale
        public var includeOriginal: Bool
        public var targets: [SubtitleLanguage]
        /// The language whose track stays visible; the others are added hidden. Nil = the first track.
        public var visibleLanguage: String?
        /// Make caption tracks. Off = only translate for the voice-over.
        public var captions = true
        /// Voice-over languages: language code → voice id ("" = the best installed voice).
        public var voices: [String: String] = [:]
        /// The voice-over language left audible; the other tracks are added muted. Nil = the first one.
        public var audibleVoice: String?
        /// Where the voice-over audio files go (inside the project folder, so CapCut can reach them).
        public var voiceOverFolder: URL?

        public init(sourceLocale: Locale, includeOriginal: Bool, targets: [SubtitleLanguage], visibleLanguage: String? = nil) {
            self.sourceLocale = sourceLocale
            self.includeOriginal = includeOriginal
            self.targets = targets
            self.visibleLanguage = visibleLanguage
        }
    }

    public struct Output {
        public var captions: [SubtitleTrack]
        public var voiceOver: [VoiceOverTrack]
    }

    public typealias Report = (_ step: String, _ fraction: Double) -> Void

    private let translator: TranslationProvider
    private var cancelled = false

    public init(translator: TranslationProvider) {
        self.translator = translator
    }

    public func cancel() { cancelled = true }

    public func run(timeline: Timeline, options: Options, report: @escaping Report) async throws -> Output {
        let duration = Double(timeline.duration) / 1_000_000
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("EnhanceCapCut-subtitles-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: work) }

        // 1. Audio: the timeline's mix, as the viewer hears it.
        report("Mixing timeline audio…", 0)
        let audioURL = work.appendingPathComponent("mix.caf")
        try await AudioRenderer.render(timeline, to: audioURL, isCancelled: { self.cancelled || Task.isCancelled }) { p in
            report("Mixing timeline audio…", 0.15 * p)
        }
        try checkCancelled()

        // 2. Speech
        let sourceLanguage = SubtitleLanguage(options.sourceLocale.language)
        let targets = options.targets.filter { $0.code != sourceLanguage.code }
        let dubbing = targets.filter { options.voices[$0.code] != nil }
        // Progress: speech up to `speechEnd`, translations up to `translateEnd`, then the voice-over.
        let speechEnd = dubbing.isEmpty ? 0.75 : 0.6, translateEnd = dubbing.isEmpty ? 1 : 0.7
        var step = "Transcribing…"
        let words = try await Transcriber.transcribe(
            fileURL: audioURL, locale: options.sourceLocale, duration: duration,
            status: { s in step = s; report(s, 0.15) },
            progress: { p in report(step, 0.15 + (speechEnd - 0.15) * p) })
        try checkCancelled()

        let cues = Segmenter.cues(from: words, language: sourceLanguage.code)

        // 3. Translations
        var tracks: [SubtitleTrack] = []
        var spoken: [String: [CaptionCue]] = [:]
        if options.includeOriginal && options.captions {
            tracks.append(SubtitleTrack(language: sourceLanguage, cues: cues, isOriginal: true, isVisible: false))
        }
        let translating = options.captions ? targets : dubbing
        for (i, target) in translating.enumerated() {
            report("Translating: \(target.displayName)…",
                   speechEnd + (translateEnd - speechEnd) * Double(i) / Double(translating.count))
            let translated = try await translator.translate(cues.map(\.text), from: options.sourceLocale.language, to: target)
            try checkCancelled()
            spoken[target.code] = zip(cues, translated).map { CaptionCue(start: $0.start, end: $0.end, text: $1) }
            guard options.captions else { continue }
            let wrapped = zip(cues, translated).map {
                CaptionCue(start: $0.start, end: $0.end, text: LineWrapper.wrap($1, language: target.code))
            }
            tracks.append(SubtitleTrack(language: target, cues: wrapped, isOriginal: false, isVisible: false))
        }
        let visible = tracks.firstIndex { $0.language.code == options.visibleLanguage } ?? 0
        if tracks.indices.contains(visible) { tracks[visible].isVisible = true }

        // 4. Voice-over
        var voiceOver: [VoiceOverTrack] = []
        if !dubbing.isEmpty, let folder = options.voiceOverFolder {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            for (i, language) in dubbing.enumerated() {
                let base = translateEnd + (1 - translateEnd) * Double(i) / Double(dubbing.count)
                report("Voicing: \(language.displayName)…", base)
                let url = folder.appendingPathComponent("\(language.code).wav")
                let voice = options.voices[language.code].flatMap { $0.isEmpty ? nil : $0 }
                let lines = try await Dubber.render(cues: spoken[language.code] ?? [], voiceID: voice,
                                                    languageCode: language.code, totalDuration: duration, to: url) { p in
                    report("Voicing: \(language.displayName)…", base + (1 - translateEnd) * p / Double(dubbing.count))
                }
                try checkCancelled()
                let length = max(duration, lines.last?.upperBound ?? 0)
                voiceOver.append(VoiceOverTrack(language: language, url: url, fileDuration: length, lines: lines, isAudible: false))
            }
            let audible = voiceOver.firstIndex { $0.language.code == options.audibleVoice } ?? 0
            voiceOver[audible].isAudible = true
        }
        report("Done", 1)
        return Output(captions: tracks, voiceOver: voiceOver)
    }

    private func checkCancelled() throws {
        if cancelled || Task.isCancelled { throw CancellationError() }
    }

    /// The timeline to transcribe: without voice-over (and its ducking) that earlier runs added.
    public static func speechTimeline(project: CapCutProject, timeline: ProjectTimeline) throws -> Timeline {
        let data = try DraftVoiceOverWriter.withoutVoiceOver(Data(contentsOf: timeline.draftURL))
        return TimelineBuilder.build(draft: try JSONDecoder().decode(Draft.self, from: data), projectURL: project.url)
    }

    /// A new folder for a run's voice-over audio, inside the project so CapCut can read it.
    public static func voiceOverFolder(in project: CapCutProject) -> URL {
        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
        return project.url.appendingPathComponent("EnhanceCapCut/voiceover \(stamp)", isDirectory: true)
    }

    /// The draft with the run's caption and voice-over tracks added. Only what the run made is touched:
    /// without captions the draft's earlier subtitles stay, without voice-over its earlier voice-over.
    public static func apply(_ output: Output, to draft: Data, original: OriginalAudio, replacePrevious: Bool,
                             projectURL: URL?) throws -> Data {
        var data = draft
        if !output.captions.isEmpty {
            data = try DraftSubtitleWriter.apply(output.captions, to: data, replacePrevious: replacePrevious)
        }
        if !output.voiceOver.isEmpty {
            data = try DraftVoiceOverWriter.apply(output.voiceOver, to: data, original: original,
                                                  replacePrevious: replacePrevious, projectURL: projectURL)
        }
        return data
    }

    /// Writes one `.srt` per track into `folder`, named `<base> <code>.srt`.
    public static func writeSRT(_ tracks: [SubtitleTrack], to folder: URL, baseName: String) throws -> [URL] {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let safe = String(baseName.map { "/:\\".contains($0) ? "-" : $0 })
        return try tracks.map { track in
            let url = folder.appendingPathComponent("\(safe) \(track.language.code).srt")
            try SRTWriter.srt(track.cues).write(to: url, atomically: true, encoding: .utf8)
            return url
        }
    }
}
