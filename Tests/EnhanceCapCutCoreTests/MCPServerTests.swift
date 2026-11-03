import XCTest
@testable import EnhanceCapCutCore

final class MCPServerTests: XCTestCase {
    /// Prints the scripted lines for a command, then exits; `hold` keeps renders running until released.
    final class FakeRunner: MCPRunner, @unchecked Sendable {
        var outputs: [String: ([String], Int32, String)] = [:]
        var started: [[String]] = []
        var hold = false
        var held: [() -> Void] = []
        var stopped = 0
        let lock = NSLock()

        func start(_ arguments: [String], line: @escaping @Sendable (String) -> Void,
                   exit: @escaping @Sendable (Int32, String) -> Void) throws -> @Sendable () -> Void {
            lock.withLock { started.append(arguments) }
            let (lines, code, stderr) = outputs[arguments[0]] ?? ([], 0, "")
            let finish = { lines.forEach(line); exit(code, stderr) }
            if hold && arguments[0] == "render" {
                lock.withLock { held.append(finish) }
                return { [self] in lock.withLock { stopped += 1 }; exit(15, "") }
            }
            finish()
            return {}
        }

        func release() { lock.withLock { held }.forEach { $0() } }
    }

    private func call(_ server: MCPServer, _ tool: String, _ arguments: [String: Any] = [:], id: Int = 1) throws -> (String, Bool) {
        let request: [String: Any] = ["jsonrpc": "2.0", "id": id, "method": "tools/call",
                                      "params": ["name": tool, "arguments": arguments]]
        let line = String(decoding: try JSONSerialization.data(withJSONObject: request), as: UTF8.self)
        let response = try XCTUnwrap(server.handle(line))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(response.utf8)) as? [String: Any])
        let result = try XCTUnwrap(object["result"] as? [String: Any], response)
        let content = try XCTUnwrap((result["content"] as? [[String: Any]])?.first?["text"] as? String)
        return (content, result["isError"] as? Bool ?? false)
    }

    private func json(_ text: String) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any])
    }

    func testHandshakeAndToolList() throws {
        let server = MCPServer(runner: FakeRunner(), version: "1.2", projects: { [] })
        let initialize = try json(XCTUnwrap(server.handle(
            #"{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"#)))
        XCTAssertEqual(initialize["id"] as? Int, 7)
        let result = try XCTUnwrap(initialize["result"] as? [String: Any])
        XCTAssertEqual(result["protocolVersion"] as? String, "2025-06-18")
        XCTAssertEqual((result["serverInfo"] as? [String: Any])?["version"] as? String, "1.2")
        let unknown = try json(XCTUnwrap(server.handle(
            #"{"jsonrpc":"2.0","id":8,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}"#)))
        XCTAssertEqual((unknown["result"] as? [String: Any])?["protocolVersion"] as? String, MCPServer.protocolVersions[0])

        XCTAssertNil(server.handle(#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#))
        let list = try json(XCTUnwrap(server.handle(#"{"jsonrpc":"2.0","id":"a","method":"tools/list"}"#)))
        let names = ((list["result"] as? [String: Any])?["tools"] as? [[String: Any]])?.compactMap { $0["name"] as? String }
        XCTAssertEqual(names, ["list_projects", "project_info", "render", "add_subtitles", "list_voices",
                               "job_status", "list_jobs", "cancel_job"])
        let missing = try json(XCTUnwrap(server.handle(#"{"jsonrpc":"2.0","id":2,"method":"resources/list"}"#)))
        XCTAssertEqual((missing["error"] as? [String: Any])?["code"] as? Int, -32601)
        let garbage = try json(XCTUnwrap(server.handle("{not json")))
        XCTAssertEqual((garbage["error"] as? [String: Any])?["code"] as? Int, -32700)
    }

    func testWaitingStatusCallsAreRecognized() {
        XCTAssertTrue(MCPServer.waits(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"job_status","arguments":{"job_id":"job-1","wait_seconds":5}}}"#))
        XCTAssertFalse(MCPServer.waits(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"job_status","arguments":{"job_id":"job-1"}}}"#))
        XCTAssertFalse(MCPServer.waits(#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"render","arguments":{"wait_seconds":5}}}"#))
        XCTAssertFalse(MCPServer.waits("{not json"))
    }

    func testRenderArguments() throws {
        XCTAssertEqual(try MCPServer.renderArguments([
            "project": "Trip", "timeline": "2", "codec": "hevc10", "quality": 70, "aspect": "9:16", "height": 1080,
            "fps": 29.97, "lufs": -14, "subtitles": false, "from_seconds": 10, "duration_seconds": 5.5, "output": "/tmp/x.mp4",
        ]), ["render", "--codec", "hevc10", "--quality", "70", "--fps", "29.97", "--aspect", "9:16", "--height", "1080",
             "--lufs", "-14", "--no-subtitles", "--from", "10", "--duration", "5.5", "-o", "/tmp/x.mp4",
             "--timeline", "2", "Trip"])
        XCTAssertEqual(try MCPServer.renderArguments([:]), ["render"])
        XCTAssertThrowsError(try MCPServer.renderArguments(["codec": "vp9"]))
        XCTAssertThrowsError(try MCPServer.renderArguments(["height": 1000]))
        XCTAssertThrowsError(try MCPServer.renderArguments(["quality": "high"]))
        XCTAssertThrowsError(try MCPServer.renderArguments(["project": "--help"]))
    }

    func testSubtitleArguments() throws {
        XCTAssertEqual(try MCPServer.subtitleArguments([
            "language": "ru-RU", "translate": ["en", "th"], "show": "en", "voice": "en,th=com.apple.voice.th",
            "hear": "th", "original": "mute", "captions": false, "include_original": false, "keep_previous": true,
            "srt_folder": "/tmp/s", "project": "Trip",
        ]), ["subtitles", "--language", "ru-RU", "--translate", "en,th", "--show", "en", "--voice", "en,th=com.apple.voice.th",
             "--hear", "th", "--original", "mute", "--srt", "/tmp/s", "--no-original", "--keep-previous", "--no-captions", "Trip"])
        XCTAssertThrowsError(try MCPServer.subtitleArguments([:]))
        // The speakers' consent to cloning can't come from an agent.
        XCTAssertThrowsError(try MCPServer.subtitleArguments(["language": "ru", "voice": ["en=clone"]]))
    }

    func testRenderJobReportsProgressAndResult() throws {
        let runner = FakeRunner()
        runner.outputs["render"] = ([
            "not supported: Color adjustment: color correct ×1",
            "Rendering Trip → /out/Trip 1080p.mp4 (1080x1920 @ 30.0 fps, H.264)",
            "  frames  42%  100/240 frames",
            "Done in 3.0s: /out/Trip 1080p.mp4",
        ], 0, "")
        let server = MCPServer(runner: runner, version: "1", projects: { [] })
        let started = try json(call(server, "render", ["project": "Trip"]).0)
        XCTAssertEqual(started["job_id"] as? String, "job-1")
        let status = try json(call(server, "job_status", ["job_id": "job-1", "wait_seconds": 5]).0)
        XCTAssertEqual(status["status"] as? String, "done")
        XCTAssertEqual(status["progress"] as? Double, 1)
        XCTAssertEqual(status["output"] as? String, "/out/Trip 1080p.mp4")
        XCTAssertEqual(status["warnings"] as? [String], ["not supported: Color adjustment: color correct ×1"])
        XCTAssertEqual(runner.started, [["render", "Trip"]])
    }

    func testFailedJobCarriesStderr() throws {
        let runner = FakeRunner()
        runner.outputs["render"] = ([], 1, "Project not found: Nope\n")
        let server = MCPServer(runner: runner, version: "1", projects: { [] })
        _ = try call(server, "render", ["project": "Nope"])
        let status = try json(call(server, "job_status", ["job_id": "job-1"]).0)
        XCTAssertEqual(status["status"] as? String, "failed")
        XCTAssertEqual(status["error"] as? String, "Project not found: Nope")
        XCTAssertTrue(try call(server, "job_status", ["job_id": "job-9"]).1)
    }

    func testJobsRunOneAtATimeAndCancel() throws {
        let runner = FakeRunner()
        runner.hold = true
        let server = MCPServer(runner: runner, version: "1", projects: { [] })
        _ = try call(server, "render", ["project": "A"])
        _ = try call(server, "render", ["project": "B"])
        _ = try call(server, "render", ["project": "C"])
        XCTAssertEqual(runner.started.count, 1)
        let queued = try json(call(server, "job_status", ["job_id": "job-3"]).0)
        XCTAssertEqual(queued["status"] as? String, "queued")
        XCTAssertEqual(queued["position"] as? Int, 2)

        XCTAssertEqual(try json(call(server, "cancel_job", ["job_id": "job-2"]).0)["status"] as? String, "cancelled")
        XCTAssertEqual(try json(call(server, "cancel_job", ["job_id": "job-1"]).0)["status"] as? String, "cancelled")
        XCTAssertEqual(runner.stopped, 1)
        // Cancelling the running one starts the next that is still queued: C, not the cancelled B.
        XCTAssertEqual(runner.started.map(\.last), ["A", "C"])
    }

    func testQuickCommandsAndProjects() throws {
        let runner = FakeRunner()
        runner.outputs["info"] = (["Project: Trip  canvas 1080x1920", "Visual layers: 3, audio clips: 1"], 0, "")
        runner.outputs["voices"] = ([], 1, "usage: ecc voices <language code>\n")
        let server = MCPServer(runner: runner, version: "1", projects: { [] })
        XCTAssertEqual(try call(server, "project_info", ["project": "Trip", "timeline": "Cut"]).0,
                       "Project: Trip  canvas 1080x1920\nVisual layers: 3, audio clips: 1")
        XCTAssertEqual(runner.started.last, ["info", "--timeline", "Cut", "Trip"])
        let voices = try call(server, "list_voices", ["language": "th"])
        XCTAssertTrue(voices.1)
        XCTAssertEqual(voices.0, "usage: ecc voices <language code>")
        XCTAssertEqual(try call(server, "list_projects").0, "[\n\n]")
    }
}
