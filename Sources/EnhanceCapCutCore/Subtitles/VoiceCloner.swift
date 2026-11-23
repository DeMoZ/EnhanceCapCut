import Foundation

public enum VoiceClonerError: LocalizedError {
    case badModel(String)
    case runFailed(String)
    /// This Mac can't run it (`VoiceCloner.unavailableReason`).
    case unavailable(String)

    public var errorDescription: String? {
        switch self {
        case .badModel(let file): return L("The voice cloning model is damaged ({0}). Delete its folder in the models folder and try again.", file)
        case .runFailed(let what): return L("Voice cloning failed: {0}", what)
        case .unavailable(let reason): return reason
        }
    }
}

/// Speaks text in the voice of a short reference recording, keeping its manner (tone, energy, emotion): Chatterbox
/// Multilingual V3 by Resemble AI (MIT), run with ONNX Runtime (`OnnxRuntime`, Apple silicon only) from the
/// four-graph export in `ModelStore.voiceCloning`. The Windows version's `VoiceCloner` is the same code in C#.
///
/// 1. `speech_encoder`: the reference (24 kHz, first 6 s) → conditioning for the language model, its speech tokens,
///    a speaker embedding and mel features.
/// 2. `embed_tokens` + `language_model`: text tokens → speech tokens (25 per second), sampled one at a time with a
///    KV cache until the stop token.
/// 3. `conditional_decoder`: reference tokens + new tokens → 24 kHz waveform of the new tokens only.
public final class VoiceCloner {
    public static let sampleRate = 24_000.0

    /// Languages the model speaks well, as Chatterbox codes. Chinese, Japanese and Hebrew are left out: the export
    /// lacks their text front ends (Cangjie, kana, diacritics), so they come out garbled.
    static let languages: Set<String> = ["ar", "da", "de", "el", "en", "es", "fi", "fr", "hi", "it", "ko", "ms", "nl",
                                         "no", "pl", "pt", "ru", "sv", "sw", "tr"]

    /// The model's code for a subtitle language code ("pt-BR" → "pt", "nb" → "no"), nil when it can't speak it.
    public static func modelLanguage(_ code: String) -> String? {
        var base = String(code.split(separator: "-").first ?? "").lowercased()
        if base == "nb" || base == "nn" { base = "no" }
        return languages.contains(base) ? base : nil
    }

    /// Sampling settings, as in Resemble AI's own multilingual pipeline except the repetition penalty: 1.2 (the
    /// ONNX export's choice) — 2.0 made lines run on past their end without classifier-free guidance.
    static let temperature: Float = 0.8
    static let minP: Float = 0.05
    static let repetitionPenalty: Float = 1.2

    static let startSpeech = 6561
    static let stopSpeech = 6562
    /// The decoder's output for the last token before the stop token is noise (upstream V3 drops it too).
    static let samplesPerToken = 960
    static let tokensPerSecond = 25.0

    private static let layers = 30, kvHeads = 16, headDim = 64, hidden = 1024

    /// Why voice cloning can't run on this Mac, nil when it can: ONNX Runtime comes for Apple silicon only.
    public static var unavailableReason: String? {
        #if arch(arm64)
        return OnnxRuntime.shared == nil ? L("Voice cloning needs ONNX Runtime, which this build of the app doesn't include.") : nil
        #else
        return L("Voice cloning needs a Mac with Apple silicon.")
        #endif
    }

    public static var isAvailable: Bool { unavailableReason == nil }

    /// Throws `unavailable` when this Mac can't clone voices: check before downloading the model.
    public static func checkAvailable() throws {
        if let reason = unavailableReason { throw VoiceClonerError.unavailable(reason) }
    }

    private let runtime: OnnxRuntime
    private let encoder: OnnxSession
    private let embedder: OnnxSession
    private let languageModel: OnnxSession
    private let decoder: OnnxSession
    private let tokenizer: ChatterboxTokenizer
    private let presentNames: [String]

    /// A reference voice, encoded once and reused for every line.
    public struct Voice {
        fileprivate let conditioning: OnnxTensor
        fileprivate let promptTokens: [Int64]
        fileprivate let speakerEmbedding: OnnxTensor
        fileprivate let speakerFeatures: OnnxTensor
    }

    public struct Speech {
        /// 24 kHz mono, quiet ends trimmed.
        public var samples: [Float]
        /// False when generation hit the length cap instead of the stop token (the line likely went wrong).
        public var finished: Bool
        public var duration: Double { Double(samples.count) / VoiceCloner.sampleRate }
    }

    /// Loads the model from `folder` (see `ModelStore.voiceCloning`). Takes up to a minute: ONNX Runtime optimizes
    /// the graphs on load.
    public init(folder: URL = ModelStore.folder(of: ModelStore.voiceCloning)) throws {
        try Self.checkAvailable()
        guard let runtime = OnnxRuntime.shared else { throw VoiceClonerError.runFailed("ONNX Runtime didn't start") }
        self.runtime = runtime
        func session(_ name: String) throws -> OnnxSession {
            let path = folder.appendingPathComponent(name).path
            guard FileManager.default.fileExists(atPath: path) else { throw VoiceClonerError.badModel(name) }
            do {
                return try runtime.session(path: path)
            } catch {
                throw VoiceClonerError.badModel(name)
            }
        }
        encoder = try session("onnx/speech_encoder.onnx")
        embedder = try session("onnx/embed_tokens.onnx")
        languageModel = try session("onnx/language_model_q4.onnx")
        decoder = try session("onnx/conditional_decoder.onnx")
        tokenizer = try ChatterboxTokenizer(url: folder.appendingPathComponent("tokenizer.json"))
        presentNames = try languageModel.outputNames().filter { $0.hasPrefix("present.") }
    }

    /// Encodes a reference recording: 24 kHz mono, the model uses its first 6 s.
    public func voice(from reference: [Float]) throws -> Voice {
        var samples = reference.isEmpty ? [Float](repeating: 0, count: Int(Self.sampleRate)) : reference
        // The model expects a normal speaking level.
        let peak = samples.reduce(0) { max($0, abs($1)) }
        if peak > 1e-4 { samples = samples.map { $0 * (0.7 / peak) } }
        let out = try run(encoder, ["audio_values": tensor(samples, [1, samples.count])],
                          ["audio_features", "audio_tokens", "speaker_embeddings", "speaker_features"])
        return Voice(conditioning: out["audio_features"]!, promptTokens: try ints(out["audio_tokens"]!),
                     speakerEmbedding: out["speaker_embeddings"]!, speakerFeatures: out["speaker_features"]!)
    }

    /// Speaks `text` in `voice`. `language` is a Chatterbox code (`modelLanguage`); `exaggeration` 0…1 scales how
    /// expressive the delivery is (0.5 = like the reference). The same seed gives the same tokens.
    public func speak(_ text: String, language: String, voice: Voice, exaggeration: Float = 0.5, seed: UInt64,
                      isCancelled: () -> Bool = { false }) throws -> Speech {
        let prepared = Self.prepare(text)
        let ids = tokenizer.encode("[\(language)]" + prepared).map(Int64.init)
        // Text positions count from the first text token; the special speech tokens sit at position 0.
        let positions = ids.enumerated().map { i, id in id >= Int64(Self.startSpeech) ? 0 : Int64(i) - 1 }
        var random = SplitMix64(seed: seed)
        let cap = Self.tokenCap(for: prepared)

        var embeds = try run(embedder, [
            "input_ids": tensor(ids, [1, ids.count]),
            "position_ids": tensor(positions, [1, positions.count]),
            "exaggeration": tensor([exaggeration], [1]),
        ], ["inputs_embeds"])["inputs_embeds"]!
        // The first step sees the reference conditioning, then the text.
        embeds = try concatenate(voice.conditioning, embeds)
        var length = try shape(embeds)[1]
        var cache: [String: OnnxTensor] = [:]
        let empty = try tensor([Float](), [1, Self.kvHeads, 0, Self.headDim])
        for layer in 0..<Self.layers {
            cache["past_key_values.\(layer).key"] = empty
            cache["past_key_values.\(layer).value"] = empty
        }

        var generated: [Int] = [Self.startSpeech]
        var finished = false
        for step in 0..<cap {
            if isCancelled() { throw CancellationError() }
            var inputs = cache
            inputs["inputs_embeds"] = embeds
            inputs["attention_mask"] = try tensor([Int64](repeating: 1, count: length), [1, length])
            let out = try run(languageModel, inputs, Set(["logits"] + presentNames))
            let logits = try lastRow(out["logits"]!)
            let next = Self.sample(logits, previous: generated, random: &random)
            generated.append(next)
            if next == Self.stopSpeech { finished = true; break }
            for name in presentNames { cache[name.replacingOccurrences(of: "present.", with: "past_key_values.")] = out[name] }
            embeds = try run(embedder, [
                "input_ids": tensor([Int64(next)], [1, 1]),
                "position_ids": tensor([Int64(step + 1)], [1, 1]),
                "exaggeration": tensor([exaggeration], [1]),
            ], ["inputs_embeds"])["inputs_embeds"]!
            length += 1
        }

        let speech = generated.dropFirst().filter { $0 < Self.startSpeech }.map(Int64.init)
        guard !speech.isEmpty else { return Speech(samples: [], finished: finished) }
        let tokens = voice.promptTokens + speech
        let wave = try run(decoder, [
            "speech_tokens": tensor(tokens, [1, tokens.count]),
            "speaker_embeddings": voice.speakerEmbedding,
            "speaker_features": voice.speakerFeatures,
        ], ["waveform"])["waveform"]!
        var samples = try floats(wave)
        samples.removeLast(min(samples.count, Self.samplesPerToken))
        return Speech(samples: Self.trimQuietEnds(samples), finished: finished)
    }

    // MARK: Text

    /// Resemble AI's punctuation clean-up: one space between words, a capital first letter, unusual punctuation
    /// replaced, a full stop added when the line has no ending punctuation.
    static func prepare(_ text: String) -> String {
        var t = text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        guard !t.isEmpty else { return "." }
        if let first = t.first, first.isLowercase { t = first.uppercased() + t.dropFirst() }
        for (old, new) in [("...", ", "), ("…", ", "), (":", ","), (" - ", ", "), (";", ", "), ("—", "-"), ("–", "-"),
                           (" ,", ","), ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"), ("«", "\""), ("»", "\"")] {
            t = t.replacingOccurrences(of: old, with: new)
        }
        while t.hasSuffix(" ") { t.removeLast() }
        if !t.hasSuffix(".") && !t.hasSuffix("!") && !t.hasSuffix("?") && !t.hasSuffix("-") && !t.hasSuffix(",") { t += "." }
        return t
    }

    /// Most tokens a line may take: room for very slow speech (5 characters a second) plus 2 s, at most 40 s.
    static func tokenCap(for text: String) -> Int {
        min(1000, Int(tokensPerSecond * (2 + Double(text.count) / 5)))
    }

    // MARK: Sampling

    /// Repetition penalty, temperature, min-p filter, then a draw from what's left.
    static func sample(_ logits: [Float], previous: [Int], random: inout SplitMix64) -> Int {
        var scores = logits
        for token in Set(previous) where token < scores.count {
            scores[token] = scores[token] < 0 ? scores[token] * repetitionPenalty : scores[token] / repetitionPenalty
        }
        let top = scores.max() ?? 0
        var weights = scores.map { exp(Double(($0 - top) / temperature)) }
        // min-p: drop tokens less than 5% as likely as the best one.
        let threshold = Double(minP)
        for i in weights.indices where weights[i] < threshold { weights[i] = 0 }
        let total = weights.reduce(0, +)
        var target = random.nextUnit() * total
        for (i, w) in weights.enumerated() where w > 0 {
            target -= w
            if target <= 0 { return i }
        }
        return weights.lastIndex { $0 > 0 } ?? 0
    }

    // MARK: Audio

    /// Cuts the near-silent start and end (frames 35 dB below the loudest 10 ms), keeping 40 ms around the speech.
    static func trimQuietEnds(_ samples: [Float]) -> [Float] {
        let frame = Int(sampleRate / 100)
        guard samples.count > frame else { return samples }
        var levels: [Float] = []
        var i = 0
        while i + frame <= samples.count {
            var sum: Float = 0
            for s in samples[i..<(i + frame)] { sum += s * s }
            levels.append((sum / Float(frame)).squareRoot())
            i += frame
        }
        let floor = (levels.max() ?? 0) * pow(10, -35 / 20)
        guard let first = levels.firstIndex(where: { $0 > floor }), let last = levels.lastIndex(where: { $0 > floor }) else {
            return []
        }
        let pad = Int(sampleRate * 0.04)
        return Array(samples[max(0, first * frame - pad)..<min(samples.count, (last + 1) * frame + pad)])
    }

    // MARK: ONNX Runtime helpers

    private func run(_ session: OnnxSession, _ inputs: [String: OnnxTensor], _ outputs: Set<String>) throws -> [String: OnnxTensor] {
        let out = try session.run(inputs, outputs)
        for name in outputs where out[name] == nil { throw VoiceClonerError.runFailed("no output \(name)") }
        return out
    }

    private func tensor<T>(_ values: [T], _ shape: [Int]) throws -> OnnxTensor {
        try OnnxTensor(runtime, values, shape: shape)
    }

    private func shape(_ value: OnnxTensor) throws -> [Int] { try value.shape().dims }

    private func floats(_ value: OnnxTensor) throws -> [Float] { try value.values(Float.self) }

    private func ints(_ value: OnnxTensor) throws -> [Int64] { try value.values(Int64.self) }

    /// The logits of the last position of a [1, n, vocabulary] tensor.
    private func lastRow(_ value: OnnxTensor) throws -> [Float] {
        try value.values(Float.self, last: shape(value)[2])
    }

    /// Two [1, n, hidden] tensors joined along n.
    private func concatenate(_ a: OnnxTensor, _ b: OnnxTensor) throws -> OnnxTensor {
        let joined = try floats(a) + floats(b)
        return try tensor(joined, [1, joined.count / Self.hidden, Self.hidden])
    }
}

/// A small seeded random generator (SplitMix64), the same in the Windows version.
struct SplitMix64 {
    private var state: UInt64

    init(seed: UInt64) { state = seed }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }

    /// Uniform in [0, 1).
    mutating func nextUnit() -> Double { Double(next() >> 11) / Double(1 << 53) }
}
