import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

final class VoiceEffectsParsingTests: XCTestCase {
    func testTogglesAreParsedLenientlyAndCarriedOnClips() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-voice-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let media = dir.appendingPathComponent("a.caf")
        try VoiceSignal.write([0, 0, 0, 0], to: media)

        let json = """
        {
          "duration": 3000000,
          "materials": {
            "audios": [{"id": "A", "path": "\(media.path)", "type": "record"}],
            "vocal_separations": [
              {"id": "VS-KEEP", "type": "vocal_separation", "choice": 2},
              {"id": "VS-REMOVE", "type": "vocal_separation", "choice": "1"},
              {"id": "VS-OFF", "type": "vocal_separation", "choice": 0},
              {"id": "VS-ODD", "type": "vocal_separation", "choice": 7}
            ],
            "realtime_denoises": [
              {"id": "RD-ON", "is_denoise": true, "denoise_rate": 0.85},
              {"id": "RD-OFF", "is_denoise": false, "denoise_rate": 0.85},
              {"id": "RD-NORATE", "is_denoise": 1}
            ]
          },
          "tracks": [{"type": "audio", "segments": [
            {"material_id": "A", "extra_material_refs": ["VS-KEEP", "RD-ON"],
             "target_timerange": {"start": 0, "duration": 1000000}, "source_timerange": {"start": 0, "duration": 1000000}},
            {"material_id": "A", "extra_material_refs": ["VS-REMOVE", "RD-OFF"],
             "target_timerange": {"start": 1000000, "duration": 1000000}},
            {"material_id": "A", "extra_material_refs": ["VS-OFF", "RD-NORATE"],
             "target_timerange": {"start": 2000000, "duration": 500000}},
            {"material_id": "A", "extra_material_refs": ["VS-ODD"],
             "target_timerange": {"start": 2500000, "duration": 500000}}
          ]}]
        }
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        XCTAssertEqual(draft.materials.vocalSeparations.map(\.choice), [2, 1, 0, 7])
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        let voices = timeline.audioClips.sorted { $0.target.start < $1.target.start }.map(\.voice)
        XCTAssertEqual(voices, [
            VoiceEffects(separation: .keepVoice, denoise: 0.85),
            VoiceEffects(separation: .removeVoice),
            VoiceEffects(denoise: 0.85),
            VoiceEffects(),
        ])
        // Supported effects are no longer listed; an unknown separation mode is.
        let audioItems = timeline.report.unsupported.filter { $0.category == "Audio" }.map(\.name)
        XCTAssertEqual(audioItems, ["vocal separation mode 7"])
    }
}

final class SpectralProcessingTests: XCTestCase {
    func testUnityGainReconstructsInput() throws {
        let signal: [Float] = VoiceSignal.noise(count: 20_000, seed: 1).enumerated().map { i, x in
            0.3 * x + 0.5 * Float(sin(Double(i) * 0.05))
        }
        let out = try VoiceSignal.roundTrip(signal) { s in s.applyGain([Float](repeating: 1, count: SpectralStream.bins)) }
        XCTAssertEqual(out.count, signal.count)
        let difference: [Float] = zip(signal, out).map { a, b in a - b }
        let error = VoiceSignal.rms(difference[...])
        XCTAssertLessThan(error, 1e-4, "STFT analysis/resynthesis must be transparent and latency-compensated")
    }

    func testDenoiserRemovesStationaryNoiseAndKeepsTone() throws {
        let sr = VoiceProcessor.sampleRate
        let noise = VoiceSignal.noise(count: Int(sr * 3), seed: 2).map { $0 * 0.05 }
        // A tone that is present only in the second half, well above the noise.
        let signal = noise.enumerated().map { i, n in
            n + (i > noise.count / 2 ? 0.3 * Float(sin(2 * Double.pi * 1_000 * Double(i) / sr)) : 0)
        }
        let out = try VoiceSignal.process(signal) { input, output in
            try SpectralDenoiser.process(input, to: output, strength: 0.85, isCancelled: { false })
        }
        let quietIn = VoiceSignal.rms(signal[1_000..<60_000]), quietOut = VoiceSignal.rms(out[1_000..<60_000])
        XCTAssertLessThan(20 * log10(quietOut / quietIn), -12, "noise-only part attenuated")
        let toneIn = VoiceSignal.rms(signal[100_000..<140_000]), toneOut = VoiceSignal.rms(out[100_000..<140_000])
        XCTAssertGreaterThan(20 * log10(toneOut / toneIn), -1, "tone kept")
    }

    func testVocalRemoverMask() throws {
        let mix = VoiceSignal.noise(count: 30_000, seed: 3).map { $0 * 0.3 }
        // Voice estimate equal to the mix → nothing left; silent estimate → mix unchanged.
        let removed = try VoiceSignal.process(mix, voice: mix) { input, voice, output in
            try VocalRemover.process(mix: input, voice: voice, voiceStart: 0, to: output, isCancelled: { false })
        }
        XCTAssertLessThan(VoiceSignal.rms(removed[...]), VoiceSignal.rms(mix[...]) * 0.01)
        let kept = try VoiceSignal.process(mix, voice: [Float](repeating: 0, count: mix.count)) { input, voice, output in
            try VocalRemover.process(mix: input, voice: voice, voiceStart: 0, to: output, isCancelled: { false })
        }
        XCTAssertEqual(VoiceSignal.rms(kept[...]), VoiceSignal.rms(mix[...]), accuracy: 1e-4)
    }

    func testDelayMeasurementAndBlend() throws {
        let dir = try VoiceSignal.tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let signal = VoiceSignal.noise(count: 96_000, seed: 4).map { $0 * 0.2 }
        let delay = 1_234
        let a = dir.appendingPathComponent("a.caf"), b = dir.appendingPathComponent("b.caf")
        try VoiceSignal.write(signal, to: a)
        try VoiceSignal.write([Float](repeating: 0, count: delay) + signal, to: b)
        XCTAssertEqual(try VoiceProcessor.measureDelay(reference: a, delayed: b), delay)
        // Unrelated signals: no measurement.
        let c = dir.appendingPathComponent("c.caf")
        try VoiceSignal.write(VoiceSignal.noise(count: 96_000, seed: 5), to: c)
        XCTAssertNil(try VoiceProcessor.measureDelay(reference: a, delayed: c))

        // 0.85 × (delayed copy, re-aligned) + 0.15 × source = source.
        let out = dir.appendingPathComponent("out.caf")
        try VoiceProcessor.blend(source: a, voice: b, voiceStart: AVAudioFramePosition(delay), voiceAmount: 0.85,
                                 to: out, isCancelled: { false })
        let blended = try VoiceSignal.read(out)
        XCTAssertEqual(blended.count, signal.count)
        XCTAssertLessThan(zip(blended, signal).map { abs($0 - $1) }.max() ?? 1, 1e-5)
    }
}

/// End-to-end check of AUSoundIsolation with speech from the system voice (`say`) over noise.
final class VoiceIsolationTests: XCTestCase {
    func testKeepAndRemoveVoice() async throws {
        let dir = try VoiceSignal.tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let speechURL = dir.appendingPathComponent("speech.wav")
        let say = Process()
        say.executableURL = URL(fileURLWithPath: "/usr/bin/say")
        say.arguments = ["-o", speechURL.path, "--data-format=LEF32@48000",
                         "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs."]
        guard (try? say.run()) != nil else { throw XCTSkip("say is not available") }
        say.waitUntilExit()
        guard say.terminationStatus == 0, let speechFile = try? AVAudioFile(forReading: speechURL) else {
            throw XCTSkip("no system voice")
        }
        let buffer = AVAudioPCMBuffer(pcmFormat: speechFile.processingFormat, frameCapacity: AVAudioFrameCount(speechFile.length))!
        try speechFile.read(into: buffer)
        let speech = Array(UnsafeBufferPointer(start: buffer.floatChannelData![0], count: Int(buffer.frameLength))).map { $0 * 0.5 }
        // Broadband noise plus a low hum, ~10 dB under the speech.
        let noise = VoiceSignal.noise(count: speech.count, seed: 6).enumerated().map { i, n in
            0.03 * n + 0.05 * Float(sin(2 * Double.pi * 120 * Double(i) / VoiceProcessor.sampleRate))
        }
        let mixURL = dir.appendingPathComponent("mix.caf")
        try VoiceSignal.write(zip(speech, noise).map(+), to: mixURL)

        func render(_ effects: VoiceEffects) async throws -> [Float] {
            let out = dir.appendingPathComponent("out-\(UUID().uuidString).caf")
            try await VoiceEffectsRenderer.render(mixURL, effects: effects, to: out)
            return try VoiceSignal.read(out)
        }
        // Keep voice: the output follows the clean speech closely and in sync (latency compensated).
        let kept = try await render(VoiceEffects(separation: .keepVoice))
        let n = min(kept.count, speech.count)
        XCTAssertGreaterThan(VoiceSignal.correlation(Array(kept[..<n]), Array(speech[..<n])), 0.8)
        // Remove voice: little speech left, the noise bed stays.
        let removed = try await render(VoiceEffects(separation: .removeVoice))
        let m = min(removed.count, noise.count)
        XCTAssertLessThan(abs(VoiceSignal.correlation(Array(removed[..<m]), Array(speech[..<m]))), 0.3)
        XCTAssertGreaterThan(VoiceSignal.correlation(Array(removed[..<m]), Array(noise[..<m])), 0.6)
        // Reduce noise: speech stays, the bed drops to roughly 1 − 0.85.
        let denoised = try await render(VoiceEffects(denoise: 0.85))
        let d = min(denoised.count, speech.count)
        let residual = zip(denoised[..<d], speech[..<d]).map { $0 - $1 }
        XCTAssertLessThan(VoiceSignal.rms(residual[...]) / VoiceSignal.rms(noise[...]), 0.5)
    }
}

/// Mono test signals written as the processor's 48 kHz stereo files (both channels equal).
enum VoiceSignal {
    static func tempDir() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-voice-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    static func noise(count: Int, seed: UInt64) -> [Float] {
        var state = seed &* 6364136223846793005 &+ 1442695040888963407
        return (0..<count).map { _ in
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Float(Int64(bitPattern: state >> 11) % 2_000_001) / 1_000_000 - 1
        }
    }

    static func write(_ samples: [Float], to url: URL) throws {
        let file = try VoiceProcessor.write(url)
        let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(max(samples.count, 1)))!
        buffer.frameLength = AVAudioFrameCount(samples.count)
        for c in 0..<2 { samples.withUnsafeBufferPointer { memcpy(buffer.floatChannelData![c], $0.baseAddress!, samples.count * 4) } }
        try file.write(from: buffer)
        file.close()
    }

    static func read(_ url: URL) throws -> [Float] {
        let file = try VoiceProcessor.read(url)
        let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length))!
        try file.read(into: buffer)
        return Array(UnsafeBufferPointer(start: buffer.floatChannelData![0], count: Int(buffer.frameLength)))
    }

    static func roundTrip(_ samples: [Float], modify: @escaping (SpectralStream) -> Void) throws -> [Float] {
        try process(samples) { input, output in
            try SpectralFileProcessor.run(inputs: [input], startFrames: [0], frames: input.length, output: output,
                                          isCancelled: { false }, modify: modify)
        }
    }

    static func process(_ samples: [Float], _ body: (AVAudioFile, AVAudioFile) throws -> Void) throws -> [Float] {
        let dir = try tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let a = dir.appendingPathComponent("in.caf"), b = dir.appendingPathComponent("out.caf")
        try write(samples, to: a)
        try body(try VoiceProcessor.read(a), try VoiceProcessor.write(b))
        return try read(b)
    }

    static func process(_ samples: [Float], voice: [Float],
                        _ body: (AVAudioFile, AVAudioFile, AVAudioFile) throws -> Void) throws -> [Float] {
        let dir = try tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let a = dir.appendingPathComponent("in.caf"), v = dir.appendingPathComponent("voice.caf")
        let b = dir.appendingPathComponent("out.caf")
        try write(samples, to: a)
        try write(voice, to: v)
        try body(try VoiceProcessor.read(a), try VoiceProcessor.read(v), try VoiceProcessor.write(b))
        return try read(b)
    }

    static func rms(_ x: ArraySlice<Float>) -> Float {
        sqrt(x.map { $0 * $0 }.reduce(0, +) / Float(max(x.count, 1)))
    }

    static func correlation(_ a: [Float], _ b: [Float]) -> Float {
        var ab: Float = 0, aa: Float = 0, bb: Float = 0
        for (x, y) in zip(a, b) { ab += x * y; aa += x * x; bb += y * y }
        return ab / max(sqrt(aa * bb), 1e-20)
    }
}
