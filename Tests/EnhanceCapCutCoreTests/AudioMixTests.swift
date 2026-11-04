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

/// Issue #55: AVFoundation's offline mixer deadlocks on scaled composition tracks and on many tracks at once, so
/// speed changes are rendered by ClipSpeedRenderer and big timelines are mixed in groups of tracks.
final class AudioMixDeadlockTests: XCTestCase {
    private func toneFile(_ dir: URL, hz: Double, seconds: Double = 4) throws -> URL {
        let rate = VoiceProcessor.sampleRate
        let tone = (0..<Int(rate * seconds)).map { Float(sin(2 * Double.pi * hz * Double($0) / rate)) / 4 }
        let url = dir.appendingPathComponent("tone\(Int(hz)).caf")
        try VoiceSignal.write(tone, to: url)
        return url
    }

    /// Frequency from upward zero crossings.
    private func frequency(_ x: ArraySlice<Float>) -> Double {
        var crossings = 0
        for i in x.indices.dropFirst() where x[i - 1] < 0 && x[i] >= 0 { crossings += 1 }
        return Double(crossings) / (Double(x.count) / VoiceProcessor.sampleRate)
    }

    private func render(_ clips: [AudioClip], duration: Int64, tracksPerMix: Int = AudioRenderer.maxTracksPerMix,
                        in dir: URL, name: String) async throws -> [Float] {
        let timeline = Timeline(name: name, canvasSize: CGSize(width: 16, height: 16), duration: duration, fps: 30,
                                layers: [], audioClips: clips, report: RenderReport())
        let url = dir.appendingPathComponent("\(name).caf")
        try await AudioRenderer.render(timeline, to: url, tracksPerMix: tracksPerMix, isCancelled: { false },
                                       progress: { _ in })
        return try VoiceSignal.read(url)
    }

    /// A 2× clip with "pitch" on (varispeed) hung the old mixer every time; now it plays an octave up.
    func testSpedUpClipsRenderWithAndWithoutPitch() async throws {
        let dir = try VoiceSignal.tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let tone = try toneFile(dir, hz: 300)
        let rate = Int(VoiceProcessor.sampleRate)
        for preservePitch in [false, true] {
            let clip = AudioClip(url: tone, target: TimeRange(start: 0, duration: 1_000_000),
                                 timeMap: TimeMap(sourceStart: 0, sourceDuration: 2_000_000, targetDuration: 1_000_000),
                                 volume: 1, fadeIn: 0, fadeOut: 0, preservePitch: preservePitch)
            let mixed = try await render([clip], duration: 1_500_000, in: dir, name: "speed\(preservePitch)")
            let body = mixed[(rate / 5)..<(rate * 4 / 5)]
            XCTAssertEqual(frequency(body), preservePitch ? 300 : 600, accuracy: 6, "preservePitch \(preservePitch)")
            XCTAssertEqual(Double(VoiceSignal.rms(body)), 0.25 / 2.squareRoot(), accuracy: 0.03)
            // Silence after the clip.
            XCTAssertLessThan(VoiceSignal.rms(mixed[(rate * 11 / 10)..<(rate * 3 / 2)]), 1e-3)
        }
    }

    /// Mixing in groups of tracks gives the same sum, whatever the group size.
    func testGroupedMixesAgree() async throws {
        let dir = try VoiceSignal.tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let tone = try toneFile(dir, hz: 220)
        var clips: [AudioClip] = []
        for i in 0..<40 {
            let sourceDuration: Int64 = i == 7 ? 2_000_000 : 1_000_000
            clips.append(AudioClip(url: tone, target: TimeRange(start: Int64(i) * 50_000, duration: 1_000_000),
                                   timeMap: TimeMap(sourceStart: Int64(i % 5) * 100_000, sourceDuration: sourceDuration,
                                                    targetDuration: 1_000_000),
                                   volume: 0.05 + Double(i % 3) * 0.02, fadeIn: i % 4 == 0 ? 200_000 : 0,
                                   fadeOut: i % 6 == 0 ? 300_000 : 0, preservePitch: i % 2 == 0))
        }
        let a = try await render(clips, duration: 3_500_000, tracksPerMix: 6, in: dir, name: "groups6")
        let b = try await render(clips, duration: 3_500_000, tracksPerMix: 16, in: dir, name: "groups16")
        XCTAssertEqual(a.count, b.count)
        XCTAssertGreaterThan(VoiceSignal.rms(a[...]), 0.05)
        // Within −40 dB of the mix (sample rounding where groups start and end).
        XCTAssertLessThan(VoiceSignal.rms(zip(a, b).map { $0 - $1 }[...]), VoiceSignal.rms(a[...]) / 100)
        // The temporary sums are gone.
        let left = try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.hasPrefix("mix-") }
        XCTAssertEqual(left, [])
    }
}
