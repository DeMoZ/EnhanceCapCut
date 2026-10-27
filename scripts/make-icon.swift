#!/usr/bin/env swift
// Draws the app icon and writes App/AppIcon.icns (macOS) and windows/src/EnhanceCapCut.App/EnhanceCapCut.ico.
//   swift scripts/make-icon.swift            # from the repository root
//   swift scripts/make-icon.swift <dir>      # also keeps the 1024 px PNGs in <dir> for a look
//
// The icon: an hourglass-like "cut" mark (a nod to CapCut's, drawn our own way) in a cyan-to-magenta gradient on a dark
// tile, with sparkles for "enhance". All geometry is in tile units (0…1, y down) so every size is drawn, not scaled.
import AppKit
import CoreGraphics
import Foundation

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let keep = CommandLine.arguments.dropFirst().first.map { URL(fileURLWithPath: $0) }

/// macOS tiles follow Apple's grid (824 of 1024 px with a soft shadow); Windows icons fill almost the whole square.
struct Style {
    var tile: CGFloat      // tile side as a fraction of the canvas
    var corner: CGFloat    // corner radius as a fraction of the tile
    var shadow: Bool
}
let mac = Style(tile: 824.0 / 1024, corner: 0.225, shadow: true)
let windows = Style(tile: 0.94, corner: 0.2, shadow: false)

func color(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat(hex >> 16 & 0xFF) / 255, green: CGFloat(hex >> 8 & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}

/// A four-pointed star with concave sides, centred on `c` with radius `r`.
func sparkle(_ c: CGPoint, _ r: CGFloat) -> CGPath {
    let path = CGMutablePath()
    let k = r * 0.16
    path.move(to: CGPoint(x: c.x, y: c.y - r))
    path.addQuadCurve(to: CGPoint(x: c.x + r, y: c.y), control: CGPoint(x: c.x + k, y: c.y - k))
    path.addQuadCurve(to: CGPoint(x: c.x, y: c.y + r), control: CGPoint(x: c.x + k, y: c.y + k))
    path.addQuadCurve(to: CGPoint(x: c.x - r, y: c.y), control: CGPoint(x: c.x - k, y: c.y + k))
    path.addQuadCurve(to: CGPoint(x: c.x, y: c.y - r), control: CGPoint(x: c.x - k, y: c.y - k))
    path.closeSubpath()
    return path
}

func render(_ px: Int, _ style: Style) -> Data {
    let size = CGFloat(px)
    let ctx = CGContext(data: nil, width: px, height: px, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    // Tile coordinates: origin at the tile's top-left corner, 1 = tile side, y down.
    let side = (size * style.tile).rounded()
    let origin = ((size - side) / 2).rounded()
    ctx.translateBy(x: origin, y: size - origin)
    ctx.scaleBy(x: side, y: -side)

    let tile = CGPath(roundedRect: CGRect(x: 0, y: 0, width: 1, height: 1),
                      cornerWidth: style.corner, cornerHeight: style.corner, transform: nil)
    if style.shadow {
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -side * 0.012), blur: side * 0.035, color: color(0, 0.45))
        ctx.addPath(tile)
        ctx.setFillColor(color(0x0E1016))
        ctx.fillPath()
        ctx.restoreGState()
    }

    // Background: a dark diagonal gradient with a faint glow behind the mark.
    ctx.saveGState()
    ctx.addPath(tile)
    ctx.clip()
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    let background = CGGradient(colorsSpace: space, colors: [color(0x2C3140), color(0x0B0D12)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(background, start: CGPoint(x: 0, y: 0), end: CGPoint(x: 1, y: 1), options: [])
    let glow = CGGradient(colorsSpace: space, colors: [color(0x6A5CFF, 0.35), color(0x6A5CFF, 0)] as CFArray, locations: [0, 1])!
    ctx.drawRadialGradient(glow, startCenter: CGPoint(x: 0.45, y: 0.52), startRadius: 0,
                           endCenter: CGPoint(x: 0.45, y: 0.52), endRadius: 0.5, options: [])
    // A thin light edge along the top, as on the system icons.
    ctx.addPath(tile)
    ctx.setStrokeColor(color(0xFFFFFF, 0.08))
    ctx.setLineWidth(max(0.012, 1.5 / side))
    ctx.strokePath()
    ctx.restoreGState()

    // The mark: an hourglass of rounded strokes (top bar, crossing diagonals, bottom bar), filled with a gradient.
    let (left, right, top, bottom): (CGFloat, CGFloat, CGFloat, CGFloat) = (0.22, 0.58, 0.27, 0.75)
    let mark = CGMutablePath()
    mark.move(to: CGPoint(x: left, y: top))
    mark.addLine(to: CGPoint(x: right, y: top))
    mark.addLine(to: CGPoint(x: left, y: bottom))
    mark.addLine(to: CGPoint(x: right, y: bottom))
    mark.closeSubpath()
    // Small icons get thicker strokes so the shape survives 16 px.
    let width: CGFloat = side < 40 ? 0.12 : 0.095
    // Stroked in pixels: in tile units the round joins come out as coarse polygons.
    var toPixels = CGAffineTransform(scaleX: side, y: side)
    var toTile = toPixels.inverted()
    let outline = mark.copy(using: &toPixels)!
        .copy(strokingWithWidth: width * side, lineCap: .round, lineJoin: .round, miterLimit: 10)
    ctx.saveGState()
    ctx.addPath(outline.copy(using: &toTile)!)
    ctx.clip()
    let ink = CGGradient(colorsSpace: space, colors: [color(0x35E3FF), color(0x7B6CFF), color(0xFF4FB8)] as CFArray,
                         locations: [0, 0.5, 1])!
    ctx.drawLinearGradient(ink, start: CGPoint(x: left, y: top), end: CGPoint(x: right, y: bottom),
                           options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    ctx.restoreGState()

    // Sparkles: a big one off the top-right corner of the mark and, from 32 px up, a small one below it.
    ctx.setFillColor(color(0xFFFFFF))
    ctx.setShadow(offset: .zero, blur: side * 0.04, color: color(0xFFE38A, 0.8))
    ctx.addPath(sparkle(CGPoint(x: 0.79, y: 0.28), side < 40 ? 0.16 : 0.13))
    ctx.fillPath()
    if side >= 30 {
        ctx.addPath(sparkle(CGPoint(x: 0.80, y: 0.53), 0.065))
        ctx.fillPath()
    }

    let rep = NSBitmapImageRep(cgImage: ctx.makeImage()!)
    return rep.representation(using: .png, properties: [:])!
}

// macOS: an .iconset folder turned into .icns by iconutil.
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for points in [16, 32, 128, 256, 512] {
    try render(points, mac).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
    try render(points * 2, mac).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", root.appendingPathComponent("App/AppIcon.icns").path]
try iconutil.run()
iconutil.waitUntilExit()
precondition(iconutil.terminationStatus == 0, "iconutil failed")

// Windows: an .ico with PNG images (supported since Windows Vista) for the sizes Explorer and the taskbar use.
let sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
let images = sizes.map { render($0, windows) }
var ico = Data()
func append16(_ v: Int) { ico.append(contentsOf: [UInt8(v & 0xFF), UInt8(v >> 8 & 0xFF)]) }
func append32(_ v: Int) { append16(v & 0xFFFF); append16(v >> 16) }
append16(0); append16(1); append16(sizes.count)
var offset = 6 + 16 * sizes.count
for (px, png) in zip(sizes, images) {
    ico.append(UInt8(px >= 256 ? 0 : px)); ico.append(UInt8(px >= 256 ? 0 : px)) // 0 means 256
    ico.append(0); ico.append(0)       // no palette, reserved
    append16(1); append16(32)          // planes, bits per pixel
    append32(png.count); append32(offset)
    offset += png.count
}
images.forEach { ico.append($0) }
try ico.write(to: root.appendingPathComponent("windows/src/EnhanceCapCut.App/EnhanceCapCut.ico"))

if let keep {
    try FileManager.default.createDirectory(at: keep, withIntermediateDirectories: true)
    try render(1024, mac).write(to: keep.appendingPathComponent("icon-mac.png"))
    try render(1024, windows).write(to: keep.appendingPathComponent("icon-windows.png"))
    for px in [16, 32, 48] { try render(px, windows).write(to: keep.appendingPathComponent("icon-windows-\(px).png")) }
}
print("Wrote App/AppIcon.icns and windows/src/EnhanceCapCut.App/EnhanceCapCut.ico")
