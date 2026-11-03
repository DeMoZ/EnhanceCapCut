using System.Globalization;

namespace EnhanceCapCut.Draft;

/// <summary>
/// CapCut's text intro / outro / loop animations, re-implemented as opacity, scale, offset and a per-character
/// reveal over the clip's time (port of the macOS <c>TextAnimation.swift</c>; keep the two in step).
///
/// Presets are recognized by their name (CapCut stores it in the language it was applied in, so English and
/// Chinese names and the Russian UI's words are matched). Anything else is reported by name.
///
/// The motion is not calibrated against CapCut's exports yet (the author's projects have no text animations):
/// the distances and curves below are our own choice and are kept as named constants.
/// </summary>
public sealed record TextAnimations(TextAnimation? Intro = null, TextAnimation? Outro = null, TextAnimation? Loop = null)
{
    public static readonly TextAnimations None = new();

    /// <summary>Slide distance in half-canvas units (0.2 = a tenth of the canvas).</summary>
    public const double SlideDistance = 0.2;
    /// <summary>Scale a "zoom out" intro starts from (and a "zoom in" outro ends at).</summary>
    public const double ZoomLargeScale = 1.5;
    /// <summary>Opacity swing of "blink" and scale swing of "pulse" loops.</summary>
    public const double BlinkMinimumAlpha = 0.2;
    public const double PulseScale = 0.1;
    /// <summary>Shortest loop cycle; CapCut's loop <c>duration</c> is the length of one cycle.</summary>
    public const long MinimumLoopCycle = 100_000;

    public bool IsEmpty => Intro is null && Outro is null && Loop is null;

    /// <summary>The animations of a clip, and the names of the ones the renderer doesn't reproduce.</summary>
    public static (TextAnimations Animations, List<string> Unsupported) Make(AnimationMaterial? material)
    {
        var result = None;
        var unsupported = new List<string>();
        foreach (var item in material?.Animations ?? [])
        {
            var kind = item.Type switch
            {
                "in" => TextAnimationKind.Intro, "out" => TextAnimationKind.Outro, "loop" => TextAnimationKind.Loop,
                _ => (TextAnimationKind?)null,
            };
            if (kind is not { } k || TextAnimationEffect.Recognize(item.Name, k) is not { } effect)
            {
                unsupported.Add(item.Name.Length == 0 ? item.Type : item.Name);
                continue;
            }
            var animation = new TextAnimation(k, effect, Math.Max(item.Duration, 0));
            result = k switch
            {
                TextAnimationKind.Intro => result with { Intro = animation },
                TextAnimationKind.Outro => result with { Outro = animation },
                _ => result with { Loop = animation },
            };
        }
        return (result, unsupported);
    }

    /// <summary>The largest scale the animations reach (text is rasterized at it so it stays sharp).</summary>
    public double MaxScale => new[] { Intro, Outro, Loop }.Where(a => a is not null).Select(a => a!.Effect switch
    {
        TextAnimationEffect.Zoom z => Math.Max(z.Scale, 1),
        TextAnimationEffect.Pop => 1.1,
        TextAnimationEffect.Pulse => 1 + PulseScale,
        _ => 1.0,
    }).DefaultIfEmpty(1).Max();

    /// <summary>The animation state at <paramref name="offset"/> µs into a clip of <paramref name="duration"/> µs.</summary>
    public TextAnimationState State(long offset, long duration)
    {
        if (duration <= 0) return TextAnimationState.Rest;
        long din = Intro?.Duration ?? 0, dout = Outro?.Duration ?? 0;
        // CapCut squeezes an intro and outro that don't fit into the clip.
        if (din + dout > duration)
        {
            var k = (double)duration / (din + dout);
            din = (long)(din * k);
            dout = duration - din;
        }
        if (Intro is { } intro && din > 0 && offset < din)
            return intro.Effect.State(TextAnimationKind.Intro, (double)offset / din);
        if (Outro is { } outro && dout > 0 && offset >= duration - dout)
            return outro.Effect.State(TextAnimationKind.Outro, (double)(offset - (duration - dout)) / dout);
        if (Loop is { } loop)
        {
            var cycle = Math.Max(loop.Duration, MinimumLoopCycle);
            var phase = (double)((offset - din) % cycle) / cycle;
            return loop.Effect.State(TextAnimationKind.Loop, phase);
        }
        return TextAnimationState.Rest;
    }
}

public enum TextAnimationKind { Intro, Outro, Loop }

/// <param name="Duration">µs; for a loop, one cycle.</param>
public sealed record TextAnimation(TextAnimationKind Kind, TextAnimationEffect Effect, long Duration);

public abstract record TextAnimationEffect
{
    public sealed record Fade : TextAnimationEffect;
    /// <summary>Scale from (intro) or to (outro) <paramref name="Scale"/>, fading when <paramref name="Fades"/>.</summary>
    public sealed record Zoom(double Scale, bool Fades) : TextAnimationEffect;
    /// <summary>Grows from nothing with a small overshoot.</summary>
    public sealed record Pop : TextAnimationEffect;
    /// <summary>Moves in the direction (dx, dy) (+y up): an intro arrives from the opposite side, an outro leaves.</summary>
    public sealed record Slide(double Dx, double Dy) : TextAnimationEffect;
    /// <summary>Characters appear one by one (outro: disappear from the end).</summary>
    public sealed record Typewriter : TextAnimationEffect;
    public sealed record Blink : TextAnimationEffect;
    public sealed record Pulse : TextAnimationEffect;

    /// <summary>The preset for a CapCut animation name, or null when it isn't reproduced.</summary>
    public static TextAnimationEffect? Recognize(string name, TextAnimationKind kind)
    {
        var n = name.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(w => n.Contains(w, StringComparison.Ordinal));
        if (kind == TextAnimationKind.Loop)
        {
            if (Has("blink", "flicker", "flash", "闪烁", "мерца", "мига")) return new Blink();
            if (Has("pulse", "heartbeat", "breath", "心跳", "呼吸", "пульс", "сердцеби", "дыхан")) return new Pulse();
            return null;
        }
        if (Has("typewriter", "typing", "打字机", "печатн", "машинк")) return new Typewriter();
        if (Has("fade", "dissolve", "渐显", "渐隐", "растворен", "проявлен", "затухан", "угасан")) return new Fade();
        if (Has("pop", "弹入", "弹出", "всплыт")) return new Pop();
        if (Has("slide", "move", "滑动", "移动", "сдвиг", "скольж", "слайд"))
        {
            const double d = TextAnimations.SlideDistance;
            if (Has("left", "左", "влево", "налево")) return new Slide(-d, 0);
            if (Has("right", "右", "вправо", "направо")) return new Slide(d, 0);
            if (Has("up", "上", "вверх")) return new Slide(0, d);
            if (Has("down", "下", "вниз")) return new Slide(0, -d);
            return null;
        }
        const double large = TextAnimations.ZoomLargeScale;
        // Grows: an intro from nothing, an outro beyond its size while fading.
        if (Has("zoom in", "放大", "увеличен"))
            return kind == TextAnimationKind.Intro ? new Zoom(0, false) : new Zoom(large, true);
        // Shrinks: an intro from larger while appearing, an outro to nothing.
        if (Has("zoom out", "缩小", "уменьшен"))
            return kind == TextAnimationKind.Intro ? new Zoom(large, true) : new Zoom(0, false);
        return null;
    }

    /// <summary>The state at <paramref name="progress"/> (0…1) of an intro or outro, or at the phase of a loop cycle.</summary>
    public TextAnimationState State(TextAnimationKind kind, double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        if (kind == TextAnimationKind.Loop)
        {
            var wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * p); // 0 → 1 → 0 over a cycle
            return this switch
            {
                Blink => TextAnimationState.Rest with { Alpha = 1 - (1 - TextAnimations.BlinkMinimumAlpha) * wave },
                Pulse => TextAnimationState.Rest with { Scale = 1 + TextAnimations.PulseScale * wave },
                _ => TextAnimationState.Rest,
            };
        }
        var intro = kind == TextAnimationKind.Intro;
        // How far from the resting state: 1 at the start of an intro and at the end of an outro.
        var away = this switch
        {
            Fade or Typewriter => intro ? 1 - p : p,
            Pop => intro ? 1 - BackOut(p) : p * p * p,
            _ => intro ? Math.Pow(1 - p, 3) : p * p * p,
        };
        var s = TextAnimationState.Rest;
        switch (this)
        {
            case Fade: s = s with { Alpha = 1 - away }; break;
            case Zoom z:
                s = s with { Scale = 1 + (z.Scale - 1) * away };
                if (z.Fades) s = s with { Alpha = 1 - away };
                break;
            case Pop: s = s with { Scale = 1 - away }; break;
            case Slide slide:
                var sign = intro ? -1.0 : 1.0;
                s = s with { OffsetX = sign * slide.Dx * away, OffsetY = sign * slide.Dy * away, Alpha = 1 - away };
                break;
            case Typewriter: s = s with { Reveal = 1 - away }; break;
        }
        return s;
    }

    /// <summary>Ease-out with a small overshoot (≈ +10 %).</summary>
    static double BackOut(double p)
    {
        const double c = 1.70158;
        var q = p - 1;
        return 1 + (c + 1) * q * q * q + c * q * q;
    }
}

/// <summary>What the animations do to a text clip at one moment, on top of its own transform.</summary>
/// <param name="OffsetX">Half-canvas units.</param>
/// <param name="OffsetY">Half-canvas units, +y up.</param>
/// <param name="Reveal">The fraction of characters shown (typewriter).</param>
public sealed record TextAnimationState(double Alpha, double Scale, double OffsetX, double OffsetY, double Reveal)
{
    public static readonly TextAnimationState Rest = new(1, 1, 0, 0, 1);

    /// <summary>
    /// UTF-16 length of the part of <paramref name="text"/> shown at <paramref name="reveal"/>: whole characters
    /// (grapheme clusters, spaces and line breaks included), rounded down.
    /// </summary>
    public static int VisibleLength(string text, double reveal)
    {
        if (reveal >= 1) return text.Length;
        var starts = new List<int>();
        for (var i = 0; i < text.Length; i += StringInfo.GetNextTextElementLength(text, i)) starts.Add(i);
        var shown = (int)Math.Floor(Math.Max(reveal, 0) * starts.Count);
        return shown >= starts.Count ? text.Length : starts[shown];
    }
}
