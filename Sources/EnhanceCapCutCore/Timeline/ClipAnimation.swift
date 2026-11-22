import Foundation

/// A video or photo clip's In / Out animation, re-implemented as a curve on top of the clip transform.
///
/// The presets are recognised by their name as CapCut stores it (in the UI language it was applied in). The motion is our own
/// approximation of CapCut's preset, not a copy of its effect package; the constants below are where to calibrate it.
public struct ClipAnimation: Equatable {
    public enum Kind: String { case `in`, out }

    public enum Preset: String, CaseIterable {
        case fade, zoomIn, zoomOut, slideLeft, slideRight, slideUp, slideDown, rotate, rotateCounterclockwise, shake
    }

    public var kind: Kind
    public var preset: Preset
    /// Start and end in µs from the segment start.
    public var start: Int64
    public var end: Int64

    /// Scale the clip starts from (In) or ends at (Out) for "Zoom In" / "Zoom Out".
    static let zoomInScale = 0.5
    static let zoomOutScale = 1.5
    /// How far a slide travels, in half-canvas units (2 = one canvas width or height: from off-screen).
    static let slideDistance = 2.0
    /// Full turns of "Rotate".
    static let rotateDegrees = 360.0
    /// "Shake": peak offset in half-canvas units, and the number of swings over the animation.
    static let shakeAmplitude = 0.06
    static let shakeSwings = 4.0

    /// The clip's animations, and the names of the ones the renderer doesn't reproduce (Combo and unknown presets).
    public static func make(_ material: AnimationMaterial?, segmentDuration: Int64) -> ([ClipAnimation], unsupported: [String]) {
        var din: Int64 = 0, dout: Int64 = 0
        var found: [(Kind, Preset)] = []
        var unsupported: [String] = []
        for item in material?.animations ?? [] {
            guard let kind = Kind(rawValue: item.type), let preset = preset(named: item.name), item.duration > 0 else {
                unsupported.append(item.name.isEmpty ? item.type : item.name)
                continue
            }
            if kind == .in { din = item.duration } else { dout = item.duration }
            found.append((kind, preset))
        }
        // `start` is always 0 in the draft: an Out plays in the clip's last `duration`. An In and Out that don't fit
        // are squeezed into the clip, as for texts.
        let total = max(segmentDuration, 0)
        if din + dout > total, din + dout > 0 {
            din = Int64(Double(din) * Double(total) / Double(din + dout))
            dout = total - din
        }
        let animations = found.map { kind, preset in
            kind == .in ? ClipAnimation(kind: kind, preset: preset, start: 0, end: din)
                : ClipAnimation(kind: kind, preset: preset, start: total - dout, end: total)
        }
        return (animations.filter { $0.end > $0.start }, unsupported)
    }

    /// The preset for a CapCut animation name (English, Chinese or Russian UI), or nil when it isn't reproduced.
    static func preset(named name: String) -> Preset? {
        let n = name.lowercased().replacingOccurrences(of: "-", with: " ")
        func has(_ words: [String]) -> Bool { words.contains { n.contains($0) } }
        if has(["counterclockwise", "anticlockwise", "逆时针", "против час"]) { return .rotateCounterclockwise }
        if has(["rotat", "spin", "旋转", "вращ", "поворот"]) { return .rotate }
        if has(["shake", "抖动", "тряс", "дрож"]) { return .shake }
        if has(["slide", "滑动", "сдвиг", "скольж", "слайд"]) {
            if has(["left", "向左", "влево", "налево"]) { return .slideLeft }
            if has(["right", "向右", "вправо", "направо"]) { return .slideRight }
            if has(["up", "向上", "вверх"]) { return .slideUp }
            if has(["down", "向下", "вниз"]) { return .slideDown }
            return nil
        }
        if has(["zoom in", "放大", "увелич", "приближ"]) { return .zoomIn }
        if has(["zoom out", "缩小", "уменьш", "отдал"]) { return .zoomOut }
        if has(["fade", "渐显", "渐隐", "растворен", "проявлен", "затухан", "угасан", "появлен", "исчезн"]) { return .fade }
        return nil
    }

    /// Applies the animations that are running at `offset` (µs from the segment start).
    public static func apply(_ animations: [ClipAnimation], to transform: LayerTransform, offset: Int64) -> LayerTransform {
        animations.reduce(transform) { $1.apply(to: $0, offset: offset) }
    }

    func apply(to transform: LayerTransform, offset: Int64) -> LayerTransform {
        // Before an In the clip is in its far state; after an Out it stays there.
        if offset < start { return kind == .in ? apply(to: transform, progress: 0) : transform }
        if offset >= end { return kind == .out ? apply(to: transform, progress: 1) : transform }
        return apply(to: transform, progress: Double(offset - start) / Double(end - start))
    }

    /// `amount` = 1 at the animation's far state (start of In, end of Out), 0 at the clip's own transform.
    func apply(to t: LayerTransform, progress p: Double) -> LayerTransform {
        // Ease out into the clip (In), ease in away from it (Out).
        let amount = kind == .in ? pow(1 - p, 3) : pow(p, 3)
        var t = t
        switch preset {
        case .fade:
            t.alpha *= 1 - amount
        case .zoomIn, .zoomOut:
            // "Zoom In" grows: In starts small, Out ends large. "Zoom Out" shrinks: the other way round.
            let grows = preset == .zoomIn
            let far = (kind == .in) == grows ? Self.zoomInScale : Self.zoomOutScale
            let s = 1 + (far - 1) * amount
            t.scaleX *= s
            t.scaleY *= s
            t.alpha *= 1 - amount
        case .slideLeft, .slideRight, .slideUp, .slideDown:
            // The direction is the motion: an In "Slide Right" comes from the left, an Out one leaves to the right.
            let (dx, dy): (Double, Double) = switch preset {
            case .slideLeft: (-1, 0)
            case .slideRight: (1, 0)
            case .slideUp: (0, 1)
            default: (0, -1)
            }
            let d = Self.slideDistance * amount * (kind == .in ? -1 : 1)
            t.positionX += dx * d
            t.positionY += dy * d
        case .rotate, .rotateCounterclockwise:
            let sign = preset == .rotate ? 1.0 : -1.0
            t.rotation += sign * Self.rotateDegrees * amount * (kind == .in ? -1 : 1)
            t.scaleX *= 1 - amount
            t.scaleY *= 1 - amount
        case .shake:
            // A decaying (In) or growing (Out) side-to-side swing; linear envelope so it's visible throughout.
            let envelope = kind == .in ? 1 - p : p
            t.positionX += Self.shakeAmplitude * envelope * sin(2 * .pi * Self.shakeSwings * p)
        }
        return t
    }
}
