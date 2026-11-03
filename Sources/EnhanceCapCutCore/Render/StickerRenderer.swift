import CoreGraphics
import CoreImage

/// Draws natively rendered stickers ("Clock Time", see `ClockSticker`) with Core Graphics.
enum StickerRenderer {
    /// The clock `offset` µs after its segment start, `pixels` wide (the outer diameter), a square image.
    static func render(_ clock: ClockSticker, pixels: Int, atOffset offset: Int64) -> CIImage? {
        let n = max(pixels, 4)
        guard let ctx = CGContext(data: nil, width: n, height: n, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setShouldAntialias(true)
        func color(_ c: [Double]) -> CGColor {
            CGColor(srgbRed: CGFloat(c[0]), green: CGFloat(c[1]), blue: CGFloat(c[2]), alpha: 1)
        }
        let r = CGFloat(n) / 2
        let center = CGPoint(x: r, y: r)
        let ring = CGFloat(clock.ringWidth) * r
        if let face = clock.faceColor {
            ctx.setFillColor(color(face))
            ctx.fillEllipse(in: CGRect(x: ring / 2, y: ring / 2, width: 2 * r - ring, height: 2 * r - ring))
        }
        ctx.setStrokeColor(color(clock.ringColor))
        ctx.setLineWidth(ring)
        ctx.strokeEllipse(in: CGRect(x: ring / 2, y: ring / 2, width: 2 * r - ring, height: 2 * r - ring))

        let width = CGFloat(clock.handWidth) * r
        let angles = ClockSticker.handAngles(atOffset: offset)
        ctx.setStrokeColor(color(clock.handColor))
        ctx.setLineWidth(width)
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)
        // Core Graphics is y-up: clockwise from 12 o'clock is (sin a, cos a).
        func tip(_ degrees: Double, _ length: Double) -> CGPoint {
            let a = degrees * .pi / 180, l = CGFloat(length) * r - width / 2
            return CGPoint(x: center.x + l * CGFloat(sin(a)), y: center.y + l * CGFloat(cos(a)))
        }
        ctx.move(to: tip(angles.minute, clock.minuteHand))
        ctx.addLine(to: center)
        ctx.addLine(to: tip(angles.hour, clock.hourHand))
        ctx.strokePath()

        guard let cg = ctx.makeImage() else { return nil }
        return CIImage(cgImage: cg)
    }
}
