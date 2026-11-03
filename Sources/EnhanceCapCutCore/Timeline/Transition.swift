import Foundation

/// A transition from a clip into the next clip on its track (`materials.transitions`, referenced from the outgoing
/// clip's `extra_material_refs`). CapCut centres it on the cut: the outgoing clip keeps playing until the cut and
/// the incoming one starts there, each holding its edge frame for the other half (see FORMAT.md).
public struct LayerTransition {
    public enum Kind: Equatable {
        /// CapCut's "Flashback": a zoom-and-spin blur that peaks at the cut, dissolving into the next clip.
        case flashback
        /// Every other transition, approximated by a cross-fade.
        case crossFade
    }

    /// CapCut's name for it.
    public let name: String
    public let kind: Kind
    /// The incoming clip.
    public let next: VisualLayer
    /// The timeline range it plays over.
    public let range: TimeRange

    public func contains(_ t: Int64) -> Bool { t >= range.start && t < range.end }

    /// 0 at the start, 0.5 at the cut, 1 at the end.
    public func progress(at t: Int64) -> Double {
        guard range.duration > 0 else { return 1 }
        return min(max(Double(t - range.start) / Double(range.duration), 0), 1)
    }

    /// "Flashback"'s effect id in CapCut's library.
    static let flashbackID = "7252586720078598658"
    /// Clips closer than this (µs) count as adjacent.
    static let adjacency: Int64 = 40_000

    public static func kind(name: String, resourceID: String) -> Kind {
        name == "Flashback" || resourceID == flashbackID ? .flashback : .crossFade
    }

    /// The transition from `from` into `next`, or nil when they don't touch. CapCut keeps a transition within half
    /// of each clip; so do we.
    static func make(name: String, resourceID: String, duration: Int64, from: VisualLayer,
                     into next: VisualLayer) -> LayerTransition? {
        let cut = from.target.end
        guard abs(next.target.start - cut) <= adjacency else { return nil }
        let half = min(duration / 2, from.target.duration / 2, next.target.duration / 2)
        guard half > 0 else { return nil }
        return LayerTransition(name: name, kind: kind(name: name, resourceID: resourceID), next: next,
                               range: TimeRange(start: cut - half, duration: 2 * half))
    }
}

/// Our own "Flashback" look, calibrated by eye against CapCut's exports (ATV): both clips zoom in and turn a little
/// while a zoom-and-spin blur builds up to the cut and fades out after it; the picture dissolves around the cut.
public enum FlashbackLook {
    /// Copies averaged for the blur.
    public static let blurCopies = 16
    /// Zoom at the peak (fraction of the frame) and the extra zoom the blur spreads over.
    public static let zoom = 0.2
    public static let blurZoom = 0.25
    /// Rotation at the peak and the extra rotation the blur spreads over, degrees clockwise.
    public static let rotation = 6.0
    public static let blurRotation = 14.0
    /// The dissolve into the next clip runs over this part of the transition, centred on the cut.
    public static let dissolveStart = 0.35
    public static let dissolveEnd = 0.65

    /// Strength of the effect: 0 at the edges, 1 at the cut.
    public static func strength(_ p: Double) -> Double { sin(Double.pi * min(max(p, 0), 1)) }

    /// How much of the next clip shows.
    public static func mix(_ p: Double) -> Double {
        let x = min(max((p - dissolveStart) / (dissolveEnd - dissolveStart), 0), 1)
        return x * x * (3 - 2 * x)
    }

    /// Scale and clockwise rotation (degrees) about the frame centre of blur copy `i` at progress `p`.
    public static func copy(_ i: Int, at p: Double) -> (scale: Double, rotation: Double) {
        let s = strength(p)
        let f = blurCopies > 1 ? Double(i) / Double(blurCopies - 1) : 0
        return (1 + (zoom + blurZoom * f) * s, (rotation + blurRotation * f) * s)
    }
}
