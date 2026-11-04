import Foundation

/// The shapes of CapCut's clip masks (see FORMAT.md › Masks).
public enum MaskShape: String, CaseIterable {
    /// "Split": a straight edge; the part above the line (before rotation) is kept.
    case line
    /// "Mirror": a band `height` tall.
    case mirror
    case circle
    case rectangle
    case heart
    case star
}

/// A mask's placement, in the clip's own frame (before the clip transform): center in half-frame units (+y up),
/// size as fractions of the frame's width and height.
public struct MaskGeometry: Equatable {
    public var centerX: Double
    public var centerY: Double
    public var width: Double
    public var height: Double
    /// Degrees, clockwise.
    public var rotation: Double
    /// 0...1.
    public var feather: Double
    /// 0...1 of half the shorter side.
    public var roundCorner: Double

    public init(centerX: Double = 0, centerY: Double = 0, width: Double = 0.5, height: Double = 0.5,
                rotation: Double = 0, feather: Double = 0, roundCorner: Double = 0) {
        self.centerX = centerX
        self.centerY = centerY
        self.width = width
        self.height = height
        self.rotation = rotation
        self.feather = feather
        self.roundCorner = roundCorner
    }
}

/// Mask properties that can be keyframed: `KFTypeCommonMask<raw value>` (older drafts: `KFTypeMask<raw value>`).
public enum MaskProperty: String, CaseIterable {
    case positionX = "PositionX"
    case positionY = "PositionY"
    case width = "SizeWidth"
    case height = "SizeHeight"
    case rotation = "Rotation"
    case feather = "Feather"
    case roundCorner = "RoundCorner"
    /// Read so it isn't reported as unknown; the renderer reports a non-zero expansion instead.
    case expansion = "Expansion"

    /// The property of a keyframe group's `property_type`, nil when it isn't a known mask property.
    public init?(propertyType: String) {
        for prefix in ["KFTypeCommonMask", "KFTypeMask"] where propertyType.hasPrefix(prefix) {
            self.init(rawValue: String(propertyType.dropFirst(prefix.count)))
            return
        }
        return nil
    }

    public static func isMask(_ propertyType: String) -> Bool {
        propertyType.hasPrefix("KFTypeCommonMask") || propertyType.hasPrefix("KFTypeMask")
    }
}

/// One of a clip's masks. A clip can have several; what any of them keeps is shown.
public struct ClipMask {
    public enum Kind: Equatable {
        case shape(MaskShape)
        /// "Brush": CapCut's painted strokes, one raw 8-bit frame per file named by its source time in µs.
        case brush(URL)
    }

    public var kind: Kind
    public var geometry: MaskGeometry
    public var inverted: Bool
    /// Keyframes on the clip's source time axis (µs), like CapCut stores them.
    public var keyframes: [MaskProperty: KeyframeTrack]

    public init(kind: Kind, geometry: MaskGeometry = MaskGeometry(), inverted: Bool = false,
                keyframes: [MaskProperty: KeyframeTrack] = [:]) {
        self.kind = kind
        self.geometry = geometry
        self.inverted = inverted
        self.keyframes = keyframes
    }

    /// The geometry at source time `t` (µs), with keyframes applied.
    public func geometry(at t: Int64) -> MaskGeometry {
        var g = geometry
        func kf(_ p: MaskProperty) -> Double? { keyframes[p]?.value(at: t) }
        g.centerX = kf(.positionX) ?? g.centerX
        g.centerY = kf(.positionY) ?? g.centerY
        g.width = kf(.width) ?? g.width
        g.height = kf(.height) ?? g.height
        g.rotation = kf(.rotation) ?? g.rotation
        g.feather = kf(.feather) ?? g.feather
        g.roundCorner = kf(.roundCorner) ?? g.roundCorner
        return g
    }

    /// The shape of a mask material's `resource_type` (and name, for the geometric shapes).
    public static func shape(resourceType: String, name: String) -> MaskShape? {
        if let shape = MaskShape(rawValue: resourceType) { return shape }
        let n = name.lowercased()
        switch resourceType {
        case "geometric_shape", "heart", "star", "love", "pentagram":
            if n.contains("heart") || resourceType == "heart" || resourceType == "love" { return .heart }
            if n.contains("star") || resourceType == "star" || resourceType == "pentagram" { return .star }
            return nil
        default:
            return nil
        }
    }
}
