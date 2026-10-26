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

    // MARK: Cloned voice

    /// <summary>Voice id for "the original speaker's voice, cloned" (<see cref="VoiceCloner"/>), in place of a system voice id.</summary>
    public const string ClonedVoiceId = "clone";
    /// <summary>
    /// A cloned line is read back with Whisper and made again (another seed) when its character error rate against the
    /// text is above this: about one line in ten comes out garbled or cut short.
    /// </summary>
    const double MaxLineError = 0.35;
    const int CloneAttempts = 3;
    /// <summary>The reference for a line starts at the line and is at least this long (the model uses its first 6 s).</summary>
    const double ReferenceSeconds = 4;
    const double MaxReferenceSeconds = 6;

    /// <summary>
    /// Speaks every cue in the voice of the original speaker and writes a 48 kHz mono WAV aligned to the timeline. Each
    /// line's reference is the original speech at that line (<paramref name="voice"/>: the timeline's voice, 24 kHz mono),
    /// so its tone and emotion carry over. With <paramref name="checker"/>, garbled lines are made again.
    /// </summary>
    /// <returns>The speech ranges actually used (seconds), one per cue.</returns>
    public static async Task<List<(double Start, double End)>> RenderClonedAsync(VoiceCloner cloner, WhisperChecker? checker,
        IReadOnlyList<CaptionCue> cues, string language, float[] voice, float exaggeration, double totalDuration, string path,
        Action<double>? progress = null, CancellationToken cancel = default)
    {
        var code = VoiceCloner.ModelLanguage(language) ?? throw new ArgumentException($"Voice cloning doesn't speak {language}");
        var output = new List<float>((int)(totalDuration * SampleRate) + SampleRate);
        var ranges = new List<(double Start, double End)>();
        for (var i = 0; i < cues.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var cue = cues[i];
            var text = cue.Text.Replace('\n', ' ');
            var slotEnd = i + 1 < cues.Count ? cues[i + 1].Start : Math.Max(cue.End, totalDuration);
            var actualStart = Math.Max(cue.Start, (double)output.Count / SampleRate);
            var slot = Math.Max(0.3, slotEnd - actualStart);
            var limit = i + 1 < cues.Count ? MaxSpeedUp : LastLineSpeedUp;

            var reference = cloner.VoiceFrom(ReferenceRange(cue, voice));
            VoiceCloner.Speech? best = null;
            var bestError = double.MaxValue;
            for (var attempt = 0; attempt < CloneAttempts; attempt++)
            {
                var speech = cloner.Speak(text, code, reference, exaggeration, (ulong)i * 1000 + (ulong)attempt + 1, cancel);
                var error = speech.Finished ? 0 : 1.0;
                if (checker is not null && speech.Samples.Length > 0)
                {
                    var heard = await checker.TextAsync(AudioInput.Resample(speech.Samples, VoiceCloner.SampleRate, AudioInput.SampleRate), code, cancel);
                    error = Math.Max(error, LineError(text, heard));
                }
                if (best is null || error < bestError || (error == bestError && speech.Duration < best.Duration))
                {
                    best = speech;
                    bestError = error;
                }
                if (error <= MaxLineError) break;
            }
            var samples = best?.Samples ?? [];
            var duration = (double)samples.Length / VoiceCloner.SampleRate;
            if (duration > slot * 1.02) samples = TimeStretch.Stretch(samples, Math.Min(limit, duration / slot), VoiceCloner.SampleRate);
            samples = AudioInput.Resample(samples, VoiceCloner.SampleRate, SampleRate);

            var startFrame = Math.Max((int)(cue.Start * SampleRate), output.Count);
            while (output.Count < startFrame) output.Add(0);
            output.AddRange(samples);
            ranges.Add(((double)startFrame / SampleRate, (double)output.Count / SampleRate));
            progress?.Invoke((double)(i + 1) / cues.Count);
        }
        var total = (int)(totalDuration * SampleRate);
        while (output.Count < total) output.Add(0);
        AudioInput.WriteWav(path, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output), SampleRate);
        return ranges;
    }

    /// <summary>
    /// The original speech a line is cloned from: from the line's start, at least <see cref="ReferenceSeconds"/> (taken
    /// from before the line near the end of the timeline), at most <see cref="MaxReferenceSeconds"/>.
    /// </summary>
    internal static float[] ReferenceRange(CaptionCue cue, float[] voice)
    {
        const int rate = VoiceCloner.SampleRate;
        var total = (double)voice.Length / rate;
        var start = Math.Max(0, cue.Start);
        var end = Math.Min(Math.Min(total, Math.Max(cue.End, start + ReferenceSeconds)), start + MaxReferenceSeconds);
        if (end - start < ReferenceSeconds) start = Math.Max(0, end - ReferenceSeconds);
        var a = Math.Min(voice.Length, (int)(start * rate));
        var b = Math.Min(voice.Length, (int)(end * rate));
        return voice[a..Math.Max(a, b)];
    }

    /// <summary>
    /// Character error rate of <paramref name="heard"/> against <paramref name="expected"/>: edit distance over letters
    /// and digits (lower case, punctuation and spacing ignored) divided by the expected length.
    /// </summary>
    internal static double LineError(string expected, string heard)
    {
        static List<string> Normalized(string s)
        {
            var characters = new List<string>();
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(s.ToLowerInvariant());
            while (e.MoveNext())
            {
                var character = e.GetTextElement();
                if (char.IsLetterOrDigit(character, 0)) characters.Add(character);
            }
            return characters;
        }
        var a = Normalized(expected);
        var b = Normalized(heard);
        if (a.Count == 0) return b.Count == 0 ? 0 : 1;
        var row = Enumerable.Range(0, b.Count + 1).ToArray();
        for (var i = 1; i <= a.Count; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return (double)row[b.Count] / a.Count;
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
