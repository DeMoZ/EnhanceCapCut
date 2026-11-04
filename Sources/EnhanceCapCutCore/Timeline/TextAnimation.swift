import Foundation

/// CapCut's text intro / outro / loop animations, re-implemented as opacity, scale, offset and a per-character
/// reveal over the clip's time.
///
/// Presets are recognized by their name (CapCut stores it in the language it was applied in, so English and
/// Chinese names and the Russian UI's words are matched). Anything else is reported by name.
///
/// The motion is not calibrated against CapCut's exports yet (the author's projects have no text animations):
/// the distances and curves below are our own choice and are kept as named constants.
public struct TextAnimations: Equatable {
    public var intro: TextAnimation?
    public var outro: TextAnimation?
    public var loop: TextAnimation?

    public init(intro: TextAnimation? = nil, outro: TextAnimation? = nil, loop: TextAnimation? = nil) {
        self.intro = intro
        self.outro = outro
        self.loop = loop
    }

    public var isEmpty: Bool { intro == nil && outro == nil && loop == nil }

    /// Slide distance in half-canvas units (0.2 = a tenth of the canvas).
    static let slideDistance = 0.2
    /// Scale a "zoom out" intro starts from (and a "zoom in" outro ends at).
    static let zoomLargeScale = 1.5
    /// Opacity swing of "blink" and scale swing of "pulse" loops.
    static let blinkMinimumAlpha = 0.2
    static let pulseScale = 0.1
    /// Shortest loop cycle; CapCut's loop `duration` is the length of one cycle.
    static let minimumLoopCycle: Int64 = 100_000

    /// The animations of a clip, and the names of the ones the renderer doesn't reproduce.
    public static func make(_ material: AnimationMaterial?) -> (TextAnimations, unsupported: [String]) {
        var result = TextAnimations()
        var unsupported: [String] = []
        for item in material?.animations ?? [] {
            guard let kind = TextAnimation.Kind(rawValue: item.type),
                  let effect = TextAnimation.Effect.recognize(item.name, kind: kind) else {
                unsupported.append(item.name.isEmpty ? item.type : item.name)
                continue
            }
            let animation = TextAnimation(kind: kind, effect: effect, duration: max(item.duration, 0))
            switch kind {
            case .intro: result.intro = animation
            case .outro: result.outro = animation
            case .loop: result.loop = animation
            }
        }
        return (result, unsupported)
    }

    /// The largest scale the animations reach (text is rasterized at it so it stays sharp).
    public var maxScale: Double {
        [intro, outro, loop].compactMap { $0 }.map { animation -> Double in
            switch animation.effect {
            case let .zoom(scale, _): return max(scale, 1)
            case .pop: return 1.1
            case .pulse: return 1 + TextAnimations.pulseScale
            default: return 1
            }
        }.max() ?? 1
    }

    /// The animation state at `offset` µs into a clip of `duration` µs.
    public func state(atOffset offset: Int64, duration: Int64) -> TextAnimationState {
        guard duration > 0 else { return TextAnimationState() }
        var din = intro?.duration ?? 0, dout = outro?.duration ?? 0
        // CapCut squeezes an intro and outro that don't fit into the clip.
        if din + dout > duration {
            let k = Double(duration) / Double(din + dout)
            din = Int64(Double(din) * k)
            dout = duration - din
        }
        if let intro, din > 0, offset < din {
            return intro.effect.state(kind: .intro, progress: Double(offset) / Double(din))
        }
        if let outro, dout > 0, offset >= duration - dout {
            return outro.effect.state(kind: .outro, progress: Double(offset - (duration - dout)) / Double(dout))
        }
        if let loop {
            let cycle = max(loop.duration, TextAnimations.minimumLoopCycle)
            let phase = Double((offset - din) % cycle) / Double(cycle)
            return loop.effect.state(kind: .loop, progress: phase)
        }
        return TextAnimationState()
    }
}

public struct TextAnimation: Equatable {
    public enum Kind: String { case intro = "in", outro = "out", loop }

    public enum Effect: Equatable {
        case fade
        /// Scale from (intro) or to (outro) `scale`, fading when `fades`.
        case zoom(scale: Double, fades: Bool)
        /// Grows from nothing with a small overshoot.
        case pop
        /// Moves in the direction (dx, dy) (+y up): an intro arrives from the opposite side, an outro leaves.
        case slide(dx: Double, dy: Double)
        /// Characters appear one by one (outro: disappear from the end).
        case typewriter
        case blink
        case pulse

        /// The preset for a CapCut animation name, or nil when it isn't reproduced.
        static func recognize(_ name: String, kind: Kind) -> Effect? {
            let n = name.lowercased()
            func has(_ words: [String]) -> Bool { words.contains { n.contains($0) } }
            if kind == .loop {
                if has(["blink", "flicker", "flash", "闪烁", "мерца", "мига"]) { return .blink }
                if has(["pulse", "heartbeat", "breath", "心跳", "呼吸", "пульс", "сердцеби", "дыхан"]) { return .pulse }
                return nil
            }
            if has(["typewriter", "typing", "打字机", "печатн", "машинк"]) { return .typewriter }
            if has(["fade", "dissolve", "渐显", "渐隐", "растворен", "проявлен", "затухан", "угасан"]) { return .fade }
            if has(["pop", "弹入", "弹出", "всплыт"]) { return .pop }
            if has(["slide", "move", "滑动", "移动", "сдвиг", "скольж", "слайд"]) {
                let d = TextAnimations.slideDistance
                if has(["left", "左", "влево", "налево"]) { return .slide(dx: -d, dy: 0) }
                if has(["right", "右", "вправо", "направо"]) { return .slide(dx: d, dy: 0) }
                if has(["up", "上", "вверх"]) { return .slide(dx: 0, dy: d) }
                if has(["down", "下", "вниз"]) { return .slide(dx: 0, dy: -d) }
                return nil
            }
            let large = TextAnimations.zoomLargeScale
            if has(["zoom in", "放大", "увеличен"]) {
                // Grows: an intro from nothing, an outro beyond its size while fading.
                return kind == .intro ? .zoom(scale: 0, fades: false) : .zoom(scale: large, fades: true)
            }
            if has(["zoom out", "缩小", "уменьшен"]) {
                // Shrinks: an intro from larger while appearing, an outro to nothing.
                return kind == .intro ? .zoom(scale: large, fades: true) : .zoom(scale: 0, fades: false)
            }
            return nil
        }

        /// The state at `progress` (0…1) of an intro or outro, or at the phase of a loop cycle.
        func state(kind: Kind, progress: Double) -> TextAnimationState {
            let p = min(max(progress, 0), 1)
            if kind == .loop {
                let wave = 0.5 - 0.5 * cos(2 * .pi * p)  // 0 → 1 → 0 over a cycle
                var s = TextAnimationState()
                switch self {
                case .blink: s.alpha = 1 - (1 - TextAnimations.blinkMinimumAlpha) * wave
                case .pulse: s.scale = 1 + TextAnimations.pulseScale * wave
                default: break
                }
                return s
            }
            // How far from the resting state: 1 at the start of an intro and at the end of an outro.
            let away: Double
            switch self {
            case .fade, .typewriter: away = kind == .intro ? 1 - p : p
            case .pop: away = kind == .intro ? 1 - backOut(p) : p * p * p
            default: away = kind == .intro ? pow(1 - p, 3) : p * p * p
            }
            var s = TextAnimationState()
            switch self {
            case .fade: s.alpha = 1 - away
            case let .zoom(scale, fades):
                s.scale = 1 + (scale - 1) * away
                if fades { s.alpha = 1 - away }
            case .pop: s.scale = 1 - away
            case let .slide(dx, dy):
                let sign = kind == .intro ? -1.0 : 1.0
                s.offsetX = sign * dx * away
                s.offsetY = sign * dy * away
                s.alpha = 1 - away
            case .typewriter: s.reveal = 1 - away
            case .blink, .pulse: break
            }
            return s
        }

        /// Ease-out with a small overshoot (≈ +10 %).
        private func backOut(_ p: Double) -> Double {
            let c = 1.70158, q = p - 1
            return 1 + (c + 1) * q * q * q + c * q * q
        }
    }

    public var kind: Kind
    public var effect: Effect
    /// µs; for a loop, one cycle.
    public var duration: Int64

    public init(kind: Kind, effect: Effect, duration: Int64) {
        self.kind = kind
        self.effect = effect
        self.duration = duration
    }
}

/// What the animations do to a text clip at one moment, on top of its own transform.
public struct TextAnimationState: Equatable {
    public var alpha = 1.0
    public var scale = 1.0
    /// Half-canvas units, +y up.
    public var offsetX = 0.0
    public var offsetY = 0.0
    /// The fraction of characters shown (typewriter).
    public var reveal = 1.0

    public init() {}

    /// UTF-16 length of the part of `text` shown at `reveal`: whole characters (grapheme clusters, spaces
    /// and line breaks included), rounded down.
    public static func visibleLength(of text: String, reveal: Double) -> Int {
        guard reveal < 1 else { return text.utf16.count }
        let shown = Int((max(reveal, 0) * Double(text.count)).rounded(.down))
        return text.prefix(shown).utf16.count
    }
}
