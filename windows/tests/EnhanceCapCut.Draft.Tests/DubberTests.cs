using EnhanceCapCut.Draft;
using EnhanceCapCut.Subtitles;

namespace EnhanceCapCut.Draft.Tests;

public class DubberTests
{
    /// <summary>Speaks 0.1 s per character at rate 1, as a tone padded with 0.2 s of silence on both sides.</summary>
    sealed class ToneEngine : IVoiceEngine
    {
        public List<double> Rates { get; } = [];

        public IReadOnlyList<DubVoice> Voices(string languageCode) =>
        [
            new("m-gb", "George", "en-GB", "United Kingdom", VoiceGender.Male),
            new("f-us", "Zira", "en-US", "United States", VoiceGender.Female),
            new("m-us", "David", "en-US", "United States", VoiceGender.Male),
        ];

        public Task<float[]> SpeakAsync(string text, string voiceId, double rate, CancellationToken cancel)
        {
            Rates.Add(rate);
            var pad = Dubber.SampleRate / 5;
            var speech = (int)(text.Length * 0.1 / rate * Dubber.SampleRate);
            var samples = new float[pad + speech + pad];
            for (var i = 0; i < speech; i++) samples[pad + i] = 0.5f * MathF.Sin(i * 0.05f) + (i % 2 == 0 ? 0.1f : -0.1f);
            return Task.FromResult(samples);
        }
    }

    [Fact]
    public async Task PlacesLinesOnTheTimelineAndSpeedsUpLongOnes()
    {
        var engine = new ToneEngine();
        var path = Path.Combine(Path.GetTempPath(), $"ecc-dub-{Guid.NewGuid()}.wav");
        try
        {
            // 1 s of speech in a 2 s slot; 2 s of speech in a 1.6 s slot (sped up); the last line overruns the end.
            List<CaptionCue> cues = [new(0.5, 2, "0123456789"), new(2.5, 4, "01234567890123456789"), new(4.1, 5, "0123456789")];
            var lines = await Dubber.RenderAsync(engine, cues, "f-us", totalDuration: 5, path);

            Assert.Equal(3, lines.Count);
            Assert.Equal(0.5, lines[0].Start, 3);
            Assert.Equal(1.54, lines[0].End, 2);  // 1 s + 2 × 20 ms kept around the speech
            Assert.Equal(2.5, lines[1].Start, 3);
            Assert.True(lines[1].End - lines[1].Start < 1.75, $"line 2 lasts {lines[1].End - lines[1].Start} s");
            Assert.Contains(engine.Rates, r => r > 1);
            Assert.True(lines[2].Start >= lines[1].End);

            var (samples, channels, rate) = AudioInput.ParseWav(File.ReadAllBytes(path));
            Assert.Equal(1, channels);
            Assert.Equal(Dubber.SampleRate, rate);
            Assert.Equal(lines[2].End, (double)samples.Length / rate, 3);
            Assert.Equal(0, samples[Dubber.SampleRate / 4]);  // silence before the first line
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultVoicePrefersGenderThenTheMainRegion()
    {
        var engine = new ToneEngine();
        var english = SubtitleLanguage.Find("en")!;
        Assert.Equal("m-us", Dubber.DefaultVoice(engine, english, VoiceGender.Male)?.Id);
        Assert.Equal("f-us", Dubber.DefaultVoice(engine, english, VoiceGender.Female)?.Id);
        Assert.Equal("f-us", Dubber.DefaultVoice(engine, english)?.Id);
        Assert.Equal("Zira (United States)", engine.Voices("en")[1].Label);
    }
}
