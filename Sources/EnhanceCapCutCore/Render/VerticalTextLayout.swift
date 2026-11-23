import AppKit
import CoreText

/// Places the characters of vertical text (CapCut's `typesetting` = 1) in columns.
///
/// No vertical text was available to calibrate against a CapCut export, so this follows the usual vertical (CJK)
/// typesetting: every character stays upright and is stacked top to bottom, centered in its column; a newline or a
/// column longer than the wrap limit (the same `line_max_width` limit horizontal lines use) starts a new column to
/// the left of the previous one; columns are spaced like horizontal lines; alignment 0/1/2 puts the columns' text at
/// the top / middle / bottom. A column that overflows breaks after its last space when it has one, otherwise before
/// the character that doesn't fit. Mirrored in C# by `EnhanceCapCut.Render.VerticalTextLayout`.
public enum VerticalTextLayout {
    /// Vertical advance of a character, in its font size.
    public static let characterAdvance: CGFloat = 1
    /// Vertical advance of a space, in its font size.
    public static let spaceAdvance: CGFloat = 0.5

    public enum Kind: Equatable { case character, space, newline }

    /// Where one character goes: its index in the input, the center of its column (from the left) and the top of its
    /// cell (from the top of the text box).
    public struct Placement: Equatable {
        public var index: Int
        public var centerX: CGFloat
        public var top: CGFloat
    }

    /// Lays out characters of the given kinds and vertical advances. `limit` is the longest column, `columnPitch`
    /// the distance between column centers and `columnWidth` the width of one column. Spaces and newlines get no
    /// placement. Returns the placements and the size of the text box.
    public static func layout(kinds: [Kind], advances: [CGFloat], limit: CGFloat, columnPitch: CGFloat,
                              columnWidth: CGFloat, alignment: Int) -> (placements: [Placement], size: CGSize) {
        let columns = columns(kinds: kinds, advances: advances, limit: limit)
        func length(_ column: [Int]) -> CGFloat {
            // Trailing spaces don't count, so alignment sees the visible text.
            var end = column.count
            while end > 0, kinds[column[end - 1]] == .space { end -= 1 }
            return column[..<end].reduce(0) { $0 + advances[$1] }
        }
        let lengths = columns.map(length)
        let height = lengths.max() ?? 0
        let width = columns.isEmpty ? 0 : CGFloat(columns.count - 1) * columnPitch + columnWidth
        var placements: [Placement] = []
        for (c, column) in columns.enumerated() {
            let centerX = width - columnWidth / 2 - CGFloat(c) * columnPitch
            var y: CGFloat = alignment == 0 ? 0 : alignment == 2 ? height - lengths[c] : (height - lengths[c]) / 2
            for i in column {
                if kinds[i] == .character { placements.append(Placement(index: i, centerX: centerX, top: y)) }
                y += advances[i]
            }
        }
        return (placements, CGSize(width: width, height: height))
    }

    /// Splits the characters into columns (indices; newlines dropped, spaces at a wrap dropped).
    static func columns(kinds: [Kind], advances: [CGFloat], limit: CGFloat) -> [[Int]] {
        var columns: [[Int]] = []
        var current: [Int] = []
        var length: CGFloat = 0
        for i in kinds.indices {
            switch kinds[i] {
            case .newline:
                columns.append(current)
                current = []
                length = 0
                continue
            case .space where current.isEmpty && !columns.isEmpty && i > 0 && kinds[i - 1] != .newline:
                continue  // a wrapped column doesn't start with a space
            default: break
            }
            if length + advances[i] > limit, !current.isEmpty {
                if kinds[i] == .space {
                    columns.append(current)
                    current = []
                    length = 0
                    continue
                }
                if let space = current.lastIndex(where: { kinds[$0] == .space }), space > 0 {
                    columns.append(Array(current[..<space]))
                    current = Array(current[(space + 1)...])
                } else {
                    columns.append(current)
                    current = []
                }
                length = current.reduce(0) { $0 + advances[$1] }
            }
            current.append(i)
            length += advances[i]
        }
        columns.append(current)
        return columns
    }
}

extension TextRasterizer {
    /// One drawn piece of text: its fill and outline lines, its left edge and its baseline, both from the top-left of
    /// the text box.
    typealias Piece = (fill: CTLine, outline: CTLine, x: CGFloat, baseline: CGFloat)

    /// Vertical text: one piece per character (grapheme cluster), placed by `VerticalTextLayout`. `ascent` is the
    /// part of a cell of `baseSize` above the baseline.
    static func verticalPieces(fill: NSAttributedString, outline: NSAttributedString, baseSize: CGFloat,
                               ascent: CGFloat, letterSpacing: CGFloat, limit: CGFloat, columnPitch: CGFloat,
                               alignment: Int) -> (pieces: [Piece], size: CGSize) {
        let text = fill.string as NSString
        var ranges: [NSRange] = []
        text.enumerateSubstrings(in: NSRange(location: 0, length: text.length), options: .byComposedCharacterSequences) {
            _, range, _, _ in ranges.append(range)
        }
        var kinds: [VerticalTextLayout.Kind] = [], advances: [CGFloat] = [], sizes: [CGFloat] = []
        for range in ranges {
            let piece = text.substring(with: range)
            let size = (fill.attribute(.font, at: range.location, effectiveRange: nil) as? NSFont)?.pointSize ?? baseSize
            let kind: VerticalTextLayout.Kind = piece.rangeOfCharacter(from: .newlines) != nil ? .newline
                : piece.trimmingCharacters(in: .whitespaces).isEmpty ? .space : .character
            kinds.append(kind)
            sizes.append(size)
            let advance = kind == .space ? VerticalTextLayout.spaceAdvance : VerticalTextLayout.characterAdvance
            advances.append(kind == .newline ? 0 : size * (advance + letterSpacing))
        }
        let (placements, boxSize) = VerticalTextLayout.layout(kinds: kinds, advances: advances, limit: limit,
                                                              columnPitch: columnPitch, columnWidth: baseSize,
                                                              alignment: alignment)
        func line(_ source: NSAttributedString, _ range: NSRange) -> CTLine {
            // Letter spacing goes between the stacked characters, not after each one.
            let character = NSMutableAttributedString(attributedString: source.attributedSubstring(from: range))
            character.removeAttribute(.kern, range: NSRange(location: 0, length: character.length))
            return CTLineCreateWithAttributedString(character)
        }
        let pieces: [Piece] = placements.map { p in
            let range = ranges[p.index]
            let fillLine = line(fill, range)
            let width = CGFloat(CTLineGetTypographicBounds(fillLine, nil, nil, nil))
            return (fillLine, line(outline, range), p.centerX - width / 2, p.top + sizes[p.index] * ascent / baseSize)
        }
        return (pieces, boxSize)
    }
}
