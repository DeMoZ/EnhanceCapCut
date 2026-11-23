import EnhanceCapCutCore
import Foundation

// Hidden `ecc dump-timeline` / `ecc dump-writers` (not in help): the canonical dumps the golden-parity runner compares
// with the Rust core's and the Windows CLI's (openspec/changes/rust-core, design decision 11; the format is in
// docs/DEVELOPMENT.md › Golden parity and in core/ecc-golden/src/dump.rs). Removed once the apps use the Rust core.

enum Golden {
    /// A JSON value written by hand, so all three implementations print it the same way.
    indirect enum J {
        case null, bool(Bool), int(Int64), num(Double), str(String), arr([J])

        func write(to out: inout String) {
            switch self {
            case .null: out += "null"
            case let .bool(b): out += b ? "true" : "false"
            case let .int(i): out += "\(i)"
            case let .num(d): out += String(format: "%.6f", d)
            case let .str(s): out += Golden.quoted(s)
            case let .arr(items):
                out += "["
                for (i, item) in items.enumerated() {
                    if i > 0 { out += "," }
                    item.write(to: &out)
                }
                out += "]"
            }
        }
    }

    static func quoted(_ s: String) -> String {
        var out = "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case _ where scalar.value < 0x20: out += String(format: "\\u%04x", scalar.value)
            default: out.unicodeScalars.append(scalar)
            }
        }
        return out + "\""
    }

    static func n(_ v: Double) -> J { .num(v) }
    static func i<T: BinaryInteger>(_ v: T) -> J { .int(Int64(v)) }
    static func s(_ v: String) -> J { .str(v) }
    static func nums(_ v: [Double]) -> J { .arr(v.map { .num($0) }) }
    static func ints(_ v: [Int64]) -> J { .arr(v.map { .int($0) }) }
    static func opt<T>(_ v: T?, _ f: (T) -> J) -> J { v.map(f) ?? .null }

    final class Dump {
        var out = ""
        let project: String

        init(project: URL) { self.project = project.path }

        func line(_ fields: [(String, J)]) {
            out += "{"
            for (k, (key, value)) in fields.enumerated() {
                if k > 0 { out += "," }
                out += Golden.quoted(key) + ":"
                value.write(to: &out)
            }
            out += "}\n"
        }

        func path(_ url: URL) -> J {
            let text = url.path
            return .str(text.hasPrefix(project + "/") ? "<project>" + text.dropFirst(project.count) : text)
        }
    }

    static func map(_ m: TimeMap) -> J {
        .arr([i(m.sourceStart), i(m.sourceDuration), i(m.targetDuration), .bool(m.isConstant)])
    }

    static func range(_ r: TimeRange) -> J { ints([r.start, r.duration]) }

    static func transform(_ t: LayerTransform) -> J {
        .arr([n(t.scaleX), n(t.scaleY), n(t.rotation), n(t.positionX), n(t.positionY), .bool(t.flipH), .bool(t.flipV),
              n(t.alpha)])
    }

    static func geometry(_ g: MaskGeometry) -> J {
        nums([g.centerX, g.centerY, g.width, g.height, g.rotation, g.feather, g.roundCorner])
    }

    static func textAnimation(_ a: TextAnimation?) -> J {
        opt(a) { a in
            let effect: String
            switch a.effect {
            case .fade: effect = "fade"
            case let .zoom(scale, fades): effect = "zoom \(String(format: "%.6f", scale)) \(fades)"
            case .pop: effect = "pop"
            case let .slide(dx, dy): effect = "slide \(String(format: "%.6f", dx)) \(String(format: "%.6f", dy))"
            case .typewriter: effect = "typewriter"
            case .blink: effect = "blink"
            case .pulse: effect = "pulse"
            }
            return .arr([s(effect), i(a.duration)])
        }
    }

    static func kind(_ k: LayerTransition.Kind) -> String { k == .flashback ? "flashback" : "crossFade" }

    /// The frame times: 0, every clip edge ±1 µs, every keyframe ±1 µs (on the timeline), each transition's start, cut
    /// and last microsecond, and 16 even steps; within 0…duration.
    static func sampleTimes(_ t: Timeline) -> [Int64] {
        var times: [Int64] = [0]
        for l in t.layers {
            for edge in [l.target.start, l.target.end] { times += [edge - 1, edge, edge + 1] }
            for track in l.keyframes.values {
                for time in track.times {
                    let at = l.target.start + l.timeMap.offset(atSourceTime: time)
                    times += [at - 1, at, at + 1]
                }
            }
            if let tr = l.transitionOut {
                times += [tr.range.start, tr.range.start + tr.range.duration / 2, tr.range.end - 1]
            }
        }
        times += (0...16).map { t.duration * Int64($0) / 16 }
        return Array(Set(times.filter { $0 >= 0 && $0 <= t.duration })).sorted()
    }

    static func dumpTimeline(_ timeline: Timeline, project: URL) -> String {
        let d = Dump(project: project)
        write(timeline, to: d)
        return d.out
    }

    static func write(_ t: Timeline, to d: Dump) {
        d.line([("timeline", s(t.name)), ("canvas", ints([Int64(t.canvasSize.width), Int64(t.canvasSize.height)])),
                ("fps", n(t.fps)), ("duration", i(t.duration))])
        for item in t.report.unsupported { d.line([("unsupported", .arr([s(item.category), s(item.name), i(item.count)]))]) }
        for file in t.report.missingFiles { d.line([("missing", s(file))]) }
        for (index, l) in t.layers.enumerated() {
            let content: J
            switch l.content {
            case let .video(url): content = .arr([s("video"), d.path(url)])
            case let .photo(url): content = .arr([s("photo"), d.path(url)])
            case .text: content = .arr([s("text")])
            case let .compound(inner): content = .arr([s("compound"), i(inner.layers.count)])
            case let .sticker(spec): content = .arr([s("sticker"), s(spec.name), s(spec.clock.style.rawValue)])
            }
            let c = l.clip
            d.line([
                ("layer", i(index)), ("id", s(l.id)), ("content", content), ("track", i(l.trackIndex)),
                ("render", i(l.renderIndex)), ("main", .bool(l.isMainTrack)), ("target", range(l.target)),
                ("map", map(l.timeMap)),
                ("crop", nums([l.crop.upperLeft.x, l.crop.upperLeft.y, l.crop.lowerRight.x, l.crop.lowerRight.y])),
                ("clip", .arr([n(c.scale.x), n(c.scale.y), n(c.rotation), n(c.transform.x), n(c.transform.y),
                               .bool(c.flipHorizontal), .bool(c.flipVertical), n(c.alpha)])),
                ("uniform", .bool(l.uniformScale)), ("tone_maps_hdr", .bool(l.toneMapsHDR)),
                ("retouch", .bool(l.retouchesSkin)), ("enhance", .bool(l.enhancesQuality)),
                ("stabilization", i(l.stabilization)), ("blend", opt(l.blendMode) { s($0.rawValue) }),
                ("canvas", opt(l.canvas) { .arr([s($0.type), s($0.color), n($0.blur), s($0.image)]) }),
                ("cutout", opt(l.cutout) { .arr([d.path($0.directory), .bool($0.inverted)]) }),
                ("chroma", opt(l.chromaKey) { nums([$0.red, $0.green, $0.blue, $0.intensity, $0.shadow, $0.edgeSmooth, $0.spill]) }),
            ])
            for property in LayerProperty.allCases {
                if let track = l.keyframes[property] {
                    d.line([("keyframes", i(index)), ("property", s(property.rawValue)), ("times", ints(track.times)),
                            ("values", nums(track.values)), ("curved", .bool(track.handles != nil))])
                }
            }
            for f in l.filters {
                d.line([("layer_filter", i(index)), ("name", s(f.name)), ("luts", .arr(f.luts.map(d.path))),
                        ("intensity", n(f.intensity))])
            }
            for e in l.effects {
                d.line([("effect", i(index)), ("kind", s(e.kind.rawValue)), ("start", i(e.start)), ("speed", n(e.speed))])
            }
            if let tr = l.transitionOut {
                let next = t.layers.firstIndex { $0 === tr.next } ?? -1
                d.line([("transition", i(index)), ("name", s(tr.name)), ("kind", s(kind(tr.kind))), ("next", i(next)),
                        ("range", range(tr.range))])
            }
            for m in l.masks {
                let (kind, brush): (String, J)
                switch m.kind {
                case let .shape(shape): (kind, brush) = (shape.rawValue, .null)
                case let .brush(url): (kind, brush) = ("brush", d.path(url))
                }
                let keyframed = MaskProperty.allCases.filter { m.keyframes[$0] != nil }.map { s($0.rawValue) }
                d.line([("mask", i(index)), ("kind", s(kind)), ("brush", brush), ("inverted", .bool(m.inverted)),
                        ("geometry", geometry(m.geometry)), ("keyframed", .arr(keyframed))])
            }
            for a in l.animations {
                d.line([("animation", i(index)), ("kind", s(a.kind.rawValue)), ("preset", s(a.preset.rawValue)),
                        ("range", ints([a.start, a.end]))])
            }
            if case let .text(spec) = l.content {
                let m = spec.material
                let styles: [J] = spec.content.styles.map { st in
                    let r = spec.content.utf16Range(of: st)
                    return .arr([i(r.lowerBound), i(r.upperBound), opt(st.size, n), opt(st.fontPath, s), opt(st.fillColor, nums),
                                 opt(st.fillAlpha, n), i(st.strokes.count), .bool(st.bold), .bool(st.italic), .bool(st.underline)])
                }
                d.line([
                    ("text", i(index)), ("type", s(m.type)), ("content", s(spec.content.text)), ("font", s(m.fontPath)),
                    ("size", n(m.fontSize)), ("color", s(m.textColor)), ("alignment", i(m.alignment)),
                    ("spacing", nums([m.lineSpacing, m.letterSpacing, m.lineMaxWidth])),
                    ("border", .arr([s(m.borderColor), n(m.borderWidth)])),
                    ("background", .arr([s(m.backgroundColor), n(m.backgroundAlpha)])), ("shadow", .bool(m.hasShadow)),
                    ("typesetting", i(m.typesetting)), ("styles", .arr(styles)),
                    ("intro", textAnimation(spec.animations.intro)), ("outro", textAnimation(spec.animations.outro)),
                    ("loop", textAnimation(spec.animations.loop)),
                    ("glow", opt(spec.glow) { .arr($0.color.map(n) + [n($0.radius)]) }),
                ])
            }
            if case let .compound(inner) = l.content {
                d.line([("compound", i(index))])
                write(inner, to: d)
                d.line([("end", i(index))])
            }
        }
        for (k, c) in t.audioClips.enumerated() {
            let v = c.voice
            let separation = switch v.separation {
            case .none: "none"
            case .removeVoice: "removeVoice"
            case .keepVoice: "keepVoice"
            }
            d.line([
                ("audio", i(k)), ("path", d.path(c.url)), ("target", range(c.target)), ("map", map(c.timeMap)),
                ("volume", n(c.volume)), ("fade", ints([c.fadeIn, c.fadeOut])), ("pitch", .bool(c.preservePitch)),
                ("voice", .arr([s(separation), opt(v.denoise, n), opt(v.enhance, n), opt(v.filter) { s($0.name) },
                                opt(v.filter) { d.path($0.directory) }, opt(v.filter) { n($0.strength) }])),
            ])
            if let e = c.volumeEnvelope {
                d.line([("envelope", i(k)), ("times", ints(e.times)), ("values", nums(e.values)),
                        ("curved", .bool(e.handles != nil))])
            }
            // The audio renderer samples the time map every 50 ms; at most 200 samples per clip here.
            let duration = max(c.target.duration, 0)
            let step = max(50_000, (duration + 199) / 200)
            let offsets = Array(stride(from: Int64(0), through: duration, by: Int(step)))
            d.line([("samples", i(k)), ("step", i(step)), ("source", ints(offsets.map { c.timeMap.sourceTime(atOffset: $0) }))])
        }
        for (j, f) in t.filters.enumerated() {
            d.line([("filter", i(j)), ("id", s(f.id)), ("target", range(f.target)), ("track", i(f.trackIndex)),
                    ("render", i(f.renderIndex)), ("name", s(f.filter.name)), ("luts", .arr(f.filter.luts.map(d.path))),
                    ("intensity", n(f.filter.intensity))])
        }
        for time in sampleTimes(t) {
            // As in FrameCompositor.frame(at:): active layers, plus both clips of a running transition.
            var mixing = Set<ObjectIdentifier>()
            for l in t.layers {
                if let tr = l.transitionOut, tr.contains(time) { mixing.insert(ObjectIdentifier(l)); mixing.insert(ObjectIdentifier(tr.next)) }
            }
            let filters = t.filters.indices.filter { t.filters[$0].isActive(at: time) }.map { i($0) }
            d.line([("t", i(time)), ("filters", .arr(filters))])
            for (index, l) in t.layers.enumerated() where l.isActive(at: time) || mixing.contains(ObjectIdentifier(l)) {
                let offset = time - l.target.start
                let source = l.timeMap.sourceTime(atOffset: offset)
                var text: J = .null
                if case let .text(spec) = l.content {
                    let st = spec.animations.state(atOffset: offset, duration: l.target.duration)
                    text = nums([st.alpha, st.scale, st.offsetX, st.offsetY, st.reveal])
                }
                var fx: J = .null
                if !l.effects.isEmpty {
                    let e = VideoEffectState.combined(l.effects.map { $0.state(atOffset: offset) })
                    fx = nums([e.zoom, e.rotation, e.blur, e.flash])
                }
                var tx: J = .null
                if let tr = l.transitionOut, tr.contains(time) {
                    tx = .arr([i(t.layers.firstIndex { $0 === tr.next } ?? -1), s(kind(tr.kind)), n(tr.progress(at: time))])
                }
                var clock: J = .null
                if case .sticker = l.content {
                    let h = ClockSticker.handAngles(atOffset: source)
                    clock = nums([h.minute, h.hour])
                }
                d.line([("f", i(index)), ("offset", i(offset)), ("source", i(source)), ("tr", transform(l.transform(at: time))),
                        ("text", text), ("fx", fx), ("masks", .arr(l.masks.map { geometry($0.geometry(at: source)) })),
                        ("tx", tx), ("clock", clock)])
            }
        }
    }

    // MARK: Writers

    static func language(_ code: String) -> SubtitleLanguage { SubtitleLanguage(Locale.Language(identifier: code)) }

    static let captionTracks = [
        SubtitleTrack(language: language("ru"), cues: [CaptionCue(start: 0.5, end: 2, text: "Привет, мир"),
                                                        CaptionCue(start: 2, end: 3.01, text: "Вторая\nстрока 😳")],
                      isOriginal: true, isVisible: false),
        SubtitleTrack(language: language("en"), cues: [CaptionCue(start: 0.5, end: 2, text: "Hello, world")],
                      isOriginal: false, isVisible: true),
        SubtitleTrack(language: language("th"), cues: [CaptionCue(start: 1.234, end: 4.5, text: "สวัสดีครับ ทุกคน")],
                      isOriginal: false, isVisible: true),
        SubtitleTrack(language: language("zh-Hans"), cues: [CaptionCue(start: 5, end: 6, text: "你好")],
                      isOriginal: false, isVisible: false),
    ]

    static func voiceTracks(_ project: URL) -> [VoiceOverTrack] {
        [
            VoiceOverTrack(language: language("en"), url: project.appendingPathComponent("EnhanceCapCut/voiceover 1/en.wav"),
                           fileDuration: 10, lines: [1...2.5, 3...4, 7...9.25], isAudible: true),
            VoiceOverTrack(language: language("th"), url: URL(fileURLWithPath: "/elsewhere/th.wav"), fileDuration: 10,
                           lines: [1...2], isAudible: false),
            VoiceOverTrack(language: language("en"), url: project.appendingPathComponent("bg.wav"), fileDuration: 10,
                           lines: [0.75...4.4], isAudible: true, isBackground: true),
        ]
    }

    static func isUUID(_ b: ArraySlice<UInt8>) -> Bool {
        guard b.count == 36 else { return false }
        for (k, c) in b.enumerated() {
            if [8, 13, 18, 23].contains(k) {
                if c != UInt8(ascii: "-") { return false }
            } else if !((48...57).contains(c) || (65...70).contains(c)) {
                return false
            }
        }
        return true
    }

    static func uuids(in bytes: [UInt8]) -> Set<ArraySlice<UInt8>> {
        var ids = Set<ArraySlice<UInt8>>()
        var k = 0
        while k + 36 <= bytes.count {
            if isUUID(bytes[k..<k + 36]) { ids.insert(bytes[k..<k + 36]); k += 36 } else { k += 1 }
        }
        return ids
    }

    /// UUIDs replaced by `<id1>`, `<id2>`… in order of first appearance; ids already in the input stay. The project
    /// folder becomes `<project>`.
    static func normalize(_ data: Data, keep: Set<ArraySlice<UInt8>>, project: String) -> String {
        let bytes = [UInt8](data)
        var seen: [ArraySlice<UInt8>: Int] = [:]
        var out: [UInt8] = []
        var k = 0
        while k < bytes.count {
            if k + 36 <= bytes.count, isUUID(bytes[k..<k + 36]) {
                let id = bytes[k..<k + 36]
                if keep.contains(id) {
                    out += id
                } else {
                    let n = seen[id] ?? (seen.count + 1)
                    seen[id] = n
                    out += Array("<id\(n)>".utf8)
                }
                k += 36
            } else {
                out.append(bytes[k])
                k += 1
            }
        }
        return String(decoding: out, as: UTF8.self).replacingOccurrences(of: project, with: "<project>")
    }

    static func dumpWriters(project: CapCutProject, timeline: ProjectTimeline) throws -> String {
        let draft = try Data(contentsOf: timeline.draftURL)
        let keep = uuids(in: [UInt8](draft))
        var out = ""
        func section(_ title: String, _ data: Data) {
            out += "== \(title)\n" + normalize(data, keep: keep, project: project.url.path) + "\n"
        }
        let now = Date(timeIntervalSince1970: 1_700_000_000.5)
        let once = try DraftSubtitleWriter.apply(captionTracks, to: draft, replacePrevious: true, now: now)
        section("captions", once)
        section("captions again", try DraftSubtitleWriter.apply(captionTracks, to: once, replacePrevious: true, now: now))
        let voice = voiceTracks(project.url)
        var last = draft
        for mode in OriginalAudio.allCases {
            last = try DraftVoiceOverWriter.apply(voice, to: draft, original: mode, replacePrevious: true, projectURL: project.url)
            section("voice-over \(mode.nameToken)", last)
        }
        section("without voice-over", try DraftVoiceOverWriter.withoutVoiceOver(last))
        return out
    }
}
