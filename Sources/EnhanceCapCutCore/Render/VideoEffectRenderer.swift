import CoreImage

extension FrameCompositor {
    /// `image` (a clip's picture) with the clip's video effects in this frame (`LayerFrame.effect`), inside its own
    /// extent.
    func withEffects(_ image: CIImage, effect: VideoEffectState?) -> CIImage {
        guard let effect else { return image }
        return VideoEffectRenderer.apply(effect, to: image)
    }
}

enum VideoEffectRenderer {
    /// Blur, zoom and rotation about the centre (edges clamped), then the mix towards white; same extent as `image`.
    static func apply(_ state: VideoEffectState, to image: CIImage) -> CIImage {
        let e = image.extent
        guard !state.isIdentity, e.width >= 1, e.height >= 1, !e.isInfinite else { return image }
        var out = image.clampedToExtent()
        if state.blur > 0 { out = out.applyingGaussianBlur(sigma: state.blur * Double(min(e.width, e.height))) }
        if abs(state.zoom - 1) > 0.0005 || state.rotation != 0 {
            let transform = CGAffineTransform(translationX: -e.midX, y: -e.midY)
                .concatenating(CGAffineTransform(scaleX: state.zoom, y: state.zoom))
                .concatenating(CGAffineTransform(rotationAngle: -state.rotation * .pi / 180))
                .concatenating(CGAffineTransform(translationX: e.midX, y: e.midY))
            out = out.transformed(by: transform)
        }
        out = out.cropped(to: e)
        if state.flash > 0 {
            // CIColorMatrix works on unpremultiplied colour: rgb·(1−f) + f keeps the alpha.
            let k = CGFloat(1 - state.flash), f = CGFloat(state.flash)
            out = out.applyingFilter("CIColorMatrix", parameters: [
                "inputRVector": CIVector(x: k, y: 0, z: 0, w: 0),
                "inputGVector": CIVector(x: 0, y: k, z: 0, w: 0),
                "inputBVector": CIVector(x: 0, y: 0, z: k, w: 0),
                "inputBiasVector": CIVector(x: f, y: f, z: f, w: 0),
            ])
        }
        return out
    }
}
