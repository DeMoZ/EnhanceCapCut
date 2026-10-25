using EnhanceCapCut.Draft;
using EnhanceCapCut.Subtitles;
using EnhanceCapCut.Windows;

namespace EnhanceCapCut.Windows.Tests;

/// <summary>The voice-over voices (Windows.Media.SpeechSynthesis). Windows only; elsewhere they do nothing.</summary>
public sealed class VoiceTests
{
    [Fact]
    public async Task SpeaksWithAnInstalledEnglishVoice()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var voices = new WindowsVoices();
        var english = SubtitleLanguage.Find("en")!;
        var voice = Dubber.DefaultVoice(voices, english);
        Assert.NotNull(voice);
        Assert.StartsWith("en-", voice.Language, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft", voice.Name);

        var slow = await voices.SpeakAsync("This is how the voice-over will sound.", voice.Id, 1, CancellationToken.None);
        var fast = await voices.SpeakAsync("This is how the voice-over will sound.", voice.Id, 2, CancellationToken.None);
        Assert.InRange(slow.Length / (double)Dubber.SampleRate, 1, 6);
        Assert.True(fast.Length < slow.Length * 0.75, $"rate 2: {fast.Length} samples, rate 1: {slow.Length}");
        Assert.True(slow.Max(MathF.Abs) > 0.05f);
    }

    [Fact]
    public async Task RendersAVoiceOverTrack()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var voices = new WindowsVoices();
        var voice = Dubber.DefaultVoice(voices, SubtitleLanguage.Find("en")!)!;
        var path = Path.Combine(Path.GetTempPath(), $"ecc-voice-{Guid.NewGuid()}.wav");
        try
        {
            List<CaptionCue> cues = [new(0.5, 2.5, "Hello there."), new(3, 4, "This line has to be spoken quite a bit faster.")];
            var lines = await Dubber.RenderAsync(voices, cues, voice.Id, totalDuration: 6, path);
            Assert.Equal(2, lines.Count);
            Assert.Equal(0.5, lines[0].Start, 3);
            Assert.True(lines[1].Start >= 3 && lines[1].Start >= lines[0].End);
            var (samples, channels, rate) = AudioInput.ParseWav(File.ReadAllBytes(path));
            Assert.Equal((1, Dubber.SampleRate), (channels, rate));
            Assert.True(samples.Length >= 6 * Dubber.SampleRate);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
