import Foundation

/// A coding key that accepts any string, so models can read keys without declaring an enum.
struct AnyKey: CodingKey {
    var stringValue: String
    var intValue: Int?
    init(_ string: String) { stringValue = string }
    init?(stringValue: String) { self.stringValue = stringValue }
    init?(intValue: Int) { self.intValue = intValue; stringValue = String(intValue) }
}

/// CapCut drafts change between app versions. Every field is read leniently: a missing key,
/// a `null` or a value of an unexpected type yields `nil` instead of failing the whole draft.
extension KeyedDecodingContainer where Key == AnyKey {
    func opt<T: Decodable>(_ key: String, as type: T.Type = T.self) -> T? {
        guard let value = try? decodeIfPresent(T.self, forKey: AnyKey(key)) else { return nil }
        return value
    }

    func num(_ key: String) -> Double? {
        if let d: Double = opt(key) { return d }
        if let s: String = opt(key) { return Double(s) }
        return nil
    }

    func int64(_ key: String) -> Int64? {
        if let i: Int64 = opt(key) { return i }
        if let d: Double = opt(key) { return Int64(d.rounded()) }
        return nil
    }

    func str(_ key: String) -> String? { opt(key) }

    func bool(_ key: String) -> Bool? {
        if let b: Bool = opt(key) { return b }
        if let i: Int = opt(key) { return i != 0 }
        return nil
    }

    /// Decodes an array, skipping elements that fail to decode.
    func list<T: Decodable>(_ key: String, of type: T.Type = T.self) -> [T] {
        guard let wrapped: [Lossy<T>] = opt(key) else { return [] }
        return wrapped.compactMap(\.value)
    }
}

/// Wraps a value so that a single malformed array element doesn't break decoding of the array.
struct Lossy<T: Decodable>: Decodable {
    let value: T?
    init(from decoder: Decoder) throws {
        value = try? T(from: decoder)
    }
}

extension Decoder {
    func anyKeyed() throws -> KeyedDecodingContainer<AnyKey> {
        try container(keyedBy: AnyKey.self)
    }
}
