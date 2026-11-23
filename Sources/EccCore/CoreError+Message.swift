// Hand-written, next to the generated EccCore.swift (scripts/build-core.sh rewrites only that file).
import Foundation

/// The localized text of a core error. UniFFI's `errorDescription` is `String(reflecting: self)`, which uses this
/// conformance, so `localizedDescription` is the message the core wrote (design decision 4).
extension CoreError: CustomDebugStringConvertible {
    public var message: String {
        switch self {
        case .ProjectOpen(let message), .NoTimelineList(let message), .NotFound(let message),
             .Unreadable(let message), .Malformed(let message), .Io(let message), .Internal(let message):
            return message
        }
    }

    public var debugDescription: String { message }
}
