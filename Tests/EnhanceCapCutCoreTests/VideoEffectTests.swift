import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class VideoEffectTests: XCTestCase {

    func testRendererKeepsTheFrameAndFlashesWhite() {
        let image = CIImage(color: CIColor(red: 0, green: 0, blue: 0)).cropped(to: CGRect(x: 0, y: 0, width: 16, height: 8))
        let state = VideoEffectState(zoom: 1.5, rotation: -10, blur: 0.05, flash: 0.5)
        let out = VideoEffectRenderer.apply(state, to: image)
        XCTAssertEqual(out.extent, image.extent)
        var pixel = [UInt8](repeating: 0, count: 4)
        CIContext(options: [.workingColorSpace: NSNull()])
            .render(out, toBitmap: &pixel, rowBytes: 4, bounds: CGRect(x: 8, y: 4, width: 1, height: 1),
                    format: .RGBA8, colorSpace: nil)
        XCTAssertEqual(Double(pixel[0]), 127.5, accuracy: 2)
        XCTAssertEqual(pixel[3], 255)
        XCTAssertTrue(VideoEffectRenderer.apply(.identity, to: image) === image)
    }
}
