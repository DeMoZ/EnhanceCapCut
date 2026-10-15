import AVFoundation
import CoreImage

/// Builds the composited frame of a timeline at increasing times.
///
/// Layout follows CapCut: each visual is first fitted inside the canvas (aspect fit), then scaled by
/// the clip scale, rotated clockwise and moved by the clip position (half-canvas units, +y up).
final class FrameCompositor {
    /// The colour space frames are composed for and encoded from. Core Image reads BT.709-tagged video
    /// with an sRGB-like display curve, so the sRGB curve (BT.709 primaries) round-trips SDR footage
    /// exactly; `CGColorSpace.itur_709` (the camera OETF) would lift every frame. Files are tagged BT.709.
    static let outputColorSpace = CGColorSpace(name: CGColorSpace.sRGB)!

    let timeline: Timeline
    /// Output (canvas) size in pixels.
    let size: CGSize
    private let photos: PhotoCache
    private var sources: [String: VideoFrameSource] = [:]
    private var failed: Set<String> = []
    private var nested: [String: FrameCompositor] = [:]
    private var textImages: [String: (image: CIImage, scale: CGFloat)] = [:]
    /// Messages about clips that could not be decoded (reported once each).
    private(set) var warnings: [String] = []

    init(timeline: Timeline, size: CGSize, photos: PhotoCache = PhotoCache()) {
        self.timeline = timeline
        self.size = size
        self.photos = photos
    }

    var rect: CGRect { CGRect(origin: .zero, size: size) }

    /// The composited frame at timeline time `t` (µs). Opaque, extent = `rect`.
    func frame(at t: Int64) async -> CIImage {
        let active = timeline.layers.filter { $0.isActive(at: t) }
        releaseInactive(active: Set(active.map(\.id)))

        var result = CIImage(color: .black).cropped(to: rect)
        var mainImage: (VisualLayer, CIImage)?
        var layerImages: [(VisualLayer, CIImage)] = []
        for layer in active {
            guard let image = await content(of: layer, at: t) else { continue }
            if layer.isMainTrack && mainImage == nil { mainImage = (layer, image) }
            layerImages.append((layer, image))
        }

        if let (layer, image) = mainImage, let canvas = layer.canvas {
            result = background(canvas, source: image).composited(over: result)
        }
        for (layer, image) in layerImages {
            result = place(image, layer: layer, at: t).composited(over: result)
        }
        return result.cropped(to: rect)
    }

    // MARK: Content

    private func content(of layer: VisualLayer, at t: Int64) async -> CIImage? {
        let offset = t - layer.target.start
        switch layer.content {
        case let .video(url):
            guard let source = await source(for: layer, url: url) else { return nil }
            return cropped(source.image(at: layer.timeMap.sourceTime(atOffset: offset)), layer.crop)

        case let .photo(url):
            return cropped(photos.image(url, maxDimension: max(size.width, size.height)), layer.crop)

        case let .compound(inner):
            let compositor: FrameCompositor
            if let existing = nested[layer.id] {
                compositor = existing
            } else {
                // Render the nested canvas at the same pixel density as this one.
                let k = size.width / max(timeline.canvasSize.width, 1)
                let innerSize = CGSize(width: (inner.canvasSize.width * k).rounded(),
                                       height: (inner.canvasSize.height * k).rounded())
                compositor = FrameCompositor(timeline: inner, size: innerSize, photos: photos)
                nested[layer.id] = compositor
            }
            return await compositor.frame(at: layer.timeMap.sourceTime(atOffset: offset))

        case let .text(spec):
            return textImage(spec, layer: layer).image
        }
    }

    private func source(for layer: VisualLayer, url: URL) async -> VideoFrameSource? {
        if let existing = sources[layer.id] { return existing }
        guard !failed.contains(layer.id) else { return nil }
        let map = layer.timeMap
        do {
            let source = try await VideoFrameSource.open(url: url, from: map.sourceStart,
                                                         to: map.sourceStart + map.sourceDuration)
            sources[layer.id] = source
            return source
        } catch {
            failed.insert(layer.id)
            warnings.append("\(url.lastPathComponent): \(error.localizedDescription)")
            return nil
        }
    }

    private func releaseInactive(active: Set<String>) {
        for id in sources.keys where !active.contains(id) { sources[id] = nil }
        for id in nested.keys where !active.contains(id) { nested[id] = nil }
        for id in textImages.keys where !active.contains(id) { textImages[id] = nil }
    }

    private func cropped(_ image: CIImage?, _ crop: Crop) -> CIImage? {
        guard let image, !crop.isIdentity else { return image }
        let w = image.extent.width, h = image.extent.height
        let r = CGRect(x: crop.upperLeft.x * w, y: (1 - crop.lowerRight.y) * h,
                       width: (crop.lowerRight.x - crop.upperLeft.x) * w,
                       height: (crop.lowerRight.y - crop.upperLeft.y) * h)
        guard r.width >= 1, r.height >= 1 else { return image }
        return image.cropped(to: r).movedToOrigin()
    }

    /// Text is rasterized once per clip, at the largest scale it reaches, so it stays sharp.
    private func textImage(_ spec: TextLayerSpec, layer: VisualLayer) -> (image: CIImage?, scale: CGFloat) {
        if let cached = textImages[layer.id] { return (cached.image, cached.scale) }
        var maxScale = max(abs(layer.clip.scale.x), abs(layer.clip.scale.y))
        for p in [LayerProperty.scaleX, .scaleY] {
            if let values = layer.keyframes[p]?.values { maxScale = max(maxScale, values.map(abs).max() ?? 0) }
        }
        let scale = CGFloat(min(max(maxScale, 1), 8))
        guard let image = TextRasterizer.render(spec, canvasWidth: size.width, scale: scale) else { return (nil, 1) }
        textImages[layer.id] = (image, scale)
        return (image, scale)
    }

    // MARK: Layout

    private func place(_ image: CIImage, layer: VisualLayer, at t: Int64) -> CIImage {
        let tr = layer.transform(at: t)
        let w = image.extent.width, h = image.extent.height
        guard w > 0, h > 0 else { return CIImage.empty() }

        var base: CGFloat
        if case .text = layer.content {
            base = 1 / (textImages[layer.id]?.scale ?? 1)
        } else {
            base = min(size.width / w, size.height / h)
        }
        var sx = base * CGFloat(tr.scaleX) * (tr.flipH ? -1 : 1)
        var sy = base * CGFloat(tr.scaleY) * (tr.flipV ? -1 : 1)
        var source = image

        // Large downscales alias with plain bilinear sampling; pre-shrink with Lanczos.
        let shrink = min(abs(sx), abs(sy))
        if shrink < 0.7, shrink > 0.01 {
            source = image.applyingFilter("CILanczosScaleTransform",
                                          parameters: [kCIInputScaleKey: shrink, kCIInputAspectRatioKey: 1]).movedToOrigin()
            sx /= shrink
            sy /= shrink
        }
        let sw = source.extent.width, sh = source.extent.height
        let transform = CGAffineTransform(translationX: -sw / 2, y: -sh / 2)
            .concatenating(CGAffineTransform(scaleX: sx, y: sy))
            .concatenating(CGAffineTransform(rotationAngle: -CGFloat(tr.rotation) * .pi / 180))
            .concatenating(CGAffineTransform(translationX: size.width / 2 * (1 + CGFloat(tr.positionX)),
                                             y: size.height / 2 * (1 + CGFloat(tr.positionY))))
        var placed = source.transformed(by: transform)
        if tr.alpha < 0.999 {
            placed = placed.applyingFilter("CIColorMatrix", parameters: [
                "inputAVector": CIVector(x: 0, y: 0, z: 0, w: CGFloat(max(tr.alpha, 0))),
            ])
        }
        return placed
    }

    private func background(_ canvas: CanvasMaterial, source: CIImage) -> CIImage {
        switch canvas.type {
        case "canvas_blur":
            let w = source.extent.width, h = source.extent.height
            guard w > 0, h > 0 else { return CIImage.empty() }
            let fill = max(size.width / w, size.height / h)
            let scaled = source.transformed(by: CGAffineTransform(scaleX: fill, y: fill)).movedToOrigin()
            let dx = (scaled.extent.width - size.width) / 2, dy = (scaled.extent.height - size.height) / 2
            let centered = scaled.transformed(by: CGAffineTransform(translationX: -dx, y: -dy))
            let radius = max(CGFloat(canvas.blur), 0.05) * min(size.width, size.height) * 0.08
            return centered.clampedToExtent()
                .applyingGaussianBlur(sigma: radius)
                .cropped(to: rect)
        case "canvas_image":
            guard let image = photos.image(URL(fileURLWithPath: canvas.image), maxDimension: max(size.width, size.height)) else {
                return CIImage.empty()
            }
            let fill = max(size.width / image.extent.width, size.height / image.extent.height)
            let scaled = image.transformed(by: CGAffineTransform(scaleX: fill, y: fill))
            let dx = (scaled.extent.width - size.width) / 2, dy = (scaled.extent.height - size.height) / 2
            return scaled.transformed(by: CGAffineTransform(translationX: -dx, y: -dy)).cropped(to: rect)
        default:
            guard !canvas.color.isEmpty else { return CIImage.empty() }
            return CIImage(color: CIColor(cgColor: TextRasterizer.hexColor(canvas.color))).cropped(to: rect)
        }
    }
}
