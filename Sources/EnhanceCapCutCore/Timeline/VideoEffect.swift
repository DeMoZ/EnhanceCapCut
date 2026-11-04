import Foundation

/// A `materials.video_effects` entry ("Effects → Video effects" in CapCut). Its look lives in an effect package in
/// CapCut's cache; the effects we render are re-implemented in `VideoEffect`.
public struct VideoEffectMaterial: Decodable {
    public var id: String
    public var name: String
    /// The effect's id in CapCut's library (`effect_id` / `resource_id`).
    public var resourceID: String
    /// 0 = applied to the clip it's attached to (`extra_material_refs`), 2 = an effect-track segment over the frame.
    public var applyTargetType: Int
    /// The sliders (`adjust_params`): `effects_adjust_speed`, `effects_adjust_size`, … by name.
    public var params: [String: Double]

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        name = c.str("name") ?? ""
        resourceID = c.str("effect_id") ?? c.str("resource_id") ?? ""
        applyTargetType = Int(c.num("apply_target_type") ?? 0)
        var params: [String: Double] = [:]
        for p in c.list("adjust_params", of: AdjustParam.self) where !p.name.isEmpty { params[p.name] = p.value }
        self.params = params
    }

    struct AdjustParam: Decodable {
        var name: String
        var value: Double?
        init(from decoder: Decoder) throws {
            let c = try decoder.anyKeyed()
            name = c.str("name") ?? ""
            value = c.num("value") ?? c.num("default_value")
        }
    }
}

/// A video effect on a clip, re-implemented in our code: its look is a function of time (`state`) that the
/// compositors apply to the clip's picture inside its own frame (see FORMAT.md, "Video effects").
public struct VideoEffect: Equatable {
    public enum Kind: String, CaseIterable {
        /// "Pudding Bounce": the clip opens on a white flash, zoomed in and blurred, and bounces back to rest.
        case puddingBounce
        /// "Hazy Rotation": the clip opens zoomed in, tilted and out of focus, then turns and settles.
        case hazyRotation
    }

    public var kind: Kind
    /// Where the effect starts, relative to the clip start (µs).
    public var start: Int64
    /// Playback rate of the effect: 1 at CapCut's default "Speed" slider.
    public var speed: Double

    public init(kind: Kind, start: Int64 = 0, speed: Double = 1) {
        self.kind = kind
        self.start = start
        self.speed = speed
    }

    /// CapCut's effect ids of the effects we render.
    static let kinds: [String: Kind] = [
        "7534579654615649553": .puddingBounce,
        "7486402432189091127": .hazyRotation,
    ]

    /// CapCut's default "Speed" slider value (`effects_adjust_speed`).
    static let defaultSpeedSlider = 1.0 / 3

    /// The effect of a material applied to a clip, or nil when it isn't one we render.
    public init?(material: VideoEffectMaterial) {
        guard material.applyTargetType == 0, let kind = VideoEffect.kinds[material.resourceID] else { return nil }
        let slider = material.params["effects_adjust_speed"] ?? VideoEffect.defaultSpeedSlider
        self.init(kind: kind, speed: max(slider, 0.01) / VideoEffect.defaultSpeedSlider)
    }

    /// The effect's look `offset` µs after the clip start.
    public func state(atOffset offset: Int64) -> VideoEffectState {
        guard offset >= start else { return .identity }
        let t = Double(offset - start) / 1_000_000 * speed
        switch kind {
        case .puddingBounce: return VideoEffect.puddingBounce(t)
        case .hazyRotation: return VideoEffect.hazyRotation(t)
        }
    }

    // Calibration against the author's CapCut export ("Мокрая прогулка", 148.9 s and 31.5 s; 30 fps frame grabs).

    /// White mix at the first frame (mean luma 146 → 204 of 255) and its decay time: halves every 2 frames.
    static let puddingFlash = 0.53, puddingFlashDecay = 0.097
    /// Zoom at the first frame (≈1.45×), its decay and the bounce period (a slight undershoot at ≈0.3 s).
    static let puddingZoom = 0.45, puddingZoomDecay = 0.09, puddingBouncePeriod = 0.6
    /// Blur at the first frame (fraction of the picture's shorter side) and when it is gone (sharp again at 0.4 s).
    static let puddingBlur = 0.008, puddingBlurEnd = 0.4
    static let puddingEnd = 0.8

    static func puddingBounce(_ t: Double) -> VideoEffectState {
        guard t < puddingEnd else { return .identity }
        let blurLeft = max(0, 1 - t / puddingBlurEnd)
        return VideoEffectState(
            zoom: 1 + puddingZoom * exp(-t / puddingZoomDecay) * cos(2 * .pi * t / puddingBouncePeriod),
            rotation: 0,
            blur: puddingBlur * blurLeft * blurLeft,
            flash: t < 0.35 ? puddingFlash * exp(-t / puddingFlashDecay) : 0)
    }

    /// The opening hold (zoomed in ≈2×, tilted, heavy blur) and the settle that follows (gone at ≈0.36 s).
    static let hazyHold = 0.13, hazySettle = 0.23
    static let hazyZoom = 1.0, hazyTilt = -15.0, hazyBlur = 0.012

    static func hazyRotation(_ t: Double) -> VideoEffectState {
        guard t < hazyHold + hazySettle else { return .identity }
        let u = max(0, t - hazyHold) / hazySettle
        let left = (1 - u) * (1 - u)
        return VideoEffectState(zoom: 1 + hazyZoom * left, rotation: hazyTilt * left, blur: hazyBlur * left, flash: 0)
    }
}

/// What a video effect does to a clip's picture at one moment, applied in this order inside the picture's own frame:
/// blur, then zoom and rotation about the centre (edges clamped), then a mix towards white.
public struct VideoEffectState: Equatable {
    public var zoom: Double
    /// Degrees, clockwise on screen.
    public var rotation: Double
    /// Gaussian blur radius as a fraction of the picture's shorter side.
    public var blur: Double
    /// Mix towards white, 0...1 (alpha kept).
    public var flash: Double

    public static let identity = VideoEffectState(zoom: 1, rotation: 0, blur: 0, flash: 0)

    public var isIdentity: Bool {
        abs(zoom - 1) < 0.0005 && abs(rotation) < 0.01 && blur < 0.0002 && flash < 0.002
    }

    /// Several effects on one clip: zooms multiply, the rest add up.
    public static func combined(_ states: [VideoEffectState]) -> VideoEffectState {
        states.reduce(.identity) { a, b in
            VideoEffectState(zoom: a.zoom * b.zoom, rotation: a.rotation + b.rotation, blur: a.blur + b.blur,
                             flash: min(a.flash + b.flash, 1))
        }
    }
}
