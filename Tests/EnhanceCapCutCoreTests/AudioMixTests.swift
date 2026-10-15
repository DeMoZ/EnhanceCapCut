import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

/// Mixer levels: back-to-back clips of one tone with different volumes share a lane.
final class AudioMixTests: XCTestCase {
    func testPerClipVolumesOnASharedLane() async throws {
        let dir = try VoiceSignal.tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        // 10 s of a 440 Hz tone at amplitude 1/8 (−21 dB RMS).
        let rate = VoiceProcessor.sampleRate
        let tone = (0..<Int(rate * 10)).map { Float(sin(2 * Double.pi * 440 * Double($0) / rate)) / 8 }
        let toneURL = dir.appendingPathComponent("tone.caf")
        try VoiceSignal.write(tone, to: toneURL)

        let volumes = [5, 0.5, 8, 0.5, 1, 2]
        let length: Int64 = 1_500_000
        let clips = volumes.enumerated().map { i, volume in
            AudioClip(url: toneURL, target: TimeRange(start: Int64(i) * length, duration: length),
                      timeMap: TimeMap(sourceStart: Int64(i) * length, sourceDuration: length, targetDuration: length),
                      volume: volume, fadeIn: 0, fadeOut: 0, preservePitch: true)
        }
        let timeline = Timeline(name: "tone", canvasSize: CGSize(width: 16, height: 16),
                                duration: Int64(volumes.count) * length, fps: 30, layers: [], audioClips: clips,
                                report: RenderReport())
        let out = dir.appendingPathComponent("mix.caf")
        try await AudioRenderer.render(timeline, to: out, isCancelled: { false }, progress: { _ in })
        let mixed = try VoiceSignal.read(out)

        let toneDB = 20 * log10(1 / (8 * sqrt(2.0)))
        let ceilingDB = 20 * log10(sqrt(0.5)) - 0.2  // a sine limited to the −0.2 dBFS peak ceiling
        let frames = Int(Double(length) / 1_000_000 * rate)
        for (i, volume) in volumes.enumerated() {
            // The middle of each clip, away from the limiter's attack and release.
            let slice = mixed[(i * frames + frames / 4)..<(i * frames + frames * 3 / 4)]
            let measured = 20 * log10(Double(VoiceSignal.rms(slice)))
            let expected = min(toneDB + 20 * log10(volume), ceilingDB)
            XCTAssertEqual(measured, expected, accuracy: 0.3, "clip \(i) at volume \(volume)")
        }
    }
}
