import AVFoundation
import CoreImage
import ImageIO

/// Decodes one clip's video sequentially. The renderer asks for monotonically increasing source
/// times; frames are read forward and the latest frame at or before the requested time is returned.
final class VideoFrameSource {
    private let reader: AVAssetReader
    private let output: AVAssetReaderTrackOutput
    private let orientation: CGImagePropertyOrientation
    private var current: CIImage?
    private var pending: (pts: Int64, buffer: CVPixelBuffer)?
    private var finished = false

    private init(reader: AVAssetReader, output: AVAssetReaderTrackOutput, orientation: CGImagePropertyOrientation) {
        self.reader = reader
        self.output = output
        self.orientation = orientation
    }

    deinit { reader.cancelReading() }

    /// Opens `url` for reading source times in `from..<to` (µs).
    static func open(url: URL, from: Int64, to: Int64) async throws -> VideoFrameSource {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else {
            throw RenderError.noVideoTrack(url)
        }
        let transform = try await track.load(.preferredTransform)
        let reader = try AVAssetReader(asset: asset)
        let start = max(0, from)
        let end = max(to, start + 1)
        reader.timeRange = CMTimeRange(start: CMTime(value: start, timescale: 1_000_000),
                                       end: CMTime(value: end + 100_000, timescale: 1_000_000))
        let settings: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:],
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ]
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
        output.alwaysCopiesSampleData = false
        guard reader.canAdd(output) else { throw RenderError.cannotRead(url) }
        reader.add(output)
        guard reader.startReading() else { throw RenderError.cannotRead(url) }
        return VideoFrameSource(reader: reader, output: output, orientation: orientation(for: transform))
    }

    /// The frame displayed at source time `t` (µs), already rotated upright.
    func image(at t: Int64) -> CIImage? {
        if pending == nil && !finished { readNext() }
        while let next = pending, next.pts <= t {
            current = makeImage(next.buffer)
            pending = nil
            readNext()
        }
        if current == nil, let next = pending {
            // Requested time precedes the first decoded frame: show the first frame.
            current = makeImage(next.buffer)
        }
        return current
    }

    private func readNext() {
        while !finished {
            guard let sample = output.copyNextSampleBuffer() else {
                finished = true
                return
            }
            guard let buffer = CMSampleBufferGetImageBuffer(sample) else { continue }
            let pts = CMSampleBufferGetPresentationTimeStamp(sample)
            pending = (CMTimeConvertScale(pts, timescale: 1_000_000, method: .roundHalfAwayFromZero).value, buffer)
            return
        }
    }

    private func makeImage(_ buffer: CVPixelBuffer) -> CIImage {
        var image: CIImage
        let transfer = CVBufferCopyAttachment(buffer, kCVImageBufferTransferFunctionKey, nil) as? String
        let isHLG = transfer == (kCVImageBufferTransferFunction_ITU_R_2100_HLG as String)
        let isPQ = transfer == (kCVImageBufferTransferFunction_SMPTE_ST_2084_PQ as String)
        if isHLG || isPQ {
            // HDR footage (iPhone HLG): decode to linear light, tone-map into SDR, then shape it like
            // CapCut's SDR rendition (flatter: lifted shadows, compressed highlights).
            image = CIImage(cvPixelBuffer: buffer)
            let headroom = image.contentHeadroom > 1 ? image.contentHeadroom : (isHLG ? 4.926 : 49.26)
            image = image
                .applyingFilter("CIToneMapHeadroom", parameters: [
                    "inputSourceHeadroom": headroom, "inputTargetHeadroom": 1.0,
                ])
                .applyingFilter("CIColorCurves", parameters: [
                    "inputCurvesData": HDRLook.curveData,
                    "inputCurvesDomain": CIVector(x: 0, y: 1),
                    "inputColorSpace": FrameCompositor.outputColorSpace,
                ])
        } else {
            // SDR footage: keep the code values as they are (only the YCbCr matrix is applied), like CapCut.
            // The renderer's output space is the same, so cuts without effects are pixel-faithful.
            image = CIImage(cvPixelBuffer: buffer, options: [.colorSpace: FrameCompositor.outputColorSpace])
        }
        if orientation != .up { image = image.oriented(orientation) }
        return image.movedToOrigin()
    }

    static func orientation(for t: CGAffineTransform) -> CGImagePropertyOrientation {
        func near(_ a: CGFloat, _ b: CGFloat) -> Bool { abs(a - b) < 0.01 }
        switch (t.a, t.b, t.c, t.d) {
        case let (a, b, c, d) where near(a, 0) && near(b, 1) && near(c, -1) && near(d, 0): return .right
        case let (a, b, c, d) where near(a, 0) && near(b, -1) && near(c, 1) && near(d, 0): return .left
        case let (a, b, c, d) where near(a, -1) && near(b, 0) && near(c, 0) && near(d, -1): return .down
        case let (a, b, c, d) where near(a, -1) && near(b, 0) && near(c, 0) && near(d, 1): return .upMirrored
        case let (a, b, c, d) where near(a, 1) && near(b, 0) && near(c, 0) && near(d, -1): return .downMirrored
        default: return .up
        }
    }
}

/// CapCut's look for HDR clips exported to SDR, measured on the author's exports: luma quantiles of
/// CapCut's output against ours after a plain reference-white tone map (8-bit, sRGB-encoded) form one
/// consistent curve across clips and projects.
enum HDRLook {
    static let points: [(Float, Float)] = [
        (0, 0), (3, 8), (14, 23), (31, 38), (56, 60), (80, 79), (105, 97), (128, 113), (157, 133),
        (184, 153), (205, 175), (215, 189), (230, 207), (249, 240), (255, 248),
    ]

    /// 256-entry RGB float table for CIColorCurves.
    static let curveData: Data = {
        var table: [Float] = []
        for i in 0..<256 {
            let x = Float(i)
            var y = x
            for k in 1..<points.count where x <= points[k].0 {
                let (x0, y0) = points[k - 1], (x1, y1) = points[k]
                y = y0 + (y1 - y0) * (x - x0) / max(x1 - x0, 1)
                break
            }
            let v = y / 255
            table += [v, v, v]
        }
        return table.withUnsafeBufferPointer { Data(buffer: $0) }
    }()
}

/// Still images, decoded once and shared between clips.
final class PhotoCache {
    private var images: [URL: CIImage] = [:]

    func image(_ url: URL, maxDimension: CGFloat) -> CIImage? {
        if let cached = images[url] { return cached }
        guard var image = CIImage(contentsOf: url, options: [.applyOrientationProperty: true]) else { return nil }
        image = image.movedToOrigin()
        // Huge photos are downscaled once so per-frame work stays small.
        let longest = max(image.extent.width, image.extent.height)
        if longest > maxDimension * 1.5 {
            let s = maxDimension * 1.5 / longest
            image = image.applyingFilter("CILanczosScaleTransform", parameters: [kCIInputScaleKey: s, kCIInputAspectRatioKey: 1])
            image = image.movedToOrigin()
            let context = CIContext()
            if let cg = context.createCGImage(image, from: image.extent) { image = CIImage(cgImage: cg) }
        }
        images[url] = image
        return image
    }
}

public enum RenderError: LocalizedError {
    case noVideoTrack(URL)
    case cannotRead(URL)
    case writerFailed(String)
    case cancelled
    case ffmpeg(String)

    public var errorDescription: String? {
        switch self {
        case let .noVideoTrack(url): return "No video track in \(url.lastPathComponent)"
        case let .cannotRead(url): return "Cannot read \(url.lastPathComponent)"
        case let .writerFailed(message): return "Writing the video failed: \(message)"
        case .cancelled: return "Export cancelled"
        case let .ffmpeg(message): return "ffmpeg: \(message)"
        }
    }
}

extension CIImage {
    func movedToOrigin() -> CIImage {
        let e = extent
        guard e.origin != .zero, e.isFinite else { return self }
        return transformed(by: CGAffineTransform(translationX: -e.origin.x, y: -e.origin.y))
    }
}

extension CGRect {
    var isFinite: Bool { !isInfinite && !isNull && width.isFinite && height.isFinite }
}
