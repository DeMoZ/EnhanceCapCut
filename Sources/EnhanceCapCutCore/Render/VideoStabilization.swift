import AVFoundation
import CoreImage

/// "Stabilize" on macOS: reads a clip's frames once (luma only, downscaled) to plan the corrections with `Stabilizer`,
/// then moves each rendered frame onto the smoothed camera path.
enum VideoStabilization {
    /// The plan for source times `from..<to` (µs) of `url` at CapCut's `level`; nil when the video can't be read.
    static func analyze(url: URL, from: Int64, to: Int64, level: Int) async -> StabilizationPlan? {
        let asset = AVURLAsset(url: url)
        guard let track = try? await asset.loadTracks(withMediaType: .video).first,
              let transform = try? await track.load(.preferredTransform),
              let reader = try? AVAssetReader(asset: asset) else { return nil }
        let start = max(0, from)
        reader.timeRange = CMTimeRange(start: CMTime(value: start, timescale: 1_000_000),
                                       end: CMTime(value: max(to, start + 1), timescale: 1_000_000))
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarFullRange,
        ])
        output.alwaysCopiesSampleData = false
        guard reader.canAdd(output) else { return nil }
        reader.add(output)
        guard reader.startReading() else { return nil }
        defer { reader.cancelReading() }

        let turns = quarterTurns(VideoFrameSource.orientation(for: transform))
        var times: [Int64] = []
        var motions: [Motion?] = []
        var previous: GrayImage?
        var aspect = 16.0 / 9
        while let sample = output.copyNextSampleBuffer() {
            guard let buffer = CMSampleBufferGetImageBuffer(sample) else { continue }
            let pts = CMTimeConvertScale(CMSampleBufferGetPresentationTimeStamp(sample), timescale: 1_000_000,
                                         method: .roundHalfAwayFromZero).value
            guard let image = luma(buffer)?.rotated(quarterTurns: turns) else { continue }
            aspect = Double(image.width) / Double(max(image.height, 1))
            motions.append(previous.flatMap { MotionEstimator.estimate(from: $0, to: image) })
            times.append(pts)
            previous = image
        }
        guard !times.isEmpty else { return nil }
        return Stabilizer.plan(times: times, motions: motions, level: level, aspect: aspect)
    }

    /// `image` (a whole upright frame) moved by `correction`, cropped back to its own extent.
    static func apply(_ correction: Motion, to image: CIImage) -> CIImage {
        let extent = image.extent
        guard extent.height > 0 else { return image }
        let h = extent.height
        // Motion is in y-down units of the frame height with clockwise angles; Core Image is y-up.
        let t = CGAffineTransform(translationX: -extent.midX, y: -extent.midY)
            .concatenating(CGAffineTransform(scaleX: exp(correction.logScale), y: exp(correction.logScale)))
            .concatenating(CGAffineTransform(rotationAngle: -correction.angle))
            .concatenating(CGAffineTransform(translationX: extent.midX + correction.dx * h,
                                             y: extent.midY - correction.dy * h))
        return image.clampedToExtent().transformed(by: t).cropped(to: extent)
    }

    /// The clockwise quarter turns that bring stored frames upright.
    static func quarterTurns(_ orientation: CGImagePropertyOrientation) -> Int {
        switch orientation {
        case .right, .rightMirrored: return 1
        case .down, .downMirrored: return 2
        case .left, .leftMirrored: return 3
        default: return 0
        }
    }

    private static func luma(_ buffer: CVPixelBuffer) -> GrayImage? {
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(buffer, .readOnly) }
        guard CVPixelBufferGetPlaneCount(buffer) > 0,
              let base = CVPixelBufferGetBaseAddressOfPlane(buffer, 0) else { return nil }
        let width = CVPixelBufferGetWidthOfPlane(buffer, 0), height = CVPixelBufferGetHeightOfPlane(buffer, 0)
        return GrayImage(downscaling: base.assumingMemoryBound(to: UInt8.self), width: width, height: height,
                         rowBytes: CVPixelBufferGetBytesPerRowOfPlane(buffer, 0),
                         factor: MotionEstimator.factor(width: width, height: height))
    }
}
