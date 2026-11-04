import XCTest
@testable import EnhanceCapCutCore

final class MaskTests: XCTestCase {
    /// Coverage of one shape mask on a `w`×`h` raster.
    private func raster(_ shape: MaskShape, _ g: MaskGeometry, inverted: Bool = false, w: Int = 40, h: Int = 20) -> [UInt8] {
        MaskRaster.render([.shape(shape, g, inverted: inverted)], width: w, height: h)
    }

    private func at(_ r: [UInt8], _ x: Int, _ y: Int, w: Int = 40) -> UInt8 { r[y * w + x] }

    func testRectangleCenterSizeAndInvert() {
        // Half the frame, centered: x 10..<30, y 5..<15.
        let g = MaskGeometry(width: 0.5, height: 0.5)
        let r = raster(.rectangle, g)
        XCTAssertEqual(at(r, 20, 10), 255)
        XCTAssertEqual(at(r, 10, 5), 255)
        XCTAssertEqual(at(r, 9, 10), 0)
        XCTAssertEqual(at(r, 20, 15), 0)
        let inv = raster(.rectangle, g, inverted: true)
        XCTAssertEqual(at(inv, 20, 10), 0)
        XCTAssertEqual(at(inv, 0, 0), 255)
        // Center in half-frame units, +y up: (0.5, 0.5) is the top-right quarter's center (30, 5).
        let moved = raster(.rectangle, MaskGeometry(centerX: 0.5, centerY: 0.5, width: 0.2, height: 0.2))
        XCTAssertEqual(at(moved, 30, 5), 255)
        XCTAssertEqual(at(moved, 10, 15), 0)
    }

    func testRotationIsClockwise() {
        // A wide thin bar turned 90° becomes a tall one.
        let bar = MaskGeometry(width: 0.9, height: 0.05, rotation: 90)
        let r = raster(.rectangle, bar, w: 40, h: 40)
        XCTAssertEqual(at(r, 20, 3, w: 40), 255)
        XCTAssertEqual(at(r, 35, 20, w: 40), 0)
        // The line keeps what's above it; rotated clockwise by 90° that becomes the right side.
        XCTAssertEqual(at(raster(.line, MaskGeometry()), 20, 2), 255)
        XCTAssertEqual(at(raster(.line, MaskGeometry()), 20, 17), 0)
        let turned = raster(.line, MaskGeometry(rotation: 90))
        XCTAssertEqual(at(turned, 2, 10), 0)
        XCTAssertEqual(at(turned, 37, 10), 255)
    }

    func testShapes() {
        let g = MaskGeometry(width: 0.5, height: 1)
        for shape in [MaskShape.circle, .heart, .star] {
            let r = raster(shape, g)
            XCTAssertEqual(at(r, 20, 10), 255, "\(shape) covers its center")
            XCTAssertEqual(at(r, 1, 1), 0, "\(shape) leaves the corner")
        }
        // A rounded rectangle cuts its corners.
        let square = MaskGeometry(width: 0.5, height: 1, roundCorner: 1)
        XCTAssertEqual(at(raster(.rectangle, square), 11, 1), 0)
        XCTAssertEqual(at(raster(.rectangle, MaskGeometry(width: 0.5, height: 1)), 11, 1), 255)
        // Mirror: a band `height` tall.
        let band = raster(.mirror, MaskGeometry(height: 0.2))
        XCTAssertEqual(at(band, 5, 10), 255)
        XCTAssertEqual(at(band, 5, 4), 0)
    }

    func testFeatherIsASoftEdge() {
        let sharp = raster(.line, MaskGeometry(), w: 40, h: 200)
        let soft = raster(.line, MaskGeometry(feather: 0.5), w: 40, h: 200)
        XCTAssertEqual(at(sharp, 0, 90, w: 40), 255)
        let mid = at(soft, 0, 100, w: 40)
        XCTAssertTrue(mid > 100 && mid < 155, "half-way on the edge, got \(mid)")
        XCTAssertTrue(at(soft, 0, 95, w: 40) > at(soft, 0, 105, w: 40))
    }

    func testUnionAndBitmaps() {
        let left = MaskRaster.Layer.shape(.rectangle, MaskGeometry(centerX: -0.5, width: 0.5, height: 1), inverted: false)
        // A 2×1 brush frame: left hidden, right shown.
        let brush = MaskRaster.Layer.bitmap([0, 255], width: 2, height: 1, inverted: false)
        let r = MaskRaster.render([left, brush], width: 40, height: 20)
        XCTAssertEqual(at(r, 5, 10), 255, "the rectangle")
        XCTAssertEqual(at(r, 38, 10), 255, "the brush")
        XCTAssertTrue((100...160).contains(at(r, 20, 10)), "between the brush's pixels")
        XCTAssertEqual(MaskRaster.rasterSize(width: 1920, height: 1080).width, MaskRaster.maxSide)
        XCTAssertEqual(MaskRaster.rasterSize(width: 640, height: 480).width, 640)
    }

    func testKeyframesAreOnTheSourceTimeAxis() {
        let mask = ClipMask(kind: .shape(.rectangle), geometry: MaskGeometry(centerX: 0.3),
                            keyframes: [.positionX: KeyframeTrack(times: [10_000_000, 12_000_000], values: [0, 1])])
        XCTAssertEqual(mask.geometry(at: 11_000_000).centerX, 0.5, accuracy: 1e-9)
        XCTAssertEqual(mask.geometry(at: 0).centerX, 0)
        XCTAssertEqual(MaskProperty(propertyType: "KFTypeCommonMaskSizeWidth"), .width)
        XCTAssertEqual(MaskProperty(propertyType: "KFTypeMaskRoundCorner"), .roundCorner)
        XCTAssertNil(MaskProperty(propertyType: "KFTypeCommonMaskTwist"))
        XCTAssertEqual(ClipMask.shape(resourceType: "geometric_shape", name: "Heart"), .heart)
        XCTAssertEqual(ClipMask.shape(resourceType: "line", name: "Split"), .line)
        XCTAssertNil(ClipMask.shape(resourceType: "custom", name: "Brush"))
    }

    func testTimelineBuildsMasks() throws {
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("ecc-masks-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: dir) }
        let strokes = dir.appendingPathComponent("adjust_mask/B1", isDirectory: true)
        try fm.createDirectory(at: strokes, withIntermediateDirectories: true)
        try Data([0, 255]).write(to: strokes.appendingPathComponent("2000000"))
        try Data(#"{"data": [{"width": 2, "height": 1, "pts": 2000000}]}"#.utf8).write(to: strokes.appendingPathComponent("data.json"))
        let photo = dir.appendingPathComponent("p.png")
        try Data([0]).write(to: photo)
        let root = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##/adjust_mask/"
        let json = """
        {"tracks": [{"type": "video", "segments": [
           {"id": "S0", "material_id": "V", "target_timerange": {"start": 0, "duration": 1000000},
            "extra_material_refs": ["M1", "M2", "M3", "M4"],
            "common_keyframes": [
              {"property_type": "KFTypeCommonMaskPositionX", "material_id": "C1",
               "keyframe_list": [{"time_offset": 0, "values": [0.1]}, {"time_offset": 1000000, "values": [0.3]}]},
              {"property_type": "KFTypeCommonMaskPositionX", "material_id": "C2",
               "keyframe_list": [{"time_offset": 0, "values": [0.9]}]}]}]}],
         "materials": {
           "videos": [{"id": "V", "type": "photo", "path": "\(photo.path)"}],
           "common_mask": [
             {"id": "M1", "constant_material_id": "C1", "name": "Rectangle", "resource_type": "rectangle",
              "config": {"width": 0.2, "height": 0.4, "centerX": 0, "centerY": 0.5, "rotation": 30, "feather": 0.1,
                         "roundCorner": 0.2, "invert": true}},
             {"id": "M2", "constant_material_id": "C9", "name": "Brush", "resource_type": "custom",
              "loader_work_space": "\(root)B1", "config": {}},
             {"id": "M3", "name": "Brush", "resource_type": "custom", "loader_work_space": "", "config": {}},
             {"id": "M4", "name": "Wave", "resource_type": "wave", "config": {"expansion": 0.2}}]}}
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        let masks = try XCTUnwrap(timeline.layers.first).masks
        XCTAssertEqual(masks.count, 2)
        XCTAssertEqual(masks[0].kind, .shape(.rectangle))
        XCTAssertEqual(masks[0].inverted, true)
        XCTAssertEqual(masks[0].geometry, MaskGeometry(centerX: 0, centerY: 0.5, width: 0.2, height: 0.4, rotation: 30,
                                                       feather: 0.1, roundCorner: 0.2))
        XCTAssertEqual(masks[0].geometry(at: 500_000).centerX, 0.2, accuracy: 1e-9, "its own keyframes only")
        guard case let .brush(folder) = masks[1].kind else { return XCTFail("brush") }
        XCTAssertEqual(folder.resolvingSymlinksInPath().path, strokes.resolvingSymlinksInPath().path)
        XCTAssertTrue(masks[1].keyframes.isEmpty)
        let found = Set(timeline.report.unsupported.map { "\($0.category): \($0.name)" })
        XCTAssertEqual(found, ["Mask: brush strokes not saved in the project", "Mask: expansion", "Mask: Wave"])

        // The brush frame, stretched over the clip, and redrawn only when it changes.
        let frames = ClipMaskFrames([masks[1]])
        let first = try XCTUnwrap(frames.frame(at: 0, width: 4, height: 2))
        XCTAssertEqual(first.width, 4)
        XCTAssertEqual(first.pixels[0], 0)
        XCTAssertEqual(first.pixels[3], 255)
        XCTAssertTrue(first.changed)
        XCTAssertEqual(frames.frame(at: 40_000, width: 4, height: 2)?.changed, false)
    }
}
