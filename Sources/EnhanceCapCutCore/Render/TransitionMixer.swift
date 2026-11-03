import CoreImage

/// Mixes the two clips of a transition. Both come in as "plates": the clip placed on the canvas (with the canvas
/// background on the main track), extent = the frame.
enum TransitionMixer {
    static func mix(_ kind: LayerTransition.Kind, from a: CIImage, to b: CIImage, progress p: Double,
                    rect: CGRect) -> CIImage {
        switch kind {
        case .crossFade:
            return dissolve(a, b, p).cropped(to: rect)
        case .flashback:
            return flashback(a, b, p, rect: rect)
        }
    }

    private static func dissolve(_ a: CIImage, _ b: CIImage, _ amount: Double) -> CIImage {
        if amount <= 0 { return a }
        if amount >= 1 { return b }
        return a.applyingFilter("CIDissolveTransition", parameters: [
            kCIInputTargetImageKey: b, kCIInputTimeKey: amount,
        ])
    }

    /// See `FlashbackLook`: the dissolved picture averaged over copies zoomed and turned about the centre.
    private static func flashback(_ a: CIImage, _ b: CIImage, _ p: Double, rect: CGRect) -> CIImage {
        let mixed = dissolve(a, b, FlashbackLook.mix(p)).cropped(to: rect)
        guard FlashbackLook.strength(p) > 0.001 else { return mixed }
        // Edge pixels fill what turning uncovers.
        let source = mixed.clampedToExtent()
        var result = CIImage(color: .clear).cropped(to: rect)
        for i in 0..<FlashbackLook.blurCopies {
            let (scale, rotation) = FlashbackLook.copy(i, at: p)
            let transform = CGAffineTransform(translationX: -rect.midX, y: -rect.midY)
                .concatenating(CGAffineTransform(scaleX: scale, y: scale))
                .concatenating(CGAffineTransform(rotationAngle: -rotation * .pi / 180))
                .concatenating(CGAffineTransform(translationX: rect.midX, y: rect.midY))
            let weight = CGFloat(1) / CGFloat(i + 1)
            let copy = source.transformed(by: transform).cropped(to: rect).applyingFilter("CIColorMatrix", parameters: [
                "inputAVector": CIVector(x: 0, y: 0, z: 0, w: weight),
            ])
            // Source-over with weight 1/(i+1) keeps a running average of the copies.
            result = copy.composited(over: result)
        }
        return result.cropped(to: rect)
    }
}
