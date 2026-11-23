import XCTest
@testable import EnhanceCapCutCore

final class KeyframeEasingTests: XCTestCase {
    /// Two keyframes 0 → 100 over 1 s; `curve` and the handles go on both.
    func group(curve: String, out: Double, in incoming: Double) throws -> KeyframeGroup {
        let json = """
        {"property_type": "KFTypePositionX", "keyframe_list": [
          {"time_offset": 0, "values": [0], "curveType": "\(curve)",
           "left_control": {"x": 0, "y": 0}, "right_control": {"x": \(out), "y": 0}},
          {"time_offset": 1000000, "values": [100], "curveType": "\(curve)",
           "left_control": {"x": \(incoming), "y": 0}, "right_control": null}]}
        """
        return try JSONDecoder().decode(KeyframeGroup.self, from: Data(json.utf8))
    }

    func testLineKeyframesStayLinearWhateverTheHandles() throws {
        let track = try XCTUnwrap(KeyframeTrack(group: group(curve: "Line", out: 400_000, in: -400_000)))
        XCTAssertNil(track.handles)
        XCTAssertEqual(track.value(at: 250_000), 25, accuracy: 1e-9)
        XCTAssertEqual(track.breaks(step: 50_000), [0, 1_000_000])
    }

    func testCurvedKeyframesFollowTheCubicBezier() throws {
        let track = try XCTUnwrap(KeyframeTrack(group: group(curve: "Cubic", out: 400_000, in: -400_000)))
        // Ease in-out: slow at both ends, symmetric, exact at the middle and the keyframes.
        XCTAssertEqual(track.value(at: 0), 0)
        XCTAssertEqual(track.value(at: 250_000), 13.5307, accuracy: 1e-3)
        XCTAssertEqual(track.value(at: 500_000), 50, accuracy: 1e-6)
        XCTAssertEqual(track.value(at: 250_000) + track.value(at: 750_000), 100, accuracy: 1e-6)
        XCTAssertEqual(track.value(at: 1_000_000), 100)
        XCTAssertEqual(track.breaks(step: 250_000), [0, 250_000, 500_000, 750_000, 1_000_000])
        // Scaling (volume envelopes) scales the handles too.
        XCTAssertEqual(track.scaled(by: 0.5).value(at: 250_000), track.value(at: 250_000) / 2, accuracy: 1e-9)
    }

    func testZeroHandlesAndHandlesPastTheSpanStayMonotonic() throws {
        let straight = try XCTUnwrap(KeyframeTrack(group: group(curve: "Cubic", out: 0, in: 0)))
        XCTAssertEqual(straight.value(at: 300_000), 30, accuracy: 1e-3)
        let wild = try XCTUnwrap(KeyframeTrack(group: group(curve: "Cubic", out: 5_000_000, in: -5_000_000)))
        var last = -1.0
        for t in stride(from: Int64(0), through: 1_000_000, by: 10_000) {
            let v = wild.value(at: t)
            XCTAssertGreaterThanOrEqual(v, last - 1e-6)
            last = v
        }
    }

    /// Clip keyframes are on the source time axis: a clip trimmed to start 5 s into its media has keyframes from
    /// 5 000 000 (Durian1, checked against CapCut's export).
    func testClipKeyframesAreOnTheSourceTimeAxis() {
        let layer = VisualLayer(
            id: "L", content: .photo(URL(fileURLWithPath: "/tmp/x.jpg")), trackIndex: 0, renderIndex: 0,
            isMainTrack: true, target: TimeRange(start: 10_000_000, duration: 2_000_000),
            timeMap: TimeMap(sourceStart: 5_000_000, sourceDuration: 2_000_000, targetDuration: 2_000_000), crop: Crop(),
            clip: Clip(), uniformScale: true,
            keyframes: [.positionX: KeyframeTrack(times: [5_000_000, 6_000_000], values: [0, 1])], canvas: nil)
        XCTAssertEqual(layer.transform(at: 10_000_000).positionX, 0, accuracy: 1e-9)
        XCTAssertEqual(layer.transform(at: 10_500_000).positionX, 0.5, accuracy: 1e-9)
        XCTAssertEqual(layer.transform(at: 11_500_000).positionX, 1, accuracy: 1e-9)
    }
}
