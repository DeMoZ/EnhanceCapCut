import Foundation

// Typed view of CapCut's draft (`draft_info.json` on the Mac, `draft_content.json` on Windows; draft version
// 360000 / new_version 187.0.0 and later).
// Only the fields the renderer and the export report need are modelled; everything else is ignored.
// All times are in microseconds, as in the draft.

public struct Draft: Decodable {
    public var id: String
    public var name: String
    public var duration: Int64
    public var fps: Double
    public var canvas: CanvasConfig
    public var tracks: [Track]
    public var materials: Materials
    public var config: DraftConfig

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        name = c.str("name") ?? ""
        duration = c.int64("duration") ?? 0
        fps = c.num("fps") ?? 30
        canvas = c.opt("canvas_config") ?? CanvasConfig(width: 1920, height: 1080, ratio: "original")
        tracks = c.list("tracks")
        materials = c.opt("materials") ?? Materials()
        config = c.opt("config") ?? DraftConfig()
    }
}

public struct CanvasConfig: Decodable {
    public var width: Int
    public var height: Int
    public var ratio: String

    public init(width: Int, height: Int, ratio: String) {
        self.width = width; self.height = height; self.ratio = ratio
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        width = Int(c.num("width") ?? 1920)
        height = Int(c.num("height") ?? 1080)
        ratio = c.str("ratio") ?? "original"
    }
}

public struct DraftConfig: Decodable {
    /// The speaker toggle on the main track.
    public var videoMute = false

    init() {}
    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        videoMute = c.bool("video_mute") ?? false
    }
}

public struct TimeRange: Decodable, Equatable {
    public var start: Int64
    public var duration: Int64
    public var end: Int64 { start + duration }

    public init(start: Int64, duration: Int64) { self.start = start; self.duration = duration }
    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        start = c.int64("start") ?? 0
        duration = c.int64("duration") ?? 0
    }
}

public struct Track: Decodable {
    public var id: String
    /// "video", "audio", "text", "sticker", "effect", "filter", ...
    public var type: String
    /// Bit flags of the track header toggles. Bit 0 is "mute"; bit 1 is "hide".
    public var attribute: Int
    public var flag: Int
    public var segments: [Segment]

    public var isMuted: Bool { attribute & 1 != 0 }
    public var isHidden: Bool { attribute & 2 != 0 }

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? UUID().uuidString
        type = c.str("type") ?? ""
        attribute = Int(c.num("attribute") ?? 0)
        flag = Int(c.num("flag") ?? 0)
        segments = c.list("segments")
    }
}

public struct Point2: Decodable, Equatable {
    public var x: Double
    public var y: Double
    public init(x: Double, y: Double) { self.x = x; self.y = y }
    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        x = c.num("x") ?? 0
        y = c.num("y") ?? 0
    }
}

public struct Clip: Decodable {
    public var scale = Point2(x: 1, y: 1)
    /// Degrees, clockwise on screen.
    public var rotation: Double = 0
    /// Offset of the layer center in half-canvas units; +y is up.
    public var transform = Point2(x: 0, y: 0)
    public var flipHorizontal = false
    public var flipVertical = false
    public var alpha: Double = 1

    public init() {}
    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        scale = c.opt("scale") ?? Point2(x: 1, y: 1)
        rotation = c.num("rotation") ?? 0
        transform = c.opt("transform") ?? Point2(x: 0, y: 0)
        if let flip = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("flip")) {
            flipHorizontal = flip.bool("horizontal") ?? false
            flipVertical = flip.bool("vertical") ?? false
        }
        alpha = c.num("alpha") ?? 1
    }
}

public struct Keyframe: Decodable {
    public var timeOffset: Int64
    public var values: [Double]
    public var curveType: String

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        timeOffset = c.int64("time_offset") ?? 0
        values = c.opt("values") ?? []
        curveType = c.str("curveType") ?? "Line"
    }
}

public struct KeyframeGroup: Decodable {
    /// e.g. "KFTypePositionX", "KFTypeScaleX", "KFTypeRotation", "KFTypeAlpha", "KFTypeVolume".
    public var propertyType: String
    public var keyframes: [Keyframe]

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        propertyType = c.str("property_type") ?? ""
        keyframes = c.list("keyframe_list")
    }
}

public struct Segment: Decodable {
    public var id: String
    public var materialId: String
    public var sourceTimerange: TimeRange?
    public var targetTimerange: TimeRange
    public var speed: Double
    public var volume: Double
    public var reverse: Bool
    public var visible: Bool
    public var isToneModify: Bool
    public var clip: Clip?
    public var uniformScale: Bool
    public var extraMaterialRefs: [String]
    public var renderIndex: Int
    public var trackRenderIndex: Int
    public var keyframes: [KeyframeGroup]
    /// "Loop" playback of a clip shorter than its segment (not supported).
    public var isLoop: Bool

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? UUID().uuidString
        materialId = c.str("material_id") ?? ""
        sourceTimerange = c.opt("source_timerange")
        targetTimerange = c.opt("target_timerange") ?? TimeRange(start: 0, duration: 0)
        speed = c.num("speed") ?? 1
        volume = c.num("volume") ?? 1
        reverse = c.bool("reverse") ?? false
        visible = c.bool("visible") ?? true
        isToneModify = c.bool("is_tone_modify") ?? false
        clip = c.opt("clip")
        if let u = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("uniform_scale")) {
            uniformScale = u.bool("on") ?? true
        } else {
            uniformScale = true
        }
        extraMaterialRefs = c.opt("extra_material_refs") ?? []
        renderIndex = Int(c.num("render_index") ?? 0)
        trackRenderIndex = Int(c.num("track_render_index") ?? 0)
        keyframes = c.list("common_keyframes")
        isLoop = c.bool("is_loop") ?? false
    }
}

// MARK: - Materials

public struct Materials: Decodable {
    public var videos: [VideoMaterial] = []
    public var audios: [AudioMaterial] = []
    public var texts: [TextMaterial] = []
    public var canvases: [CanvasMaterial] = []
    public var speeds: [SpeedMaterial] = []
    public var audioFades: [AudioFadeMaterial] = []
    public var drafts: [CombinationMaterial] = []
    public var loudnesses: [LoudnessMaterial] = []
    public var vocalSeparations: [VocalSeparationMaterial] = []
    public var realtimeDenoises: [RealtimeDenoiseMaterial] = []
    public var vocalBeautifys: [VocalBeautifyMaterial] = []
    public var audioEffects: [AudioEffectMaterial] = []
    /// Every material of every category, with just the fields used for reporting.
    public var all: [String: [GenericMaterial]] = [:]

    init() {}
    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        videos = c.list("videos")
        audios = c.list("audios")
        texts = c.list("texts")
        canvases = c.list("canvases")
        speeds = c.list("speeds")
        audioFades = c.list("audio_fades")
        drafts = c.list("drafts")
        loudnesses = c.list("loudnesses")
        vocalSeparations = c.list("vocal_separations")
        realtimeDenoises = c.list("realtime_denoises")
        vocalBeautifys = c.list("vocal_beautifys")
        audioEffects = c.list("audio_effects")
        for key in c.allKeys {
            let items: [GenericMaterial] = c.list(key.stringValue)
            if !items.isEmpty { all[key.stringValue] = items }
        }
    }
}

/// The common subset of every material: enough to name it in the "not supported yet" list.
public struct GenericMaterial: Decodable {
    public var id: String
    public var type: String
    public var name: String
    public var path: String
    /// For `material_animations`: the names of the text/sticker animations it holds.
    public var animationNames: [String]
    /// Toggles used by audio-processing materials (`enable`, `is_denoise`, `choice`).
    public var enable: Bool?
    public var isDenoise: Bool?
    public var choice: Int?

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        guard let id = c.str("id") else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "no id"))
        }
        self.id = id
        type = c.str("type") ?? ""
        name = c.str("name") ?? c.str("effect_name") ?? c.str("material_name") ?? ""
        path = c.str("path") ?? ""
        animationNames = c.list("animations", of: NamedItem.self).map(\.name)
        enable = c.bool("enable")
        isDenoise = c.bool("is_denoise")
        choice = c.num("choice").map { Int($0) }
    }

    struct NamedItem: Decodable {
        var name: String
        init(from decoder: Decoder) throws {
            let c = try decoder.anyKeyed()
            name = c.str("name") ?? c.str("type") ?? "animation"
        }
    }
}

public struct Crop: Decodable, Equatable {
    // Normalized corners, origin at the top-left of the source frame.
    public var upperLeft = Point2(x: 0, y: 0)
    public var lowerRight = Point2(x: 1, y: 1)

    public var isIdentity: Bool {
        upperLeft.x <= 0.0001 && upperLeft.y <= 0.0001 && lowerRight.x >= 0.9999 && lowerRight.y >= 0.9999
    }

    public init() {}
    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        upperLeft = Point2(x: c.num("upper_left_x") ?? 0, y: c.num("upper_left_y") ?? 0)
        lowerRight = Point2(x: c.num("lower_right_x") ?? 1, y: c.num("lower_right_y") ?? 1)
    }
}

public struct VideoMaterial: Decodable {
    public var id: String
    /// "video" or "photo".
    public var type: String
    public var path: String
    public var name: String
    public var width: Int
    public var height: Int
    public var duration: Int64
    public var hasAudio: Bool
    public var hasSoundSeparated: Bool
    public var reversePath: String
    public var crop: Crop
    /// "Stabilize" level, 0 = off.
    public var stableLevel: Int
    /// "Remove background" mode (`matting.flag`), 0 = off; 1 and 3 seen (custom / auto cutout).
    public var mattingFlag: Int
    /// AI processing applied to the clip: `video_algorithm.algorithms[].type` plus the names of the
    /// non-null per-feature configs (e.g. "super_resolution", "deflicker").
    public var algorithms: [String]
    /// "Retouch" (face beauty) is on.
    public var beautyMode: Bool

    /// Per-feature configs inside `video_algorithm` that change the picture when set.
    static let algorithmConfigs = ["super_resolution", "deflicker", "noise_reduction", "motion_blur_config",
                                   "smart_complement_frame", "complement_frame_config", "ai_expression_driven",
                                   "ai_motion_driven", "mouth_shape_driver"]

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        type = c.str("type") ?? "video"
        path = c.str("path") ?? ""
        name = c.str("material_name") ?? ""
        width = Int(c.num("width") ?? 0)
        height = Int(c.num("height") ?? 0)
        duration = c.int64("duration") ?? 0
        hasAudio = c.bool("has_audio") ?? true
        hasSoundSeparated = c.bool("has_sound_separated") ?? false
        reversePath = c.str("reverse_path") ?? ""
        crop = c.opt("crop") ?? Crop()
        stableLevel = (try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("stable")))
            .flatMap { $0.num("stable_level") }.map { Int($0) } ?? 0
        mattingFlag = (try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("matting")))
            .flatMap { $0.num("flag") }.map { Int($0) } ?? 0
        var algorithms: [String] = []
        if let a = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("video_algorithm")) {
            algorithms = a.list("algorithms", of: AlgorithmItem.self).map(\.type)
            for key in Self.algorithmConfigs where a.contains(AnyKey(key)) && !((try? a.decodeNil(forKey: AnyKey(key))) ?? true) {
                algorithms.append(key)
            }
        }
        self.algorithms = algorithms
        beautyMode = c.bool("is_set_beauty_mode") ?? false
    }

    struct AlgorithmItem: Decodable {
        var type: String
        init(from decoder: Decoder) throws {
            type = try decoder.anyKeyed().str("type") ?? "unknown"
        }
    }
}

public struct AudioMaterial: Decodable {
    public var id: String
    /// "music", "extract_music", "record", "video_original_sound", "text_to_audio", ...
    public var type: String
    public var path: String
    public var name: String
    public var duration: Int64

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        type = c.str("type") ?? ""
        path = c.str("path") ?? ""
        name = c.str("name") ?? ""
        duration = c.int64("duration") ?? 0
    }
}

public struct CanvasMaterial: Decodable {
    public var id: String
    /// "canvas_color", "canvas_blur" or "canvas_image".
    public var type: String
    public var color: String
    public var blur: Double
    public var image: String

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        type = c.str("type") ?? "canvas_color"
        color = c.str("color") ?? ""
        blur = c.num("blur") ?? 0
        image = c.str("image") ?? ""
    }
}

public struct SpeedMaterial: Decodable {
    public var id: String
    /// 0 = constant, 1 = curve.
    public var mode: Int
    public var speed: Double
    public var curveName: String
    /// x: normalized position in the segment's timeline duration, y: speed multiplier.
    public var curvePoints: [Point2]

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        mode = Int(c.num("mode") ?? 0)
        speed = c.num("speed") ?? 1
        if let curve = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("curve_speed")) {
            curveName = curve.str("name") ?? ""
            curvePoints = curve.list("speed_points")
        } else {
            curveName = ""
            curvePoints = []
        }
    }
}

public struct AudioFadeMaterial: Decodable {
    public var id: String
    public var fadeIn: Int64
    public var fadeOut: Int64

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        fadeIn = c.int64("fade_in_duration") ?? 0
        fadeOut = c.int64("fade_out_duration") ?? 0
    }
}

/// Per-clip "Normalize loudness": CapCut stores the measured loudness and the target.
public struct LoudnessMaterial: Decodable {
    public var id: String
    public var enable: Bool
    public var targetLoudness: Double
    public var averageLoudness: Double?

    /// Gain in dB that brings the clip to its target, or 0 when disabled.
    public var gainDB: Double {
        guard enable, let avg = averageLoudness, avg > -70 else { return 0 }
        return targetLoudness - avg
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        enable = c.bool("enable") ?? false
        targetLoudness = c.num("target_loudness") ?? -23
        if let p = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("loudness_param")) {
            averageLoudness = p.num("avg_loudness")
        }
    }
}

/// Per-clip "Isolate voice": `choice` 0 = off, 1 = "Remove vocal" (keep the background),
/// 2 = "Keep vocal". CapCut renders the result into `production_path` (obfuscated, not read by us).
public struct VocalSeparationMaterial: Decodable {
    public var id: String
    public var choice: Int

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        choice = c.num("choice").map { Int($0) } ?? 0
    }
}

/// Per-clip "Reduce noise". CapCut applies its model while playing/exporting; nothing is cached.
public struct RealtimeDenoiseMaterial: Decodable {
    public var id: String
    public var isDenoise: Bool
    /// Strength 0...1 (always 0.85 in the author's projects; not adjustable in the UI).
    public var rate: Double

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        isDenoise = c.bool("is_denoise") ?? false
        rate = c.num("denoise_rate") ?? 0.85
    }
}

/// Per-clip "Enhance voice". CapCut caches its result in `production_path` (obfuscated, not read by us).
public struct VocalBeautifyMaterial: Decodable {
    public var id: String
    public var enable: Bool
    /// How much of the non-voice sound is kept, 0...100 (75 by default; 100 on a few clips).
    public var ambientSoundLevel: Double

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        enable = c.bool("enable") ?? false
        ambientSoundLevel = c.num("ambient_sound_level") ?? 75
    }
}

/// Per-clip voice filter ("Voice filters" in CapCut: "Booming", "Old Hollywood", "Live House"…).
/// The effect itself is a processor graph in CapCut's effect cache (see `VoiceFilterGraph`).
public struct AudioEffectMaterial: Decodable {
    public var id: String
    public var name: String
    /// The cached effect directory (absolute, may be stale).
    public var path: String
    /// The effect's id in CapCut's library; also the cache folder name (`Cache/effect/<id>/<hash>`).
    public var resourceID: String
    public var categoryName: String
    /// The "strength" slider, 0...1 (default 1; the only parameter the voice filters expose).
    public var strength: Double

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        name = c.str("name") ?? ""
        path = c.str("path") ?? ""
        resourceID = c.str("resource_id") ?? c.str("third_resource_id") ?? c.str("effect_id") ?? ""
        categoryName = c.str("category_name") ?? ""
        let params: [AudioAdjustParam] = c.list("audio_adjust_params")
        strength = params.first { $0.name == "strength" }?.value ?? params.first?.value ?? 1
    }

    struct AudioAdjustParam: Decodable {
        var name: String
        var value: Double?
        init(from decoder: Decoder) throws {
            let c = try decoder.anyKeyed()
            name = c.str("name") ?? ""
            value = c.num("value") ?? c.num("default_value")
        }
    }
}

/// A compound clip: a nested draft embedded in the parent draft.
public struct CombinationMaterial: Decodable {
    public var id: String
    public var draft: Draft?

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        draft = c.opt("draft")
    }
}

public struct TextMaterial: Decodable {
    public var id: String
    /// "text" or "subtitle".
    public var type: String
    /// JSON string: `{"text": "...", "styles": [...]}`.
    public var content: String
    public var fontPath: String
    public var fontSize: Double
    public var textColor: String
    public var alignment: Int
    public var lineSpacing: Double
    public var letterSpacing: Double
    public var lineMaxWidth: Double
    public var borderColor: String
    public var borderWidth: Double
    public var backgroundColor: String
    public var backgroundAlpha: Double
    public var backgroundRoundRadius: Double
    public var backgroundWidth: Double
    public var backgroundHeight: Double
    public var hasShadow: Bool
    public var shadowColor: String
    public var shadowAlpha: Double
    public var shadowSmoothing: Double
    public var shadowDistance: Double
    public var shadowAngle: Double
    public var globalAlpha: Double
    public var boldWidth: Double
    public var italicDegree: Double
    public var underline: Bool
    /// `1` = vertical typesetting (unsupported, rendered horizontally).
    public var typesetting: Int
    /// "Curve" text (bent along an arc), not supported.
    public var curveEnabled: Bool

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        type = c.str("type") ?? "text"
        content = c.str("content") ?? ""
        fontPath = c.str("font_path") ?? ""
        fontSize = c.num("font_size") ?? 15
        textColor = c.str("text_color") ?? "#FFFFFF"
        alignment = Int(c.num("alignment") ?? 1)
        lineSpacing = c.num("line_spacing") ?? 0.02
        letterSpacing = c.num("letter_spacing") ?? 0
        lineMaxWidth = c.num("line_max_width") ?? 0.82
        borderColor = c.str("border_color") ?? ""
        borderWidth = c.num("border_width") ?? 0.08
        backgroundColor = c.str("background_color") ?? ""
        backgroundAlpha = c.num("background_alpha") ?? 1
        backgroundRoundRadius = c.num("background_round_radius") ?? 0
        backgroundWidth = c.num("background_width") ?? 0.14
        backgroundHeight = c.num("background_height") ?? 0.14
        hasShadow = c.bool("has_shadow") ?? false
        shadowColor = c.str("shadow_color") ?? ""
        shadowAlpha = c.num("shadow_alpha") ?? 0.9
        shadowSmoothing = c.num("shadow_smoothing") ?? 0.45
        shadowDistance = c.num("shadow_distance") ?? 5
        shadowAngle = c.num("shadow_angle") ?? -45
        globalAlpha = c.num("global_alpha") ?? 1
        boldWidth = c.num("bold_width") ?? 0
        italicDegree = c.num("italic_degree") ?? 0
        underline = c.bool("underline") ?? false
        typesetting = Int(c.num("typesetting") ?? 0)
        curveEnabled = (try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("text_curve")))
            .flatMap { $0.bool("enable") } ?? false
    }
}

/// The rich-text payload stored as a JSON string in `TextMaterial.content`.
public struct TextContent: Decodable {
    public var text: String
    public var styles: [TextStyle]

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        text = c.str("text") ?? ""
        styles = c.list("styles")
    }

    public static func parse(_ json: String) -> TextContent? {
        guard let data = json.data(using: .utf8) else { return nil }
        return try? JSONDecoder().decode(TextContent.self, from: data)
    }
}

public struct TextStyle: Decodable {
    /// UTF-16 offsets [start, end).
    public var range: [Int]
    public var size: Double?
    public var fontPath: String?
    /// RGB in 0...1.
    public var fillColor: [Double]?
    public var fillAlpha: Double?
    public var strokes: [TextStroke]
    public var bold: Bool
    public var italic: Bool
    public var underline: Bool
    /// Set when the text uses a CapCut text-effect package (not supported yet).
    public var effectStylePath: String?

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        range = c.opt("range") ?? []
        size = c.num("size")
        if let font = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("font")) {
            fontPath = font.str("path")
        }
        if let fill = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("fill")) {
            fillAlpha = fill.num("alpha")
            if let content = try? fill.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("content")),
               let solid = try? content.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("solid")) {
                fillColor = solid.opt("color")
                if let a = solid.num("alpha") { fillAlpha = (fillAlpha ?? 1) * a }
            }
        }
        strokes = c.list("strokes")
        bold = c.bool("bold") ?? false
        italic = c.bool("italic") ?? false
        underline = c.bool("underline") ?? false
        if let effect = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("effectStyle")) {
            effectStylePath = effect.str("path")
        }
    }
}

public struct TextStroke: Decodable {
    /// Relative to the font size.
    public var width: Double
    public var color: [Double]
    public var alpha: Double

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        width = c.num("width") ?? 0.06
        var color: [Double] = [0, 0, 0]
        var alpha = c.num("alpha") ?? 1
        if let content = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("content")),
           let solid = try? content.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("solid")) {
            color = solid.opt("color") ?? color
            if let a = solid.num("alpha") { alpha *= a }
        }
        self.color = color
        self.alpha = alpha
    }
}
