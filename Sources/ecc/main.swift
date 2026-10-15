import EnhanceCapCutCore
import Foundation

// Command-line front end, used for automation and for verifying renders without the UI.
//   ecc list
//   ecc info [project]
//   ecc render [project] [-o file] [--codec h264|hevc|hevc10|prores|proreshq] [--quality 0-100 | --bitrate Mbps]
//              [--fps N] [--aspect project|9:16|1:1|16:9] [--height N] [--lufs -14] [--no-subtitles]
//              [--from s] [--duration s]
//   ecc voice <media> -o out.wav [--keep-voice | --remove-voice] [--denoise 0.85] [--enhance 75]

func fail(_ message: String) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(1)
}

var args = Array(CommandLine.arguments.dropFirst())
let command = args.isEmpty ? "help" : args.removeFirst()

func option(_ name: String) -> String? {
    guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
    let value = args[i + 1]
    args.removeSubrange(i...(i + 1))
    return value
}

func project() -> CapCutProject {
    if let arg = args.first(where: { !$0.hasPrefix("-") }) {
        guard let project = ProjectLocator.resolve(arg) else { fail("Project not found: \(arg)") }
        return project
    }
    guard let open = ProjectLocator.openProject() else { fail("No project is open in CapCut; pass a name") }
    return open
}

func seconds(_ us: Int64) -> String { String(format: "%.2fs", Double(us) / 1_000_000) }

switch command {
case "list":
    for p in ProjectLocator.allProjects() {
        print("\(p.isOpen ? "*" : " ") \(p.name)\t\(p.modified)")
    }

case "info":
    let p = project()
    let timeline = try TimelineBuilder.build(project: p)
    print("Project: \(p.name)  canvas \(Int(timeline.canvasSize.width))x\(Int(timeline.canvasSize.height))  "
        + "\(timeline.fps) fps  duration \(seconds(timeline.duration))")
    print("Visual layers: \(timeline.layers.count), audio clips: \(timeline.audioClips.count)")
    for layer in timeline.layers {
        let kind: String
        switch layer.content {
        case let .video(url): kind = "video " + url.lastPathComponent
        case let .photo(url): kind = "photo " + url.lastPathComponent
        case let .text(spec): kind = "text \"" + spec.content.text.prefix(30).replacingOccurrences(of: "\n", with: " ") + "\""
        case let .compound(t): kind = "compound (\(t.layers.count) layers)"
        }
        let speed = layer.timeMap.isConstant ? String(format: "x%.2f", layer.timeMap.averageSpeed) : "curve"
        print("  [\(layer.renderIndex)/t\(layer.trackIndex)] \(seconds(layer.target.start))+\(seconds(layer.target.duration)) \(speed) \(kind)")
    }
    for clip in timeline.audioClips {
        var fx = ""
        if clip.voice.separation == .keepVoice { fx += " [keep voice]" }
        if clip.voice.separation == .removeVoice { fx += " [remove voice]" }
        if let d = clip.voice.denoise { fx += String(format: " [denoise %.2f]", d) }
        if let e = clip.voice.enhance { fx += String(format: " [enhance %.0f]", e * 100) }
        print("  audio \(seconds(clip.target.start))+\(seconds(clip.target.duration)) vol \(String(format: "%.2f", clip.volume)) \(clip.url.lastPathComponent)\(fx)")
    }
    if !timeline.report.unsupported.isEmpty {
        print("Not supported yet:")
        for item in timeline.report.unsupported { print("  \(item.category): \(item.name) ×\(item.count)") }
    }
    if !timeline.report.missingFiles.isEmpty {
        print("Missing media:")
        for file in timeline.report.missingFiles { print("  \(file)") }
    }

case "render":
    // Start from the app's saved settings so CLI renders match the last export from the window.
    var settings = ExportSettings.load(from: UserDefaults(suiteName: "dev.enhancecapcut.EnhanceCapCut") ?? .standard)
    if let v = option("--codec") {
        guard let c = ExportSettings.Codec(rawValue: v) else { fail("Unknown codec \(v)") }
        settings.codec = c
    }
    if let v = option("--quality").flatMap(Double.init) { settings.rateControl = .quality; settings.quality = v }
    if let v = option("--bitrate").flatMap(Double.init) { settings.rateControl = .bitrate; settings.bitrateMbps = v }
    if let v = option("--fps").flatMap(Double.init) { settings.frameRate = v }
    if let v = option("--aspect") {
        guard let a = ExportSettings.Aspect(rawValue: v) else { fail("Unknown aspect \(v)") }
        settings.aspect = a
    }
    if let v = option("--height").flatMap(Int.init) {
        guard let r = ExportSettings.Resolution(rawValue: v) else { fail("Resolution must be 0, 720, 1080, 1440 or 2160") }
        settings.resolution = r
    }
    if args.contains("--no-subtitles") { settings.burnInSubtitles = false; args.removeAll { $0 == "--no-subtitles" } }
    if let v = option("--lufs").flatMap(Double.init) { settings.normalizeLoudness = true; settings.targetLUFS = v }
    let from = option("--from").flatMap(Double.init)
    let length = option("--duration").flatMap(Double.init)
    let out = option("-o")
    let p = project()
    let timeline = try TimelineBuilder.build(project: p)
    let size = settings.outputSize(canvas: timeline.canvasSize)
    let fps = settings.fps(project: timeline.fps)
    let url = out.map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
        ?? URL(fileURLWithPath: settings.outputFolder).appendingPathComponent(settings.fileName(project: p.name, size: size, fps: fps))
    let exporter = Exporter(timeline: timeline, settings: settings, outputURL: url)
    if from != nil || length != nil {
        let start = Int64((from ?? 0) * 1_000_000)
        let duration = length.map { Int64($0 * 1_000_000) } ?? (timeline.duration - start)
        exporter.range = TimeRange(start: start, duration: min(duration, timeline.duration - start))
    }
    for item in timeline.report.unsupported { print("not supported: \(item.category): \(item.name) ×\(item.count)") }
    for file in timeline.report.missingFiles { print("missing: \(file)") }
    print("Rendering \(p.name) → \(url.path) (\(Int(size.width))x\(Int(size.height)) @ \(fps) fps, \(settings.codec.title))")
    var lastPrint = Date.distantPast
    let started = Date()
    try await exporter.run { progress in
        if Date().timeIntervalSince(lastPrint) > 2 || progress.fraction >= 1 {
            lastPrint = Date()
            print(String(format: "  %@ %3.0f%%  %d/%d frames", progress.phase.rawValue, progress.fraction * 100,
                         progress.framesDone, progress.framesTotal))
        }
    }
    for warning in exporter.warnings { print("warning: \(warning)") }
    print(String(format: "Done in %.1fs: %@", Date().timeIntervalSince(started), url.path))

case "voice":
    // Audition "Isolate voice" / "Reduce noise" / "Enhance voice" on a single media file.
    var effects = VoiceEffects()
    if let i = args.firstIndex(of: "--keep-voice") { effects.separation = .keepVoice; args.remove(at: i) }
    if let i = args.firstIndex(of: "--remove-voice") { effects.separation = .removeVoice; args.remove(at: i) }
    if let v = option("--denoise").flatMap(Double.init) { effects.denoise = v }
    if let v = option("--enhance").flatMap(Double.init) { effects.enhance = v / 100 }
    guard let out = option("-o"), let input = args.first else { fail("usage: ecc voice <media> -o out.wav [--keep-voice | --remove-voice] [--denoise 0.85] [--enhance 75]") }
    guard !effects.isEmpty else { fail("pass --keep-voice, --remove-voice, --denoise and/or --enhance") }
    let started = Date()
    try await VoiceEffectsRenderer.render(URL(fileURLWithPath: (input as NSString).expandingTildeInPath), effects: effects,
                                          to: URL(fileURLWithPath: (out as NSString).expandingTildeInPath))
    print(String(format: "Done in %.1fs: %@", Date().timeIntervalSince(started), out))

default:
    print("usage: ecc list | info [project] | render [project] [options] | voice <media> -o out.wav [options]")
}
