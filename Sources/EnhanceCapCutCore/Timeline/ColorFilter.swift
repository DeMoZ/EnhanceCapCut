import Foundation

/// One of CapCut's "Filters" (`materials.effects`, `type` "filter"): colour lookup tables read from the filter's
/// package in CapCut's effect cache, applied in order at the filter's intensity.
public struct ColorFilter: Hashable {
    public var name: String
    /// The lookup tables, in the order the package applies them: `.cube` files or PNG lookup images (see
    /// `ColorFilter.layout`).
    public var luts: [URL]
    /// The "intensity" slider (`value`), 0...1.
    public var intensity: Double

    public init(name: String, luts: [URL], intensity: Double = 1) {
        self.name = name
        self.luts = luts
        self.intensity = intensity
    }

    /// The largest lookup table we read (entries per channel).
    public static let maxSize = 64

    /// How a PNG lookup image holds its N×N×N table. In every layout red grows to the right and green downwards
    /// inside a tile, and blue picks the tile.
    public enum Layout: Equatable {
        /// √N × √N tiles of N×N pixels, blue tile by tile, row by row (512×512 for N = 64).
        case grid(size: Int, tilesPerRow: Int)
        /// N tiles of N×N side by side (N² × N).
        case row(size: Int)
        /// N tiles of N×N stacked (N × N²).
        case column(size: Int)

        public var size: Int {
            switch self {
            case let .grid(size, _), let .row(size), let .column(size): size
            }
        }

        /// The pixel holding entry (r, g, b).
        public func pixel(r: Int, g: Int, b: Int) -> (x: Int, y: Int) {
            switch self {
            case let .grid(n, k): ((b % k) * n + r, (b / k) * n + g)
            case let .row(n): (b * n + r, g)
            case let .column(n): (r, b * n + g)
            }
        }
    }

    /// The layout of a lookup image of this size, or nil when it isn't one.
    public static func layout(width: Int, height: Int) -> Layout? {
        func valid(_ n: Int) -> Bool { n >= 2 && n <= maxSize }
        if width == height {
            // N = k², side = N·k = k³.
            for k in 2...8 where k * k * k == width && valid(k * k) { return .grid(size: k * k, tilesPerRow: k) }
            return nil
        }
        if height * height == width, valid(height) { return .row(size: height) }
        if width * width == height, valid(width) { return .column(size: width) }
        return nil
    }

    /// The width and height of a PNG or JPEG file, from its header, whatever its extension says (some packages ship
    /// a JPEG named `filter.png`); nil for anything else.
    public static func imageSize(_ url: URL) -> (width: Int, height: Int)? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let data = try? handle.read(upToCount: 65536) else { return nil }
        return imageSize([UInt8](data))
    }

    /// The width and height in the first bytes of a PNG or JPEG file.
    public static func imageSize(_ b: [UInt8]) -> (width: Int, height: Int)? {
        func be(_ at: Int, _ n: Int) -> Int { b[at..<at + n].reduce(0) { $0 << 8 | Int($1) } }
        if b.count >= 24, b.prefix(8) == [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] { return (be(16, 4), be(20, 4)) }
        guard b.count >= 4, b[0] == 0xFF, b[1] == 0xD8 else { return nil }
        // JPEG: walk the segments to the frame header (SOFn: C0–CF except C4 DHT, C8 JPG, CC DAC).
        var i = 2
        while i + 9 < b.count {
            guard b[i] == 0xFF else { return nil }
            let marker = b[i + 1]
            if marker == 0xFF { i += 1; continue }
            if (0xC0...0xCF).contains(marker), ![0xC4, 0xC8, 0xCC].contains(marker) { return (be(i + 7, 2), be(i + 5, 2)) }
            if marker == 0xD9 || marker == 0xDA { return nil }
            i += 2 + be(i + 2, 2)
        }
        return nil
    }

    /// The `LUT_3D_SIZE` of a `.cube` file, or nil when it has none or it's out of range.
    public static func cubeSize(_ url: URL) -> Int? {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        for line in text.split(whereSeparator: \.isNewline) {
            let parts = line.split(whereSeparator: \.isWhitespace)
            if parts.count == 2, parts[0] == "LUT_3D_SIZE", let n = Int(parts[1]), n >= 2, n <= maxSize { return n }
        }
        return nil
    }

    /// The lookup table in a filter package: the first `.cube` or lookup-shaped PNG, by path, preferring files whose
    /// name mentions "lut".
    public static func lutFile(in package: URL) -> URL? { lutFiles(in: package).first }

    /// Every `.cube` or lookup-shaped PNG in a folder, those whose name mentions "lut" first, then by path.
    public static func lutFiles(in package: URL) -> [URL] {
        let fm = FileManager.default
        guard let walker = fm.enumerator(at: package, includingPropertiesForKeys: nil) else { return [] }
        var candidates: [(rank: Int, path: String, url: URL)] = []
        for case let url as URL in walker {
            let ext = url.pathExtension.lowercased()
            let usable: Bool
            switch ext {
            case "cube": usable = cubeSize(url) != nil
            case "png", "jpg", "jpeg": usable = imageSize(url).flatMap { layout(width: $0.width, height: $0.height) } != nil
            default: continue
            }
            guard usable else { continue }
            let rank = url.lastPathComponent.lowercased().contains("lut") ? 0 : 1
            let relative = String(url.standardizedFileURL.path.dropFirst(package.standardizedFileURL.path.count))
            candidates.append((rank, relative, url))
        }
        return candidates.sorted { ($0.rank, $0.path) < ($1.rank, $1.path) }.map(\.url)
    }

    /// The parts of a package (`config.json` → `effect.Link[]`), in the order they're applied (`zorder`).
    public static func features(in package: URL) -> [URL] {
        guard let data = try? Data(contentsOf: package.appendingPathComponent("config.json")),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let links = (json["effect"] as? [String: Any])?["Link"] as? [[String: Any]] else { return [] }
        return links.enumerated().compactMap { i, link -> (Double, Int, URL)? in
            guard let path = link["path"] as? String, !path.isEmpty else { return nil }
            return ((link["zorder"] as? NSNumber)?.doubleValue ?? 0, i, package.appendingPathComponent(path, isDirectory: true))
        }.sorted { ($0.0, $0.1) < ($1.0, $1.1) }.map(\.2)
    }

    /// The lookup table a package part uses: a table in it whose path without the extension (`image/filter`) one of
    /// its `material/*.material` files names. Parts often carry unused tables, so unreferenced ones don't count.
    public static func referencedLutFile(in feature: URL) -> URL? {
        let fm = FileManager.default
        let materialDir = feature.appendingPathComponent("material", isDirectory: true)
        let materials = ((try? fm.contentsOfDirectory(at: materialDir, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.pathExtension == "material" }.compactMap { try? Data(contentsOf: $0) }
        guard !materials.isEmpty else { return nil }
        return lutFiles(in: feature).first { url in
            let relative = String(url.standardizedFileURL.deletingPathExtension().path
                .dropFirst(feature.standardizedFileURL.path.count + 1))
            let name = Data(relative.utf8)
            return materials.contains { $0.range(of: name) != nil }
        }
    }

    /// A package's lookup tables in the order it applies them: the table each part's materials use; for a package
    /// without such references, its first table (`lutFile`).
    public static func lutFiles(package: URL) -> [URL] {
        let referenced = features(in: package).compactMap(referencedLutFile)
        if !referenced.isEmpty { return referenced }
        return lutFile(in: package).map { [$0] } ?? []
    }

    /// The lookup tables for a filter: in `path` (the cached package), otherwise in the first
    /// `<cache>/<resourceID>/<hash>` that has any. Empty when none is found.
    public static func locate(path: URL?, resourceID: String, cacheRoots: [URL] = VoiceFilter.cacheRoots) -> [URL] {
        let fm = FileManager.default
        var isDirectory: ObjCBool = false
        if let path, fm.fileExists(atPath: path.path, isDirectory: &isDirectory), isDirectory.boolValue {
            let luts = lutFiles(package: path)
            if !luts.isEmpty { return luts }
        }
        guard !resourceID.isEmpty else { return [] }
        for root in cacheRoots {
            let folder = root.appendingPathComponent(resourceID, isDirectory: true)
            let entries = (try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
            for entry in entries.sorted(by: { $0.path < $1.path }) {
                let luts = lutFiles(package: entry)
                if !luts.isEmpty { return luts }
            }
        }
        return []
    }
}

/// A filter on a filter track: it colours everything painted below it (`renderIndex`) while it's active.
public struct FilterLayer {
    public var id: String
    public var target: TimeRange
    public var trackIndex: Int
    public var renderIndex: Int
    public var filter: ColorFilter

    public init(id: String, target: TimeRange, trackIndex: Int, renderIndex: Int, filter: ColorFilter) {
        self.id = id
        self.target = target
        self.trackIndex = trackIndex
        self.renderIndex = renderIndex
        self.filter = filter
    }

    public func isActive(at t: Int64) -> Bool { t >= target.start && t < target.end }

    /// True when the filter is painted after `layer` (and so colours it).
    public func covers(_ layer: VisualLayer) -> Bool {
        (renderIndex, trackIndex) > (layer.renderIndex, layer.trackIndex)
    }
}
