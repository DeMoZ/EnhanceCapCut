import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

final class ColorTests: XCTestCase {
    func testHexColors() {
        let c = TextRasterizer.hexColor("#FF000080")
        XCTAssertEqual(c.components?[0] ?? 0, 0, accuracy: 0.01)
        XCTAssertEqual(c.alpha, 1, accuracy: 0.01)
        let d = TextRasterizer.hexColor("#80FF0000")
        XCTAssertEqual(d.alpha, 128.0 / 255, accuracy: 0.01)
        XCTAssertEqual(d.components?[0] ?? 0, 1, accuracy: 0.01)
    }
}

final class AudioSpeedTests: XCTestCase {
    /// A 2x clip keeps its pitch unless "pitch" is toggled; both kinds can share one mix.
    func testPitchModeIsPerClip() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        // 4 s of 440 Hz.
        let rate = 48_000.0
        let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 1)!
        let sine = dir.appendingPathComponent("sine.caf")
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(rate * 4))!
        buffer.frameLength = buffer.frameCapacity
        for i in 0..<Int(buffer.frameLength) {
            buffer.floatChannelData![0][i] = Float(0.5 * sin(2 * Double.pi * 440 * Double(i) / rate))
        }
        try AVAudioFile(forWriting: sine, settings: format.settings).write(from: buffer)

        let map = TimeMap(sourceStart: 0, sourceDuration: 4_000_000, targetDuration: 2_000_000)
        let clips = [
            AudioClip(url: sine, target: TimeRange(start: 0, duration: 2_000_000), timeMap: map,
                      volume: 1, fadeIn: 0, fadeOut: 0, preservePitch: true),
            AudioClip(url: sine, target: TimeRange(start: 2_000_000, duration: 2_000_000), timeMap: map,
                      volume: 1, fadeIn: 0, fadeOut: 0, preservePitch: false),
        ]
        let timeline = Timeline(name: "t", canvasSize: CGSize(width: 2, height: 2), duration: 4_000_000, fps: 30,
                                layers: [], audioClips: clips, report: RenderReport())
        let out = dir.appendingPathComponent("mix.caf")
        try await AudioRenderer.render(timeline, to: out, isCancelled: { false }, progress: { _ in })

        let file = try AVAudioFile(forReading: out)
        let mixed = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length))!
        try file.read(into: mixed)
        func frequency(from start: Double, to end: Double) -> Double {
            let a = Int(start * rate), b = Int(end * rate)
            var crossings = 0
            var previous = mixed.floatChannelData![0][a]
            for i in (a + 1)..<b {
                let v = mixed.floatChannelData![0][i]
                if (previous < 0) != (v < 0) { crossings += 1 }
                previous = v
            }
            return Double(crossings) / 2 / (end - start)
        }
        XCTAssertEqual(frequency(from: 0.5, to: 1.5), 440, accuracy: 20)
        XCTAssertEqual(frequency(from: 2.5, to: 3.5), 880, accuracy: 30)
    }
}
