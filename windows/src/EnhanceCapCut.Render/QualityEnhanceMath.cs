namespace EnhanceCapCut.Render;

/// <summary>
/// CapCut's AI "Enhance quality" (<c>video_algorithm.algorithms[]</c> type <c>QualityEnhance</c>; see FORMAT.md › AI
/// processing), approximated (port of the macOS QualityEnhance): the frame is upscaled, lightly denoised and sharpened
/// with an unsharp mask, <c>out = d + amount · (d − blur(d, sigma))</c>. CapCut's own result (a super-resolution model)
/// is cached encrypted in the project and can't be used. Calibrated on TwoHoursGay against CapCut's export (frame 0): the
/// mean |luma − blur(luma, 3 px)| is 2.18 in the export, 1.51 for a plain upscale and 2.35 in our render.
/// </summary>
public static class QualityEnhanceMath
{
    /// <summary>Sources up to this long side are upscaled by <see cref="Upscale"/> first, like CapCut's result
    /// (360×640 → 720×1280).</summary>
    public const int UpscaleMaxSide = 1280;
    public const int Upscale = 2;
    /// <summary>Denoise: a Gaussian blur of this sigma (pixels of the upscaled frame).</summary>
    public const double DenoiseSigma = 0.6;
    public const double SharpenSigma = 1.5;
    public const double SharpenAmount = 2;

    /// <summary>The factor a frame of <paramref name="width"/>×<paramref name="height"/> is upscaled by.</summary>
    public static int Scale(int width, int height) => Math.Max(width, height) <= UpscaleMaxSide ? Upscale : 1;
}
