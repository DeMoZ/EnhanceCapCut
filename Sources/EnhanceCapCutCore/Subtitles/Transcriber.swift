import AVFoundation
import Speech

/// A recognised word (or punctuation-attached token) with its position in the audio.
public struct TimedWord {
    public var text: String
    public var start: Double
    public var end: Double

    public init(text: String, start: Double, end: Double) {
        self.text = text
        self.start = start
        self.end = end
    }
}

public enum TranscriberError: LocalizedError {
    case unsupportedLocale(String)
    case nothingRecognised
    case modelFailed(String)

    public var errorDescription: String? {
        switch self {
        case .unsupportedLocale(let l): return "macOS can't transcribe \(l) speech."
        case .nothingRecognised: return "No speech recognized. Check the spoken language and audio level."
        case .modelFailed(let path): return "Whisper couldn't run the model at \(path). Delete it to download it again."
        }
    }
}

/// On-device speech recognition via SpeechAnalyzer (macOS 26+).
/// Uses SpeechTranscriber when the locale is supported (best for long-form audio),
/// otherwise falls back to DictationTranscriber, which covers more languages (Russian, Thai, …).
/// Dictation is made for short utterances: given a whole noisy timeline it returns a single result and drops
/// most of the words, so its audio goes in as short pieces cut at pauses (`SpeechChunker`).
/// Ported from SubDub, the Final Cut Pro version.
@available(macOS 26, *)
public enum Transcriber {
    /// All locales that can be used as the source language.
    public static func supportedLocales() async -> [Locale] {
        var seen = Set<String>()
        var result: [Locale] = []
        for l in await SpeechTranscriber.supportedLocales + DictationTranscriber.supportedLocales {
            let id = l.identifier(.bcp47)
            if seen.insert(id).inserted { result.append(l) }
        }
        return result
    }

    public static func transcribe(fileURL: URL, locale: Locale, duration: Double,
                           status: @escaping (String) -> Void,
                           progress: @escaping (Double) -> Void) async throws -> [TimedWord] {
        let speechLocale = await SpeechTranscriber.supportedLocale(equivalentTo: locale)
        let dictationLocale = await DictationTranscriber.supportedLocale(equivalentTo: locale)

        // A fresh module per analyzer run; modules aren't shared between analyzers.
        let makeModule: () -> any SpeechModule
        if let l = speechLocale {
            makeModule = { SpeechTranscriber(locale: l, transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange]) }
        } else if let l = dictationLocale {
            makeModule = { DictationTranscriber(locale: l, contentHints: [], transcriptionOptions: [.punctuation],
                                                reportingOptions: [], attributeOptions: [.audioTimeRange]) }
        } else {
            throw TranscriberError.unsupportedLocale(locale.identifier)
        }

        let module = makeModule()
        if await AssetInventory.status(forModules: [module]) != .installed {
            status("Downloading speech model (one time)…")
            _ = try? await AssetInventory.reserve(locale: speechLocale ?? dictationLocale!)
            if let request = try await AssetInventory.assetInstallationRequest(supporting: [module]) {
                try await request.downloadAndInstall()
            }
        }
        status("Transcribing…")

        var words: [TimedWord] = []
        if speechLocale != nil {
            words = try await analyze(AVAudioFile(forReading: fileURL), module: module, offset: 0) { end in
                if duration > 0 { progress(min(1, end / duration)) }
            }
        } else {
            let work = FileManager.default.temporaryDirectory.appendingPathComponent("EnhanceCapCut-speech-\(UUID().uuidString)")
            try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: work) }
            let pieces = try SpeechChunker.split(fileURL, into: work)
            for piece in pieces {
                // Per channel for wide stereo: the version with the most words wins (dictation drops words
                // rather than inventing them), then the one with more letters.
                var best: [TimedWord] = []
                for url in piece.urls {
                    try Task.checkCancellation()
                    let found = try await analyze(AVAudioFile(forReading: url), module: makeModule(), offset: piece.start) { _ in }
                    let letters = { (w: [TimedWord]) in w.reduce(0) { $0 + $1.text.count } }
                    if (found.count, letters(found)) > (best.count, letters(best)) { best = found }
                }
                words += best
                if duration > 0 { progress(min(1, piece.end / duration)) }
            }
        }
        if ProcessInfo.processInfo.environment["ECC_DEBUG_WORDS"]?.isEmpty == false {
            for w in words { FileHandle.standardError.write(String(format: "%.2f-%.2f %@\n", w.start, w.end, w.text).data(using: .utf8)!) }
        }
        progress(1)
        guard !words.isEmpty else { throw TranscriberError.nothingRecognised }
        return words
    }

    /// Runs one analyzer over `file`; word times are shifted by `offset` seconds.
    private static func analyze(_ file: AVAudioFile, module: any SpeechModule, offset: Double,
                                reached: @escaping (Double) -> Void) async throws -> [TimedWord] {
        let analyzer = SpeechAnalyzer(modules: [module])
        let collector = Task { () -> [TimedWord] in
            var words: [TimedWord] = []
            func add(_ text: AttributedString, _ range: CMTimeRange) {
                words += Self.words(from: text, fallback: range).map {
                    TimedWord(text: $0.text, start: $0.start + offset, end: $0.end + offset)
                }
                reached(offset + range.end.seconds)
            }
            if let speech = module as? SpeechTranscriber {
                for try await r in speech.results { add(r.text, r.range) }
            } else if let dictation = module as? DictationTranscriber {
                for try await r in dictation.results { add(r.text, r.range) }
            }
            return words
        }
        do {
            if let last = try await analyzer.analyzeSequence(from: file) {
                try await analyzer.finalizeAndFinish(through: last)
            } else {
                await analyzer.cancelAndFinishNow()
            }
        } catch {
            collector.cancel()
            throw error
        }
        return try await collector.value
    }

    /// Splits a result into word-level tokens using the per-run audio time ranges.
    private static func words(from text: AttributedString, fallback: CMTimeRange) -> [TimedWord] {
        var out: [TimedWord] = []
        for run in text.runs {
            let piece = String(text[run.range].characters)
            let range = run[AttributeScopes.SpeechAttributes.TimeRangeAttribute.self]
            let trimmed = piece.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { continue }

            if let range, range.duration.seconds > 0 || out.isEmpty {
                // A run may contain several space-separated tokens; split time evenly.
                let tokens = trimmed.split(whereSeparator: \.isWhitespace).map(String.init)
                let step = range.duration.seconds / Double(tokens.count)
                for (i, t) in tokens.enumerated() {
                    let s = range.start.seconds + step * Double(i)
                    out.append(TimedWord(text: t, start: s, end: s + step))
                }
            } else if !out.isEmpty, !piece.first!.isWhitespace {
                // Untimed punctuation glued to the previous word.
                out[out.count - 1].text += trimmed
            } else {
                let s = out.last?.end ?? fallback.start.seconds
                out.append(TimedWord(text: trimmed, start: s, end: s))
            }
        }
        if out.isEmpty {
            let t = String(text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
            if !t.isEmpty { out.append(TimedWord(text: t, start: fallback.start.seconds, end: fallback.end.seconds)) }
        }
        return out
    }
}
