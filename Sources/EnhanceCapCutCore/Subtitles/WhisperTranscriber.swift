import AVFoundation
import whisper

/// Offline speech recognition with Whisper large-v3-turbo through whisper.cpp (Metal) — the Windows version's
/// `SpeechRecognizer`, ported: the same model, pieces, word building and DTW correction, so both versions give the
/// same words. Measured against CapCut's own captions on four Russian vlogs, it drops fewer phrases than Apple
/// dictation in noisy dialogue (word error 16% vs 31% on the hardest one) with the same word timing.
public enum WhisperTranscriber {
    /// A word ends this long after its last token's aligned time, unless the next word starts sooner.
    static let wordTail = 0.3
    /// DTW places a token this long after the word is heard: measured against CapCut's own word times on four vlogs
    /// (median +0.11…0.14 s), where Apple dictation's times were within ±0.07 s.
    static let dtwLag = 0.12
    static let sampleRate = 16_000.0

    public static func transcribe(fileURL: URL, locale: Locale,
                                  progress: @escaping (Double) -> Void) async throws -> [TimedWord] {
        let samples = try mono16k(fileURL)
        let modelPath = ModelStore.path(of: ModelStore.whisper).path
        let abort = AbortFlag()
        return try await withTaskCancellationHandler {
            try await Task.detached(priority: .userInitiated) {
                try recognise(samples, language: whisperLanguage(locale), modelPath: modelPath, abort: abort, progress: progress)
            }.value
        } onCancel: {
            abort.isSet = true
        }
    }

    /// Whisper's code for `locale`'s language ("ru", "zh", "yue", …).
    static func whisperLanguage(_ locale: Locale) -> String {
        let code = locale.language.languageCode?.identifier ?? "en"
        return whisper_lang_id(code) >= 0 ? code : String(code.prefix(2))
    }

    private static func recognise(_ samples: [Float], language: String, modelPath: String, abort: AbortFlag,
                                  progress: (Double) -> Void) throws -> [TimedWord] {
        whisper_log_set({ _, _, _ in }, nil)
        var contextParams = whisper_context_default_params()
        contextParams.use_gpu = true
        // DTW token times need it off (whisper.cpp leaves t_dtw unset with flash attention).
        contextParams.flash_attn = false
        contextParams.dtw_token_timestamps = true
        contextParams.dtw_aheads_preset = WHISPER_AHEADS_LARGE_V3_TURBO
        guard let context = whisper_init_from_file_with_params(modelPath, contextParams) else {
            throw TranscriberError.modelFailed(modelPath)
        }
        defer { whisper_free(context) }

        let languageName = strdup(language)
        defer { free(languageName) }
        var params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY)
        params.language = UnsafePointer(languageName)
        params.token_timestamps = true
        params.n_threads = Int32(max(1, ProcessInfo.processInfo.activeProcessorCount - 1))
        params.print_progress = false
        params.print_realtime = false
        params.print_timestamps = false
        params.abort_callback = { data in Unmanaged<AbortFlag>.fromOpaque(data!).takeUnretainedValue().isSet }
        params.abort_callback_user_data = Unmanaged.passUnretained(abort).toOpaque()

        var words: [TimedWord] = []
        // Pieces within Whisper's 30 s window (see SpeechChunker); times are shifted back to the recording's.
        for range in SpeechChunker.ranges(samples, sampleRate: sampleRate, lengths: .whisper) {
            let offset = Double(range.lowerBound) / sampleRate
            let status = samples[range].withUnsafeBufferPointer {
                whisper_full(context, params, $0.baseAddress, Int32($0.count))
            }
            if abort.isSet { throw CancellationError() }
            guard status == 0 else { throw TranscriberError.modelFailed(modelPath) }
            for segment in 0..<whisper_full_n_segments(context) {
                words += segmentWords(context, segment, offset: offset)
            }
            progress(Double(range.upperBound) / Double(samples.count))
        }
        words = withoutAnnotations(words)
        if ProcessInfo.processInfo.environment["ECC_DEBUG_WORDS"]?.isEmpty == false {
            for w in words { FileHandle.standardError.write(String(format: "%.2f-%.2f %@\n", w.start, w.end, w.text).data(using: .utf8)!) }
        }
        guard !words.isEmpty else { throw TranscriberError.nothingRecognised }
        return words
    }

    /// Words from tokens: a token that starts with a space starts a word; others (word pieces, punctuation) continue
    /// it. Times are the tokens' DTW positions (centiseconds), Whisper's own as a fallback.
    private static func segmentWords(_ context: OpaquePointer, _ segment: Int32, offset: Double) -> [TimedWord] {
        var pieces: [(text: String, start: Double, last: Double)] = []
        for token in 0..<whisper_full_n_tokens(context, segment) {
            let text = String(cString: whisper_full_get_token_text(context, segment, token))
            // Special tokens: [_BEG_], [_TT_123], <|endoftext|>, …
            if text.isEmpty || text.hasPrefix("[_") || text.hasPrefix("<|") { continue }
            let data = whisper_full_get_token_data(context, segment, token)
            let time = offset + (data.t_dtw >= 0 ? max(0, Double(data.t_dtw) / 100 - dtwLag) : Double(data.t0) / 100)
            if let last = pieces.last, !text.first!.isWhitespace {
                pieces[pieces.count - 1] = (last.text + text, last.start, max(last.last, time))
            } else if !text.trimmingCharacters(in: .whitespaces).isEmpty {
                pieces.append((text.trimmingCharacters(in: .whitespaces), time, time))
            }
        }
        let segmentEnd = offset + Double(whisper_full_get_segment_t1(context, segment)) / 100
        var words = pieces.enumerated().map { i, p in
            let next = i + 1 < pieces.count ? pieces[i + 1].start : segmentEnd
            return TimedWord(text: p.text, start: p.start, end: max(p.start, min(p.last + wordTail, max(next, p.last))))
        }
        // No usable tokens: the whole segment, split later with interpolated times.
        let text = String(cString: whisper_full_get_segment_text(context, segment)).trimmingCharacters(in: .whitespaces)
        if words.isEmpty, !text.isEmpty {
            words.append(TimedWord(text: text, start: offset + Double(whisper_full_get_segment_t0(context, segment)) / 100, end: segmentEnd))
        }
        return words
    }

    /// Without Whisper's sound annotations ("*смех*", "[музыка]", "(аплодисменты)", "♪"): over street noise or music
    /// it writes them where nobody speaks.
    static func withoutAnnotations(_ words: [TimedWord]) -> [TimedWord] {
        let pairs: [Character: Character] = ["*": "*", "[": "]", "(": ")", "♪": "♪"]
        var out: [TimedWord] = []
        var closer: Character?
        for word in words {
            let text = word.text.trimmingCharacters(in: CharacterSet(charactersIn: ".,!?…:;"))
            if let c = closer {
                if text.last == c { closer = nil }
                continue
            }
            if let first = text.first, let c = pairs[first] {
                // A lone "♪" or "*" is a whole annotation; otherwise it runs to the word with the closer.
                let closed = text.count == 1 ? first == c : text.last == c
                if !closed { closer = c }
                continue
            }
            out.append(word)
        }
        return out
    }

    /// The recording as 16 kHz mono (channels averaged, like the Windows version's `AudioInput`).
    static func mono16k(_ url: URL) throws -> [Float] { try MonoAudio.read(url, sampleRate: sampleRate) }
}

/// Whisper kept loaded to read back short pieces of speech: checks that a cloned line says what it should.
public final class WhisperChecker {
    private let context: OpaquePointer

    public init(modelPath: String = ModelStore.path(of: ModelStore.whisper).path) throws {
        whisper_log_set({ _, _, _ in }, nil)
        var contextParams = whisper_context_default_params()
        contextParams.use_gpu = true
        guard let context = whisper_init_from_file_with_params(modelPath, contextParams) else {
            throw TranscriberError.modelFailed(modelPath)
        }
        self.context = context
    }

    deinit { whisper_free(context) }

    /// The text of `samples` (16 kHz mono, under 30 s) in `language` (a Whisper code: "en", "ru", …).
    func text(_ samples: [Float], language: String) throws -> String {
        let languageName = strdup(language)
        defer { free(languageName) }
        var params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY)
        params.language = UnsafePointer(languageName)
        params.n_threads = Int32(max(1, ProcessInfo.processInfo.activeProcessorCount - 1))
        params.single_segment = true
        params.no_context = true
        params.print_progress = false
        params.print_realtime = false
        params.print_timestamps = false
        let status = samples.withUnsafeBufferPointer { whisper_full(context, params, $0.baseAddress, Int32($0.count)) }
        guard status == 0 else { throw TranscriberError.modelFailed("whisper") }
        return (0..<whisper_full_n_segments(context))
            .map { String(cString: whisper_full_get_segment_text(context, $0)) }
            .joined(separator: " ")
    }
}

/// Set from the cancelling task, read by whisper.cpp's abort callback.
private final class AbortFlag: @unchecked Sendable {
    var isSet = false
}
