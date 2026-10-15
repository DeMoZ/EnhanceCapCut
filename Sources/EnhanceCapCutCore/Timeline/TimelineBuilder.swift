import CoreGraphics
import Foundation

public enum TimelineBuilder {
    public static func build(project: CapCutProject) throws -> Timeline {
        build(draft: try project.loadDraft(), projectURL: project.url)
    }

    public static func build(draft: Draft, projectURL: URL) -> Timeline {
        var builder = Builder(projectURL: projectURL)
        return builder.build(draft, depth: 0)
    }
}

private struct Builder {
    let projectURL: URL
    var unsupported: [String: [String: Int]] = [:]
    var missing: Set<String> = []
    /// Nested drafts of compound clips, built once per combination material.
    var nestedTimelines: [String: Timeline] = [:]
    var combos: [String: CombinationMaterial] = [:]
    /// Voice filter preset directories by material path and resource id (nil: not usable).
    var filterDirectories: [String: URL?] = [:]
    let fm = FileManager.default

    init(projectURL: URL) { self.projectURL = projectURL }

    mutating func build(_ draft: Draft, depth: Int) -> Timeline {
        let m = draft.materials
        let videos = Dictionary(m.videos.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let audios = Dictionary(m.audios.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let texts = Dictionary(m.texts.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let canvases = Dictionary(m.canvases.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let speeds = Dictionary(m.speeds.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let fades = Dictionary(m.audioFades.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        // Nested drafts may reference combinations stored in an outer draft.
        for combo in m.drafts where combos[combo.id] == nil { combos[combo.id] = combo }
        let loudness = Dictionary(m.loudnesses.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let separations = Dictionary(m.vocalSeparations.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let denoises = Dictionary(m.realtimeDenoises.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let enhancements = Dictionary(m.vocalBeautifys.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let audioEffects = Dictionary(m.audioEffects.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        var generic: [String: (String, GenericMaterial)] = [:]
        for (key, items) in m.all { for item in items { generic[item.id] = (key, item) } }

        let mainTrack = draft.tracks.firstIndex { $0.type == "video" }
        var layers: [(VisualLayer, Int)] = []
        var clips: [AudioClip] = []
        var order = 0

        for (trackIndex, track) in draft.tracks.enumerated() {
            for segment in track.segments {
                order += 1
                let refs = segment.extraMaterialRefs
                let speed = refs.lazy.compactMap { speeds[$0] }.first
                let fade = refs.lazy.compactMap { fades[$0] }.first
                let gain = pow(10, (refs.lazy.compactMap { loudness[$0] }.first?.gainDB ?? 0) / 20)
                let timeMap = makeTimeMap(segment, speed: speed)
                var voice = VoiceEffects()
                if let choice = refs.lazy.compactMap({ separations[$0] }).first?.choice {
                    voice.separation = VoiceEffects.Separation(rawValue: choice) ?? .none
                    if voice.separation == .none, choice != 0 { note("Audio", "vocal separation mode \(choice)") }
                }
                if let denoise = refs.lazy.compactMap({ denoises[$0] }).first, denoise.isDenoise {
                    voice.denoise = min(max(denoise.rate, 0), 1)
                }
                if let enhance = refs.lazy.compactMap({ enhancements[$0] }).first, enhance.enable {
                    voice.enhance = min(max(enhance.ambientSoundLevel / 100, 0), 1)
                }
                // A voice filter we can't run (preset not cached, unknown processor) is reported instead.
                var filterIssue: String?
                if let effect = refs.lazy.compactMap({ audioEffects[$0] }).first {
                    if let directory = voiceFilterDirectory(effect) {
                        voice.filter = VoiceFilter(name: effect.name, directory: directory, strength: min(max(effect.strength, 0), 1))
                    } else {
                        filterIssue = effect.name.isEmpty ? effect.resourceID : effect.name
                    }
                }
                func reportAll() {
                    reportRefs(refs, generic: generic)
                    if let filterIssue { note("Audio effect", filterIssue) }
                }
                if track.type != "audio" { reportAll() }
                if segment.reverse { note("Reverse playback", "reversed clip (rendered forward)") }

                switch track.type {
                case "video":
                    guard !track.isHidden, segment.visible else { continue }
                    let isMain = trackIndex == mainTrack
                    let video = videos[segment.materialId]
                    var content: VisualContent?
                    var audioURL: URL?
                    var compoundAudio: [AudioClip] = []

                    if let combo = refs.compactMap({ combos[$0] }).first,
                       let timeline = nestedTimeline(combo, depth: depth) {
                        content = .compound(timeline)
                        compoundAudio = timeline.audioClips
                    } else if let video {
                        if let url = DraftLoader.resolvePath(video.path, projectURL: projectURL), fm.fileExists(atPath: url.path) {
                            content = video.type == "photo" ? .photo(url) : .video(url)
                            if video.type != "photo", video.hasAudio, !video.hasSoundSeparated { audioURL = url }
                        } else {
                            missing.insert(video.path.isEmpty ? video.name : video.path)
                        }
                    }
                    guard let content else { continue }

                    let layer = VisualLayer(
                        id: segment.id, content: content, trackIndex: trackIndex, renderIndex: segment.renderIndex,
                        isMainTrack: isMain, target: segment.targetTimerange, timeMap: timeMap,
                        crop: video?.crop ?? Crop(), clip: segment.clip ?? Clip(), uniformScale: segment.uniformScale,
                        keyframes: keyframes(segment), canvas: isMain ? refs.lazy.compactMap { canvases[$0] }.first : nil)
                    layers.append((layer, order))

                    let muted = track.isMuted || (isMain && draft.config.videoMute) || segment.volume <= 0
                    if !muted, let audioURL {
                        clips.append(AudioClip(url: audioURL, target: segment.targetTimerange, timeMap: timeMap,
                                               volume: segment.volume * gain, fadeIn: fade?.fadeIn ?? 0,
                                               fadeOut: fade?.fadeOut ?? 0, preservePitch: !segment.isToneModify,
                                               voice: voice))
                    }
                    if !muted, !compoundAudio.isEmpty {
                        clips += nestAudio(compoundAudio, in: segment, timeMap: timeMap, volume: segment.volume * gain, voice: voice)
                    }

                case "audio":
                    guard !track.isMuted, segment.volume > 0 else { continue }
                    reportAll()
                    if let combo = refs.compactMap({ combos[$0] }).first,
                       let timeline = nestedTimeline(combo, depth: depth) {
                        // Audio detached from a compound clip.
                        clips += nestAudio(timeline.audioClips, in: segment, timeMap: timeMap, volume: segment.volume * gain,
                                           voice: voice)
                        continue
                    }
                    guard let audio = audios[segment.materialId] else { continue }
                    guard let url = DraftLoader.resolvePath(audio.path, projectURL: projectURL),
                          fm.fileExists(atPath: url.path) else {
                        missing.insert(audio.path.isEmpty ? audio.name : audio.path)
                        continue
                    }
                    clips.append(AudioClip(url: url, target: segment.targetTimerange, timeMap: timeMap,
                                           volume: segment.volume * gain, fadeIn: fade?.fadeIn ?? 0,
                                           fadeOut: fade?.fadeOut ?? 0, preservePitch: !segment.isToneModify,
                                           voice: voice))

                case "text":
                    guard !track.isHidden, segment.visible, let text = texts[segment.materialId],
                          let parsed = TextContent.parse(text.content),
                          !parsed.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
                    if text.typesetting != 0 { note("Text", "vertical typesetting (rendered horizontally)") }
                    let layer = VisualLayer(
                        id: segment.id, content: .text(TextLayerSpec(material: text, content: parsed)),
                        trackIndex: trackIndex, renderIndex: segment.renderIndex, isMainTrack: false,
                        target: segment.targetTimerange, timeMap: timeMap, crop: Crop(), clip: segment.clip ?? Clip(),
                        uniformScale: segment.uniformScale, keyframes: keyframes(segment), canvas: nil)
                    layers.append((layer, order))

                default:
                    // Stickers, effects and filters on their own tracks.
                    guard !track.isHidden else { continue }
                    if let (key, item) = generic[segment.materialId] {
                        report(key: key, item: item)
                    } else {
                        note(track.type.capitalized, "segment")
                    }
                }
            }
        }

        let sorted = layers.sorted { a, b in
            if a.0.renderIndex != b.0.renderIndex { return a.0.renderIndex < b.0.renderIndex }
            if a.0.trackIndex != b.0.trackIndex { return a.0.trackIndex < b.0.trackIndex }
            return a.1 < b.1
        }.map(\.0)

        let end = max(draft.duration, sorted.map(\.target.end).max() ?? 0, clips.map(\.target.end).max() ?? 0)
        var report = RenderReport()
        report.unsupported = unsupported.flatMap { category, names in
            names.map { RenderReport.Item(category: category, name: $0.key, count: $0.value) }
        }.sorted { ($0.category, $0.name) < ($1.category, $1.name) }
        report.missingFiles = missing.sorted()

        return Timeline(name: draft.name, canvasSize: CGSize(width: draft.canvas.width, height: draft.canvas.height),
                        duration: end, fps: draft.fps > 0 ? draft.fps : 30, layers: sorted, audioClips: clips,
                        report: report)
    }

    mutating func nestedTimeline(_ combo: CombinationMaterial, depth: Int) -> Timeline? {
        if let cached = nestedTimelines[combo.id] { return cached }
        guard let draft = combo.draft, depth < 8 else { return nil }
        let timeline = build(draft, depth: depth + 1)
        nestedTimelines[combo.id] = timeline
        return timeline
    }

    func makeTimeMap(_ segment: Segment, speed: SpeedMaterial?) -> TimeMap {
        let target = segment.targetTimerange
        let speedValue = speed?.speed ?? segment.speed
        let source = segment.sourceTimerange
            ?? TimeRange(start: 0, duration: Int64((Double(target.duration) * speedValue).rounded()))
        let curve = (speed?.mode == 1) ? (speed?.curvePoints ?? []) : []
        return TimeMap(sourceStart: source.start, sourceDuration: source.duration,
                       targetDuration: target.duration, curvePoints: curve)
    }

    func keyframes(_ segment: Segment) -> [LayerProperty: KeyframeTrack] {
        var result: [LayerProperty: KeyframeTrack] = [:]
        for group in segment.keyframes {
            if let property = LayerProperty(rawValue: group.propertyType), let track = KeyframeTrack(group: group) {
                result[property] = track
            }
        }
        return result
    }

    /// Re-times a compound clip's audio into the parent timeline (constant speed only).
    /// `voice` (the compound segment's own voice effects) overrides the nested clips' settings.
    mutating func nestAudio(_ nested: [AudioClip], in segment: Segment, timeMap: TimeMap, volume: Double,
                            voice: VoiceEffects = VoiceEffects()) -> [AudioClip] {
        guard timeMap.isConstant else {
            note("Compound clip", "audio of a compound clip with a speed curve")
            return []
        }
        let speed = timeMap.averageSpeed
        let windowStart = timeMap.sourceStart, windowEnd = timeMap.sourceStart + timeMap.sourceDuration
        var result: [AudioClip] = []
        for clip in nested {
            let start = max(clip.target.start, windowStart), end = min(clip.target.end, windowEnd)
            guard end > start, clip.timeMap.isConstant else { continue }
            let innerSpeed = clip.timeMap.averageSpeed
            let srcStart = clip.timeMap.sourceTime(atOffset: start - clip.target.start)
            let srcDuration = Int64(Double(end - start) * innerSpeed)
            let parentStart = segment.targetTimerange.start + Int64(Double(start - windowStart) / speed)
            let parentDuration = Int64(Double(end - start) / speed)
            var copy = clip
            copy.target = TimeRange(start: parentStart, duration: parentDuration)
            copy.timeMap = TimeMap(sourceStart: srcStart, sourceDuration: srcDuration, targetDuration: parentDuration)
            copy.volume *= volume
            if voice.separation != .none { copy.voice.separation = voice.separation }
            if let denoise = voice.denoise { copy.voice.denoise = denoise }
            if let enhance = voice.enhance { copy.voice.enhance = enhance }
            if let filter = voice.filter { copy.voice.filter = filter }
            copy.fadeIn = start == clip.target.start ? Int64(Double(clip.fadeIn) / speed) : 0
            copy.fadeOut = end == clip.target.end ? Int64(Double(clip.fadeOut) / speed) : 0
            result.append(copy)
        }
        return result
    }

    /// The cached preset directory for a voice filter, or nil when it's missing or uses a processor
    /// `VoiceFilterGraph` doesn't implement.
    mutating func voiceFilterDirectory(_ effect: AudioEffectMaterial) -> URL? {
        let key = effect.path + "|" + effect.resourceID
        if let cached = filterDirectories[key] { return cached }
        let path = DraftLoader.resolvePath(effect.path, projectURL: projectURL)
        var directory = VoiceFilter.locate(path: path, resourceID: effect.resourceID)
        if let dir = directory, (try? VoiceFilterGraph.load(directory: dir)) == nil { directory = nil }
        filterDirectories[key] = .some(directory)
        return directory
    }

    // MARK: Report

    mutating func note(_ category: String, _ name: String) {
        unsupported[category, default: [:]][name, default: 0] += 1
    }

    mutating func reportRefs(_ refs: [String], generic: [String: (String, GenericMaterial)]) {
        for ref in refs {
            if let (key, item) = generic[ref] { report(key: key, item: item) }
        }
    }

    static let ignored: Set<String> = [
        "speeds", "placeholder_infos", "sound_channel_mappings", "material_colors", "canvases", "beats",
        "loudnesses", "audio_fades", "drafts", "videos", "audios", "texts", "ai_translates", "smart_crops",
        "realtime_denoises_placeholder",
        // Rendered by VoiceProcessor (voice filters whose preset can't be run are noted where they're read).
        "vocal_separations", "realtime_denoises", "vocal_beautifys", "audio_effects",
    ]

    mutating func report(key: String, item: GenericMaterial) {
        guard !Builder.ignored.contains(key) else { return }
        let name = item.name.isEmpty ? item.type : item.name
        switch key {
        case "transitions": note("Transition", name)
        case "video_effects": note("Video effect", name)
        case "stickers": note("Sticker", name)
        case "common_mask", "masks": note("Mask", name)
        case "hsl": note("HSL color", "color adjustment")
        case "manual_deformations": note("Manual deformation", "body/face reshape")
        case "chromas": note("Chroma key", "chroma key")
        case "material_animations":
            for animation in item.animationNames { note("Animation", animation) }
        case "effects":
            switch item.type {
            case "filter": note("Filter", name)
            case "text_effect": note("Text effect", name)
            case "text_shape": if !item.name.isEmpty { note("Text shape", item.name) }
            case "color_correct", "smart_color_adjust": note("Color adjustment", item.type.replacingOccurrences(of: "_", with: " "))
            case let t where t.contains("makeup") || t.contains("beauty") || t == "figure" || t.contains("face"):
                note("Beauty / makeup", name)
            default: note("Effect", name)
            }
        default:
            note("Other", key + (item.type.isEmpty ? "" : " (\(item.type))"))
        }
    }
}
