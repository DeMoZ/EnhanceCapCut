import CoreGraphics
import Foundation

/// A draft resolved into what the renderer needs: visual layers in paint order and audio clips.
public final class Timeline {
    public let name: String
    /// The draft's canvas in pixels. Layout is relative, so any output size can be rendered.
    public let canvasSize: CGSize
    public let duration: Int64
    public let fps: Double
    /// Bottom to top.
    public let layers: [VisualLayer]
    public let audioClips: [AudioClip]
    public let report: RenderReport

    init(name: String, canvasSize: CGSize, duration: Int64, fps: Double, layers: [VisualLayer],
         audioClips: [AudioClip], report: RenderReport) {
        self.name = name
        self.canvasSize = canvasSize
        self.duration = duration
        self.fps = fps
        self.layers = layers
        self.audioClips = audioClips
        self.report = report
    }

    /// The same timeline without caption (auto-subtitle) clips.
    public func withoutSubtitles() -> Timeline {
        let kept = layers.filter { layer in
            if case let .text(spec) = layer.content { return spec.material.type != "subtitle" }
            return true
        }
        return Timeline(name: name, canvasSize: canvasSize, duration: duration, fps: fps, layers: kept,
                        audioClips: audioClips, report: report)
    }

    public var hasSubtitles: Bool {
        layers.contains { layer in
            if case let .text(spec) = layer.content { return spec.material.type == "subtitle" }
            return false
        }
    }
}

public enum VisualContent {
    case video(URL)
    case photo(URL)
    case text(TextLayerSpec)
    case compound(Timeline)
}

public struct TextLayerSpec {
    public var material: TextMaterial
    public var content: TextContent
}

/// Layer properties that can be keyframed.
public enum LayerProperty: String, CaseIterable {
    case positionX = "KFTypePositionX"
    case positionY = "KFTypePositionY"
    case scaleX = "KFTypeScaleX"
    case scaleY = "KFTypeScaleY"
    case rotation = "KFTypeRotation"
    case alpha = "KFTypeAlpha"
    case volume = "KFTypeVolume"
}

public struct LayerTransform: Equatable {
    public var scaleX: Double
    public var scaleY: Double
    /// Degrees, clockwise.
    public var rotation: Double
    /// Half-canvas units, +y up.
    public var positionX: Double
    public var positionY: Double
    public var flipH: Bool
    public var flipV: Bool
    public var alpha: Double
}

public final class VisualLayer {
    public let id: String
    public let content: VisualContent
    public let trackIndex: Int
    public let renderIndex: Int
    /// The first video track: its clips get the canvas background.
    public let isMainTrack: Bool
    public let target: TimeRange
    public let timeMap: TimeMap
    public let crop: Crop
    public let clip: Clip
    public let uniformScale: Bool
    public let keyframes: [LayerProperty: KeyframeTrack]
    public let canvas: CanvasMaterial?

    init(id: String, content: VisualContent, trackIndex: Int, renderIndex: Int, isMainTrack: Bool,
         target: TimeRange, timeMap: TimeMap, crop: Crop, clip: Clip, uniformScale: Bool,
         keyframes: [LayerProperty: KeyframeTrack], canvas: CanvasMaterial?) {
        self.id = id
        self.content = content
        self.trackIndex = trackIndex
        self.renderIndex = renderIndex
        self.isMainTrack = isMainTrack
        self.target = target
        self.timeMap = timeMap
        self.crop = crop
        self.clip = clip
        self.uniformScale = uniformScale
        self.keyframes = keyframes
        self.canvas = canvas
    }

    public func isActive(at t: Int64) -> Bool { t >= target.start && t < target.end }

    /// Clip transform at timeline time `t`, with keyframes applied.
    public func transform(at t: Int64) -> LayerTransform {
        let offset = t - target.start
        func kf(_ p: LayerProperty) -> Double? { keyframes[p]?.value(at: offset) }
        var sx = kf(.scaleX) ?? clip.scale.x
        var sy = kf(.scaleY) ?? clip.scale.y
        if uniformScale {
            if keyframes[.scaleX] != nil { sy = sx } else if keyframes[.scaleY] != nil { sx = sy }
        }
        return LayerTransform(
            scaleX: sx, scaleY: sy,
            rotation: kf(.rotation) ?? clip.rotation,
            positionX: kf(.positionX) ?? clip.transform.x,
            positionY: kf(.positionY) ?? clip.transform.y,
            flipH: clip.flipHorizontal, flipV: clip.flipVertical,
            alpha: kf(.alpha) ?? clip.alpha)
    }
}

public struct AudioClip {
    public var url: URL
    public var target: TimeRange
    public var timeMap: TimeMap
    /// Linear gain; CapCut allows values above 1.
    public var volume: Double
    public var fadeIn: Int64
    public var fadeOut: Int64
    public var preservePitch: Bool
    /// Volume keyframes as a multiplier of `volume`, offsets from the clip start (nil = constant).
    public var volumeEnvelope: KeyframeTrack? = nil
    /// "Isolate voice" / "Reduce noise" / "Enhance voice", applied to the source before the mix.
    public var voice = VoiceEffects()
}

/// CapCut's per-clip voice processing.
public struct VoiceEffects: Hashable {
    public enum Separation: Int {
        case none = 0
        /// "Remove vocal": keep only the background (music, ambience).
        case removeVoice = 1
        /// "Keep vocal": keep only the voice.
        case keepVoice = 2
    }

    public var separation: Separation = .none
    /// "Reduce noise" strength 0...1, nil when off.
    public var denoise: Double?
    /// "Enhance voice" ambient sound level 0...1 (`ambient_sound_level` / 100), nil when off.
    public var enhance: Double?
    /// A voice filter ("Booming", "Old Hollywood"…), applied after the other stages.
    public var filter: VoiceFilter?

    public init(separation: Separation = .none, denoise: Double? = nil, enhance: Double? = nil, filter: VoiceFilter? = nil) {
        self.separation = separation
        self.denoise = denoise
        self.enhance = enhance
        self.filter = filter
    }

    public var isEmpty: Bool { separation == .none && denoise == nil && enhance == nil && filter == nil }
}

/// One of CapCut's "Voice filters": its cached processor graph and the strength slider.
public struct VoiceFilter: Hashable {
    public var name: String
    /// The cached effect directory holding `mdsp_preset.json` (and any audio it loops).
    public var directory: URL
    /// 0...1.
    public var strength: Double

    public init(name: String, directory: URL, strength: Double = 1) {
        self.name = name
        self.directory = directory
        self.strength = strength
    }

    /// Roots of CapCut's effect cache: the sandboxed (App Store) app's and the direct download's.
    public static var cacheRoots: [URL] {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return [
            "Library/Containers/com.lemon.lvoverseas/Data/Movies/CapCut/User Data/Cache/effect",
            "Movies/CapCut/User Data/Cache/effect",
        ].map { home.appendingPathComponent($0, isDirectory: true) }
    }

    /// The cached directory for an effect: `path` (or a folder in it) when it holds a preset,
    /// otherwise the first `<cache>/<resourceID>/<hash>` that does.
    public static func locate(path: URL?, resourceID: String, cacheRoots: [URL] = cacheRoots) -> URL? {
        let fm = FileManager.default
        func hasPreset(_ dir: URL) -> Bool { fm.fileExists(atPath: dir.appendingPathComponent("mdsp_preset.json").path) }
        func search(_ folder: URL) -> URL? {
            if hasPreset(folder) { return folder }
            let entries = (try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
            return entries.sorted(by: { $0.path < $1.path }).first(where: hasPreset)
        }
        if let path, let dir = search(path) { return dir }
        guard !resourceID.isEmpty else { return nil }
        return cacheRoots.lazy.compactMap { search($0.appendingPathComponent(resourceID, isDirectory: true)) }.first
    }
}

public struct RenderReport {
    public struct Item: Hashable, Identifiable {
        public var category: String
        public var name: String
        public var count: Int
        public var id: String { category + "/" + name }
    }

    /// Effects, transitions, stickers, filters... present in the project but not rendered.
    public var unsupported: [Item] = []
    /// Media files referenced by the project that don't exist on disk (rendered as empty).
    public var missingFiles: [String] = []

    public var isEmpty: Bool { unsupported.isEmpty && missingFiles.isEmpty }
}
