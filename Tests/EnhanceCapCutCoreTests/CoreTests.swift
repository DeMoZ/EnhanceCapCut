import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

final class TimeMapTests: XCTestCase {
    func testConstantSpeed() {
        let map = TimeMap(sourceStart: 1_000_000, sourceDuration: 4_000_000, targetDuration: 2_000_000)
        XCTAssertTrue(map.isConstant)
        XCTAssertEqual(map.averageSpeed, 2, accuracy: 1e-9)
        XCTAssertEqual(map.sourceTime(atOffset: 0), 1_000_000)
        XCTAssertEqual(map.sourceTime(atOffset: 1_000_000), 3_000_000)
        XCTAssertEqual(map.sourceTime(atOffset: 2_000_000), 5_000_000)
    }

    func testCurveCoversWholeSourceAndIsMonotonic() {
        // CapCut's "Flash in" preset.
        let points = [Point2(x: 0, y: 1), Point2(x: 0.19, y: 5.2), Point2(x: 0.78, y: 5.2),
                      Point2(x: 0.94, y: 1), Point2(x: 1, y: 1)]
        let map = TimeMap(sourceStart: 0, sourceDuration: 10_000_000, targetDuration: 2_000_000, curvePoints: points)
        XCTAssertFalse(map.isConstant)
        XCTAssertEqual(map.sourceTime(atOffset: 0), 0)
        XCTAssertEqual(map.sourceTime(atOffset: 2_000_000), 10_000_000)
        var last: Int64 = -1
        for o in stride(from: Int64(0), through: 2_000_000, by: 10_000) {
            let s = map.sourceTime(atOffset: o)
            XCTAssertGreaterThanOrEqual(s, last)
            last = s
        }
        // Fast in the middle, slow at the ends.
        XCTAssertGreaterThan(map.speed(at: 0.5), map.speed(at: 0.02) * 3)
    }

    private let flashIn = [Point2(x: 0, y: 1), Point2(x: 0.19, y: 5.2), Point2(x: 0.78, y: 5.2),
                           Point2(x: 0.94, y: 1), Point2(x: 1, y: 1)]

    func testOffsetIsInverseOfSourceTime() {
        let map = TimeMap(sourceStart: 500_000, sourceDuration: 10_000_000, targetDuration: 2_000_000, curvePoints: flashIn)
        for o in stride(from: Int64(0), through: 2_000_000, by: 100_000) {
            XCTAssertEqual(Double(map.offset(atSourceTime: map.sourceTime(atOffset: o))), Double(o), accuracy: 2_000)
        }
        let constant = TimeMap(sourceStart: 0, sourceDuration: 4_000_000, targetDuration: 1_000_000)
        XCTAssertEqual(constant.offset(atSourceTime: 2_000_000), 500_000)
        XCTAssertEqual(constant.offset(atSourceTime: 9_000_000), 1_000_000)
    }

    func testSliceFollowsTheCurve() {
        let map = TimeMap(sourceStart: 0, sourceDuration: 10_000_000, targetDuration: 2_000_000, curvePoints: flashIn)
        let slice = map.slice(from: 400_000, to: 1_400_000)
        XCTAssertEqual(slice.targetDuration, 1_000_000)
        for o in stride(from: Int64(0), through: 1_000_000, by: 50_000) {
            XCTAssertEqual(Double(slice.sourceTime(atOffset: o)), Double(map.sourceTime(atOffset: 400_000 + o)),
                           accuracy: 1_000)
        }
        // A slice of the constant-speed middle collapses to a constant map.
        XCTAssertTrue(map.slice(from: 800_000, to: 1_200_000).isConstant)
        XCTAssertTrue(TimeMap(sourceStart: 0, sourceDuration: 3, targetDuration: 1).slice(from: 0, to: 1).isConstant)
    }

    func testSampledCompositionOfTwoSpeeds() {
        // 2x over 3x = 6x.
        let outer = TimeMap(sourceStart: 1_000_000, sourceDuration: 2_000_000, targetDuration: 1_000_000)
        let inner = TimeMap(sourceStart: 0, sourceDuration: 9_000_000, targetDuration: 3_000_000)
        let composed = TimeMap(targetDuration: 1_000_000) { u in
            inner.preciseSourceTime(atOffset: outer.preciseSourceTime(atOffset: u * 1_000_000))
        }
        XCTAssertTrue(composed.isConstant)
        XCTAssertEqual(composed.sourceStart, 3_000_000)
        XCTAssertEqual(composed.averageSpeed, 6, accuracy: 1e-6)
    }
}

final class KeyframeTests: XCTestCase {
    func testLinearInterpolationAndClamping() {
        let track = KeyframeTrack(times: [0, 1_000, 3_000], values: [0, 1, -1])
        XCTAssertEqual(track.value(at: -5), 0)
        XCTAssertEqual(track.value(at: 500), 0.5, accuracy: 1e-9)
        XCTAssertEqual(track.value(at: 2_000), 0, accuracy: 1e-9)
        XCTAssertEqual(track.value(at: 9_000), -1)
    }
}

final class DraftParsingTests: XCTestCase {
    /// A minimal synthetic draft with odd values (nulls, wrong types, unknown keys) that must not break parsing.
    let json = """
    {
      "id": "D", "name": "Test", "duration": 3000000, "fps": 30.0, "unknown_field": {"x": [1, 2]},
      "canvas_config": {"width": 1080, "height": 1920, "ratio": "9:16", "background": null},
      "config": {"video_mute": false},
      "tracks": [
        {"id": "T1", "type": "video", "attribute": 0, "flag": 0, "segments": [
          {"id": "S1", "material_id": "V1", "source_timerange": {"start": 0, "duration": 6000000},
           "target_timerange": {"start": 0, "duration": 3000000}, "speed": 2.0, "volume": 1.5,
           "clip": {"scale": {"x": 1.2, "y": 1.2}, "rotation": 90, "transform": {"x": 0.1, "y": -0.2},
                    "flip": {"horizontal": true, "vertical": false}, "alpha": 0.5},
           "extra_material_refs": ["SP1", "C1", "TR1"], "render_index": 0,
           "common_keyframes": [{"property_type": "KFTypePositionX", "keyframe_list": [
             {"time_offset": 0, "values": [0.0]}, {"time_offset": 1000000, "values": [0.5]}]}]}
        ]},
        {"id": "T2", "type": "audio", "attribute": 1, "segments": []},
        {"id": "T3", "type": "text", "attribute": 0, "segments": [
          {"id": "S2", "material_id": "X1", "target_timerange": {"start": 0, "duration": 1000000}, "clip": null}
        ]}
      ],
      "materials": {
        "videos": [{"id": "V1", "type": "video", "path": "/nonexistent/clip.mov", "width": 1920, "height": 1080,
                    "crop": {"upper_left_x": 0.1, "upper_left_y": 0.0, "lower_right_x": 0.9, "lower_right_y": 1.0}}],
        "speeds": [{"id": "SP1", "type": "speed", "mode": 0, "speed": 2.0, "curve_speed": null}],
        "canvases": [{"id": "C1", "type": "canvas_blur", "blur": 0.5, "color": ""}],
        "transitions": [{"id": "TR1", "type": "transition", "name": "Flashback"}],
        "texts": [{"id": "X1", "type": "subtitle", "font_size": "not a number",
                   "content": "{\\"text\\":\\"Hello\\",\\"styles\\":[{\\"range\\":[0,5],\\"size\\":8,\\"fill\\":{\\"content\\":{\\"solid\\":{\\"color\\":[1,0,0]}}}}]}"}],
        "audios": null
      }
    }
    """

    func testParsesLeniently() throws {
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        XCTAssertEqual(draft.canvas.width, 1080)
        XCTAssertEqual(draft.tracks.count, 3)
        XCTAssertTrue(draft.tracks[1].isMuted)
        let segment = draft.tracks[0].segments[0]
        XCTAssertEqual(segment.clip?.scale.x, 1.2)
        XCTAssertEqual(segment.clip?.flipHorizontal, true)
        XCTAssertEqual(segment.keyframes.first?.keyframes.count, 2)
        XCTAssertEqual(draft.materials.videos.first?.crop.upperLeft.x, 0.1)
        XCTAssertEqual(draft.materials.texts.first?.fontSize, 15, "invalid number falls back to the default")
        let content = try XCTUnwrap(TextContent.parse(draft.materials.texts[0].content))
        XCTAssertEqual(content.text, "Hello")
        XCTAssertEqual(content.styles.first?.fillColor ?? [], [1, 0, 0])
    }

    func testTimelineReportsMissingMediaAndUnsupported() throws {
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: URL(fileURLWithPath: "/tmp"))
        XCTAssertEqual(timeline.report.missingFiles, ["/nonexistent/clip.mov"])
        XCTAssertTrue(timeline.report.unsupported.contains { $0.category == "Transition" && $0.name == "Flashback" })
        // The missing video produces no layer; the subtitle does.
        XCTAssertEqual(timeline.layers.count, 1)
        XCTAssertTrue(timeline.hasSubtitles)
        XCTAssertEqual(timeline.withoutSubtitles().layers.count, 0)
    }

    func testPlaceholderPathResolution() {
        let url = DraftLoader.resolvePath("##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##/audio/a.aac",
                                          projectURL: URL(fileURLWithPath: "/Users/x/Projects/My Project"))
        XCTAssertEqual(url?.path, "/Users/x/Projects/My Project/audio/a.aac")
        XCTAssertNil(DraftLoader.resolvePath("", projectURL: URL(fileURLWithPath: "/tmp")))
    }
}

final class LayerTests: XCTestCase {
    func testKeyframesOverrideClipAndUniformScale() throws {
        var clip = Clip()
        clip.scale = Point2(x: 2, y: 2)
        let layer = VisualLayer(
            id: "L", content: .photo(URL(fileURLWithPath: "/tmp/x.jpg")), trackIndex: 0, renderIndex: 0,
            isMainTrack: true, target: TimeRange(start: 1_000, duration: 2_000),
            timeMap: TimeMap(sourceStart: 0, sourceDuration: 2_000, targetDuration: 2_000), crop: Crop(), clip: clip,
            uniformScale: true, keyframes: [.scaleX: KeyframeTrack(times: [0, 2_000], values: [1, 3])], canvas: nil)
        let t = layer.transform(at: 2_000)
        XCTAssertEqual(t.scaleX, 2, accuracy: 1e-9)
        XCTAssertEqual(t.scaleY, 2, accuracy: 1e-9, "uniform scale follows the X keyframes")
        XCTAssertTrue(layer.isActive(at: 1_000))
        XCTAssertFalse(layer.isActive(at: 3_000))
    }
}

final class ExportSettingsTests: XCTestCase {
    func testOutputSize() {
        var s = ExportSettings()
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1080, height: 1920)), CGSize(width: 1080, height: 1920))
        s.aspect = .landscape
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1080, height: 1920)), CGSize(width: 1920, height: 1080))
        s.aspect = .square
        s.resolution = .p720
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1080, height: 1920)), CGSize(width: 720, height: 720))
        s.aspect = .project
        s.resolution = .p2160
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1440, height: 1920)), CGSize(width: 2160, height: 2880))
    }

    func testFileNameTemplate() {
        var s = ExportSettings()
        s.codec = .prores422
        s.fileNameTemplate = "{project}-{res}-{fps}-{codec}"
        XCTAssertEqual(s.fileName(project: "Trip", size: CGSize(width: 1080, height: 1920), fps: 30),
                       "Trip-1080x1920-30-prores422.mov")
    }

    func testFileNameUsesGregorianDates() {
        var s = ExportSettings()
        s.fileNameTemplate = "{date}"
        let date = ISO8601DateFormatter().date(from: "2026-10-15T12:00:00Z")!
        XCTAssertTrue(s.fileName(project: "P", size: .zero, fps: 30, date: date).hasPrefix("2026-10-1"))
    }

    func testDecodingOldSettingsKeepsDefaults() throws {
        let decoded = try JSONDecoder().decode(ExportSettings.self, from: Data(#"{"codec":"h264","quality":55}"#.utf8))
        XCTAssertEqual(decoded.codec, .h264)
        XCTAssertEqual(decoded.quality, 55)
        XCTAssertEqual(decoded.burnInSubtitles, true)
    }
}

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
