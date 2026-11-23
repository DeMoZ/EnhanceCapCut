import XCTest
import EccCore

/// The generated Swift bindings of the Rust core (`EccCore`, scripts/build-core.sh) on a fixture project of
/// `core/fixtures`: a timeline is built and read, a frame evaluated, captions written, errors and panics arrive as
/// `CoreError`. The core's own tests are in `core/` (`cargo test`); this only checks the crossing.
final class RustCoreBindingTests: XCTestCase {
    private let repo = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent()

    /// The fixture `core/fixtures/<name>` (one without media) as a project folder.
    private func project(_ name: String) throws -> URL {
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-binding-\(UUID().uuidString)")
        let folder = parent.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: repo.appendingPathComponent("core/fixtures/\(name)/draft.fixture.json"),
                                         to: folder.appendingPathComponent("draft_info.json"))
        addTeardownBlock { try? FileManager.default.removeItem(at: parent) }
        return folder
    }

    private func environment(_ project: URL) -> CoreEnvironment {
        CoreEnvironment(projectsRoot: project.deletingLastPathComponent().path,
                        capcutCacheRoots: [project.appendingPathComponent("cache").path],
                        backupRoot: project.appendingPathComponent("backups").path)
    }

    func testBuildsATimelineAndEvaluatesAFrame() throws {
        let folder = try project("captions")
        let timeline = try Timeline.build(projectPath: folder.path, timeline: nil, env: environment(folder))
        XCTAssertEqual(timeline.duration(), 8_000_000)
        XCTAssertEqual(timeline.canvas(), Size(width: 1920, height: 1080))
        let layers = timeline.layers()
        XCTAssertEqual(layers.count, 6)
        guard case .text(let spec) = layers[0].content else { return XCTFail("\(layers[0].content)") }
        XCTAssertEqual(spec.content.text, "Привет, мир")
        XCTAssertTrue(timeline.hasSubtitles())
        XCTAssertLessThan(timeline.withoutSubtitles().layers().count, layers.count)

        let frame = try timeline.frame(t: 1_000_000)
        XCTAssertEqual(frame.time, 1_000_000)
        XCTAssertTrue(frame.layers.contains { $0.index == 0 && $0.offset == 1_000_000 })
        XCTAssertNotNil(frame.layers.first?.text)
        let map = layers[0].timeMap
        XCTAssertEqual(map.sourceTimes(offsets: [0, 500_000]), [map.sourceTime(offset: 0), map.sourceTime(offset: 500_000)])

        let info = try formatInfo(project: folder.path, timeline: nil, env: environment(folder))
        let expected = try String(contentsOf: repo.appendingPathComponent("core/fixtures/captions/expected/info.txt"),
                                  encoding: .utf8)
        XCTAssertEqual(info, expected)
    }

    func testWritesCaptionsWithABackup() throws {
        let folder = try project("captions")
        let timeline = try XCTUnwrap(projectTimeline(projectPath: folder.path, nameOrNumber: nil))
        let before = try Data(contentsOf: URL(fileURLWithPath: timeline.draftPath))
        let edits = DraftEdits(captions: [SubtitleTrack(language: "en", cues: [CaptionCue(start: 0.5, end: 2, text: "Hello")],
                                                        isOriginal: false, isVisible: true)],
                               voiceOver: [], original: .lower, replacePrevious: true)
        let applied = try applyEdits(edits: edits, draft: before, projectPath: folder.path,
                                     ids: .counting(after: 0), nowUs: 1_700_000_000_000_000)
        XCTAssertTrue(String(decoding: applied, as: UTF8.self).contains("00000000-0000-4000-8000-000000000001"))

        let result = try writeTimeline(projectPath: folder.path, timeline: timeline, edits: edits,
                                       backupRoot: environment(folder).backupRoot, capcutRunning: false, ids: .random,
                                       nowUs: 1_700_000_000_000_000)
        XCTAssertEqual(result.cueCount, 1)
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: result.backupFolder).appendingPathComponent("draft_info.json")),
                       before)
        let rebuilt = try Timeline.build(projectPath: folder.path, timeline: nil, env: environment(folder))
        XCTAssertGreaterThan(rebuilt.layers().count, 6)
    }

    func testErrorsAndPanicsArriveAsCoreErrors() {
        XCTAssertThrowsError(try Timeline.build(projectPath: "/nonexistent/ecc", timeline: nil, env: defaultEnvironment())) {
            guard case CoreError.Unreadable = $0 else { return XCTFail("\($0)") }
        }
        XCTAssertThrowsError(try debugPanic(message: "boom")) {
            XCTAssertEqual($0 as? CoreError, .Internal(message: "boom"))
            XCTAssertEqual($0.localizedDescription, "boom")
        }
    }
}
