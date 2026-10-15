import AppKit
import CoreImage
import CoreText

/// Renders CapCut text/subtitle materials with Core Text.
///
/// Calibration against the user's CapCut exports (ink widths of known strings):
/// - a style `size` of 1 equals `canvasWidth / 155` pixels of font size for fonts picked in CapCut
///   (size 15 on a 1080-wide canvas ≈ 105 px), but `canvasWidth / 172` for CapCut's default
///   `SystemFont/en.ttf`;
/// - lines are spaced by about one font size (plus `line_spacing`), tighter than the fonts' own metrics;
/// - the text box is centered on the clip position.
enum TextRasterizer {
    static let sizeDivisor: CGFloat = 155
    static let defaultFontSizeDivisor: CGFloat = 172
    static let lineHeight: CGFloat = 0.97
    /// CapCut wraps a little before `line_max_width`: for 0.82 its limit measured between 0.786 and
    /// 0.817 of the canvas width; we use 0.80.
    static let wrapFactor: CGFloat = 0.80 / 0.82
    /// Visible outline thickness per unit of CapCut's stroke `width` (measured on exports).
    static let strokeScale: CGFloat = 0.75

    /// Returns the text as an image whose extent starts at the origin; `scale` multiplies the canvas-relative size.
    static func render(_ spec: TextLayerSpec, canvasWidth: CGFloat, scale: CGFloat) -> CIImage? {
        let material = spec.material
        let text = spec.content.text as NSString
        guard text.length > 0 else { return nil }
        func unit(_ fontPath: String?) -> CGFloat {
            let path = fontPath ?? material.fontPath
            let isDefault = path.isEmpty || path.contains("/Font/SystemFont/")
            return canvasWidth / (isDefault ? defaultFontSizeDivisor : sizeDivisor) * scale
        }
        let baseStyle = spec.content.styles.first

        let attributed = NSMutableAttributedString(string: text as String)
        let full = NSRange(location: 0, length: text.length)
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = material.alignment == 0 ? .left : material.alignment == 2 ? .right : .center
        let baseSize = CGFloat(baseStyle?.size ?? material.fontSize) * unit(baseStyle?.fontPath)
        attributed.addAttribute(.paragraphStyle, value: paragraph, range: full)

        var maxFontSize: CGFloat = 0
        // Outline pass: (range, outline thickness relative to font size, color).
        var outlines: [(NSRange, CGFloat, CGColor)] = []
        var strokeWidth: CGFloat = 0
        let styles = spec.content.styles.isEmpty ? [nil] : spec.content.styles.map(Optional.some)
        for style in styles {
            var range = full
            if let r = style?.range, r.count == 2 {
                let lo = max(0, min(r[0], text.length)), hi = max(lo, min(r[1], text.length))
                range = NSRange(location: lo, length: hi - lo)
            }
            guard range.length > 0 else { continue }
            let size = CGFloat(style?.size ?? material.fontSize) * unit(style?.fontPath)
            maxFontSize = max(maxFontSize, size)
            let font = makeFont(path: style?.fontPath ?? material.fontPath, size: size,
                                bold: (style?.bold ?? false) || material.boldWidth > 0,
                                italic: (style?.italic ?? false) || material.italicDegree > 0)
            attributed.addAttribute(.font, value: font, range: range)
            let alpha = (style?.fillAlpha ?? 1) * material.globalAlpha
            let color = style?.fillColor.map { rgb($0, alpha: alpha) } ?? hexColor(material.textColor, alpha: alpha)
            attributed.addAttribute(.foregroundColor, value: NSColor(cgColor: color) ?? .white, range: range)
            if material.letterSpacing != 0 {
                attributed.addAttribute(.kern, value: CGFloat(material.letterSpacing) * size, range: range)
            }
            if (style?.underline ?? false) || material.underline {
                attributed.addAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue, range: range)
            }
            if let stroke = style?.strokes.first, stroke.width > 0 {
                let relative = CGFloat(stroke.width) * strokeScale
                strokeWidth = max(strokeWidth, relative * size)
                outlines.append((range, relative, rgb(stroke.color, alpha: stroke.alpha * material.globalAlpha)))
            }
        }
        if outlines.isEmpty, !material.borderColor.isEmpty, material.borderWidth > 0 {
            let relative = CGFloat(material.borderWidth) * strokeScale
            strokeWidth = relative * maxFontSize
            outlines.append((full, relative, hexColor(material.borderColor, alpha: material.globalAlpha)))
        }
        guard maxFontSize > 0 else { return nil }
        applyFallbacks(attributed)

        // Break lines at the material's max line width (a fraction of the canvas width) and at newlines.
        let wrapWidth = max(CGFloat(material.lineMaxWidth), 0.05) * wrapFactor * canvasWidth * scale
        let outline = NSMutableAttributedString(attributedString: attributed)
        outline.addAttribute(.foregroundColor, value: NSColor.clear, range: full)
        for (range, relative, color) in outlines {
            // Positive stroke width = outline only, in percent of the font size, centered on the glyph edge.
            outline.addAttribute(.strokeWidth, value: relative * 200, range: range)
            outline.addAttribute(.strokeColor, value: NSColor(cgColor: color) ?? .black, range: range)
        }
        let typesetter = CTTypesetterCreateWithAttributedString(attributed)
        var lines: [(fill: CTLine, outline: CTLine, width: CGFloat)] = []
        var start = 0
        while start < text.length {
            let count = max(1, CTTypesetterSuggestLineBreak(typesetter, start, Double(wrapWidth)))
            var range = NSRange(location: start, length: count)
            start += count
            // Drop the trailing newline so it doesn't add width.
            while range.length > 0, let scalar = Unicode.Scalar(text.character(at: range.location + range.length - 1)),
                  CharacterSet.newlines.contains(scalar) {
                range.length -= 1
            }
            let fill = CTLineCreateWithAttributedString(attributed.attributedSubstring(from: range))
            let stroke = CTLineCreateWithAttributedString(outline.attributedSubstring(from: range))
            let width = CGFloat(CTLineGetTypographicBounds(fill, nil, nil, nil)) - CGFloat(CTLineGetTrailingWhitespaceWidth(fill))
            lines.append((fill, stroke, max(width, 0)))
        }
        guard !lines.isEmpty else { return nil }

        // CapCut spaces lines by about one font size, ignoring the font's own ascent/descent/leading.
        let pitch = baseSize * (lineHeight + CGFloat(material.lineSpacing))
        let baseFont = makeFont(path: baseStyle?.fontPath ?? material.fontPath, size: baseSize, bold: false, italic: false)
        let a = CTFontGetAscent(baseFont), d = CTFontGetDescent(baseFont)
        let ascent = baseSize * a / max(a + d, 1), descent = baseSize - ascent
        let textSize = CGSize(width: ceil(lines.map(\.width).max() ?? 0),
                              height: ceil(CGFloat(lines.count - 1) * pitch + ascent + descent))

        let hasBackground = !material.backgroundColor.isEmpty && material.backgroundAlpha > 0
        let padX = hasBackground ? CGFloat(material.backgroundWidth) * baseSize : 0
        let padY = hasBackground ? CGFloat(material.backgroundHeight) * baseSize : 0
        let shadowOffset = material.hasShadow ? CGFloat(material.shadowDistance) * baseSize / 18 : 0
        let shadowBlur = material.hasShadow ? CGFloat(material.shadowSmoothing) * baseSize / 3 : 0
        // Glyphs can overhang the line boxes; keep room for them, the outline and the shadow.
        let margin = ceil(strokeWidth * 2 + shadowOffset + shadowBlur * 2 + maxFontSize * 0.4 + 4)
        let boxSize = CGSize(width: textSize.width + padX * 2, height: textSize.height + padY * 2)
        let width = Int(boxSize.width + margin * 2), height = Int(boxSize.height + margin * 2)
        guard width > 0, height > 0, width < 16384, height < 16384,
              let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }

        let boxRect = CGRect(x: margin, y: margin, width: boxSize.width, height: boxSize.height)
        if hasBackground {
            let radius = CGFloat(material.backgroundRoundRadius) * min(boxRect.width, boxRect.height) / 2
            ctx.setFillColor(hexColor(material.backgroundColor, alpha: material.backgroundAlpha * material.globalAlpha))
            ctx.addPath(CGPath(roundedRect: boxRect, cornerWidth: radius, cornerHeight: radius, transform: nil))
            ctx.fillPath()
        }
        ctx.textMatrix = .identity
        if material.hasShadow {
            let angle = CGFloat(material.shadowAngle) * .pi / 180
            let color = hexColor(material.shadowColor.isEmpty ? "#000000" : material.shadowColor,
                                 alpha: material.shadowAlpha * material.globalAlpha)
            ctx.setShadow(offset: CGSize(width: cos(angle) * shadowOffset, height: sin(angle) * shadowOffset),
                          blur: shadowBlur, color: color)
        }

        let textLeft = margin + padX
        let firstBaseline = CGFloat(height) - margin - padY - ascent  // CG origin is bottom-left
        func draw(_ pick: (CTLine, CTLine) -> CTLine) {
            for (i, line) in lines.enumerated() {
                let x: CGFloat
                switch paragraph.alignment {
                case .left: x = textLeft
                case .right: x = textLeft + textSize.width - line.width
                default: x = textLeft + (textSize.width - line.width) / 2
                }
                ctx.textPosition = CGPoint(x: x, y: firstBaseline - CGFloat(i) * pitch)
                CTLineDraw(pick(line.fill, line.outline), ctx)
            }
        }
        if !outlines.isEmpty {
            // Outline first, fill on top, so the stroke only grows outward.
            ctx.saveGState()
            ctx.setLineJoin(.round)
            draw { $1 }
            ctx.restoreGState()
            ctx.setShadow(offset: .zero, blur: 0, color: nil)
        }
        draw { fill, _ in fill }

        guard let cg = ctx.makeImage() else { return nil }
        return CIImage(cgImage: cg)
    }

    private static var fontCache: [String: CTFontDescriptor] = [:]
    private static let fontLock = NSLock()

    private static func descriptor(for path: String) -> CTFontDescriptor? {
        if let cached = fontCache[path] { return cached }
        guard !path.isEmpty, FileManager.default.fileExists(atPath: path),
              let list = CTFontManagerCreateFontDescriptorsFromURL(URL(fileURLWithPath: path) as CFURL) as? [CTFontDescriptor],
              let first = list.first else { return nil }
        fontCache[path] = first
        return first
    }

    static func makeFont(path: String, size: CGFloat, bold: Bool, italic: Bool) -> NSFont {
        fontLock.lock()
        let descriptor = descriptor(for: path)
        fontLock.unlock()
        var font: CTFont = descriptor.map { CTFontCreateWithFontDescriptor($0, size, nil) }
            ?? CTFontCreateUIFontForLanguage(.system, size, nil)
            ?? CTFontCreateWithName("Helvetica" as CFString, size, nil)
        var traits: CTFontSymbolicTraits = []
        if bold { traits.insert(.traitBold) }
        if italic { traits.insert(.traitItalic) }
        if !traits.isEmpty, let styled = CTFontCreateCopyWithSymbolicTraits(font, size, nil, traits, traits) {
            font = styled
        }
        return font as NSFont
    }

    // MARK: Fallback fonts

    /// CapCut's bundled fallback fonts, read from the installed app at render time. CapCut's default font
    /// (en.ttf) is Latin-only. Measured on exports: Cyrillic comes out at the nominal size, while Thai (and
    /// presumably the other Noto scripts) is scaled so its ascent+descent matches the primary font's,
    /// which makes Thai glyphs ≈0.66× the nominal size.
    static let capCutFontDirectory = "/Applications/CapCut.app/Contents/Resources/Font/SystemFont"
    static let fallbackFontFiles: [(file: String, matchLineHeight: Bool)] = [
        ("NotoSans-Regular.ttf", false), ("NotoSansThai-Regular.ttf", true), ("NotoSansArabic-Regular.ttf", true),
        ("NotoSansHebrew-Regular.ttf", true), ("NotoSansBengali-Regular.ttf", true), ("NotoSansKhmer-Regular.ttf", true),
        ("NotoSansMyanmar-Regular.ttf", true), ("ja.ttf", true), ("ko.ttf", true), ("zh-hans.ttf", true),
        ("NotoSansSymbols2-Regular.ttf", true),
    ]
    private static var fallbackDescriptors: [(CTFontDescriptor, Bool)]?

    private static func fallbacks() -> [(CTFontDescriptor, Bool)] {
        fontLock.lock()
        defer { fontLock.unlock() }
        if fallbackDescriptors == nil {
            fallbackDescriptors = fallbackFontFiles.compactMap { entry in
                descriptor(for: capCutFontDirectory + "/" + entry.file).map { ($0, entry.matchLineHeight) }
            }
        }
        return fallbackDescriptors ?? []
    }

    private static func lineHeight(_ font: CTFont) -> CGFloat {
        (CTFontGetAscent(font) + CTFontGetDescent(font)) / max(CTFontGetSize(font), 1)
    }

    private static func covers(_ font: CTFont, _ chars: [UniChar]) -> Bool {
        var glyphs = [CGGlyph](repeating: 0, count: chars.count)
        return CTFontGetGlyphsForCharacters(font, chars, &glyphs, chars.count)
    }

    /// Assigns CapCut's fallback fonts to characters the styled font can't draw.
    static func applyFallbacks(_ string: NSMutableAttributedString) {
        let descriptors = fallbacks()
        guard !descriptors.isEmpty else { return }
        let text = string.string as NSString
        var replacements: [(NSRange, NSFont)] = []
        string.enumerateAttribute(.font, in: NSRange(location: 0, length: text.length)) { value, runRange, _ in
            guard let primary = value as? NSFont else { return }
            let primaryCT = primary as CTFont
            var index = runRange.location
            while index < NSMaxRange(runRange) {
                let range = text.rangeOfComposedCharacterSequence(at: index)
                index = NSMaxRange(range)
                var chars = [UniChar](repeating: 0, count: range.length)
                text.getCharacters(&chars, range: range)
                if chars.allSatisfy({ $0 < 0x80 }) || covers(primaryCT, chars) { continue }
                for (descriptor, matchLineHeight) in descriptors {
                    let candidate = CTFontCreateWithFontDescriptor(descriptor, primary.pointSize, nil)
                    guard covers(candidate, chars) else { continue }
                    let size = matchLineHeight
                        ? primary.pointSize * lineHeight(primaryCT) / max(lineHeight(candidate), 0.01)
                        : primary.pointSize
                    replacements.append((range, CTFontCreateWithFontDescriptor(descriptor, size, nil) as NSFont))
                    break
                }
            }
        }
        for (range, font) in replacements { string.addAttribute(.font, value: font, range: range) }
    }

    static func rgb(_ c: [Double], alpha: Double) -> CGColor {
        let r = c.count > 0 ? c[0] : 1, g = c.count > 1 ? c[1] : 1, b = c.count > 2 ? c[2] : 1
        return CGColor(srgbRed: r, green: g, blue: b, alpha: alpha)
    }

    /// "#RRGGBB" or "#AARRGGBB" (CapCut uses both).
    static func hexColor(_ hex: String, alpha: Double = 1) -> CGColor {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard let v = UInt64(s, radix: 16) else { return CGColor(srgbRed: 1, green: 1, blue: 1, alpha: alpha) }
        var a = 1.0
        let rgb: UInt64
        if s.count == 8 { a = Double((v >> 24) & 0xFF) / 255; rgb = v & 0xFFFFFF } else { rgb = v }
        return CGColor(srgbRed: Double((rgb >> 16) & 0xFF) / 255, green: Double((rgb >> 8) & 0xFF) / 255,
                       blue: Double(rgb & 0xFF) / 255, alpha: a * alpha)
    }
}
