import AVFoundation
import Foundation

/// The speech recogniser for the source language.
public enum SpeechEngine: String, CaseIterable, Sendable {
    /// Apple's on-device SpeechTranscriber / DictationTranscriber: nothing to download.
    case apple
    /// Whisper large-v3-turbo (`WhisperTranscriber`), as in the Windows version: a one-time model download.
    case whisper
}

/// Timeline → audio mix → speech recognition → cues → translations → voice-over. The result goes into
/// the draft with `DraftSubtitleWriter` / `DraftVoiceOverWriter` and into `.srt` files. Ported from
/// SubDub, the Final Cut Pro version.
@available(macOS 26, *)
public final class SubtitlePipeline {
    public struct Options {
        public var sourceLocale: Locale
        public var engine: SpeechEngine = .apple
        public var includeOriginal: Bool
        public var targets: [SubtitleLanguage]
        /// The language whose track stays visible; the others are added hidden. Nil = the first track.
        public var visibleLanguage: String?
        /// Make caption tracks. Off = only translate for the voice-over.
        public var captions = true
        /// Voice-over languages: language code → voice id ("" = the best installed voice,
        /// `Dubber.clonedVoiceID` = the original speaker's voice, cloned).
        public var voices: [String: String] = [:]
        /// How expressive cloned voices are, 0…1 (0.5 = like the original).
        public var exaggeration: Float = 0.5
        /// Also make the project's sound without its voice, for `OriginalAudio.replace`.
        public var background = false
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

        let sourceLanguage = SubtitleLanguage(options.sourceLocale.language)
        let targets = options.targets.filter { $0.code != sourceLanguage.code }
        let dubbing = targets.filter { options.voices[$0.code] != nil }
        let cloning = dubbing.contains { options.voices[$0.code] == Dubber.clonedVoiceID }
        // Cloned lines are checked with Whisper, so it's needed then too.
        var models = cloning ? ModelStore.voiceCloning : []
        if (options.engine == .whisper || cloning) && !ModelStore.isDownloaded(ModelStore.whisper) { models.append(ModelStore.whisper) }
        if !ModelStore.isDownloaded(models) {
            try await ModelStore.ensure(models) { p in report("Downloading models (one time)…", 0.15 * p) }
        }

        // 1. Audio: the timeline's mix, as the viewer hears it.
        report("Mixing timeline audio…", 0)
        let audioURL = work.appendingPathComponent("mix.caf")
        try await AudioRenderer.render(timeline, to: audioURL, isCancelled: { self.cancelled || Task.isCancelled }) { p in
            report("Mixing timeline audio…", 0.15 * p)
        }
        try checkCancelled()

        // 2. Speech
        // Progress: speech up to `speechEnd`, translations up to `translateEnd`, then the voice-over.
        let speechEnd = dubbing.isEmpty ? 0.75 : 0.6, translateEnd = dubbing.isEmpty ? 1 : 0.7
        var step = "Transcribing…"
        let words: [TimedWord]
        switch options.engine {
        case .apple:
            words = try await Transcriber.transcribe(
                fileURL: audioURL, locale: options.sourceLocale, duration: duration,
                status: { s in step = s; report(s, 0.15) },
                progress: { p in report(step, 0.15 + (speechEnd - 0.15) * p) })
        case .whisper:
            words = try await WhisperTranscriber.transcribe(fileURL: audioURL, locale: options.sourceLocale) { p in
                report(step, 0.15 + (speechEnd - 0.15) * p)
            }
        }
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
            // The original voice alone: what cloned lines copy.
            var cloner: VoiceCloner?, checker: WhisperChecker?, originalVoice: [Float] = []
            if cloning {
                report("Isolating the original voice…", translateEnd)
                let voiceURL = work.appendingPathComponent("voice.wav")
                try await VoiceEffectsRenderer.render(audioURL, effects: VoiceEffects(separation: .keepVoice), to: voiceURL)
                originalVoice = try MonoAudio.read(voiceURL, sampleRate: VoiceCloner.sampleRate)
                try checkCancelled()
                report("Loading the voice cloning model…", translateEnd)
                cloner = try VoiceCloner()
                checker = try WhisperChecker()
            }
            for (i, language) in dubbing.enumerated() {
                let base = translateEnd + (1 - translateEnd) * Double(i) / Double(dubbing.count)
                report("Voicing: \(language.displayName)…", base)
                let url = folder.appendingPathComponent("\(language.code).wav")
                let voice = options.voices[language.code].flatMap { $0.isEmpty ? nil : $0 }
                let languageProgress: (Double) -> Void = { p in
                    report("Voicing: \(language.displayName)…", base + (1 - translateEnd) * p / Double(dubbing.count))
                }
                let lines: [ClosedRange<Double>]
                if voice == Dubber.clonedVoiceID, let cloner {
                    lines = try Dubber.renderCloned(cues: spoken[language.code] ?? [], language: language.code, voice: originalVoice,
                                                    exaggeration: options.exaggeration, cloner: cloner, checker: checker,
                                                    totalDuration: duration, to: url,
                                                    isCancelled: { self.cancelled || Task.isCancelled }, progress: languageProgress)
                } else {
                    lines = try await Dubber.render(cues: spoken[language.code] ?? [], voiceID: voice,
                                                    languageCode: language.code, totalDuration: duration, to: url,
                                                    progress: languageProgress)
                }
                try checkCancelled()
                let length = max(duration, lines.last?.upperBound ?? 0)
                voiceOver.append(VoiceOverTrack(language: language, url: url, fileDuration: length, lines: lines, isAudible: false))
            }
            let audible = voiceOver.firstIndex { $0.language.code == options.audibleVoice } ?? 0
            voiceOver[audible].isAudible = true

            // The project's sound without its voice, faded in where the original is muted under the voice-over.
            if options.background {
                report("Separating the background…", 0.99)
                let backgroundURL = work.appendingPathComponent("background-full.wav")
                try await VoiceEffectsRenderer.render(audioURL, effects: VoiceEffects(separation: .removeVoice), to: backgroundURL)
                try checkCancelled()
                let ranges = DraftVoiceOverWriter.duckedRanges(voiceOver[audible].lines)
                let url = folder.appendingPathComponent("background.wav")
                try Self.writeBackground(from: backgroundURL, ranges: ranges, duration: duration, to: url)
                let lines = ranges.map {
                    max(0, Double($0.lowerBound - DraftVoiceOverWriter.fadeDown) / 1_000_000)
                        ... min(duration, Double($0.upperBound + DraftVoiceOverWriter.fadeUp) / 1_000_000)
                }
                voiceOver.append(VoiceOverTrack(language: voiceOver[audible].language, url: url, fileDuration: duration,
                                                lines: lines, isAudible: true, isBackground: true))
            }
        }
        report("Done", 1)
        return Output(captions: tracks, voiceOver: voiceOver)
    }

    /// `source` (timeline-aligned, 48 kHz) with `DraftVoiceOverWriter.backgroundGain` applied, as 16-bit stereo WAV.
    static func writeBackground(from source: URL, ranges: [Range<Int64>], duration: Double, to url: URL) throws {
        let input = try AVAudioFile(forReading: source)
        let format = input.processingFormat
        try? FileManager.default.removeItem(at: url)
        let output = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: format.sampleRate,
            AVNumberOfChannelsKey: format.channelCount, AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
        let chunk: AVAudioFrameCount = 65_536
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: chunk)!
        let total = AVAudioFramePosition(duration * format.sampleRate)
        var position: AVAudioFramePosition = 0
        // The ranges are sorted and their ramps don't meet (`duckedRanges` merges those): one range matters at a time.
        var next = 0
        while position < total, input.framePosition < input.length {
            try input.read(into: buffer, frameCount: AVAudioFrameCount(min(AVAudioFramePosition(chunk), total - position)))
            guard buffer.frameLength > 0 else { break }
            for f in 0..<Int(buffer.frameLength) {
                let t = Int64(Double(position + AVAudioFramePosition(f)) / format.sampleRate * 1_000_000)
                while next < ranges.count && t >= ranges[next].upperBound + DraftVoiceOverWriter.fadeUp { next += 1 }
                let gain = next < ranges.count ? Float(DraftVoiceOverWriter.backgroundGain(at: t, range: ranges[next])) : 0
                for c in 0..<Int(format.channelCount) { buffer.floatChannelData![c][f] *= gain }
            }
            try output.write(from: buffer)
            position += AVAudioFramePosition(buffer.frameLength)
        }
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
