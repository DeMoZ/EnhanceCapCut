import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class QualityEnhanceTests: XCTestCase {

    func testUpscalesSmallFramesAndSharpens() {
        XCTAssertEqual(QualityEnhance.scale(width: 360, height: 640), 2)
        XCTAssertEqual(QualityEnhance.scale(width: 1080, height: 1920), 1)

        // A soft vertical edge: dark left half, light right half, a 4 px ramp between.
        let w = 64, h = 16
        var pixels = [UInt8](repeating: 255, count: w * h * 4)
        for y in 0..<h {
            for x in 0..<w {
                let v = UInt8(max(60, min(180, 60 + (x - 30) * 30)))
                let i = (y * w + x) * 4
                pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v
            }
        }
        let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
        let image = CIImage(bitmapData: Data(pixels), bytesPerRow: w * 4, size: CGSize(width: w, height: h),
                            format: .RGBA8, colorSpace: sRGB)
        let out = QualityEnhance.apply(image)
        XCTAssertEqual(out.extent, CGRect(x: 0, y: 0, width: 2 * w, height: 2 * h))
        let ow = 2 * w
        var result = [UInt8](repeating: 0, count: ow * 2 * h * 4)
        CIContext().render(out, toBitmap: &result, rowBytes: ow * 4, bounds: out.extent, format: .RGBA8, colorSpace: sRGB)
        let row = (0..<ow).map { Int(result[(h * ow + $0) * 4]) }
        XCTAssertLessThan(row.min()!, 55, "the dark side overshoots below 60 next to the edge")
        XCTAssertGreaterThan(row.max()!, 185, "the light side overshoots above 180 next to the edge")
        XCTAssertEqual(row[4], 60, accuracy: 2, "flat areas are kept")
        XCTAssertEqual(row[ow - 5], 180, accuracy: 2, "flat areas are kept")
    }
}
