import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class BlendModeTests: XCTestCase {

    /// The blend runs on sRGB-encoded values (like CapCut's and Direct2D's) although the working space is linear.
    func testBlendsSRGBValues() {
        let srgb = CGColorSpace(name: CGColorSpace.sRGB)!
        let rect = CGRect(x: 0, y: 0, width: 4, height: 4)
        let backdrop = CIImage(color: CIColor(red: 0.5, green: 0.5, blue: 0.5, colorSpace: srgb)!).cropped(to: rect)
        let layer = CIImage(color: CIColor(red: 0.8, green: 0.2, blue: 0.5, colorSpace: srgb)!).cropped(to: rect)
        let context = CIContext(options: [.workingColorSpace: CGColorSpace(name: CGColorSpace.extendedLinearSRGB)!])
        func pixel(_ mode: BlendMode?, _ image: CIImage = layer) -> [Int] {
            var pixels = [UInt8](repeating: 0, count: 4 * 4 * 4)
            context.render(FrameCompositor.blended(image, over: backdrop, mode: mode), toBitmap: &pixels, rowBytes: 16,
                           bounds: rect, format: .RGBA8, colorSpace: srgb)
            return pixels[0..<4].map { Int($0) }
        }
        func assertClose(_ a: [Int], _ b: [Int], _ message: String, line: UInt = #line) {
            XCTAssertTrue(zip(a, b).allSatisfy { abs($0 - $1) <= 2 }, "\(message): \(a) vs \(b)", line: line)
        }
        assertClose(pixel(nil), [204, 51, 128, 255], "no mode: plain source-over")
        assertClose(pixel(.normal), [204, 51, 128, 255], "normal")
        assertClose(pixel(.screen), [230, 153, 191, 255], "screen = 1 − (1 − a)(1 − b)")
        assertClose(pixel(.multiply), [102, 26, 64, 255], "multiply = a · b")
        assertClose(pixel(.darken), [128, 51, 128, 255], "darken = min")
        assertClose(pixel(.lighten), [204, 128, 128, 255], "lighten = max")
        let half = layer.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: 0.5)])
        assertClose(pixel(.screen, half), [179, 140, 160, 255], "opacity mixes the blended colour with the backdrop")
    }
}
