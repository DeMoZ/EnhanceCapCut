import Foundation

/// Runs `ecc` with some arguments. The MCP server does all its work through it, so the tools behave exactly like
/// the command line (and a crash in a render never takes the server down).
public protocol MCPRunner: Sendable {
    /// Starts `ecc arguments`: every stdout line goes to `line`, then `exit` gets the exit status and stderr.
    /// Returns what stops the run.
    func start(_ arguments: [String], line: @escaping @Sendable (String) -> Void,
               exit: @escaping @Sendable (Int32, String) -> Void) throws -> @Sendable () -> Void
}

/// The `ecc` executable in a child process.
public struct ProcessRunner: MCPRunner {
    public let executable: URL
    public init(executable: URL) { self.executable = executable }

    public func start(_ arguments: [String], line: @escaping @Sendable (String) -> Void,
                      exit: @escaping @Sendable (Int32, String) -> Void) throws -> @Sendable () -> Void {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err
        process.standardInput = FileHandle.nullDevice
        try process.run()
        let stderr = MCPBox(Data())
        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async {
            stderr.value = err.fileHandleForReading.readDataToEndOfFile()
            group.leave()
        }
        DispatchQueue.global().async {
            var pending = Data()
            while true {
                let chunk = out.fileHandleForReading.availableData
                if chunk.isEmpty { break }
                pending.append(chunk)
                while let newline = pending.firstIndex(of: UInt8(ascii: "\n")) {
                    line(String(decoding: pending[pending.startIndex..<newline], as: UTF8.self))
                    pending.removeSubrange(pending.startIndex...newline)
                }
            }
            if !pending.isEmpty { line(String(decoding: pending, as: UTF8.self)) }
            group.wait()
            process.waitUntilExit()
            exit(process.terminationStatus, String(decoding: stderr.value, as: UTF8.self))
        }
        return { if process.isRunning { process.terminate() } }
    }
}

final class MCPBox<T>: @unchecked Sendable {
    var value: T
    init(_ value: T) { self.value = value }
}

/// The MCP (Model Context Protocol) server behind `ecc mcp`, so AI agents can list projects, read what will be
/// rendered, export and add subtitles. JSON-RPC 2.0, one message per line on stdin/stdout.
///
/// Renders and subtitles take minutes, longer than clients wait for a tool call, so they are jobs: the tool
/// returns a job id at once and `job_status` reports progress (optionally waiting a while for the end). Jobs run
/// one at a time, in the order they were given, each as `ecc render …` / `ecc subtitles …` in a child process.
///
/// The C# twin is `windows/src/EnhanceCapCut.Draft/McpServer.cs`: same tools, arguments and results, apart from
/// what the platforms differ in (codecs, the speech engine, `original: replace`).
public final class MCPServer: @unchecked Sendable {
    public typealias Object = [String: Any]

    /// Protocol revisions this server speaks; the newest one is offered to clients asking for another.
    static let protocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]
    /// Longest `job_status` wait, below the usual client timeout for one tool call.
    static let maxWaitSeconds = 50.0
    /// How many output lines a job keeps, and how many of the last ones `job_status` shows.
    static let keptLines = 200, shownLines = 20

    let runner: MCPRunner
    let version: String
    let projects: () -> [CapCutProject]
    private let lock = NSLock()
    private var jobs: [Job] = []
    private var nextID = 1

    public init(runner: MCPRunner, version: String, projects: @escaping () -> [CapCutProject] = { ProjectLocator.allProjects() }) {
        self.runner = runner
        self.version = version
        self.projects = projects
    }

    /// Serves stdin until it closes, then stops the running job: nobody is left to collect it.
    public func serve() {
        let output = NSLock()
        let group = DispatchGroup()
        while let line = readLine(strippingNewline: true) {
            let respond = {
                guard let response = self.handle(line) else { return }
                output.lock()
                FileHandle.standardOutput.write(Data((response + "\n").utf8))
                output.unlock()
            }
            // In order, except a job_status that waits: that one mustn't hold up the others.
            if Self.waits(line) { DispatchQueue.global().async(group: group, execute: respond) } else { respond() }
        }
        group.wait()
        cancelAll()
    }

    /// Whether the message is a job_status call with a wait.
    static func waits(_ line: String) -> Bool {
        guard let message = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? Object,
              message["method"] as? String == "tools/call", let params = message["params"] as? Object,
              params["name"] as? String == "job_status", let arguments = params["arguments"] as? Object else { return false }
        return ((try? number(arguments, "wait_seconds")).flatMap { $0 } ?? 0) > 0
    }

    /// One JSON-RPC message in, the response line out (none for notifications).
    public func handle(_ line: String) -> String? {
        guard !line.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        guard let message = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? Object else {
            return encode(["jsonrpc": "2.0", "id": NSNull(), "error": ["code": -32700, "message": "Parse error"]])
        }
        guard let id = message["id"], !(id is NSNull) else { return nil }
        let method = message["method"] as? String ?? ""
        let params = message["params"] as? Object ?? [:]
        var response: Object = ["jsonrpc": "2.0", "id": id]
        switch method {
        case "initialize":
            let asked = params["protocolVersion"] as? String ?? ""
            response["result"] = [
                "protocolVersion": Self.protocolVersions.contains(asked) ? asked : Self.protocolVersions[0],
                "capabilities": ["tools": [String: Any]()],
                "serverInfo": ["name": "enhancecapcut", "title": "EnhanceCapCut", "version": version],
                "instructions": Self.instructions,
            ] as Object
        case "ping":
            response["result"] = [String: Any]()
        case "tools/list":
            response["result"] = ["tools": Self.tools]
        case "tools/call":
            let name = params["name"] as? String ?? ""
            guard Self.tools.contains(where: { $0["name"] as? String == name }) else {
                response["error"] = ["code": -32602, "message": "Unknown tool: \(name)"] as Object
                break
            }
            let arguments = params["arguments"] as? Object ?? [:]
            do {
                response["result"] = ["content": [["type": "text", "text": try call(name, arguments)]], "isError": false] as Object
            } catch {
                let text = (error as? MCPError)?.message ?? "\(error)"
                response["result"] = ["content": [["type": "text", "text": text]], "isError": true] as Object
            }
        default:
            response["error"] = ["code": -32601, "message": "Method not found: \(method)"] as Object
        }
        return encode(response)
    }

    func call(_ tool: String, _ a: Object) throws -> String {
        switch tool {
        case "list_projects":
            return json(projects().map { p in
                ["name": p.name, "open": p.isOpen, "modified": ISO8601DateFormatter().string(from: p.modified),
                 "timelines": p.timelines().map { ["name": $0.name, "main": $0.isMain] as Object }] as Object
            })
        case "project_info":
            return try capture(["info"] + Self.timelineArguments(a))
        case "list_voices":
            return try capture(["voices", try Self.string(a, "language", required: true)!])
        case "render":
            return json(status(enqueue(tool, try Self.renderArguments(a))))
        case "add_subtitles":
            return json(status(enqueue(tool, try Self.subtitleArguments(a))))
        case "job_status":
            let job = try find(a)
            let wait = min(max(try Self.number(a, "wait_seconds") ?? 0, 0), Self.maxWaitSeconds)
            let deadline = Date().addingTimeInterval(wait)
            while Date() < deadline && !synchronized({ job.isFinished }) { Thread.sleep(forTimeInterval: 0.2) }
            return json(status(job))
        case "list_jobs":
            return json(synchronized { jobs }.map { status($0, lines: 0) })
        case "cancel_job":
            let job = try find(a)
            let stop: (() -> Void)? = synchronized {
                if job.state == "queued" { job.state = "cancelled"; job.finished = Date() }
                if job.state == "running" { job.cancelRequested = true; return job.stop }
                return nil
            }
            stop?()
            return json(status(job))
        default:
            throw MCPError("Unknown tool: \(tool)")
        }
    }

    // MARK: Arguments → ecc command lines

    static func renderArguments(_ a: Object) throws -> [String] {
        var args = ["render"]
        if let v = try string(a, "codec") {
            guard codecs.contains(v) else { throw MCPError("codec must be one of " + codecs.joined(separator: ", ")) }
            args += ["--codec", v]
        }
        if let v = try number(a, "quality") {
            guard (0...100).contains(v) else { throw MCPError("quality must be 0–100") }
            args += ["--quality", format(v)]
        }
        if let v = try number(a, "bitrate_mbps") { args += ["--bitrate", format(v)] }
        if let v = try number(a, "fps") { args += ["--fps", format(v)] }
        if let v = try string(a, "aspect") {
            guard aspects.contains(v) else { throw MCPError("aspect must be one of " + aspects.joined(separator: ", ")) }
            args += ["--aspect", v]
        }
        if let v = try number(a, "height") {
            guard heights.contains(v) else { throw MCPError("height must be 0 (the project's), 720, 1080, 1440 or 2160") }
            args += ["--height", format(v)]
        }
        if let v = try number(a, "lufs") { args += ["--lufs", format(v)] }
        if try bool(a, "subtitles") == false { args.append("--no-subtitles") }
        if let v = try number(a, "from_seconds") { args += ["--from", format(v)] }
        if let v = try number(a, "duration_seconds") { args += ["--duration", format(v)] }
        if let v = try string(a, "output") { args += ["-o", v] }
        return args + (try timelineArguments(a))
    }

    static func subtitleArguments(_ a: Object) throws -> [String] {
        var args = ["subtitles", "--language", try string(a, "language", required: true)!]
        if let v = try strings(a, "translate"), !v.isEmpty { args += ["--translate", v.joined(separator: ",")] }
        if let v = try string(a, "show") { args += ["--show", v] }
        if let v = try strings(a, "voice"), !v.isEmpty {
            // Cloning copies a real person's voice and needs their consent, which only a person can confirm.
            guard !v.contains(where: { $0.lowercased().hasSuffix("=clone") }) else {
                throw MCPError("Voice cloning needs the speakers' consent, confirmed by a person: use the app or "
                    + "`ecc subtitles … --consent` instead.")
            }
            args += ["--voice", v.joined(separator: ",")]
        }
        if let v = try string(a, "hear") { args += ["--hear", v] }
        if let v = try string(a, "original") {
            guard originals.contains(v) else { throw MCPError("original must be one of " + originals.joined(separator: ", ")) }
            args += ["--original", v]
        }
        if let v = try string(a, "engine") {
            guard engines.contains(v) else { throw MCPError("engine must be one of " + engines.joined(separator: ", ")) }
            args += ["--engine", v]
        }
        if let v = try string(a, "srt_folder") { args += ["--srt", v] }
        if try bool(a, "include_original") == false { args.append("--no-original") }
        if try bool(a, "keep_previous") == true { args.append("--keep-previous") }
        if try bool(a, "captions") == false { args.append("--no-captions") }
        return args + (try timelineArguments(a))
    }

    /// `--timeline` and the project, last: `ecc` takes the first argument that isn't an option as the project.
    static func timelineArguments(_ a: Object) throws -> [String] {
        var args: [String] = []
        if let v = try string(a, "timeline") { args += ["--timeline", v] }
        if let v = try string(a, "project") {
            guard !v.hasPrefix("-") else { throw MCPError("project must be a project name or folder") }
            args.append(v)
        }
        return args
    }

    static func format(_ v: Double) -> String {
        v == v.rounded() && abs(v) < 1e15 ? String(Int64(v)) : String(v)
    }

    static func string(_ a: Object, _ key: String, required: Bool = false) throws -> String? {
        switch a[key] {
        case nil, is NSNull:
            if required { throw MCPError("\(key) is required") }
            return nil
        case let v as String:
            if v.isEmpty && required { throw MCPError("\(key) is required") }
            return v.isEmpty ? nil : v
        case let v as NSNumber where !isBool(v): return format(v.doubleValue)
        default: throw MCPError("\(key) must be a string")
        }
    }

    static func number(_ a: Object, _ key: String) throws -> Double? {
        switch a[key] {
        case nil, is NSNull: return nil
        case let v as NSNumber where !isBool(v): return v.doubleValue
        case let v as String:
            guard let d = Double(v) else { throw MCPError("\(key) must be a number") }
            return d
        default: throw MCPError("\(key) must be a number")
        }
    }

    static func bool(_ a: Object, _ key: String) throws -> Bool? {
        switch a[key] {
        case nil, is NSNull: return nil
        case let v as NSNumber where isBool(v): return v.boolValue
        default: throw MCPError("\(key) must be true or false")
        }
    }

    /// A list of strings, or one comma-separated string.
    static func strings(_ a: Object, _ key: String) throws -> [String]? {
        switch a[key] {
        case nil, is NSNull: return nil
        case let v as String: return v.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        case let v as [Any]:
            return try v.map { guard let s = $0 as? String else { throw MCPError("\(key) must be a list of strings") }; return s }
        default: throw MCPError("\(key) must be a list of strings")
        }
    }

    private static func isBool(_ n: NSNumber) -> Bool { CFGetTypeID(n) == CFBooleanGetTypeID() }

    // MARK: Jobs

    final class Job {
        let id: String, tool: String, arguments: [String]
        var state = "queued"  // queued, running, done, failed, cancelled
        var progress: Double?, phase = "", output: String?
        var files: [String] = [], warnings: [String] = [], lines: [String] = []
        var error: String?
        let created = Date()
        var started: Date?, finished: Date?
        var stop: (() -> Void)?
        var cancelRequested = false
        var isFinished: Bool { ["done", "failed", "cancelled"].contains(state) }
        init(id: String, tool: String, arguments: [String]) { self.id = id; self.tool = tool; self.arguments = arguments }
    }

    private func synchronized<T>(_ body: () throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body()
    }

    func enqueue(_ tool: String, _ arguments: [String]) -> Job {
        let job = synchronized {
            let job = Job(id: "job-\(nextID)", tool: tool, arguments: arguments)
            nextID += 1
            jobs.append(job)
            return job
        }
        startNext()
        return job
    }

    private func startNext() {
        let next: Job? = synchronized {
            guard !jobs.contains(where: { $0.state == "running" }),
                  let job = jobs.first(where: { $0.state == "queued" }) else { return nil }
            job.state = "running"
            job.started = Date()
            return job
        }
        guard let job = next else { return }
        do {
            let stop = try runner.start(job.arguments, line: { [weak self] line in
                self?.synchronized { Self.read(line, into: job) }
            }, exit: { [weak self] code, stderr in
                guard let self else { return }
                self.synchronized {
                    job.finished = Date()
                    job.stop = nil
                    if job.cancelRequested {
                        job.state = "cancelled"
                    } else if code == 0 {
                        job.state = "done"
                        job.progress = 1
                    } else {
                        job.state = "failed"
                        let lines = stderr.split(whereSeparator: \.isNewline).map(String.init).filter { !$0.isEmpty }
                        job.error = lines.isEmpty ? "ecc exited with code \(code)" : lines.suffix(5).joined(separator: "\n")
                    }
                }
                self.startNext()
            })
            synchronized {
                if job.state == "running" { job.stop = stop }
            }
        } catch {
            synchronized {
                job.state = "failed"
                job.error = "Couldn't start ecc: \(error.localizedDescription)"
                job.finished = Date()
            }
            startNext()
        }
    }

    /// What a line of `ecc render` / `ecc subtitles` output says about the job.
    static func read(_ raw: String, into job: Job) {
        let line = raw.trimmingCharacters(in: .whitespaces)
        guard !line.isEmpty else { return }
        job.lines.append(line)
        if job.lines.count > keptLines { job.lines.removeFirst(job.lines.count - keptLines) }
        if let range = line.range(of: #"(\d+(\.\d+)?)%"#, options: .regularExpression),
           let percent = Double(line[range].dropLast()) {
            job.progress = min(percent / 100, 1)
        }
        if line.hasPrefix("Rendering "), let arrow = line.range(of: " → "),
           let paren = line.range(of: " (", options: .backwards), arrow.upperBound <= paren.lowerBound {
            job.output = String(line[arrow.upperBound..<paren.lowerBound])
        } else if line.hasPrefix("Done in "), let colon = line.range(of: ": ") {
            job.output = String(line[colon.upperBound...])
        } else if let prefix = ["Wrote ", "Updated "].first(where: line.hasPrefix) {
            job.files.append(String(line.dropFirst(prefix.count)))
        } else if ["not supported:", "missing:", "warning:"].contains(where: line.hasPrefix) {
            job.warnings.append(line)
        } else {
            job.phase = line
        }
    }

    private func find(_ a: Object) throws -> Job {
        let id = try Self.string(a, "job_id", required: true)!
        guard let job = synchronized({ jobs.first { $0.id == id } }) else { throw MCPError("No job \(id)") }
        return job
    }

    func status(_ job: Job, lines: Int = shownLines) -> Object {
        synchronized {
            var s: Object = ["job_id": job.id, "tool": job.tool, "status": job.state,
                             "command": (["ecc"] + job.arguments).joined(separator: " ")]
            if let p = job.progress { s["progress"] = (p * 1000).rounded() / 1000 }
            if !job.phase.isEmpty && !job.isFinished { s["phase"] = job.phase }
            if let o = job.output { s["output"] = o }
            if !job.files.isEmpty { s["files"] = job.files }
            if !job.warnings.isEmpty { s["warnings"] = job.warnings }
            if let e = job.error { s["error"] = e }
            if let started = job.started {
                s["elapsed_seconds"] = ((job.finished ?? Date()).timeIntervalSince(started) * 10).rounded() / 10
            }
            if job.state == "queued" {
                s["position"] = jobs.filter { $0.state == "queued" }.firstIndex { $0 === job }.map { $0 + 1 } ?? 0
            }
            if lines > 0 && !job.lines.isEmpty { s["log"] = Array(job.lines.suffix(lines)) }
            return s
        }
    }

    func cancelAll() {
        let stops: [() -> Void] = synchronized {
            for job in jobs where job.state == "queued" { job.state = "cancelled" }
            return jobs.filter { $0.state == "running" }.compactMap { job in job.cancelRequested = true; return job.stop }
        }
        stops.forEach { $0() }
    }

    /// Runs a quick `ecc` command and returns its output.
    func capture(_ arguments: [String]) throws -> String {
        let out = MCPBox<[String]>([]), result = MCPBox<(Int32, String)>((0, ""))
        let done = DispatchSemaphore(value: 0)
        let outLock = NSLock()
        _ = try runner.start(arguments, line: { line in
            outLock.lock(); out.value.append(line); outLock.unlock()
        }, exit: { code, stderr in
            result.value = (code, stderr)
            done.signal()
        })
        done.wait()
        let text = outLock.withLock { out.value.joined(separator: "\n") }
        guard result.value.0 == 0 else {
            let error = result.value.1.trimmingCharacters(in: .whitespacesAndNewlines)
            throw MCPError(error.isEmpty ? "ecc exited with code \(result.value.0)" : error)
        }
        return text
    }

    private func encode(_ object: Object) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: object, options: [.withoutEscapingSlashes])) ?? Data("{}".utf8)
        return String(decoding: data, as: UTF8.self)
    }

    private func json(_ value: Any) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]))
            ?? Data("null".utf8)
        return String(decoding: data, as: UTF8.self)
    }

    // MARK: Tool definitions

    static let codecs = ExportSettings.Codec.allCases.map(\.rawValue)
    static let aspects = ExportSettings.Aspect.allCases.map(\.rawValue)
    static let heights: [Double] = [0, 720, 1080, 1440, 2160]
    static let originals = ["keep", "lower", "mute", "replace", "remove"]
    static let engines = ["apple", "whisper"]

    static let instructions = """
        EnhanceCapCut renders CapCut projects with its own renderer. Projects are named as in list_projects; \
        without a project the one open in CapCut is used. render and add_subtitles start jobs that run one at a \
        time: poll job_status (wait_seconds up to 50 to wait for the end) until status is done, failed or \
        cancelled. Options a render doesn't give fall back to the app's saved export settings. Check project_info \
        first: it lists features that won't be rendered and missing media.
        """

    private static let project: Object = ["type": "string",
        "description": "Project name (as in list_projects) or folder; the project open in CapCut when omitted"]
    private static let timeline: Object = ["type": "string",
        "description": "Timeline name or 1-based number in a project with several; the main one when omitted"]

    static let tools: [Object] = [
        tool("list_projects", "List CapCut projects: name, whether open in CapCut, last change, timelines.", [:]),
        tool("project_info", "What will be rendered from a project: canvas, fps, duration, layers, audio clips, "
            + "CapCut features not supported yet, missing media.",
             ["project": project, "timeline": timeline]),
        tool("render", "Export a project to a video file. Starts a job and returns its id at once; follow it with "
            + "job_status. Options left out come from the app's saved export settings.",
             ["project": project, "timeline": timeline,
              "output": ["type": "string", "description": "Output file (.mp4, .mov for ProRes); the app's export folder and file name pattern when omitted"],
              "codec": ["type": "string", "enum": codecs],
              "quality": ["type": "number", "minimum": 0, "maximum": 100, "description": "Constant quality 0–100"],
              "bitrate_mbps": ["type": "number", "description": "Average bitrate instead of quality"],
              "fps": ["type": "number", "description": "Frame rate; 0 = the project's"],
              "aspect": ["type": "string", "enum": aspects, "description": "Frame shape; other than the project's crops to fill"],
              "height": ["type": "integer", "enum": [0, 720, 1080, 1440, 2160], "description": "Output height; 0 = the project's"],
              "lufs": ["type": "number", "description": "Normalize loudness to this integrated LUFS, e.g. -14"],
              "subtitles": ["type": "boolean", "description": "Burn in the project's subtitles (default from settings)"],
              "from_seconds": ["type": "number", "description": "Render a part only: start"],
              "duration_seconds": ["type": "number", "description": "Render a part only: length"]]),
        tool("add_subtitles", "Transcribe a timeline and add captions to the CapCut project, optionally translated and "
            + "voiced over (macOS 26+). Writes into the project, keeping a backup; voice-over goes to a new timeline. "
            + "Starts a job; follow it with job_status.",
             ["project": project, "timeline": timeline,
              "language": ["type": "string", "description": "Spoken language, e.g. ru-RU"],
              "translate": ["type": "array", "items": ["type": "string"], "description": "Languages to translate into, e.g. [\"en\", \"th\"]"],
              "show": ["type": "string", "description": "Language of the visible caption track"],
              "include_original": ["type": "boolean", "description": "Keep a track in the spoken language (default true)"],
              "captions": ["type": "boolean", "description": "Add caption tracks (default true); false = voice-over only"],
              "keep_previous": ["type": "boolean", "description": "Keep caption tracks added earlier (default: replace them)"],
              "voice": ["type": "array", "items": ["type": "string"],
                        "description": "Voice-over languages, each \"th\" or \"th=<voice id from list_voices>\""],
              "hear": ["type": "string", "description": "Language of the audible voice-over track"],
              "original": ["type": "string", "enum": originals, "description": "Original audio under the voice-over (default lower)"],
              "engine": ["type": "string", "enum": engines, "description": "Speech recognition (default apple)"],
              "srt_folder": ["type": "string", "description": "Also write .srt files there"]],
             required: ["language"]),
        tool("list_voices", "Installed voices for a language, with the ids add_subtitles takes.",
             ["language": ["type": "string", "description": "Language code, e.g. th"]], required: ["language"]),
        tool("job_status", "Progress and result of a render or add_subtitles job: status (queued, running, done, "
            + "failed, cancelled), progress 0–1, output file, warnings, error, last log lines.",
             ["job_id": ["type": "string"],
              "wait_seconds": ["type": "number", "minimum": 0, "maximum": 50,
                               "description": "Wait up to this long for the job to finish before answering"]],
             required: ["job_id"]),
        tool("list_jobs", "All jobs of this session with their status.", [:]),
        tool("cancel_job", "Cancel a queued or running job.", ["job_id": ["type": "string"]], required: ["job_id"]),
    ]

    private static func tool(_ name: String, _ description: String, _ properties: Object, required: [String] = []) -> Object {
        var schema: Object = ["type": "object", "properties": properties]
        if !required.isEmpty { schema["required"] = required }
        return ["name": name, "description": description, "inputSchema": schema]
    }
}

struct MCPError: Error {
    let message: String
    init(_ message: String) { self.message = message }
}
