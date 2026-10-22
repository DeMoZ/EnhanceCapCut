import Foundation

/// Maps a time offset inside a segment's timeline range to a time in its source media.
/// Constant speed is a straight line; a CapCut speed curve is integrated numerically.
public struct TimeMap: Equatable {
    public var sourceStart: Int64
    public var sourceDuration: Int64
    public var targetDuration: Int64
    /// Normalized cumulative source position S(u) sampled at `u = i / (count - 1)`; nil for constant speed.
    public var curveTable: [Double]?

    public init(sourceStart: Int64, sourceDuration: Int64, targetDuration: Int64, curvePoints: [Point2] = []) {
        self.sourceStart = sourceStart
        self.sourceDuration = sourceDuration
        self.targetDuration = targetDuration
        self.curveTable = curvePoints.count >= 2 ? TimeMap.integrate(curvePoints) : nil
    }

    /// A map sampled from a monotonic function giving the source time (µs) at a normalized target
    /// position u in 0...1. Used to slice and compose maps; a straight line collapses to constant speed.
    public init(targetDuration: Int64, sampling source: (Double) -> Double) {
        let n = TimeMap.tableSize
        var values = (0...n).map { source(Double($0) / Double(n)) }
        for i in 1...n { values[i] = max(values[i], values[i - 1]) }
        let start = values[0], span = values[n] - values[0]
        sourceStart = Int64(start.rounded())
        sourceDuration = Int64(span.rounded())
        self.targetDuration = targetDuration
        guard span > 0 else { curveTable = nil; return }
        let table = values.map { ($0 - start) / span }
        let isLine = table.indices.allSatisfy { abs(table[$0] - Double($0) / Double(n)) < 1e-4 }
        curveTable = isLine ? nil : table
    }

    public var isConstant: Bool { curveTable == nil }
    public var averageSpeed: Double {
        targetDuration > 0 ? Double(sourceDuration) / Double(targetDuration) : 1
    }

    /// Source time (µs) shown at `offset` µs after the segment's timeline start.
    public func sourceTime(atOffset offset: Int64) -> Int64 {
        Int64(preciseSourceTime(atOffset: Double(offset)).rounded())
    }

    public func preciseSourceTime(atOffset offset: Double) -> Double {
        guard targetDuration > 0 else { return Double(sourceStart) }
        let u = min(max(offset / Double(targetDuration), 0), 1)
        return Double(sourceStart) + normalizedSource(u) * Double(sourceDuration)
    }

    /// Inverse of `sourceTime`: the offset at which source time `time` is shown (clamped to the segment).
    public func offset(atSourceTime time: Int64) -> Int64 {
        guard sourceDuration > 0, targetDuration > 0 else { return 0 }
        let s = min(max(Double(time - sourceStart) / Double(sourceDuration), 0), 1)
        var u = s
        if !isConstant {
            var lo = 0.0, hi = 1.0
            for _ in 0..<40 {
                let mid = (lo + hi) / 2
                if normalizedSource(mid) < s { lo = mid } else { hi = mid }
            }
            u = hi
        }
        return Int64((u * Double(targetDuration)).rounded())
    }

    /// The part of this map between two offsets, as a map of its own (offset 0 = `from`).
    public func slice(from a: Int64, to b: Int64) -> TimeMap {
        if isConstant {
            let s0 = sourceTime(atOffset: a)
            return TimeMap(sourceStart: s0, sourceDuration: sourceTime(atOffset: b) - s0, targetDuration: b - a)
        }
        return TimeMap(targetDuration: b - a) { u in preciseSourceTime(atOffset: Double(a) + u * Double(b - a)) }
    }

    /// S(u) in 0...1.
    public func normalizedSource(_ u: Double) -> Double {
        guard let table = curveTable else { return u }
        let pos = u * Double(table.count - 1)
        let i = min(Int(pos), table.count - 2)
        let f = pos - Double(i)
        return table[i] + (table[i + 1] - table[i]) * f
    }

    /// Instantaneous speed multiplier at normalized position u (relative to the average).
    public func speed(at u: Double) -> Double {
        guard let table = curveTable else { return averageSpeed }
        let n = Double(table.count - 1)
        let i = min(Int(u * n), table.count - 2)
        return (table[i + 1] - table[i]) * n * averageSpeed
    }

    static let tableSize = 1024

    /// Integrates the piecewise-linear speed curve so that S(0) = 0 and S(1) = 1.
    static func integrate(_ rawPoints: [Point2]) -> [Double] {
        let points = rawPoints.sorted { $0.x < $1.x }
        func v(_ x: Double) -> Double {
            if x <= points[0].x { return max(points[0].y, 0.01) }
            for k in 1..<points.count where x <= points[k].x {
                let a = points[k - 1], b = points[k]
                let t = b.x > a.x ? (x - a.x) / (b.x - a.x) : 1
                return max(a.y + (b.y - a.y) * t, 0.01)
            }
            return max(points[points.count - 1].y, 0.01)
        }
        var table = [Double](repeating: 0, count: tableSize + 1)
        var acc = 0.0
        var prev = v(0)
        for i in 1...tableSize {
            let x = Double(i) / Double(tableSize)
            let cur = v(x)
            acc += (prev + cur) * 0.5 / Double(tableSize)
            table[i] = acc
            prev = cur
        }
        if acc > 0 { table = table.map { $0 / acc } }
        return table
    }
}

/// Linear interpolation over a segment's keyframes for one property.
public struct KeyframeTrack: Equatable {
    public var times: [Int64]
    public var values: [Double]

    public init(times: [Int64], values: [Double]) {
        self.times = times
        self.values = values
    }

    public func value(at offset: Int64) -> Double {
        guard let first = times.first, let last = times.last else { return 0 }
        if offset <= first { return values[0] }
        if offset >= last { return values[values.count - 1] }
        var i = 1
        while i < times.count && times[i] < offset { i += 1 }
        let t0 = times[i - 1], t1 = times[i]
        let f = t1 > t0 ? Double(offset - t0) / Double(t1 - t0) : 1
        return values[i - 1] + (values[i] - values[i - 1]) * f
    }

    public init?(group: KeyframeGroup) {
        let frames = group.keyframes.filter { !$0.values.isEmpty }.sorted { $0.timeOffset < $1.timeOffset }
        guard !frames.isEmpty else { return nil }
        times = frames.map(\.timeOffset)
        values = frames.map { $0.values[0] }
    }
}
