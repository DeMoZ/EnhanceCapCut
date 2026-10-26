import XCTest
@testable import EnhanceCapCutCore

final class DonationsTests: XCTestCase {
    private let file = """
    {
      "schema": 1,
      "future": {"anything": true},
      "message": {"en": "Support it", "ru": "Поддержите", "pt-BR": "Apoie"},
      "methods": [
        {"id": "cloudtips", "title": {"en": "Card", "ru": "Карта"}, "url": "https://pay.cloudtips.ru/p/1"},
        {"id": "plain", "title": "Plain title", "description": {"ru": "Только по-русски"}, "url": "https://example.com/d"},
        {"id": "http", "title": {"en": "Insecure"}, "url": "http://example.com"},
        {"id": "script", "title": {"en": "Script"}, "url": "javascript:alert(1)"},
        {"id": "untitled", "url": "https://example.com"},
        42
      ]
    }
    """

    func testReadsMethodsLenientlyAndOnlyHttps() throws {
        let info = try XCTUnwrap(DonationInfo.parse(Data(file.utf8)))
        XCTAssertEqual(info.schema, 1)
        XCTAssertEqual(info.methods.map(\.id), ["cloudtips", "plain"])
        XCTAssertEqual(info.methods[0].url.absoluteString, "https://pay.cloudtips.ru/p/1")
        XCTAssertEqual(info.methods[1].title.resolved(for: ["ru"]), "Plain title")
        // A description without English shows only for its language.
        XCTAssertEqual(info.methods[1].description?.resolved(for: ["ru-RU"]), "Только по-русски")
        XCTAssertNil(info.methods[1].description?.resolved(for: ["de"]))
    }

    func testPicksTheLanguage() throws {
        let message = try XCTUnwrap(DonationInfo.parse(Data(file.utf8))?.message)
        XCTAssertEqual(message.resolved(for: ["pt-BR"]), "Apoie")
        XCTAssertEqual(message.resolved(for: ["pt_br"]), "Apoie")
        XCTAssertEqual(message.resolved(for: ["ru-RU", "en-US"]), "Поддержите")
        // The next preferred language before English.
        XCTAssertEqual(message.resolved(for: ["de-DE", "ru"]), "Поддержите")
        XCTAssertEqual(message.resolved(for: ["de-DE"]), "Support it")
        XCTAssertEqual(message.resolved(for: []), "Support it")
    }

    func testRejectsFilesWithoutMethods() {
        XCTAssertNil(DonationInfo.parse(Data("not json".utf8)))
        XCTAssertNil(DonationInfo.parse(Data(#"{"schema": 2, "methods": []}"#.utf8)))
        XCTAssertNil(DonationInfo.parse(Data(#"{"methods": "none"}"#.utf8)))
    }

    func testReadsTheCachedCopy() throws {
        let cache = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-donations-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: cache) }
        XCTAssertNil(Donations.cached(at: cache))
        try Data(file.utf8).write(to: cache)
        XCTAssertEqual(Donations.cached(at: cache)?.methods.count, 2)
    }
}
