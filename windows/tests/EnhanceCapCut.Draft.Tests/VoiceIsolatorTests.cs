using EnhanceCapCut.Subtitles;

namespace EnhanceCapCut.Draft.Tests;

public class VoiceIsolatorTests
{
    [Fact]
    public void ErbBandsCoverTheSpectrum()
    {
        var widths = VoiceIsolator.ErbWidths(48_000, 960, 32, 2);
        Assert.Equal(32, widths.Length);
        Assert.Equal(481, widths.Sum());
        Assert.All(widths, w => Assert.True(w >= 2));
        // Narrow bands at the bottom, wide at the top.
        Assert.Equal(2, widths[0]);
        Assert.True(widths[^1] > 20);
    }

    /// <summary>
    /// The model on speech (the macOS system voice) in noise, skipped until the model is downloaded and where there's
    /// no <c>say</c>: the noise goes down and the voice stays in place (no delay).
    /// </summary>
    [Fact]
    public void ReducesNoiseWithoutDelay()
    {
        if (!File.Exists("/usr/bin/say") || VoiceIsolator.Load() is not { } isolator) return;
        using var _ = isolator;
        var wav = Path.Combine(Path.GetTempPath(), $"ecc-say-{Guid.NewGuid()}.wav");
        using (var say = System.Diagnostics.Process.Start("/usr/bin/say",
            ["-o", wav, "--data-format=LEI16@48000", "The quick brown fox jumps over the lazy dog."]))
        {
            say.WaitForExit();
            if (say.ExitCode != 0 || !File.Exists(wav)) return;
        }
        var (samples, channels, rate) = AudioInput.ParseWav(File.ReadAllBytes(wav), wav);
        File.Delete(wav);
        Assert.Equal(48_000, rate);
        var voice = AudioInput.ToMono(samples, channels, rate, rate).Select(v => v * 0.5f).ToArray();
        var random = new Random(1);
        var mix = voice.Select(v => v + (float)(0.03 * (random.NextDouble() * 2 - 1))).ToArray();
        var output = isolator.Isolate(mix);
        Assert.Equal(mix.Length, output.Length);
        double Error(float[] x, int shift = 0)
        {
            double e = 0;
            for (var i = 2_000; i < x.Length - 2_000; i++) e += (x[i + shift] - voice[i]) * (double)(x[i + shift] - voice[i]);
            return e;
        }
        Assert.True(Error(output) < Error(mix) * 0.5, $"error {Error(output)} vs {Error(mix)}");
        // Aligned: a shift of a millisecond makes it worse.
        Assert.True(Error(output) < Error(output, 48) && Error(output) < Error(output, -48));
    }
}
