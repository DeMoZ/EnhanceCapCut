import Foundation

/// A keyframe's easing: CapCut's `curveType` and its bezier handles.
public struct KeyframeHandles: Equatable {
    /// `curveType` "Line": a span between two linear keyframes is a straight line whatever the handles say.
    public var linear: Bool
    /// `left_control` (incoming) and `right_control` (outgoing), relative to the keyframe: x in µs, y in the
    /// property's units.
    public var left: Point2
    public var right: Point2

    public init(linear: Bool, left: Point2, right: Point2) {
        self.linear = linear
        self.left = left
        self.right = right
    }

    init(_ keyframe: Keyframe) {
        self.init(linear: keyframe.curveType.isEmpty || keyframe.curveType == "Line",
                  left: keyframe.leftControl, right: keyframe.rightControl)
    }

    func scaled(by factor: Double) -> KeyframeHandles {
        KeyframeHandles(linear: linear, left: Point2(x: left.x, y: left.y * factor),
                        right: Point2(x: right.x, y: right.y * factor))
    }
}

/// A cubic bezier in (time, value) between two keyframes, like a graph editor's curve: P0 = (0, v0),
/// P1 = P0 + the first keyframe's outgoing handle, P2 = P3 + the second one's incoming handle, P3 = (span, v1).
/// Zero handles give a straight line. Handle times are clamped into the span so that time only moves forward.
enum KeyframeBezier {
    static func value(at u: Double, span d: Double, from v0: Double, to v1: Double,
                      out: Point2, in incoming: Point2) -> Double {
        guard d > 0 else { return v1 }
        let x1 = min(max(out.x, 0), d), x2 = min(max(d + incoming.x, 0), d)
        let y1 = v0 + out.y, y2 = v1 + incoming.y
        let s = parameter(for: min(max(u, 0), d), x1: x1, x2: x2, d: d)
        let r = 1 - s
        return r * r * r * v0 + 3 * r * r * s * y1 + 3 * r * s * s * y2 + s * s * s * v1
    }

    /// The bezier parameter where x(s) = u: Newton steps from the linear guess, bisection when they stall.
    static func parameter(for u: Double, x1: Double, x2: Double, d: Double) -> Double {
        func x(_ s: Double) -> Double {
            let r = 1 - s
            return 3 * r * r * s * x1 + 3 * r * s * s * x2 + s * s * s * d
        }
        func dx(_ s: Double) -> Double {
            let r = 1 - s
            return 3 * r * r * x1 + 6 * r * s * (x2 - x1) + 3 * s * s * (d - x2)
        }
        let tolerance = 1e-6 * d
        var s = u / d
        for _ in 0..<8 {
            let e = x(s) - u
            if abs(e) < tolerance { return s }
            let slope = dx(s)
            if abs(slope) < 1e-9 * d { break }
            s = min(max(s - e / slope, 0), 1)
        }
        var lo = 0.0, hi = 1.0
        for _ in 0..<60 {
            s = (lo + hi) / 2
            let e = x(s) - u
            if abs(e) < tolerance { break }
            if e < 0 { lo = s } else { hi = s }
        }
        return s
    }
}
