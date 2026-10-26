import AVFoundation

public enum VoiceGender: String, CaseIterable, Identifiable, Sendable {
    case female = "Female", male = "Male"
    public var id: String { rawValue }
}

/// A system voice that can speak a subtitle language.
public struct DubVoice: Identifiable, Hashable, Sendable {
    public let id: String          // AVSpeechSynthesisVoice.identifier, or `SiriVoices.idPrefix` + id
    public let name: String
    public let language: String    // BCP-47, e.g. "en-US"
    public let quality: AVSpeechSynthesisVoiceQuality
    public let gender: VoiceGender?

    public var label: String {
        let region = Locale(identifier: language).region.flatMap { Locale(identifier: "en").localizedString(forRegionCode: $0.identifier) }
        let q: String
        switch quality {
        case .premium: q = isSiri ? " · Siri" : " · Premium"
        case .enhanced: q = " · Enhanced"
        default: q = ""
        }
        return region.map { "\(name) (\($0))\(q)" } ?? "\(name)\(q)"
    }

    public var isSiri: Bool { id.hasPrefix(SiriVoices.idPrefix) }

    /// Siri voices first, then modern Apple voices, Eloquence (Eddy, Flo…), legacy MacinTalk (Fred, Ralph…).
    var tier: Int {
        if isSiri { return -1 }
        if id.contains(".eloquence.") { return 1 }
        if id.hasPrefix("com.apple.speech.synthesis.voice.") { return 2 }
        return 0
    }

    init(siriID: String, name: String, language: String, gender: VoiceGender?) {
        id = SiriVoices.idPrefix + siriID
        self.name = name
        self.language = language
        quality = .premium
        self.gender = gender
    }

    init(_ voice: AVSpeechSynthesisVoice) {
        id = voice.identifier
        name = voice.name
        language = voice.language
        quality = voice.quality
        switch voice.gender {
        case .female: gender = .female
        case .male: gender = .male
        default: gender = Self.knownGenders[voice.name]
        }
    }

    /// Voices that don't report a gender.
    private static let knownGenders: [String: VoiceGender] = [
        "Eddy": .male, "Reed": .male, "Rocko": .male, "Grandpa": .male,
        "Flo": .female, "Sandy": .female, "Shelley": .female, "Grandma": .female,
        "Albert": .male, "Fred": .male, "Junior": .male, "Ralph": .male, "Kathy": .female,
    ]
}

public enum DubberError: LocalizedError {
    case noVoice(String)

    public var errorDescription: String? {
        switch self {
        case .noVoice(let l): return "No system voice is installed for \(l). Add one in System Settings ▸ Accessibility ▸ Read & Speak (ⓘ next to System voice)."
        }
    }
}

/// Voice-over: speaks translated cues with on-device Apple voices and renders one audio file per
/// language that lines up with the timeline (file time 0 = timeline start). Ported from SubDub.
public enum Dubber {
    public static let sampleRate: Double = 48_000
    /// Fastest speed-up allowed to make a line fit its slot, relative to the default rate.
    static let maxSpeedUp = 1.35

    /// Voices for a subtitle language code ("en", "zh-Hans", "pt-BR", …), best quality first.
    public static func voices(for code: String) -> [DubVoice] {
        let wanted = Locale.Language(identifier: code)
        let all = AVSpeechSynthesisVoice.speechVoices().filter { v in
            let l = Locale.Language(identifier: v.language)
            guard l.languageCode == wanted.languageCode else { return false }
            if code == "zh-Hant" { return ["TW", "HK"].contains(l.region?.identifier ?? "") }
            if code == "zh-Hans" { return l.region?.identifier == "CN" }
            if code.contains("-"), let r = wanted.region { return l.region == r }
            return true
        }
        // Novelty voices (Bells, Bubbles, …) are not useful for dubbing.
        let usable = all.filter { !$0.voiceTraits.contains(.isNoveltyVoice) && !$0.voiceTraits.contains(.isPersonalVoice) }
        return usable
            .map(DubVoice.init)
            .sorted { a, b in
                if a.quality != b.quality { return a.quality.rawValue > b.quality.rawValue }
                if a.tier != b.tier { return a.tier < b.tier }
                return a.name < b.name
            }
    }

    /// Best voice for a language, preferring the requested gender when one is installed.
    public static func defaultVoice(for code: String, gender: VoiceGender? = nil) -> DubVoice? {
        let all = voices(for: code)
        let list = gender.map { g in all.filter { $0.gender == g } }.flatMap { $0.isEmpty ? nil : $0 } ?? all
        guard let best = list.first else { return nil }
        // Among the best-quality tier, prefer the user's region of that language, then the language's
        // main region (en → US, es → ES, …).
        let regions = [Locale.current.region?.identifier,
                       Locale.Language(identifier: Locale.Language(identifier: code).maximalIdentifier).region?.identifier]
        for region in regions.compactMap({ $0 }) {
            if let v = list.first(where: { $0.quality == best.quality && $0.tier == best.tier && Locale(identifier: $0.language).region?.identifier == region }) {
                return v
            }
        }
        return best
    }

    /// Speaks every cue and writes a 48 kHz mono WAV aligned to the timeline.
    /// - Returns: the speech ranges actually used (seconds), one per cue.
    public static func render(cues: [CaptionCue], voiceID: String?, languageCode: String, totalDuration: Double,
                              to url: URL, progress: @escaping (Double) -> Void) async throws -> [ClosedRange<Double>] {
        if let voiceID, voiceID.hasPrefix(SiriVoices.idPrefix) {
            return try await SiriVoices.render(cues: cues, voiceID: voiceID, totalDuration: totalDuration, to: url, progress: progress)
        }
        guard let voice = voiceID.flatMap(AVSpeechSynthesisVoice.init(identifier:)) ?? defaultVoice(for: languageCode).flatMap({ AVSpeechSynthesisVoice(identifier: $0.id) }) else {
            throw DubberError.noVoice(languageCode)
        }
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false)!
        try? FileManager.default.removeItem(at: url)
        let file = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)

        let synth = Synthesizer()
        var written: AVAudioFramePosition = 0
        var ranges: [ClosedRange<Double>] = []

        for (i, cue) in cues.enumerated() {
            try Task.checkCancellation()
            let text = cue.text.replacingOccurrences(of: "\n", with: " ")
            let slotEnd = i + 1 < cues.count ? cues[i + 1].start : max(cue.end, totalDuration)
            // Measure the slot from where this line can actually start, so lines catch up after an overrun.
            let actualStart = max(cue.start, Double(written) / sampleRate)
            let slot = max(0.3, slotEnd - actualStart)
            // The last line has nothing after it to spill into: allow a bit more speed-up there.
            let limit = i + 1 < cues.count ? maxSpeedUp : 1.6

            var speech = try await synth.speak(text, voice: voice, rate: AVSpeechUtteranceDefaultSpeechRate, format: format)
            if speech.duration > slot * 1.02 {
                // Too long for its slot. How `rate` maps to speed differs a lot between voices,
                // so binary-search the slowest rate whose measured length fits (or reaches the cap).
                let target = max(slot, speech.duration / limit)
                var lo = AVSpeechUtteranceDefaultSpeechRate, hi = Float(0.85)
                var fastest = speech
                for _ in 0..<5 {
                    let mid = (lo + hi) / 2
                    let attempt = try await synth.speak(text, voice: voice, rate: mid, format: format)
                    if attempt.duration > target * 1.02 {
                        lo = mid
                        if attempt.duration < fastest.duration { fastest = attempt }
                    } else {
                        hi = mid
                        fastest = attempt
                    }
                }
                speech = fastest
            }

            let startFrame = max(AVAudioFramePosition(cue.start * sampleRate), written)
            if startFrame > written { try writeSilence(startFrame - written, to: file, format: format) }
            try file.write(from: speech.buffer)
            written = startFrame + AVAudioFramePosition(speech.buffer.frameLength)
            ranges.append(Double(startFrame) / sampleRate ... Double(written) / sampleRate)
            progress(Double(i + 1) / Double(cues.count))
        }
        let total = AVAudioFramePosition(totalDuration * sampleRate)
        if total > written { try writeSilence(total - written, to: file, format: format) }
        return ranges
    }

    // MARK: Cloned voice

    /// Voice id for "the original speaker's voice, cloned" (`VoiceCloner`), in place of a system voice id.
    public static let clonedVoiceID = "clone"
    /// A cloned line is read back with Whisper and made again (another seed) when its character error rate against
    /// the text is above this: about one line in ten comes out garbled or cut short.
    static let maxLineError = 0.35
    static let cloneAttempts = 3
    /// The reference for a line starts at the line and is at least this long (the model uses its first 6 s).
    static let referenceSeconds = 4.0
    static let maxReferenceSeconds = 6.0

    /// Speaks every cue in the voice of the original speaker and writes a 48 kHz mono WAV aligned to the timeline.
    /// Each line's reference is the original speech at that line (`voice`: the timeline's isolated voice, 24 kHz
    /// mono), so its tone and emotion carry over. With `checker`, garbled lines are made again.
    /// - Returns: the speech ranges actually used (seconds), one per cue.
    public static func renderCloned(cues: [CaptionCue], language: String, voice: [Float], exaggeration: Float,
                                    cloner: VoiceCloner, checker: WhisperChecker?, totalDuration: Double, to url: URL,
                                    isCancelled: @escaping () -> Bool, progress: (Double) -> Void) throws -> [ClosedRange<Double>] {
        guard let code = VoiceCloner.modelLanguage(language) else { throw DubberError.noVoice(language) }
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false)!
        try? FileManager.default.removeItem(at: url)
        let file = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)

        var written: AVAudioFramePosition = 0
        var ranges: [ClosedRange<Double>] = []
        for (i, cue) in cues.enumerated() {
            if isCancelled() { throw CancellationError() }
            let text = cue.text.replacingOccurrences(of: "\n", with: " ")
            let slotEnd = i + 1 < cues.count ? cues[i + 1].start : max(cue.end, totalDuration)
            let actualStart = max(cue.start, Double(written) / sampleRate)
            let slot = max(0.3, slotEnd - actualStart)
            let limit = i + 1 < cues.count ? maxSpeedUp : 1.6

            let reference = try cloner.voice(from: referenceRange(for: cue, in: voice))
            var best: (speech: VoiceCloner.Speech, error: Double)?
            for attempt in 0..<cloneAttempts {
                let speech = try cloner.speak(text, language: code, voice: reference, exaggeration: exaggeration,
                                              seed: UInt64(i) &* 1000 &+ UInt64(attempt) &+ 1, isCancelled: isCancelled)
                var error = speech.finished ? 0 : 1.0
                if let checker, !speech.samples.isEmpty {
                    let heard = try checker.text(try MonoAudio.resample(speech.samples, from: VoiceCloner.sampleRate, to: 16_000),
                                                 language: code)
                    error = max(error, lineError(expected: text, heard: heard))
                }
                if best == nil || error < best!.error || (error == best!.error && speech.duration < best!.speech.duration) {
                    best = (speech, error)
                }
                if error <= maxLineError { break }
            }
            var samples = best?.speech.samples ?? []
            let duration = Double(samples.count) / VoiceCloner.sampleRate
            if duration > slot * 1.02 {
                samples = TimeStretch.stretch(samples, rate: min(limit, duration / slot), sampleRate: VoiceCloner.sampleRate)
            }
            samples = try MonoAudio.resample(samples, from: VoiceCloner.sampleRate, to: sampleRate)

            let startFrame = max(AVAudioFramePosition(cue.start * sampleRate), written)
            if startFrame > written { try writeSilence(startFrame - written, to: file, format: format) }
            if !samples.isEmpty, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count)) {
                buffer.frameLength = AVAudioFrameCount(samples.count)
                samples.withUnsafeBufferPointer { buffer.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
                try file.write(from: buffer)
            }
            written = startFrame + AVAudioFramePosition(samples.count)
            ranges.append(Double(startFrame) / sampleRate ... Double(written) / sampleRate)
            progress(Double(i + 1) / Double(cues.count))
        }
        let total = AVAudioFramePosition(totalDuration * sampleRate)
        if total > written { try writeSilence(total - written, to: file, format: format) }
        return ranges
    }

    /// The original speech a line is cloned from: from the line's start, at least `referenceSeconds` (taken from
    /// before the line near the end of the timeline), at most `maxReferenceSeconds`.
    static func referenceRange(for cue: CaptionCue, in voice: [Float]) -> [Float] {
        let rate = VoiceCloner.sampleRate
        let total = Double(voice.count) / rate
        var start = max(0, cue.start)
        let end = min(total, max(cue.end, start + referenceSeconds), start + maxReferenceSeconds)
        if end - start < referenceSeconds { start = max(0, end - referenceSeconds) }
        let a = min(voice.count, Int(start * rate)), b = min(voice.count, Int(end * rate))
        return Array(voice[a..<max(a, b)])
    }

    /// Character error rate of `heard` against `expected`: edit distance over letters and digits (lower case,
    /// punctuation and spacing ignored) divided by the expected length.
    static func lineError(expected: String, heard: String) -> Double {
        func normalized(_ s: String) -> [Character] {
            Array(s.lowercased().filter { $0.isLetter || $0.isNumber })
        }
        let a = normalized(expected), b = normalized(heard)
        guard !a.isEmpty else { return b.isEmpty ? 0 : 1 }
        var row = Array(0...b.count)
        for i in 1...a.count {
            var diagonal = row[0]
            row[0] = i
            for j in stride(from: 1, through: b.count, by: 1) {
                let above = row[j]
                row[j] = min(row[j] + 1, row[j - 1] + 1, diagonal + (a[i - 1] == b[j - 1] ? 0 : 1))
                diagonal = above
            }
        }
        return Double(row[b.count]) / Double(a.count)
    }

    private static func writeSilence(_ frames: AVAudioFramePosition, to file: AVAudioFile, format: AVAudioFormat) throws {
        var remaining = frames
        while remaining > 0 {
            let n = AVAudioFrameCount(min(remaining, 65_536))
            let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: n)!
            buf.frameLength = n
            memset(buf.floatChannelData![0], 0, Int(n) * MemoryLayout<Float>.size)
            try file.write(from: buf)
            remaining -= AVAudioFramePosition(n)
        }
    }
}

/// Wraps AVSpeechSynthesizer.write into async/await and converts output to one format.
private final class Synthesizer {
    struct Speech {
        var buffer: AVAudioPCMBuffer
        var duration: Double { Double(buffer.frameLength) / buffer.format.sampleRate }
    }

    private let synthesizer = AVSpeechSynthesizer()

    func speak(_ text: String, voice: AVSpeechSynthesisVoice, rate: Float, format: AVAudioFormat) async throws -> Speech {
        let utterance = AVSpeechUtterance(string: text)
        utterance.voice = voice
        utterance.rate = rate
        utterance.preUtteranceDelay = 0
        utterance.postUtteranceDelay = 0

        let pieces: [AVAudioPCMBuffer] = await withCheckedContinuation { continuation in
            var collected: [AVAudioPCMBuffer] = []
            var finished = false
            synthesizer.write(utterance) { buffer in
                guard !finished else { return }
                guard let pcm = buffer as? AVAudioPCMBuffer, pcm.frameLength > 0 else {
                    finished = true
                    continuation.resume(returning: collected)
                    return
                }
                collected.append(pcm)
            }
        }
        let merged = try convert(pieces, to: format)
        return Speech(buffer: trimSilence(merged))
    }

    private func convert(_ pieces: [AVAudioPCMBuffer], to format: AVAudioFormat) throws -> AVAudioPCMBuffer {
        guard let first = pieces.first else { return AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 1)! }
        let inFrames = pieces.reduce(0) { $0 + Int($1.frameLength) }
        let ratio = format.sampleRate / first.format.sampleRate
        let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(Double(inFrames) * ratio) + 4096)!
        guard let converter = AVAudioConverter(from: first.format, to: format) else { return out }

        var index = 0
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if index < pieces.count {
                status.pointee = .haveData
                defer { index += 1 }
                return pieces[index]
            }
            status.pointee = .endOfStream
            return nil
        }
        if let error { throw error }
        return out
    }

    /// Removes leading/trailing near-silence the synthesizer adds around speech.
    private func trimSilence(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer {
        guard let data = buffer.floatChannelData?[0], buffer.frameLength > 0 else { return buffer }
        let n = Int(buffer.frameLength)
        let threshold: Float = 0.003
        var start = 0
        while start < n && abs(data[start]) < threshold { start += 1 }
        var end = n
        while end > start && abs(data[end - 1]) < threshold { end -= 1 }
        // Keep a few milliseconds so words don't start abruptly.
        let pad = Int(buffer.format.sampleRate * 0.02)
        start = max(0, start - pad)
        end = min(n, end + pad)
        guard end > start, start > 0 || end < n else { return buffer }
        let out = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: AVAudioFrameCount(end - start))!
        out.frameLength = AVAudioFrameCount(end - start)
        memcpy(out.floatChannelData![0], data + start, (end - start) * MemoryLayout<Float>.size)
        return out
    }
}
