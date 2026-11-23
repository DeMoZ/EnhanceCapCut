import CoreImage
import Foundation

/// CapCut's AI "Enhance quality" (`video_algorithm.algorithms[]` type `QualityEnhance`, see FORMAT.md › AI
/// processing), approximated: the frame is upscaled with Lanczos, lightly denoised and sharpened with an unsharp mask.
///
/// Our own approximation, not CapCut's: theirs is a super-resolution model whose result CapCut caches encrypted in
/// the project (`Resources/videoAlg`), so neither can be used. It can't invent the detail the model adds; it brings
/// edges and mid-size detail to the export's sharpness. Shared with the Windows port (`QualityEnhanceMath.cs`).
enum QualityEnhance {
    /// Sources up to this long side are upscaled by `upscale` first, like CapCut's result (360×640 → 720×1280).
    static let upscaleMaxSide: CGFloat = 1280
    static let upscale: CGFloat = 2
    /// Denoise: a Gaussian blur of this sigma (pixels of the upscaled frame).
    static let denoiseSigma: Double = 0.6
    /// Unsharp mask: `out = d + amount · (d − blur(d, sigma))`.
    static let sharpenSigma: Double = 1.5
    static let sharpenAmount: Double = 2

    // Calibrated on TwoHoursGay (360×640 selfie video, the only project using it) against CapCut's 1080×1920 export,
    // frame 0: the mean |luma − blur(luma, 3 px)| is 2.18 in the export, 1.51 for a plain upscale and 2.35 in our
    // render (with the skin retouch). Finer detail (σ 1 px: 0.65 export, 0.59 ours) is partly what the model makes up.

    /// The factor a frame of `width`×`height` is upscaled by.
    static func scale(width: CGFloat, height: CGFloat) -> CGFloat {
        max(width, height) <= upscaleMaxSide ? upscale : 1
    }

    static func apply(_ image: CIImage) -> CIImage {
        var image = image
        let k = scale(width: image.extent.width, height: image.extent.height)
        guard image.extent.width > 0, image.extent.height > 0 else { return image }
        if k != 1 {
            image = image.applyingFilter("CILanczosScaleTransform",
                                         parameters: [kCIInputScaleKey: k, kCIInputAspectRatioKey: 1]).movedToOrigin()
        }
        let extent = image.extent
        // CIUnsharpMask: in + intensity · (in − Gaussian blur of sigma = radius).
        return image.clampedToExtent().applyingGaussianBlur(sigma: denoiseSigma)
            .applyingFilter("CIUnsharpMask", parameters: [
                kCIInputRadiusKey: sharpenSigma, kCIInputIntensityKey: sharpenAmount,
            ]).cropped(to: extent)
    }
}
