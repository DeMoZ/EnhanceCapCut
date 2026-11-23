import Foundation
import XCTest
@testable import EnhanceCapCutCore

// Test conveniences over the Rust core's records (whose generated initializers take every field).

/// A mask geometry with the defaults the native mask tests use: centred, half the frame, no rotation or feather.
func geometry(centerX: Double = 0, centerY: Double = 0, width: Double = 0.5, height: Double = 0.5, rotation: Double = 0,
              feather: Double = 0, roundCorner: Double = 0) -> MaskGeometry {
    MaskGeometry(centerX: centerX, centerY: centerY, width: width, height: height, rotation: rotation, feather: feather,
                 roundCorner: roundCorner)
}

extension VideoEffectState {
    static let identity = VideoEffectState(zoom: 1, rotation: 0, blur: 0, flash: 0)
}

/// A timeline built by the core from draft JSON; media paths resolve against `project`.
func coreTimeline(_ json: String, project: URL = FileManager.default.temporaryDirectory) throws -> Timeline {
    try Timeline.fromDraft(draft: Data(json.utf8), projectPath: project.path, env: .current)
}

/// The text spec the core makes of one text material (`materialJSON`: its fields, without the id).
func textSpec(_ materialJSON: String) throws -> TextLayerSpec {
    let draft = """
        {"canvas_config":{"width":1080,"height":1920},"duration":1000000,
         "tracks":[{"type":"text","segments":[{"id":"S","material_id":"T","target_timerange":{"start":0,"duration":1000000}}]}],
         "materials":{"texts":[\(materialJSON.dropLast()),"id":"T"}]}}
        """
    let layers = try coreTimeline(draft).layers()
    guard case let .text(spec) = layers.first?.content else { throw XCTSkip("no text layer in \(draft)") }
    return spec
}

/// An audio clip of `url` (the core's record; `volumeEnvelope` and `voice` off by default).
func audioClip(url: URL, target: TimeRange, timeMap: TimeMap, volume: Double, fadeIn: Int64, fadeOut: Int64,
               preservePitch: Bool, volumeEnvelope: KeyframeTrack? = nil, voice: VoiceEffects = .only(.none)) -> AudioClip {
    AudioClip(path: url.path, target: target, timeMap: timeMap, volume: volume, fadeIn: fadeIn, fadeOut: fadeOut,
              preservePitch: preservePitch, volumeEnvelope: volumeEnvelope, voice: voice)
}

/// Voice effects with every stage off unless given.
func effects(separation: VoiceSeparation = .none, denoise: Double? = nil, enhance: Double? = nil,
             filter: VoiceFilter? = nil) -> VoiceEffects {
    VoiceEffects(separation: separation, denoise: denoise, enhance: enhance, filter: filter)
}

/// A chroma key with no shadow, edge smoothing or spill unless given.
func chromaKey(red: Double, green: Double, blue: Double, intensity: Double, shadow: Double = 0, edgeSmooth: Double = 0,
               spill: Double = 0) -> ChromaKey {
    ChromaKey(red: red, green: green, blue: blue, intensity: intensity, shadow: shadow, edgeSmooth: edgeSmooth, spill: spill)
}
