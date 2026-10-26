import EnhanceCapCutCore
import Foundation

// Command-line front end, used for automation and for verifying renders without the UI.
//   ecc list
//   ecc info [project] [--timeline name|number]
//   ecc render [project] [--timeline name|number] [-o file] [--codec h264|hevc|hevc10|prores|proreshq] [--quality 0-100 | --bitrate Mbps]
//              [--fps N] [--aspect project|9:16|1:1|16:9] [--height N] [--lufs -14] [--no-subtitles]
//              [--from s] [--duration s]
//   ecc voice <media> -o out.wav [--keep-voice | --remove-voice] [--denoise 0.85] [--enhance 75]
//             [--filter <cached effect dir | resource id>] [--strength 1]
//   ecc subtitles [project] [--timeline name|number] --language ru-RU [--engine apple|whisper] [--translate en,th]
//                 [--no-original] [--show en]
//                 [--keep-previous] [--srt folder] [--draft out.json] [--no-captions]
//                 [--voice en,th | en=<voice id>,th | en=clone] [--hear th] [--original keep|lower|mute|replace]
//                 [--exaggeration 0.5] [--consent]
//                 (macOS 26+; writes into the project unless --draft is given; translation languages must be downloaded;
//                 --engine whisper downloads its model on first use (574 MB);
//                 --voice speaks these translations with system voices, `ecc voices <language>` lists them;
//                 `clone` speaks in the original speaker's voice — only with their consent, which --consent confirms —
//                 downloading its model on first use (1.55 GB); --original replace keeps the background under the voice)
//   ecc voices <language>
//   ecc clone <reference audio> --language en --text "…" -o out.wav [--exaggeration 0.5] [--seed 1]
//             (speaks the text in the reference's voice; downloads the voice cloning model on first use, 1.55 GB)

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

/// `--timeline` picks one of the project's timelines by name or 1-based number; the main one by default.
func timeline(_ arg: String?, of project: CapCutProject) -> ProjectTimeline {
    let all = project.timelines()
    guard let arg else { return all.first(where: \.isMain) ?? all[0] }
    if let t = all.first(where: { $0.name.caseInsensitiveCompare(arg) == .orderedSame || $0.id == arg }) { return t }
    if let n = Int(arg), all.indices.contains(n - 1) { return all[n - 1] }
    fail("No timeline \(arg) in \(project.name): " + all.map(\.name).joined(separator: ", "))
}

func seconds(_ us: Int64) -> String { String(format: "%.2fs", Double(us) / 1_000_000) }

switch command {
case "list":
    for p in ProjectLocator.allProjects() {
        print("\(p.isOpen ? "*" : " ") \(p.name)\t\(p.modified)")
    }

case "info":
    let timelineArg = option("--timeline")
    let p = project()
    let pt = timeline(timelineArg, of: p)
    let timeline = try TimelineBuilder.build(project: p, timeline: pt)
    let timelines = p.timelines()
    if timelines.count > 1 {
        print("Timelines: " + timelines.map { ($0 == pt ? "*" : "") + $0.name + ($0.isMain ? " (main)" : "") }
            .joined(separator: ", "))
    }
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
        if let f = clip.voice.filter { fx += " [\(f.name)\(f.strength < 1 ? String(format: " %.0f%%", f.strength * 100) : "")]" }
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
    let timelineArg = option("--timeline")
    let p = project()
    let pt = timeline(timelineArg, of: p)
    let timeline = try TimelineBuilder.build(project: p, timeline: pt)
    let name = pt.isMain ? p.name : "\(p.name) \(pt.name)"
    let size = settings.outputSize(canvas: timeline.canvasSize)
    let fps = settings.fps(project: timeline.fps)
    let url = out.map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
        ?? URL(fileURLWithPath: settings.outputFolder).appendingPathComponent(settings.fileName(project: name, size: size, fps: fps))
    let exporter = Exporter(timeline: timeline, settings: settings, outputURL: url)
    if from != nil || length != nil {
        let start = Int64((from ?? 0) * 1_000_000)
        let duration = length.map { Int64($0 * 1_000_000) } ?? (timeline.duration - start)
        exporter.range = TimeRange(start: start, duration: min(duration, timeline.duration - start))
    }
    for item in timeline.report.unsupported { print("not supported: \(item.category): \(item.name) ×\(item.count)") }
    for file in timeline.report.missingFiles { print("missing: \(file)") }
    print("Rendering \(name) → \(url.path) (\(Int(size.width))x\(Int(size.height)) @ \(fps) fps, \(settings.codec.title))")
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
    // Audition "Isolate voice" / "Reduce noise" / "Enhance voice" / a voice filter on a single media file.
    var effects = VoiceEffects()
    if let i = args.firstIndex(of: "--keep-voice") { effects.separation = .keepVoice; args.remove(at: i) }
    if let i = args.firstIndex(of: "--remove-voice") { effects.separation = .removeVoice; args.remove(at: i) }
    if let v = option("--denoise").flatMap(Double.init) { effects.denoise = v }
    if let v = option("--enhance").flatMap(Double.init) { effects.enhance = v / 100 }
    let strength = option("--strength").flatMap(Double.init) ?? 1
    if let f = option("--filter") {
        let path = URL(fileURLWithPath: (f as NSString).expandingTildeInPath)
        guard let dir = VoiceFilter.locate(path: path, resourceID: f) else { fail("No cached voice filter preset: \(f)") }
        effects.filter = VoiceFilter(name: dir.deletingLastPathComponent().lastPathComponent, directory: dir, strength: strength)
    }
    guard let out = option("-o"), let input = args.first else {
        fail("usage: ecc voice <media> -o out.wav [--keep-voice | --remove-voice] [--denoise 0.85] [--enhance 75] [--filter dir|id] [--strength 1]")
    }
    guard !effects.isEmpty else { fail("pass --keep-voice, --remove-voice, --denoise, --enhance and/or --filter") }
    let started = Date()
    try await VoiceEffectsRenderer.render(URL(fileURLWithPath: (input as NSString).expandingTildeInPath), effects: effects,
                                          to: URL(fileURLWithPath: (out as NSString).expandingTildeInPath))
    print(String(format: "Done in %.1fs: %@", Date().timeIntervalSince(started), out))

case "subtitles":
    guard #available(macOS 26, *) else { fail("Subtitles need macOS 26 or later") }
    guard let localeID = option("--language") else { fail("pass the spoken language: --language ru-RU") }
    let targetCodes = option("--translate")?.split(separator: ",").map(String.init) ?? []
    let visible = option("--show")
    let srtFolder = option("--srt")
    let draftOut = option("--draft")
    let timelineArg = option("--timeline")
    // "en,th" or "en=<voice id>,th": voice-over languages, each with the best installed voice unless one is given.
    var voices: [String: String] = [:]
    for item in option("--voice")?.split(separator: ",") ?? [] {
        let parts = item.split(separator: "=", maxSplits: 1).map(String.init)
        voices[parts[0]] = parts.count > 1 ? parts[1] : ""
    }
    let hear = option("--hear")
    let engineArg = option("--engine") ?? "apple"
    guard let engine = SpeechEngine(rawValue: engineArg.lowercased()) else { fail("--engine takes apple or whisper") }
    let originalArg = option("--original") ?? "lower"
    guard let original = OriginalAudio.allCases.first(where: {
        $0.rawValue.split(separator: " ")[0].lowercased() == originalArg.lowercased()
    }) else {
        fail("--original takes keep, lower, mute or replace")
    }
    let exaggeration = Float(option("--exaggeration") ?? "") ?? 0.5
    if voices.values.contains(Dubber.clonedVoiceID) {
        guard args.contains("--consent") else {
            fail("Cloning copies a real person's voice. Use it only for voices you have the right to use: your own, or "
                + "people who agreed to it. Add --consent to confirm you have their consent.")
        }
        for code in voices.keys where voices[code] == Dubber.clonedVoiceID && VoiceCloner.modelLanguage(code) == nil {
            fail("Voice cloning doesn't speak \(code); give it a system voice instead")
        }
    }
    args.removeAll { $0 == "--consent" }
    let includeOriginal = !args.contains("--no-original")
    let replace = !args.contains("--keep-previous")
    let captions = !args.contains("--no-captions")
    args.removeAll { $0 == "--no-original" || $0 == "--keep-previous" || $0 == "--no-captions" }
    let p = project()
    let pt = timeline(timelineArg, of: p)
    let timeline = try SubtitlePipeline.speechTimeline(project: p, timeline: pt)
    let targets = (targetCodes + voices.keys.filter { !targetCodes.contains($0) }.sorted())
        .map { SubtitleLanguage(Locale.Language(identifier: $0)) }
    let pipeline = SubtitlePipeline(translator: InstalledTranslationProvider())
    var options = SubtitlePipeline.Options(sourceLocale: Locale(identifier: localeID), includeOriginal: includeOriginal,
                                           targets: captions ? targets : targets.filter { voices[$0.code] != nil },
                                           visibleLanguage: visible)
    options.captions = captions
    options.engine = engine
    options.voices = voices
    options.exaggeration = exaggeration
    options.background = original == .replace
    options.audibleVoice = hear
    // A dry run (--draft) keeps the voice-over audio next to the draft it writes.
    options.voiceOverFolder = draftOut.map {
        URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath).deletingLastPathComponent()
            .appendingPathComponent("voiceover", isDirectory: true)
    } ?? SubtitlePipeline.voiceOverFolder(in: p)
    var lastStep = ""
    let output = try await pipeline.run(timeline: timeline, options: options) { step, _ in
        if step != lastStep { lastStep = step; print("  \(step)") }
    }
    let tracks = output.captions
    for track in output.voiceOver {
        print("voice-over \(track.language.code)\(track.isAudible ? " (audible)" : ""): \(track.lines.count) lines → \(track.url.path)")
    }
    for track in tracks {
        print("\(track.language.code)\(track.isVisible ? " (shown)" : ""): \(track.cues.count) cues")
        for cue in track.cues.prefix(3) {
            print(String(format: "  %.2f–%.2f ", cue.start, cue.end) + cue.text.replacingOccurrences(of: "\n", with: " / "))
        }
    }
    if let srtFolder {
        let urls = try SubtitlePipeline.writeSRT(tracks, to: URL(fileURLWithPath: (srtFolder as NSString).expandingTildeInPath),
                                                 baseName: pt.isMain ? p.name : "\(p.name) \(pt.name)")
        for url in urls { print("Wrote \(url.path)") }
    }
    if let draftOut {
        let data = try SubtitlePipeline.apply(output, to: Data(contentsOf: pt.draftURL), original: original,
                                              replacePrevious: replace, projectURL: p.url)
        try data.write(to: URL(fileURLWithPath: (draftOut as NSString).expandingTildeInPath))
        print("Wrote \(draftOut)")
    } else {
        let count = tracks.reduce(0) { $0 + $1.cues.count }
        let result = try DraftSubtitleWriter.write(project: p, timeline: pt, cueCount: count) {
            try SubtitlePipeline.apply(output, to: $0, original: original, replacePrevious: replace, projectURL: p.url)
        }
        for url in result.files { print("Updated \(url.path)") }
        print("Backup: \(result.backupFolder.path)")
    }

case "clone":
    // Audition voice cloning: the text spoken in the voice of a reference recording.
    guard let language = option("--language"), let text = option("--text"), let out = option("-o"),
          let reference = args.first(where: { !$0.hasPrefix("-") }) else {
        fail("usage: ecc clone <reference audio> --language en --text \"…\" -o out.wav [--exaggeration 0.5] [--seed 1]")
    }
    guard let code = VoiceCloner.modelLanguage(language) else { fail("Voice cloning doesn't speak \(language)") }
    let exaggeration = Float(option("--exaggeration") ?? "") ?? 0.5
    let seed = UInt64(option("--seed") ?? "") ?? 1
    if !ModelStore.isDownloaded(ModelStore.voiceCloning) {
        print("Downloading the voice cloning model (one time)…")
        try await ModelStore.ensure(ModelStore.voiceCloning) { _ in }
    }
    var started = Date()
    let cloner = try VoiceCloner()
    print(String(format: "Loaded in %.1fs", Date().timeIntervalSince(started)))
    started = Date()
    let samples = try MonoAudio.read(URL(fileURLWithPath: reference), sampleRate: VoiceCloner.sampleRate)
    let voice = try cloner.voice(from: samples)
    let speech = try cloner.speak(text, language: code, voice: voice, exaggeration: exaggeration, seed: seed)
    try MonoAudio.write(speech.samples, sampleRate: VoiceCloner.sampleRate, to: URL(fileURLWithPath: out))
    print(String(format: "%.2fs of speech in %.1fs%@: %@", speech.duration, Date().timeIntervalSince(started),
                 speech.finished ? "" : " (no stop token)", out))

case "voices":
    guard let code = args.first else { fail("usage: ecc voices <language code>") }
    for voice in Dubber.voices(for: code) {
        print("\(voice.label)  [\(voice.gender?.rawValue ?? "-")]  \(voice.id)")
    }
    if SiriVoices.isAvailable {
        let wanted = Locale.Language(identifier: code).languageCode
        for voice in await SiriVoices.list() where Locale.Language(identifier: voice.language).languageCode == wanted {
            print("\(voice.label)  [\(voice.gender?.rawValue ?? "-")]  \(voice.id)")
        }
    }

default:
    print("usage: ecc list | info [project] | render [project] [options] | voice <media> -o out.wav [options] | subtitles [project] --language ru-RU [options] | voices <language> | clone <reference audio> --language en --text … -o out.wav")
}
