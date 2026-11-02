import CoreImage
import Foundation

/// CapCut's "Remove background" mask frames for one clip (see FORMAT.md › Remove background).
///
/// Each file in the `mask` folder is one raw 8-bit frame (0 = removed, 255 = kept), upright, at a low resolution
/// (e.g. 448×256 for 16:9 footage), named by its source time in µs. The size is in `../maskinfo/<time>`.
final class CutoutMask {
    let cutout: Cutout
    /// Frame times, ascending.
    let times: [Int64]
    /// From `maskinfo`, or inferred from the first frame read.
    private var size: (width: Int, height: Int)?
    private var current: (time: Int64, image: CIImage)?

    init?(_ cutout: Cutout) {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: cutout.directory.path)) ?? []
        let times = names.compactMap { Int64($0) }.sorted()
        guard !times.isEmpty else { return nil }
        self.cutout = cutout
        self.times = times
        size = Self.storedSize(info: cutout.directory.deletingLastPathComponent().appendingPathComponent("maskinfo"))
    }

    /// The mask at source time `t`, scaled to `extent` (the frame it cuts out), as a red-channel mask.
    func image(at t: Int64, extent: CGRect) -> CIImage? {
        let time = Self.nearest(times, to: t)
        if current?.time != time {
            guard let mask = load(time, aspect: extent.width / max(extent.height, 1)) else { return nil }
            current = (time, mask)
        }
        guard var mask = current?.image, mask.extent.width > 0, mask.extent.height > 0 else { return nil }
        if cutout.inverted { mask = mask.applyingFilter("CIColorInvert") }
        return mask.clampedToExtent()
            .transformed(by: CGAffineTransform(scaleX: extent.width / mask.extent.width,
                                               y: extent.height / mask.extent.height)
                .concatenating(CGAffineTransform(translationX: extent.minX, y: extent.minY)))
            .cropped(to: extent)
    }

    private func load(_ time: Int64, aspect: CGFloat) -> CIImage? {
        guard let data = try? Data(contentsOf: cutout.directory.appendingPathComponent(String(time))) else { return nil }
        if size.map({ $0.width * $0.height }) != data.count { size = Self.inferSize(bytes: data.count, aspect: aspect) }
        guard let size else { return nil }
        return CIImage(bitmapData: data, bytesPerRow: size.width, size: CGSize(width: size.width, height: size.height),
                       format: .L8, colorSpace: nil)
    }

    /// The time in `times` (ascending, non-empty) closest to `t`.
    static func nearest(_ times: [Int64], to t: Int64) -> Int64 {
        var lo = 0, hi = times.count
        while lo < hi {
            let mid = (lo + hi) / 2
            if times[mid] < t { lo = mid + 1 } else { hi = mid }
        }
        if lo == times.count { return times[lo - 1] }
        if lo > 0, t - times[lo - 1] <= times[lo] - t { return times[lo - 1] }
        return times[lo]
    }

    /// `boundingBox.width/height` of the first file in a `maskinfo` folder.
    static func storedSize(info: URL) -> (width: Int, height: Int)? {
        let names = ((try? FileManager.default.contentsOfDirectory(atPath: info.path)) ?? []).sorted()
        guard let name = names.first,
              let data = try? Data(contentsOf: info.appendingPathComponent(name)),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let box = json["boundingBox"] as? [String: Any],
              let w = (box["width"] as? NSNumber)?.intValue, let h = (box["height"] as? NSNumber)?.intValue,
              w > 0, h > 0 else { return nil }
        return (w, h)
    }

    /// Width and height that multiply to `bytes` with the ratio closest to `aspect`, for a frame without `maskinfo`.
    static func inferSize(bytes: Int, aspect: CGFloat) -> (width: Int, height: Int)? {
        guard bytes > 0, aspect > 0 else { return nil }
        var best: (width: Int, height: Int)?
        var bestError = CGFloat.infinity
        for h in 1...bytes where bytes % h == 0 {
            let w = bytes / h
            let error = abs(log(CGFloat(w) / CGFloat(h) / aspect))
            if error < bestError { (best, bestError) = ((w, h), error) }
        }
        return bestError < 0.1 ? best : nil
    }
}
