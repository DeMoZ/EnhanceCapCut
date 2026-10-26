import XCTest
@testable import EnhanceCapCutCore

final class DubTimelineTests: XCTestCase {
    private var root: URL!
    private let mainID = "40332EEA-CA6F-4C3A-A039-B145ECAED041"

    /// A project with one timeline in `Timelines/`, its root draft copy and a dock with that timeline.
    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-dub-\(UUID().uuidString)/Trip")
        let folder = root.appendingPathComponent("Timelines/\(mainID)")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("common_attachment"),
                                                withIntermediateDirectories: true)
        let draft = #"{"id":"\#(mainID)","duration":1000000,"tracks":[]}"#
        for url in [folder.appendingPathComponent("draft_info.json"), folder.appendingPathComponent("template-2.tmp"),
                    root.appendingPathComponent("draft_info.json")] {
            try Data(draft.utf8).write(to: url)
        }
        try Data("{}".utf8).write(to: folder.appendingPathComponent("attachment_editing.json"))
        try Data("{}".utf8).write(to: folder.appendingPathComponent("common_attachment/attachment_pc_timeline.json"))
        try Data("x".utf8).write(to: folder.appendingPathComponent("draft.extra"))
        try Data("""
            {"config":{"color_space":-1},"id":"P","main_timeline_id":"\(mainID)","timelines":[{"create_time":1,\
            "id":"\(mainID)","is_marked_delete":false,"name":"Timeline 01","update_time":1}],"version":0}
            """.utf8).write(to: root.appendingPathComponent("Timelines/project.json"))
        try Data("""
            {"dockItems":[{"dockIndex":0,"ratio":1,"timelineIds":["\(mainID)"],"timelineNames":["Timeline 01"]}],\
            "layoutOrientation":1}
            """.utf8).write(to: root.appendingPathComponent("timeline_layout.json"))
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root.deletingLastPathComponent())
    }

    func testCreatesANumberedDubTimeline() throws {
        let project = CapCutProject(url: root)
        let source = try XCTUnwrap(project.timelines().first)
        let name = DubTimeline.name(project: project, languages: ["en"], original: .mute)
        XCTAssertEqual(name, "Trip_dub_en_muteall_1")

        let (timeline, result) = try DubTimeline.create(project: project, source: source, name: name, cueCount: 0) {
            Data(String(decoding: $0, as: UTF8.self).replacingOccurrences(of: "\"tracks\":[]", with: "\"tracks\":[1]").utf8)
        }
        defer { try? FileManager.default.removeItem(at: result.backupFolder) }
        XCTAssertFalse(timeline.isMain)

        // The project lists it, with the transformed draft under its own id; the source is untouched.
        let timelines = project.timelines()
        XCTAssertEqual(timelines.map(\.name), ["Timeline 01", name])
        let draft = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: timelines[1].draftURL)) as? [String: Any])
        XCTAssertEqual(draft["id"] as? String, timeline.id)
        XCTAssertEqual(draft["tracks"] as? [Int], [1])
        let folder = timelines[1].draftURL.deletingLastPathComponent()
        let fm = FileManager.default
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("template-2.tmp")), try Data(contentsOf: timelines[1].draftURL))
        XCTAssertTrue(fm.fileExists(atPath: folder.appendingPathComponent("attachment_editing.json").path))
        XCTAssertTrue(fm.fileExists(atPath: folder.appendingPathComponent("common_attachment/attachment_pc_timeline.json").path))
        XCTAssertFalse(fm.fileExists(atPath: folder.appendingPathComponent("draft.extra").path))
        XCTAssertTrue(String(decoding: try Data(contentsOf: source.draftURL), as: UTF8.self).contains("\"tracks\":[]"))

        // A tab in the dock; project.json keeps its other fields.
        let layout = String(decoding: try Data(contentsOf: root.appendingPathComponent("timeline_layout.json")), as: UTF8.self)
        XCTAssertTrue(layout.contains(#""timelineIds":["\#(mainID)","\#(timeline.id)"],"timelineNames":["Timeline 01","\#(name)"]"#))
        let list = String(decoding: try Data(contentsOf: root.appendingPathComponent("Timelines/project.json")), as: UTF8.self)
        XCTAssertTrue(list.hasPrefix(#"{"config":{"color_space":-1},"id":"P""#))
        XCTAssertTrue(fm.fileExists(atPath: result.backupFolder.appendingPathComponent("Timelines/project.json").path))

        // The next one with the same settings gets the next number.
        XCTAssertEqual(DubTimeline.name(project: project, languages: ["en"], original: .mute), "Trip_dub_en_muteall_2")
        XCTAssertEqual(DubTimeline.name(project: project, languages: ["en", "th"], original: .removeVocal),
                       "Trip_dub_en-th_removevocal_1")
    }

    func testOlderSingleTimelineProjectsAreRefused() throws {
        try FileManager.default.removeItem(at: root.appendingPathComponent("Timelines/project.json"))
        let project = CapCutProject(url: root)
        let source = try XCTUnwrap(project.timelines().first)
        XCTAssertThrowsError(try DubTimeline.create(project: project, source: source, name: "x", cueCount: 0) { $0 })
    }
}
