using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>A <c>materials.video_effects</c> entry ("Effects → Video effects" in CapCut). Its look lives in an effect
/// package in CapCut's cache; the effects we render are re-implemented in <see cref="VideoEffect"/>.</summary>
public sealed class VideoEffectMaterial
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The effect's id in CapCut's library (<c>effect_id</c> / <c>resource_id</c>).</summary>
    public string ResourceID { get; set; } = "";
    /// <summary>0 = applied to the clip it's attached to (<c>extra_material_refs</c>), 2 = an effect-track segment over
    /// the frame.</summary>
    public int ApplyTargetType { get; set; }
    /// <summary>The sliders (<c>adjust_params</c>): <c>effects_adjust_speed</c>, <c>effects_adjust_size</c>, … by name.</summary>
    public Dictionary<string, double> Params { get; set; } = [];

    public static VideoEffectMaterial? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var m = new VideoEffectMaterial
        {
            Id = c.Str("id") ?? "",
            Name = c.Str("name") ?? "",
            ResourceID = c.Str("effect_id") ?? c.Str("resource_id") ?? "",
            ApplyTargetType = (int)(c.Num("apply_target_type") ?? 0),
        };
        foreach (var (name, value) in c.List("adjust_params", p => p.ValueKind != JsonValueKind.Object ? null
                     : new AdjustParam(p.Str("name") ?? "", p.Num("value") ?? p.Num("default_value"))))
        {
            if (name.Length > 0 && value is { } v) m.Params[name] = v;
        }
        return m;
    }

    sealed record AdjustParam(string Name, double? Value);
}

public enum VideoEffectKind
{
    /// <summary>"Pudding Bounce": the clip opens on a white flash, zoomed in and blurred, and bounces back to rest.</summary>
    PuddingBounce,
    /// <summary>"Hazy Rotation": the clip opens zoomed in, tilted and out of focus, then turns and settles.</summary>
    HazyRotation,
}

/// <summary>A video effect on a clip, re-implemented in our code (port of the Swift <c>VideoEffect</c>): its look is a
/// function of time (<see cref="State"/>) that the compositors apply to the clip's picture inside its own frame (see
/// FORMAT.md, "Video effects").</summary>
/// <param name="Start">Where the effect starts, relative to the clip start (µs).</param>
/// <param name="Speed">Playback rate of the effect: 1 at CapCut's default "Speed" slider.</param>
public sealed record VideoEffect(VideoEffectKind Kind, long Start = 0, double Speed = 1)
{
    /// <summary>CapCut's effect ids of the effects we render.</summary>
    static readonly Dictionary<string, VideoEffectKind> Kinds = new()
    {
        ["7534579654615649553"] = VideoEffectKind.PuddingBounce,
        ["7486402432189091127"] = VideoEffectKind.HazyRotation,
    };

    /// <summary>CapCut's default "Speed" slider value (<c>effects_adjust_speed</c>).</summary>
    const double DefaultSpeedSlider = 1.0 / 3;

    /// <summary>The effect of a material applied to a clip, or null when it isn't one we render.</summary>
    public static VideoEffect? From(VideoEffectMaterial material)
    {
        if (material.ApplyTargetType != 0 || !Kinds.TryGetValue(material.ResourceID, out var kind)) return null;
        var slider = material.Params.GetValueOrDefault("effects_adjust_speed", DefaultSpeedSlider);
        return new VideoEffect(kind, 0, Math.Max(slider, 0.01) / DefaultSpeedSlider);
    }

    /// <summary>The effect's look <paramref name="offset"/> µs after the clip start.</summary>
    public VideoEffectState State(long offset)
    {
        if (offset < Start) return VideoEffectState.Identity;
        var t = (offset - Start) / 1_000_000.0 * Speed;
        return Kind switch
        {
            VideoEffectKind.PuddingBounce => PuddingBounce(t),
            VideoEffectKind.HazyRotation => HazyRotation(t),
            _ => VideoEffectState.Identity,
        };
    }

    // Calibration against the author's CapCut export ("Мокрая прогулка", 148.9 s and 31.5 s; 30 fps frame grabs).

    /// <summary>White mix at the first frame (mean luma 146 → 204 of 255) and its decay time: halves every 2 frames.</summary>
    const double PuddingFlash = 0.53, PuddingFlashDecay = 0.097;
    /// <summary>Zoom at the first frame (≈1.45×), its decay and the bounce period (a slight undershoot at ≈0.3 s).</summary>
    const double PuddingZoom = 0.45, PuddingZoomDecay = 0.09, PuddingBouncePeriod = 0.6;
    /// <summary>Blur at the first frame (fraction of the picture's shorter side) and when it is gone (sharp again at 0.4 s).</summary>
    const double PuddingBlur = 0.008, PuddingBlurEnd = 0.4;
    const double PuddingEnd = 0.8;

    static VideoEffectState PuddingBounce(double t)
    {
        if (t >= PuddingEnd) return VideoEffectState.Identity;
        var blurLeft = Math.Max(0, 1 - t / PuddingBlurEnd);
        return new VideoEffectState(
            1 + PuddingZoom * Math.Exp(-t / PuddingZoomDecay) * Math.Cos(2 * Math.PI * t / PuddingBouncePeriod),
            0,
            PuddingBlur * blurLeft * blurLeft,
            t < 0.35 ? PuddingFlash * Math.Exp(-t / PuddingFlashDecay) : 0);
    }

    /// <summary>The opening hold (zoomed in ≈2×, tilted, heavy blur) and the settle that follows (gone at ≈0.36 s).</summary>
    const double HazyHold = 0.13, HazySettle = 0.23;
    const double HazyZoom = 1.0, HazyTilt = -15.0, HazyBlur = 0.012;

    static VideoEffectState HazyRotation(double t)
    {
        if (t >= HazyHold + HazySettle) return VideoEffectState.Identity;
        var u = Math.Max(0, t - HazyHold) / HazySettle;
        var left = (1 - u) * (1 - u);
        return new VideoEffectState(1 + HazyZoom * left, HazyTilt * left, HazyBlur * left, 0);
    }
}

/// <summary>What a video effect does to a clip's picture at one moment, applied in this order inside the picture's own
/// frame: blur, then zoom and rotation about the centre (edges clamped), then a mix towards white.</summary>
/// <param name="Rotation">Degrees, clockwise on screen.</param>
/// <param name="Blur">Gaussian blur radius as a fraction of the picture's shorter side.</param>
/// <param name="Flash">Mix towards white, 0...1 (alpha kept).</param>
public sealed record VideoEffectState(double Zoom, double Rotation, double Blur, double Flash)
{
    public static readonly VideoEffectState Identity = new(1, 0, 0, 0);

    public bool IsIdentity => Math.Abs(Zoom - 1) < 0.0005 && Math.Abs(Rotation) < 0.01 && Blur < 0.0002 && Flash < 0.002;

    /// <summary>Several effects on one clip: zooms multiply, the rest add up.</summary>
    public static VideoEffectState Combined(IEnumerable<VideoEffectState> states) =>
        states.Aggregate(Identity, (a, b) => new VideoEffectState(a.Zoom * b.Zoom, a.Rotation + b.Rotation,
            a.Blur + b.Blur, Math.Min(a.Flash + b.Flash, 1)));
}
