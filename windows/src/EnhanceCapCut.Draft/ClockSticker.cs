namespace EnhanceCapCut.Draft;

/// <summary>
/// CapCut's "Clock Time" stickers, re-drawn natively: a round clock whose two hands spin in a loop (port of the
/// Swift <c>ClockSticker</c>).
///
/// Calibrated on the author's CapCut exports (Pullups3Wide, Pullups5Wide, 1920×1080, 30 fps): the clock is centered
/// on the clip position, its outer diameter is <see cref="Diameter"/> × the canvas's shorter side × the clip scale,
/// and the animation starts over at the segment start every <see cref="Period"/>. Sizes below are fractions of the
/// outer radius.
/// </summary>
public sealed record ClockSticker(ClockSticker.ClockStyle Style)
{
    public enum ClockStyle
    {
        /// <summary>"Clock Time EN White Outline Progress Bar": a white ring and white hands, no face.</summary>
        WhiteOutline,
        /// <summary>"Clock Time EN Blue White Minimalist": a grey ring, a white face and blue hands.</summary>
        BlueWhite,
    }

    /// <summary>The sticker resources this draws, by <c>materials.stickers[].resource_id</c>.</summary>
    public static ClockStyle? StyleFor(string resourceID) => resourceID switch
    {
        "7528282080199904565" => ClockStyle.WhiteOutline,
        "7528280675909209405" => ClockStyle.BlueWhite,
        _ => null,
    };

    bool White => Style == ClockStyle.WhiteOutline;
    /// <summary>Outer diameter at clip scale 1, as a fraction of the canvas's shorter side (102 px at scale 0.214
    /// and 136 px at scale 0.281 on a 1080 px tall canvas).</summary>
    public double Diameter => White ? 0.441 : 0.448;
    /// <summary>Ring (outline) width.</summary>
    public double RingWidth => White ? 0.157 : 0.191;
    /// <summary>Hand lengths from the center to the tip of the rounded end, and the hands' width.</summary>
    public double MinuteHand => White ? 0.686 : 0.674;
    public double HourHand => White ? 0.451 : 0.465;
    public double HandWidth => White ? 0.137 : 0.132;
    /// <summary>sRGB colours, 0…1; <see cref="FaceColor"/> null = transparent.</summary>
    public double[] RingColor => White ? [1, 1, 1] : [0.325, 0.325, 0.325];
    public double[]? FaceColor => White ? null : [0.976, 0.976, 0.976];
    public double[] HandColor => White ? [1, 1, 1] : [0, 0.471, 0.824];

    /// <summary>One loop of the animation: 82 frames at 30 fps.</summary>
    public const long Period = 2_733_333;
    /// <summary>Measured hand angles every 1/15 s of the loop, in degrees clockwise from 12 o'clock, relative to the
    /// rest pose (minute hand at 12, hour hand at 3). The minute hand turns twice, the hour hand once, both slowing
    /// down; the last ~0.13 s of the loop they rest.</summary>
    static readonly double[] MinuteTable =
    [
        0, 37, 73, 107, 141, 188, 217, 245, 270, 303, 321, 336, 347, 354, 370, 389, 408, 425, 452, 469,
        486, 503, 519, 543, 558, 574, 588, 609, 621, 633, 645, 657, 674, 680, 689, 699, 706, 711, 715, 720,
    ];
    static readonly double[] HourTable =
    [
        0, 13, 29, 47, 57, 67, 84, 96, 109, 126, 138, 150, 162, 172, 189, 199, 208, 218, 232, 241,
        249, 258, 267, 277, 285, 292, 300, 309, 315, 320, 325, 330, 337, 342, 346, 349, 355, 356, 357, 360,
    ];
    const double TableStep = 1_000_000.0 / 15;

    /// <summary>The hands' angles (degrees clockwise from 12 o'clock) <paramref name="offset"/> µs after the
    /// segment start.</summary>
    public static (double Minute, double Hour) HandAngles(long offset)
    {
        var t = offset % Period;
        if (t < 0) t += Period;
        var x = t / TableStep;
        double Sample(double[] table)
        {
            var i = (int)x;
            if (i + 1 >= table.Length) return table[^1];
            return table[i] + (table[i + 1] - table[i]) * (x - i);
        }
        return (Sample(MinuteTable) % 360, (90 + Sample(HourTable)) % 360);
    }
}
