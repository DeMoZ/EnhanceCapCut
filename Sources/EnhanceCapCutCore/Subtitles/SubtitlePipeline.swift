import Foundation

/// Timeline → audio mix → speech recognition → cues → translations. The result goes into the draft
/// with `DraftSubtitleWriter` and into `.srt` files. Ported from SubDub, the Final Cut Pro version.
@available(macOS 26, *)
public final class SubtitlePipeline {
    public struct Options {
        public var sourceLocale: Locale
        public var includeOriginal: Bool
        public var targets: [SubtitleLanguage]
        /// The language whose track stays visible; the others are added hidden. Nil = the first track.
        public var visibleLanguage: String?

        public init(sourceLocale: Locale, includeOriginal: Bool, targets: [SubtitleLanguage], visibleLanguage: String? = nil) {
            self.sourceLocale = sourceLocale
            self.includeOriginal = includeOriginal
            self.targets = targets
            self.visibleLanguage = visibleLanguage
        }
    }

    public typealias Report = (_ step: String, _ fraction: Double) -> Void

    private let translator: TranslationProvider
    private var cancelled = false

    public init(translator: TranslationProvider) {
        self.translator = translator
    }

    public func cancel() { cancelled = true }

    public func run(timeline: Timeline, options: Options, report: @escaping Report) async throws -> [SubtitleTrack] {
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
        var step = "Transcribing…"
        let words = try await Transcriber.transcribe(
            fileURL: audioURL, locale: options.sourceLocale, duration: duration,
            status: { s in step = s; report(s, 0.15) },
            progress: { p in report(step, 0.15 + 0.6 * p) })
        try checkCancelled()

        let sourceLanguage = SubtitleLanguage(options.sourceLocale.language)
        let cues = Segmenter.cues(from: words, language: sourceLanguage.code)

        // 3. Translations
        var tracks: [SubtitleTrack] = []
        if options.includeOriginal {
            tracks.append(SubtitleTrack(language: sourceLanguage, cues: cues, isOriginal: true, isVisible: false))
        }
        let targets = options.targets.filter { $0.code != sourceLanguage.code }
        for (i, target) in targets.enumerated() {
            report("Translating: \(target.displayName)…", 0.75 + 0.25 * Double(i) / Double(targets.count))
            let translated = try await translator.translate(cues.map(\.text), from: options.sourceLocale.language, to: target)
            try checkCancelled()
            let wrapped = zip(cues, translated).map {
                CaptionCue(start: $0.start, end: $0.end, text: LineWrapper.wrap($1, language: target.code))
            }
            tracks.append(SubtitleTrack(language: target, cues: wrapped, isOriginal: false, isVisible: false))
        }
        let visible = tracks.firstIndex { $0.language.code == options.visibleLanguage } ?? 0
        if tracks.indices.contains(visible) { tracks[visible].isVisible = true }
        report("Done", 1)
        return tracks
    }

    private func checkCancelled() throws {
        if cancelled || Task.isCancelled { throw CancellationError() }
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
