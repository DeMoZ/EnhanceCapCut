import Foundation

/// CapCut's "Clock Time" stickers, re-drawn natively: a round clock whose two hands spin in a loop.
///
/// Calibrated on the author's CapCut exports (Pullups3Wide, Pullups5Wide, 1920×1080, 30 fps): the clock is centered
/// on the clip position, its outer diameter is `diameter` × the canvas's shorter side × the clip scale, and the
/// animation starts over at the segment start every `period`. Sizes below are fractions of the outer radius.
public struct ClockSticker: Equatable {
    public enum Style: String {
        /// "Clock Time EN White Outline Progress Bar": a white ring and white hands, no face.
        case whiteOutline
        /// "Clock Time EN Blue White Minimalist": a grey ring, a white face and blue hands.
        case blueWhite
    }

    public var style: Style

    public init(style: Style) { self.style = style }

    /// The sticker resources this draws, by `materials.stickers[].resource_id`.
    public static func style(resourceID: String) -> Style? {
        switch resourceID {
        case "7528282080199904565": return .whiteOutline
        case "7528280675909209405": return .blueWhite
        default: return nil
        }
    }

    /// Outer diameter at clip scale 1, as a fraction of the canvas's shorter side (102 px at scale 0.214 and
    /// 136 px at scale 0.281 on a 1080 px tall canvas).
    public var diameter: Double { style == .whiteOutline ? 0.441 : 0.448 }
    /// Ring (outline) width.
    public var ringWidth: Double { style == .whiteOutline ? 0.157 : 0.191 }
    /// Hand lengths from the center to the tip of the rounded end, and the hands' width.
    public var minuteHand: Double { style == .whiteOutline ? 0.686 : 0.674 }
    public var hourHand: Double { style == .whiteOutline ? 0.451 : 0.465 }
    public var handWidth: Double { style == .whiteOutline ? 0.137 : 0.132 }
    /// sRGB colours, 0…1; `face` nil = transparent.
    public var ringColor: [Double] { style == .whiteOutline ? [1, 1, 1] : [0.325, 0.325, 0.325] }
    public var faceColor: [Double]? { style == .whiteOutline ? nil : [0.976, 0.976, 0.976] }
    public var handColor: [Double] { style == .whiteOutline ? [1, 1, 1] : [0, 0.471, 0.824] }

    /// One loop of the animation: 82 frames at 30 fps.
    public static let period: Int64 = 2_733_333
    /// Measured hand angles every 1/15 s of the loop, in degrees clockwise from 12 o'clock, relative to the rest pose
    /// (minute hand at 12, hour hand at 3). The minute hand turns twice, the hour hand once, both slowing down; the
    /// last ~0.13 s of the loop they rest.
    static let minuteTable: [Double] = [
        0, 37, 73, 107, 141, 188, 217, 245, 270, 303, 321, 336, 347, 354, 370, 389, 408, 425, 452, 469,
        486, 503, 519, 543, 558, 574, 588, 609, 621, 633, 645, 657, 674, 680, 689, 699, 706, 711, 715, 720,
    ]
    static let hourTable: [Double] = [
        0, 13, 29, 47, 57, 67, 84, 96, 109, 126, 138, 150, 162, 172, 189, 199, 208, 218, 232, 241,
        249, 258, 267, 277, 285, 292, 300, 309, 315, 320, 325, 330, 337, 342, 346, 349, 355, 356, 357, 360,
    ]
    static let tableStep = 1_000_000.0 / 15

    /// The hands' angles (degrees clockwise from 12 o'clock) `offset` µs after the segment start.
    public static func handAngles(atOffset offset: Int64) -> (minute: Double, hour: Double) {
        var t = offset % period
        if t < 0 { t += period }
        let x = Double(t) / tableStep
        func sample(_ table: [Double]) -> Double {
            let i = Int(x)
            guard i + 1 < table.count else { return table[table.count - 1] }
            return table[i] + (table[i + 1] - table[i]) * (x - Double(i))
        }
        return ((sample(minuteTable)).truncatingRemainder(dividingBy: 360),
                (90 + sample(hourTable)).truncatingRemainder(dividingBy: 360))
    }
}

/// A sticker clip the renderer draws itself.
public struct StickerSpec {
    /// The sticker's name in CapCut.
    public var name: String
    public var clock: ClockSticker
}
