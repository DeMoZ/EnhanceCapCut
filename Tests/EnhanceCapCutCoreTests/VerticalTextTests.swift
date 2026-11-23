import XCTest
@testable import EnhanceCapCutCore

final class VerticalTextTests: XCTestCase {
    typealias Kind = VerticalTextLayout.Kind

    private func kinds(_ text: String) -> [Kind] {
        text.map { $0 == "\n" ? .newline : $0 == " " ? .space : .character }
    }

    private func advances(_ kinds: [Kind]) -> [CGFloat] {
        kinds.map { $0 == .newline ? 0 : $0 == .space ? 5 : 10 }
    }

    func testNewlinesStartColumns() {
        let k = kinds("AB\nC")
        XCTAssertEqual(VerticalTextLayout.columns(kinds: k, advances: advances(k), limit: 100), [[0, 1], [3]])
    }

    func testOverflowBreaksAfterLastSpace() {
        // "AB CD" with room for 3 characters: "AB" | "CD", the space dropped.
        let k = kinds("AB CD")
        XCTAssertEqual(VerticalTextLayout.columns(kinds: k, advances: advances(k), limit: 30), [[0, 1], [3, 4]])
    }

    func testOverflowWithoutSpaceBreaksBeforeCharacter() {
        let k = kinds("ABCDE")
        XCTAssertEqual(VerticalTextLayout.columns(kinds: k, advances: advances(k), limit: 20), [[0, 1], [2, 3], [4]])
    }

    func testColumnsRunRightToLeftAndAlign() {
        let k = kinds("AB\nC")
        let a = advances(k)
        let top = VerticalTextLayout.layout(kinds: k, advances: a, limit: 100, columnPitch: 12, columnWidth: 10,
                                            alignment: 0)
        XCTAssertEqual(top.size, CGSize(width: 22, height: 20))
        XCTAssertEqual(top.placements, [
            .init(index: 0, centerX: 17, top: 0), .init(index: 1, centerX: 17, top: 10),
            .init(index: 3, centerX: 5, top: 0),
        ])
        let middle = VerticalTextLayout.layout(kinds: k, advances: a, limit: 100, columnPitch: 12, columnWidth: 10,
                                               alignment: 1)
        XCTAssertEqual(middle.placements.last, .init(index: 3, centerX: 5, top: 5))
        let bottom = VerticalTextLayout.layout(kinds: k, advances: a, limit: 100, columnPitch: 12, columnWidth: 10,
                                               alignment: 2)
        XCTAssertEqual(bottom.placements.last, .init(index: 3, centerX: 5, top: 10))
    }

    func testRendersTallerThanWide() throws {
        func image(_ typesetting: Int) throws -> CIImage? {
            let content = #"{"text":"Hello","styles":[{"size":10,"range":[0,5]}]}"#
            let json = """
                {"content":\(String(reflecting: content)),"typesetting":\(typesetting)}
                """
            let spec = try textSpec(json)
            return TextRasterizer.render(spec, canvasWidth: 1080, scale: 1)
        }
        let horizontal = try XCTUnwrap(image(0)).extent, vertical = try XCTUnwrap(image(1)).extent
        XCTAssertGreaterThan(horizontal.width, horizontal.height)
        XCTAssertGreaterThan(vertical.height, vertical.width)
        XCTAssertGreaterThan(vertical.height, horizontal.height * 2)
    }
}
