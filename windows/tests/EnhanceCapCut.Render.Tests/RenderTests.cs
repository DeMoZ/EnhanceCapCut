using System.Numerics;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;

namespace EnhanceCapCut.Render.Tests;

static class Signal
{
    /// <summary>Interleaved stereo sine at 48 kHz.</summary>
    public static float[] Sine(double hz, double seconds, double amplitude = 0.5)
    {
        var frames = (int)(seconds * AudioMixer.SampleRate);
        var s = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            s[i * 2] = s[i * 2 + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / AudioMixer.SampleRate));
        return s;
    }

    /// <summary>Dominant frequency from zero crossings of the left channel.</summary>
    public static double Frequency(float[] stereo, int from, int to)
    {
        var crossings = 0;
        for (var i = from + 1; i < to; i++)
        {
            if (stereo[(i - 1) * 2] < 0 && stereo[i * 2] >= 0) crossings++;
        }
        return crossings * (double)AudioMixer.SampleRate / (to - from);
    }

    public static double Rms(float[] stereo, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++) sum += stereo[i * 2] * stereo[i * 2];
        return Math.Sqrt(sum / (to - from));
    }
}

/// <summary>Serves the same synthetic audio for every file.</summary>
sealed class FakeDecoder(float[] samples) : IAudioDecoder
{
    public DecodedAudio? Decode(string path, long fromUs, long toUs)
    {
        var from = (int)AudioMixer.Frames(fromUs);
        var to = Math.Min((int)AudioMixer.Frames(toUs), samples.Length / 2);
        return from >= to ? null : new DecodedAudio(samples[(from * 2)..(to * 2)], fromUs);
    }
}

public class AudioTests
{
    static Timeline With(params AudioClip[] clips) =>
        new("T", 1080, 1920, clips.Max(c => c.Target.End), 30, [], clips, new RenderReport());

    [Fact]
    public void LimiterKeepsTheSumUnderTheCeiling()
    {
        var loud = Signal.Sine(440, 1, 1.5);
        PeakLimiter.LimitInPlace(loud, AudioMixer.SampleRate, -0.2);
        var ceiling = Math.Pow(10, -0.2 / 20);
        Assert.True(loud.Max(Math.Abs) <= ceiling + 1e-4);
        // Timing is unchanged: the waveform still starts at zero.
        Assert.True(Math.Abs(loud[0]) < 1e-6);
    }

    [Fact]
    public void ClipIsPlacedWithVolumeAndFades()
    {
        var clip = new AudioClip("a", new TimeRange(500_000, 1_000_000), new TimeMap(0, 1_000_000, 1_000_000), 0.5,
            200_000, 0, true);
        var mix = AudioMixer.Mix(With(clip), new FakeDecoder(Signal.Sine(440, 3)), []);
        Assert.Equal(AudioMixer.Frames(1_500_000) * 2, mix.Length);
        Assert.True(Signal.Rms(mix, 0, 24_000) < 1e-6);
        // Fade-in: quieter at the start than after it; full level is 0.5 × 0.5 amplitude.
        Assert.True(Signal.Rms(mix, 24_000, 26_400) < Signal.Rms(mix, 48_000, 60_000) / 4);
        Assert.InRange(Signal.Rms(mix, 48_000, 60_000), 0.25 / Math.Sqrt(2) - 0.01, 0.25 / Math.Sqrt(2) + 0.01);
    }

    [Fact]
    public void VolumeKeyframesShapeTheClip()
    {
        // Full level for 1 s, a 0.1 s ramp down to a quarter, back up at 2 s (like voice-over ducking).
        var clip = new AudioClip("a", new TimeRange(0, 3_000_000), new TimeMap(0, 3_000_000, 3_000_000), 1, 0, 0, true)
        {
            VolumeEnvelope = new KeyframeTrack([900_000, 1_000_000, 2_000_000, 2_100_000], [1, 0.25, 0.25, 1]),
        };
        var mix = AudioMixer.Mix(With(clip), new FakeDecoder(Signal.Sine(440, 3)), []);
        var full = 0.5 / Math.Sqrt(2);
        Assert.InRange(Signal.Rms(mix, 9_600, 38_400), full - 0.01, full + 0.01);
        Assert.InRange(Signal.Rms(mix, 57_600, 86_400), full / 4 - 0.01, full / 4 + 0.01);
        Assert.InRange(Signal.Rms(mix, 105_600, 134_400), full - 0.01, full + 0.01);
    }

    [Fact]
    public void PitchToggleResamplesAndOtherwiseKeepsThePitch()
    {
        // 2 s of source played in 1 s.
        var map = new TimeMap(0, 2_000_000, 1_000_000);
        var tape = new AudioClip("a", new TimeRange(0, 1_000_000), map, 1, 0, 0, PreservePitch: false);
        var kept = tape with { PreservePitch = true };
        var source = Signal.Sine(300, 3);
        var tapeMix = AudioMixer.Mix(With(tape), new FakeDecoder(source), []);
        var keptMix = AudioMixer.Mix(With(kept), new FakeDecoder(source), []);
        Assert.InRange(Signal.Frequency(tapeMix, 4_800, 43_200), 590, 610);
        Assert.InRange(Signal.Frequency(keptMix, 4_800, 43_200), 290, 310);
        // The time-stretch keeps the level.
        Assert.InRange(Signal.Rms(keptMix, 4_800, 43_200), Signal.Rms(tapeMix, 4_800, 43_200) * 0.85,
            Signal.Rms(tapeMix, 4_800, 43_200) * 1.15);
    }

    [Fact]
    public void LoudnessOfAStereoSine()
    {
        // A 1 kHz sine at -20 dBFS in both channels measures about -20 LUFS (K-weighting is ~0 dB at 1 kHz).
        Assert.InRange(Loudness.Integrated(Signal.Sine(1000, 3, 0.1)), -20.4, -19.6);
        var quiet = Signal.Sine(1000, 3, 0.01);
        Loudness.Normalize(quiet, -14);
        Assert.InRange(Loudness.Integrated(quiet), -14.5, -13.5);
        Assert.True(double.IsNegativeInfinity(Loudness.Integrated(new float[96_000 * 2])));
    }

    [Fact]
    public void TrimmedAudioStartsAtTheRange()
    {
        var clip = new AudioClip("a", new TimeRange(1_000_000, 2_000_000), new TimeMap(0, 2_000_000, 2_000_000), 1,
            100_000, 100_000, true);
        var trimmed = AudioMixer.TrimmedForAudio(With(clip), new TimeRange(2_000_000, 5_000_000));
        var c = Assert.Single(trimmed.AudioClips);
        Assert.Equal(new TimeRange(0, 1_000_000), c.Target);
        Assert.Equal(1_000_000, c.TimeMap.SourceStart);
        Assert.Equal(0, c.FadeIn);
        Assert.Equal(100_000, c.FadeOut);
    }
}

public class LayoutTests
{
    static LayerTransform Identity => new(1, 1, 0, 0, 0, false, false, 1);

    [Fact]
    public void FitsAndCentersByDefault()
    {
        // A landscape 1920×1080 frame on a 1080×1920 canvas: fitted to the width, centered.
        var m = Layout.Placement(Identity, 1920, 1080, 1080, 1920);
        var topLeft = Vector2.Transform(new(0, 0), m);
        var bottomRight = Vector2.Transform(new(1920, 1080), m);
        Assert.Equal(0, topLeft.X, 3);
        Assert.Equal(1080, bottomRight.X, 3);
        Assert.Equal(960 - 303.75, topLeft.Y, 3);
        Assert.Equal(960 + 303.75, bottomRight.Y, 3);
    }

    [Fact]
    public void PositionIsHalfCanvasUnitsWithYUpAndRotationIsClockwise()
    {
        var moved = Identity with { PositionX = 0.5, PositionY = 0.5, Rotation = 90 };
        var m = Layout.Placement(moved, 100, 100, 1000, 1000);
        var center = Vector2.Transform(new(50, 50), m);
        Assert.Equal(750, center.X, 3);
        Assert.Equal(250, center.Y, 3);
        // Clockwise: the image's right edge midpoint ends up below the center.
        var right = Vector2.Transform(new(100, 50), m);
        Assert.Equal(750, right.X, 3);
        Assert.True(right.Y > center.Y);
    }

    [Fact]
    public void TextIsPlacedAtTheInverseOfItsRasterScale()
    {
        var m = Layout.Placement(Identity with { ScaleX = 2, ScaleY = 2 }, 400, 100, 1080, 1920, textScale: 2);
        Assert.Equal(400, Vector2.Transform(new(400, 0), m).X - Vector2.Transform(new(0, 0), m).X, 3);
    }

    [Fact]
    public void CropAndCover()
    {
        var crop = new Crop { UpperLeft = new(0.1, 0.2), LowerRight = new(0.9, 1) };
        Assert.Equal((10f, 20f, 90f, 100f), Layout.CropRect(crop, 100, 100));
        Assert.Null(Layout.CropRect(new Crop(), 100, 100));
        var cover = Layout.Cover(1920, 1080, 1080, 1920);
        Assert.Equal(1920, Vector2.Transform(new(0, 1080), cover).Y, 3);
    }

    [Fact]
    public void ColorsAndClock()
    {
        Assert.Equal(new Rgba(1, 0, 0, 0.5f), Rgba.FromHex("#FF0000", 0.5));
        Assert.Equal(128 / 255f, Rgba.FromHex("#80FF0000").A, 4);
        var ntsc = FrameClock.For(29.97);
        Assert.Equal(1001 * 10_000_000L / 30_000, ntsc.Ticks(1));
        Assert.Equal(30, FrameClock.For(30).FrameCount(1_000_000));
        Assert.Equal(2, TextCalibration.Unit(null, "", 344, 1));
    }
}
