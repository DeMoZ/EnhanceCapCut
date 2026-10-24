import Foundation

/// Locates values inside a draft's JSON text so the draft can be edited in place: everything we
/// don't touch stays byte-for-byte as CapCut wrote it (key order, number formatting, unknown fields).
struct JSONScanner {
    enum ScanError: LocalizedError {
        case malformed(Int)

        var errorDescription: String? {
            switch self {
            case let .malformed(offset): return "The draft is not valid JSON (at byte \(offset))."
            }
        }
    }

    let bytes: [UInt8]

    init(_ data: Data) { bytes = [UInt8](data) }

    /// The whole document's value.
    func root() throws -> Range<Int> {
        let start = skipSpace(0)
        return start..<(try valueEnd(start))
    }

    /// Members of the object spanning `range`, in document order.
    func members(_ range: Range<Int>) throws -> [(key: String, value: Range<Int>)] {
        guard bytes[range.lowerBound] == UInt8(ascii: "{") else { throw ScanError.malformed(range.lowerBound) }
        var out: [(String, Range<Int>)] = []
        var i = skipSpace(range.lowerBound + 1)
        while i < range.upperBound, bytes[i] != UInt8(ascii: "}") {
            let keyEnd = try stringEnd(i)
            let key = try JSONSerialization.jsonObject(with: Data(bytes[i..<keyEnd]), options: .fragmentsAllowed) as? String ?? ""
            i = skipSpace(keyEnd)
            guard i < bytes.count, bytes[i] == UInt8(ascii: ":") else { throw ScanError.malformed(i) }
            let start = skipSpace(i + 1)
            let end = try valueEnd(start)
            out.append((key, start..<end))
            i = skipSpace(end)
            if i < bytes.count, bytes[i] == UInt8(ascii: ",") { i = skipSpace(i + 1) }
        }
        return out
    }

    /// The value of `key` in the object spanning `range`.
    func value(_ key: String, in range: Range<Int>) throws -> Range<Int>? {
        try members(range).first { $0.key == key }?.value
    }

    /// Elements of the array spanning `range`.
    func elements(_ range: Range<Int>) throws -> [Range<Int>] {
        guard bytes[range.lowerBound] == UInt8(ascii: "[") else { throw ScanError.malformed(range.lowerBound) }
        var out: [Range<Int>] = []
        var i = skipSpace(range.lowerBound + 1)
        while i < range.upperBound, bytes[i] != UInt8(ascii: "]") {
            let end = try valueEnd(i)
            out.append(i..<end)
            i = skipSpace(end)
            if i < bytes.count, bytes[i] == UInt8(ascii: ",") { i = skipSpace(i + 1) }
        }
        return out
    }

    /// Parses the value spanning `range` (for reading; edits go through `JSONEdits`).
    func object(_ range: Range<Int>) -> Any? {
        try? JSONSerialization.jsonObject(with: Data(bytes[range]), options: .fragmentsAllowed)
    }

    private func skipSpace(_ start: Int) -> Int {
        var i = start
        while i < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[i]) { i += 1 }
        return i
    }

    private func stringEnd(_ start: Int) throws -> Int {
        guard start < bytes.count, bytes[start] == UInt8(ascii: "\"") else { throw ScanError.malformed(start) }
        var i = start + 1
        while i < bytes.count {
            switch bytes[i] {
            case UInt8(ascii: "\\"): i += 2
            case UInt8(ascii: "\""): return i + 1
            default: i += 1
            }
        }
        throw ScanError.malformed(start)
    }

    private func valueEnd(_ start: Int) throws -> Int {
        guard start < bytes.count else { throw ScanError.malformed(start) }
        switch bytes[start] {
        case UInt8(ascii: "\""):
            return try stringEnd(start)
        case UInt8(ascii: "{"), UInt8(ascii: "["):
            var depth = 0
            var i = start
            while i < bytes.count {
                switch bytes[i] {
                case UInt8(ascii: "\""): i = try stringEnd(i); continue
                case UInt8(ascii: "{"), UInt8(ascii: "["): depth += 1
                case UInt8(ascii: "}"), UInt8(ascii: "]"):
                    depth -= 1
                    if depth == 0 { return i + 1 }
                default: break
                }
                i += 1
            }
            throw ScanError.malformed(start)
        default:
            // Number, true, false or null.
            var i = start
            while i < bytes.count, !",}] \t\r\n".utf8.contains(bytes[i]) { i += 1 }
            guard i > start else { throw ScanError.malformed(start) }
            return i
        }
    }
}

/// Replacements of byte ranges, applied back to front.
struct JSONEdits {
    private var edits: [(range: Range<Int>, text: [UInt8])] = []

    mutating func replace(_ range: Range<Int>, with text: String) {
        edits.append((range, Array(text.utf8)))
    }

    func applied(to bytes: [UInt8]) -> Data {
        var out = bytes
        for edit in edits.sorted(by: { $0.range.lowerBound > $1.range.lowerBound }) {
            out.replaceSubrange(edit.range, with: edit.text)
        }
        return Data(out)
    }
}

/// Serializes values parsed by `JSONSerialization` (or built from them) like CapCut writes them:
/// compact, slashes unescaped, and numbers that were floating point keep a decimal point (`1.0`, not `1`).
enum JSONText {
    static func encode(_ value: Any?) -> String {
        var out = ""
        write(value, to: &out)
        return out
    }

    private static func write(_ value: Any?, to out: inout String) {
        switch value {
        case nil, is NSNull:
            out += "null"
        case let s as String:
            writeString(s, to: &out)
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() {
                out += n.boolValue ? "true" : "false"
            } else if CFNumberIsFloatType(n) {
                let d = n.doubleValue
                out += d.isFinite ? "\(d)" : "0.0"
            } else {
                out += "\(n.int64Value)"
            }
        case let a as [Any]:
            out += "["
            for (i, element) in a.enumerated() {
                if i > 0 { out += "," }
                write(element, to: &out)
            }
            out += "]"
        case let d as [String: Any]:
            out += "{"
            for (i, key) in d.keys.sorted().enumerated() {
                if i > 0 { out += "," }
                writeString(key, to: &out)
                out += ":"
                write(d[key], to: &out)
            }
            out += "}"
        default:
            out += "null"
        }
    }

    private static func writeString(_ s: String, to out: inout String) {
        out += "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case _ where scalar.value < 0x20: out += String(format: "\\u%04x", scalar.value)
            default: out.unicodeScalars.append(scalar)
            }
        }
        out += "\""
    }
}
