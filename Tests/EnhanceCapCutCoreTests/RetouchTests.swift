import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class RetouchTests: XCTestCase {

    func testSkinWeight() {
        XCTAssertEqual(SkinRetouchMath.weight(0.545, 0.361, 0.337), SkinRetouchMath.strength, accuracy: 1e-9, "skin")
        XCTAssertGreaterThan(SkinRetouchMath.weight(0.85, 0.65, 0.55), 0.4, "light skin")
        XCTAssertGreaterThan(SkinRetouchMath.weight(0.35, 0.22, 0.16), 0.4, "dark skin")
        XCTAssertEqual(SkinRetouchMath.weight(0.31, 0.48, 0.69), 0, "sky")
        XCTAssertEqual(SkinRetouchMath.weight(0.5, 0.5, 0.5), 0, "grey")
        XCTAssertEqual(SkinRetouchMath.weight(0.78, 0.16, 0.2), 0, "saturated red")
        XCTAssertEqual(SkinRetouchMath.weight(0.94, 0.59, 0.16), 0, "orange")
        XCTAssertEqual(SkinRetouchMath.weight(0.14, 0.33, 0.29), 0, "green")
    }

    func testRetouchSoftensSkinOnly() {
        // A checkerboard of two skin tones next to a checkerboard of two blues.
        let w = 800, h = 400  // blur sigma = 1 px
        var pixels = [UInt8](repeating: 255, count: w * h * 4)
        for y in 0..<h {
            for x in 0..<w {
                let i = (y * w + x) * 4, odd = (x + y) % 2 == 1
                let c: (UInt8, UInt8, UInt8) = x < w / 2 ? (odd ? (150, 100, 90) : (130, 85, 78)) : (odd ? (60, 90, 200) : (40, 70, 180))
                pixels[i] = c.0; pixels[i + 1] = c.1; pixels[i + 2] = c.2
            }
        }
        let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
        let image = CIImage(bitmapData: Data(pixels), bytesPerRow: w * 4, size: CGSize(width: w, height: h),
                            format: .RGBA8, colorSpace: sRGB)
        let out = SkinRetouch().apply(image)
        XCTAssertEqual(out.extent, image.extent)
        var result = [UInt8](repeating: 0, count: w * h * 4)
        CIContext().render(out, toBitmap: &result, rowBytes: w * 4, bounds: out.extent, format: .RGBA8, colorSpace: sRGB)
        func contrast(_ x0: Int) -> Int {
            let i = (200 * w + x0) * 4, j = (200 * w + x0 + 1) * 4
            return abs(Int(result[i]) - Int(result[j]))
        }
        XCTAssertLessThan(contrast(200), 15, "skin is smoothed (was 20)")
        XCTAssertEqual(contrast(600), 20, "blue is kept")
    }
}
