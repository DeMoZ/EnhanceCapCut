import CoreImage
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import EnhanceCapCutCore

final class ColorFilterTests: XCTestCase {

    /// A lookup image of `layout` holding `transform` (applied to 8-bit RGB), as a PNG.
    static func writeLookup(_ url: URL, width: Int, height: Int, layout: LutLayout,
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
        let identity: [Float] = (0..<8).flatMap { (i: Int) -> [Float] in
            let r = Float(i & 1), g = Float((i >> 1) & 1), b = Float((i >> 2) & 1)
            return [r, g, b]
        }
        let inverted = identity.map { 1 - $0 }
        let lut = ColorLUT(size: 2, data: ColorLUT.cube(2, inverted, intensity: 1))
        let red = CIImage(color: CIColor(red: 1, green: 0, blue: 0)).cropped(to: CGRect(x: 0, y: 0, width: 4, height: 4))
        var pixel = [UInt8](repeating: 0, count: 4)
        CIContext().render(lut.apply(to: red), toBitmap: &pixel, rowBytes: 4, bounds: CGRect(x: 0, y: 0, width: 1, height: 1),
                           format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!)
        XCTAssertEqual(Array(pixel[0..<3]), [0, 255, 255])
    }

}
