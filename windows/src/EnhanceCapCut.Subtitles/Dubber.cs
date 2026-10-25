using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Subtitles;

public enum VoiceGender { Female, Male }

/// <summary>A system voice that can speak a subtitle language.</summary>
/// <param name="Id">The engine's voice id.</param>
/// <param name="Language">BCP 47, e.g. "en-US".</param>
/// <param name="RegionName">The language's region in words ("United States"), for the label; null when unknown.</param>
public sealed record DubVoice(string Id, string Name, string Language, string? RegionName, VoiceGender? Gender)
{
    public string Label => RegionName is null ? Name : $"{Name} ({RegionName})";
}

/// <summary>A text-to-speech engine: Windows' own voices (<c>WindowsVoices</c> in EnhanceCapCut.Windows), or a test double.</summary>
public interface IVoiceEngine
{
    /// <summary>Installed voices whose language matches a subtitle language code ("en", "zh-Hans", "pt-BR", …).</summary>
    IReadOnlyList<DubVoice> Voices(string languageCode);

    /// <summary>
    /// Speaks <paramref name="text"/> as mono samples at <see cref="Dubber.SampleRate"/>. <paramref name="rate"/> is
    /// relative to the voice's normal speed (1 = normal).
    /// </summary>
    Task<float[]> SpeakAsync(string text, string voiceId, double rate, CancellationToken cancel);
}

/// <summary>
/// Voice-over: speaks translated cues with an <see cref="IVoiceEngine"/> and renders one audio file per language that
/// lines up with the timeline (file time 0 = timeline start). The counterpart of the macOS version's <c>Dubber</c>.
/// </summary>
public static class Dubber
{
    public const int SampleRate = 48_000;
    /// <summary>Fastest speed-up allowed to make a line fit its slot.</summary>
    const double MaxSpeedUp = 1.35;
    /// <summary>The last line has nothing after it to spill into: a bit more speed-up is allowed there.</summary>
    const double LastLineSpeedUp = 1.6;
    /// <summary>Samples quieter than this at the ends of a spoken line are trimmed (the engine pads lines with silence).</summary>
    const float SilenceThreshold = 0.003f;

    /// <summary>
    /// Best voice for a language, preferring the requested gender when one is installed, then the language's main
    /// region (CapCut's caption region: en → en-US, pt-BR → pt-BR, …).
    /// </summary>
    public static DubVoice? DefaultVoice(IVoiceEngine engine, SubtitleLanguage language, VoiceGender? gender = null)
    {
        var all = engine.Voices(language.Code);
        var matching = gender is { } g ? all.Where(v => v.Gender == g).ToList() : [];
        var list = matching.Count > 0 ? matching : all;
        return list.FirstOrDefault(v => string.Equals(v.Language, language.CapCut, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
    }

    /// <summary>Speaks every cue and writes a 48 kHz mono WAV aligned to the timeline.</summary>
    /// <returns>The speech ranges actually used (seconds), one per cue.</returns>
    public static async Task<List<(double Start, double End)>> RenderAsync(IVoiceEngine engine, IReadOnlyList<CaptionCue> cues,
        string voiceId, double totalDuration, string path, Action<double>? progress = null, CancellationToken cancel = default)
    {
        var output = new List<float>((int)(totalDuration * SampleRate) + SampleRate);
        var ranges = new List<(double Start, double End)>();
        for (var i = 0; i < cues.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var cue = cues[i];
            var text = cue.Text.Replace('\n', ' ');
            var slotEnd = i + 1 < cues.Count ? cues[i + 1].Start : Math.Max(cue.End, totalDuration);
            // Measure the slot from where this line can actually start, so lines catch up after an overrun.
            var actualStart = Math.Max(cue.Start, (double)output.Count / SampleRate);
            var slot = Math.Max(0.3, slotEnd - actualStart);
            var limit = i + 1 < cues.Count ? MaxSpeedUp : LastLineSpeedUp;

            var speech = TrimSilence(await engine.SpeakAsync(text, voiceId, 1, cancel));
            if (Seconds(speech) > slot * 1.02)
            {
                // Too long for its slot: binary-search the slowest rate whose measured length fits (or reaches the cap).
                var target = Math.Max(slot, Seconds(speech) / limit);
                double lo = 1, hi = limit * 1.25;
                var fastest = speech;
                for (var step = 0; step < 5; step++)
                {
                    var mid = (lo + hi) / 2;
                    var attempt = TrimSilence(await engine.SpeakAsync(text, voiceId, mid, cancel));
                    if (Seconds(attempt) > target * 1.02)
                    {
                        lo = mid;
                        if (attempt.Length < fastest.Length) fastest = attempt;
                    }
                    else
                    {
                        hi = mid;
                        fastest = attempt;
                    }
                }
                speech = fastest;
            }

            var startFrame = Math.Max((int)(cue.Start * SampleRate), output.Count);
            while (output.Count < startFrame) output.Add(0);
            output.AddRange(speech);
            ranges.Add(((double)startFrame / SampleRate, (double)output.Count / SampleRate));
            progress?.Invoke((double)(i + 1) / cues.Count);
        }
        var total = (int)(totalDuration * SampleRate);
        while (output.Count < total) output.Add(0);
        AudioInput.WriteWav(path, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output), SampleRate);
        return ranges;
    }

    static double Seconds(float[] samples) => (double)samples.Length / SampleRate;

    /// <summary>Removes leading/trailing near-silence the engine adds around speech, keeping 20 ms so words don't start abruptly.</summary>
    internal static float[] TrimSilence(float[] samples)
    {
        var start = 0;
        while (start < samples.Length && Math.Abs(samples[start]) < SilenceThreshold) start++;
        var end = samples.Length;
        while (end > start && Math.Abs(samples[end - 1]) < SilenceThreshold) end--;
        var pad = SampleRate / 50;
        start = Math.Max(0, start - pad);
        end = Math.Min(samples.Length, end + pad);
        return end > start && (start > 0 || end < samples.Length) ? samples[start..end] : samples;
    }

    /// <summary>A short sentence in the language, for previewing a voice.</summary>
    public static string Sample(SubtitleLanguage language)
    {
        if (language.Code == "zh-Hant") return "這是配音的效果。";
        return Samples.GetValueOrDefault(language.Code[..2]) ?? language.NativeName;
    }

    static readonly Dictionary<string, string> Samples = new()
    {
        ["ar"] = "هكذا سيبدو التعليق الصوتي.", ["cs"] = "Takhle bude znít dabing.", ["de"] = "So klingt die Synchronisation.",
        ["en"] = "This is how the voice-over will sound.", ["es"] = "Así sonará el doblaje.",
        ["fr"] = "Voici comment sonnera le doublage.", ["he"] = "כך יישמע הקריינות.", ["hi"] = "वॉइस-ओवर ऐसा सुनाई देगा।",
        ["id"] = "Beginilah suara sulih suaranya.", ["it"] = "Ecco come suonerà il doppiaggio.",
        ["ja"] = "吹き替えはこのように聞こえます。", ["ko"] = "더빙은 이렇게 들립니다.", ["ms"] = "Beginilah bunyi alih suara.",
        ["nl"] = "Zo klinkt de voice-over.", ["pl"] = "Tak będzie brzmiał lektor.",
        ["pt"] = "É assim que a dublagem vai soar.", ["ru"] = "Так будет звучать озвучка.", ["sv"] = "Så här kommer dubbningen att låta.",
        ["th"] = "นี่คือตัวอย่างเสียงพากย์", ["tr"] = "Seslendirme böyle duyulacak.",
        ["uk"] = "Так звучатиме озвучення.", ["vi"] = "Đây là giọng lồng tiếng.",
        ["zh"] = "这是配音的效果。",
    };
}
