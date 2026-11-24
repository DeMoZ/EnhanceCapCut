import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

final class VoiceOverWriterTests: XCTestCase {
    private let project = URL(fileURLWithPath: "/Projects/Demo")
    private let placeholder = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##"

    /// A video clip over the whole 10 s, a music clip from 6 s, and one with volume keyframes of its own.
    private var draft: String {
        """
        {"id":"D","fps":30.0,"duration":10000000,"tracks":[\
        {"id":"V","type":"video","flag":0,"attribute":0,"segments":[{"id":"S1","material_id":"M1",\
        "target_timerange":{"start":0,"duration":10000000},"volume":0.5,"common_keyframes":[]}]},\
        {"id":"A","type":"audio","flag":0,"attribute":0,"segments":[{"id":"S2","material_id":"M2",\
        "target_timerange":{"start":6000000,"duration":4000000},"volume":1.0}]},\
        {"id":"B","type":"audio","flag":0,"attribute":0,"segments":[{"id":"S3","material_id":"M3",\
        "target_timerange":{"start":0,"duration":4000000},"volume":1.0,"common_keyframes":[{"id":"K",\
        "property_type":"KFTypeVolume","keyframe_list":[{"time_offset":0,"values":[1.0]}]}]}]}],\
        "materials":{"videos":[{"id":"M1","path":"\(placeholder)/clip.mp4"}],"audios":[],"speeds":[]}}
        """
    }

    private func tracks() -> [VoiceOverTrack] {
        let url = project.appendingPathComponent("EnhanceCapCut/voiceover 1/en.wav")
        return [
            VoiceOverTrack(language: SubtitleLanguage(Locale.Language(identifier: "en")), url: url, fileDuration: 10,
                           lines: [1...2.5, 3...4], isAudible: true),
            VoiceOverTrack(language: SubtitleLanguage(Locale.Language(identifier: "th")),
                           url: URL(fileURLWithPath: "/elsewhere/th.wav"), fileDuration: 10, lines: [1...2], isAudible: false),
        ]
    }

    private func json(_ data: Data) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func segments(_ d: [String: Any], track: Int) throws -> [[String: Any]] {
        let tracks = try XCTUnwrap(d["tracks"] as? [[String: Any]])
        return try XCTUnwrap(tracks[track]["segments"] as? [[String: Any]])
    }

    private func volumeKeyframes(_ segment: [String: Any]) -> [[String: Any]] {
        (segment["common_keyframes"] as? [[String: Any]] ?? []).filter { $0["property_type"] as? String == "KFTypeVolume" }
    }

    /// A video clip with its own (off) vocal separation, a compound clip, CapCut music and an extracted sound, all under
    /// the voice, and a clip after it.
    private var vocalDraft: String {
        """
        {"id":"D","duration":10000000,"tracks":[\
        {"type":"video","attribute":0,"segments":[{"id":"S1","material_id":"M1","extra_material_refs":["VS1"],\
        "target_timerange":{"start":0,"duration":5000000},"source_timerange":{"start":2000000,"duration":5000000},\
        "volume":1.0},{"id":"S2","material_id":"M1","extra_material_refs":[],\
        "target_timerange":{"start":6000000,"duration":4000000},"volume":1.0}]},\
        {"type":"video","attribute":0,"segments":[{"id":"S3","material_id":"M1","extra_material_refs":["C1"],\
        "target_timerange":{"start":0,"duration":5000000},"volume":1.0}]},\
        {"type":"audio","attribute":0,"segments":[{"id":"S4","material_id":"A1",\
        "target_timerange":{"start":0,"duration":5000000},"volume":1.0},{"id":"S5","material_id":"A2",\
        "target_timerange":{"start":5000000,"duration":1000000},"volume":1.0}]}],\
        "materials":{"videos":[{"id":"M1","type":"video","duration":20000000}],\
        "audios":[{"id":"A1","type":"music","duration":5000000},{"id":"A2","type":"extract_music","duration":1000000}],\
        "drafts":[{"id":"C1"}],"vocal_separations":[{"id":"VS1","type":"vocal_separation","choice":0}]}}
        """
    }

    private func separation(_ d: [String: Any], of segment: [String: Any]) throws -> [String: Any]? {
        let all = try XCTUnwrap((d["materials"] as? [String: Any])?["vocal_separations"] as? [[String: Any]])
        let refs = segment["extra_material_refs"] as? [String] ?? []
        return all.first { refs.contains($0["id"] as? String ?? "") }
    }

}

final class VolumeEnvelopeTests: XCTestCase {
    func testRendererFollowsTheEnvelope() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let rate = 48_000.0
        let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 1)!
        let sine = dir.appendingPathComponent("sine.caf")
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(rate * 3))!
        buffer.frameLength = buffer.frameCapacity
        for i in 0..<Int(buffer.frameLength) {
            buffer.floatChannelData![0][i] = Float(0.5 * sin(2 * Double.pi * 440 * Double(i) / rate))
        }
        try AVAudioFile(forWriting: sine, settings: format.settings).write(from: buffer)

        // Full level for 1 s, a 0.1 s ramp down to a quarter, back up at 2 s.
        let clip = audioClip(url: sine, target: TimeRange(start: 0, duration: 3_000_000),
                             timeMap: TimeMap.constant(sourceStart: 0, sourceDuration: 3_000_000, targetDuration: 3_000_000),
                             volume: 1, fadeIn: 0, fadeOut: 0, preservePitch: true,
                             volumeEnvelope: KeyframeTrack.linear(times: [900_000, 1_000_000, 2_000_000, 2_100_000],
                                                                  values: [1, 0.25, 0.25, 1]))
        let out = dir.appendingPathComponent("mix.caf")
        try await AudioRenderer.render([clip], duration: 3_000_000, to: out, isCancelled: { false }, progress: { _ in })

        let file = try AVAudioFile(forReading: out)
        let mixed = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length))!
        try file.read(into: mixed)
        func peak(_ from: Double, _ to: Double) -> Float {
            (Int(from * rate)..<Int(to * rate)).map { abs(mixed.floatChannelData![0][$0]) }.max() ?? 0
        }
        XCTAssertEqual(peak(0.2, 0.8), 0.5, accuracy: 0.03)
        XCTAssertEqual(peak(1.2, 1.8), 0.125, accuracy: 0.02)
        XCTAssertEqual(peak(2.2, 2.8), 0.5, accuracy: 0.03)
    }
}
