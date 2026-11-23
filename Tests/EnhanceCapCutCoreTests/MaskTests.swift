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
        let g = geometry(width: 0.5, height: 0.5)
        let r = raster(.rectangle, g)
        XCTAssertEqual(at(r, 20, 10), 255)
        XCTAssertEqual(at(r, 10, 5), 255)
        XCTAssertEqual(at(r, 9, 10), 0)
        XCTAssertEqual(at(r, 20, 15), 0)
        let inv = raster(.rectangle, g, inverted: true)
        XCTAssertEqual(at(inv, 20, 10), 0)
        XCTAssertEqual(at(inv, 0, 0), 255)
        // Center in half-frame units, +y up: (0.5, 0.5) is the top-right quarter's center (30, 5).
        let moved = raster(.rectangle, geometry(centerX: 0.5, centerY: 0.5, width: 0.2, height: 0.2))
        XCTAssertEqual(at(moved, 30, 5), 255)
        XCTAssertEqual(at(moved, 10, 15), 0)
    }

    func testRotationIsClockwise() {
        // A wide thin bar turned 90° becomes a tall one.
        let bar = geometry(width: 0.9, height: 0.05, rotation: 90)
        let r = raster(.rectangle, bar, w: 40, h: 40)
        XCTAssertEqual(at(r, 20, 3, w: 40), 255)
        XCTAssertEqual(at(r, 35, 20, w: 40), 0)
        // The line keeps what's above it; rotated clockwise by 90° that becomes the right side.
        XCTAssertEqual(at(raster(.line, geometry()), 20, 2), 255)
        XCTAssertEqual(at(raster(.line, geometry()), 20, 17), 0)
        let turned = raster(.line, geometry(rotation: 90))
        XCTAssertEqual(at(turned, 2, 10), 0)
        XCTAssertEqual(at(turned, 37, 10), 255)
    }

    func testShapes() {
        let g = geometry(width: 0.5, height: 1)
        for shape in [MaskShape.circle, .heart, .star] {
            let r = raster(shape, g)
            XCTAssertEqual(at(r, 20, 10), 255, "\(shape) covers its center")
            XCTAssertEqual(at(r, 1, 1), 0, "\(shape) leaves the corner")
        }
        // A rounded rectangle cuts its corners.
        let square = geometry(width: 0.5, height: 1, roundCorner: 1)
        XCTAssertEqual(at(raster(.rectangle, square), 11, 1), 0)
        XCTAssertEqual(at(raster(.rectangle, geometry(width: 0.5, height: 1)), 11, 1), 255)
        // Mirror: a band `height` tall.
        let band = raster(.mirror, geometry(height: 0.2))
        XCTAssertEqual(at(band, 5, 10), 255)
        XCTAssertEqual(at(band, 5, 4), 0)
    }

    func testFeatherIsASoftEdge() {
        let sharp = raster(.line, geometry(), w: 40, h: 200)
        let soft = raster(.line, geometry(feather: 0.5), w: 40, h: 200)
        XCTAssertEqual(at(sharp, 0, 90, w: 40), 255)
        let mid = at(soft, 0, 100, w: 40)
        XCTAssertTrue(mid > 100 && mid < 155, "half-way on the edge, got \(mid)")
        XCTAssertTrue(at(soft, 0, 95, w: 40) > at(soft, 0, 105, w: 40))
    }

    func testUnionAndBitmaps() {
        let left = MaskRaster.Layer.shape(.rectangle, geometry(centerX: -0.5, width: 0.5, height: 1), inverted: false)
        // A 2×1 brush frame: left hidden, right shown.
        let brush = MaskRaster.Layer.bitmap([0, 255], width: 2, height: 1, inverted: false)
        let r = MaskRaster.render([left, brush], width: 40, height: 20)
        XCTAssertEqual(at(r, 5, 10), 255, "the rectangle")
        XCTAssertEqual(at(r, 38, 10), 255, "the brush")
        XCTAssertTrue((100...160).contains(at(r, 20, 10)), "between the brush's pixels")
        XCTAssertEqual(MaskRaster.rasterSize(width: 1920, height: 1080).width, MaskRaster.maxSide)
        XCTAssertEqual(MaskRaster.rasterSize(width: 640, height: 480).width, 640)
    }

}
