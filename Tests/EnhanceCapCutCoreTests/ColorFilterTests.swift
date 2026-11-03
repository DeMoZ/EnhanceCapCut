import CoreImage
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import EnhanceCapCutCore

final class ColorFilterTests: XCTestCase {
    func testLookupImageLayouts() {
        XCTAssertEqual(ColorFilter.layout(width: 512, height: 512), .grid(size: 64, tilesPerRow: 8))
        XCTAssertEqual(ColorFilter.layout(width: 64, height: 64), .grid(size: 16, tilesPerRow: 4))
        XCTAssertEqual(ColorFilter.layout(width: 1024, height: 32), .row(size: 32))
        XCTAssertEqual(ColorFilter.layout(width: 16, height: 256), .column(size: 16))
        XCTAssertNil(ColorFilter.layout(width: 500, height: 500))
        XCTAssertNil(ColorFilter.layout(width: 1080, height: 1920))
        XCTAssertNil(ColorFilter.layout(width: 4096, height: 4096), "N = 256 is over the limit")
        let grid = ColorFilter.Layout.grid(size: 64, tilesPerRow: 8)
        XCTAssertEqual(grid.pixel(r: 5, g: 7, b: 9).x, 64 + 5)
        XCTAssertEqual(grid.pixel(r: 5, g: 7, b: 9).y, 64 + 7)
        XCTAssertEqual(ColorFilter.Layout.row(size: 32).pixel(r: 1, g: 2, b: 3).x, 97)
    }

    /// A lookup image of `layout` holding `transform` (applied to 8-bit RGB), as a PNG.
    static func writeLookup(_ url: URL, width: Int, height: Int, layout: ColorFilter.Layout,
                            transform: (Int, Int, Int) -> (Int, Int, Int)) throws {
        let n = layout.size
        var pixels = [UInt8](repeating: 255, count: width * height * 4)
        for b in 0..<n { for g in 0..<n { for r in 0..<n {
            let p = layout.pixel(r: r, g: g, b: b)
            let v = transform(r * 255 / (n - 1), g * 255 / (n - 1), b * 255 / (n - 1))
            let o = (p.y * width + p.x) * 4
            pixels[o] = UInt8(v.0); pixels[o + 1] = UInt8(v.1); pixels[o + 2] = UInt8(v.2)
        } } }
        let provider = CGDataProvider(data: Data(pixels) as CFData)!
        let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
        let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(dest))
    }

    func testReadsLookupImagesAndCubes() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-lut-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let png = dir.appendingPathComponent("lut.png")
        try Self.writeLookup(png, width: 64, height: 64, layout: .grid(size: 16, tilesPerRow: 4)) { r, g, b in
            (255 - r, g, b)
        }
        let table = try XCTUnwrap(ColorLUT.readImage(png))
        XCTAssertEqual(table.size, 16)
        // Entry (r = 15, g = 0, b = 15): red inverted.
        let i = ((15 * 16 + 0) * 16 + 15) * 3
        XCTAssertEqual(table.rgb[i], 0, accuracy: 0.01)
        XCTAssertEqual(table.rgb[i + 2], 1, accuracy: 0.01)

        let cube = try XCTUnwrap(ColorLUT.parseCube("""
        # comment
        TITLE "x"
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """))
        XCTAssertEqual(cube.size, 2)
        XCTAssertEqual(cube.rgb.count, 24)
        XCTAssertNil(ColorLUT.parseCube("LUT_3D_SIZE 2\n0 0 0\n"), "too few entries")
    }

    func testIntensityBlendsWithTheOriginal() {
        // A 2-entry cube that maps everything to black, at half intensity: halfway between identity and black.
        let data = ColorLUT.cube(2, [Float](repeating: 0, count: 24), intensity: 0.5)
        let floats = data.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
        XCTAssertEqual(floats.count, 32)
        XCTAssertEqual(Array(floats[28..<32]), [0.5, 0.5, 0.5, 1], "entry (1,1,1)")
        XCTAssertEqual(floats[4], 0.5, "entry (1,0,0) red")
        XCTAssertEqual(floats[5], 0)
    }

    func testAppliesTheTableToAFrame() throws {
        let identity = (0..<8).flatMap { i in [Float(i & 1), Float(i >> 1 & 1), Float(i >> 2 & 1)] }
        let inverted = identity.map { 1 - $0 }
        let lut = ColorLUT(size: 2, data: ColorLUT.cube(2, inverted, intensity: 1))
        let red = CIImage(color: CIColor(red: 1, green: 0, blue: 0)).cropped(to: CGRect(x: 0, y: 0, width: 4, height: 4))
        var pixel = [UInt8](repeating: 0, count: 4)
        CIContext().render(lut.apply(to: red), toBitmap: &pixel, rowBytes: 4, bounds: CGRect(x: 0, y: 0, width: 1, height: 1),
                           format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!)
        XCTAssertEqual(Array(pixel[0..<3]), [0, 255, 255])
    }

    func testFiltersFromTheDraft() throws {
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("ecc-filter-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: dir) }
        // A cached package with its lookup image somewhere inside, next to a texture that isn't one.
        let package = dir.appendingPathComponent("cache/111/abc", isDirectory: true)
        try fm.createDirectory(at: package.appendingPathComponent("res"), withIntermediateDirectories: true)
        try Self.writeLookup(package.appendingPathComponent("res/filter.png"), width: 1024, height: 32,
                             layout: .row(size: 32)) { ($0, $1, $2) }
        try Self.writeLookup(package.appendingPathComponent("grain.png"), width: 100, height: 60,
                             layout: .row(size: 2)) { ($0, $1, $2) }
        let photo = dir.appendingPathComponent("p.png")
        try Data([0]).write(to: photo)

        let json = """
        {"tracks": [
           {"type": "video", "segments": [{"id": "S", "material_id": "V", "render_index": 0,
             "extra_material_refs": ["F2"], "target_timerange": {"start": 0, "duration": 1000000}}]},
           {"type": "filter", "segments": [
             {"id": "T1", "material_id": "F2", "render_index": 10001, "target_timerange": {"start": 0, "duration": 500000}},
             {"id": "T2", "material_id": "F3", "render_index": 10000, "target_timerange": {"start": 0, "duration": 500000}}]}],
         "materials": {
           "videos": [{"id": "V", "type": "photo", "path": "\(photo.path)"}],
           "effects": [
             {"id": "F1", "type": "filter", "name": "Film", "value": 0.4, "resource_id": "111",
              "path": "/somewhere/else/Cache/effect/111/abc"},
             {"id": "F2", "type": "filter", "name": "Clip filter", "value": 1, "resource_id": "111", "path": "\(package.path)"},
             {"id": "F3", "type": "filter", "name": "Not cached", "value": 1, "resource_id": "222", "path": "/nope"}]}}
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        // TimelineBuilder looks in the real cache; resolve the stale path the same way it does.
        XCTAssertEqual(ColorFilter.locate(path: URL(fileURLWithPath: "/somewhere/else"), resourceID: "111",
                                          cacheRoots: [dir.appendingPathComponent("cache")])?.lastPathComponent, "filter.png")
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        XCTAssertEqual(timeline.layers.first?.filters.map(\.name), ["Clip filter"])
        XCTAssertEqual(timeline.layers.first?.filters.first?.lut.lastPathComponent, "filter.png")
        let found = Set(timeline.report.unsupported.map { "\($0.category): \($0.name)" })
        XCTAssertTrue(found.contains("Filter: Not cached"))
        XCTAssertFalse(found.contains("Filter: Clip filter"))
        XCTAssertEqual(timeline.filters.map(\.id), ["T1"], "the uncached filter is left out")

        let layer = FilterLayer(id: "T", target: TimeRange(start: 0, duration: 10), trackIndex: 1, renderIndex: 10000,
                                filter: ColorFilter(name: "x", lut: photo))
        XCTAssertTrue(layer.covers(timeline.layers[0]))
        XCTAssertTrue(layer.isActive(at: 5))
        XCTAssertFalse(layer.isActive(at: 10))
    }
}
