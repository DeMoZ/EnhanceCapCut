using EnhanceCapCut.Subtitles;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using Gender = EnhanceCapCut.Subtitles.VoiceGender;

namespace EnhanceCapCut.Windows;

/// <summary>
/// Windows' own voices (<c>Windows.Media.SpeechSynthesis</c>, the voices of Settings ▸ Time &amp; language ▸ Speech) for
/// the voice-over — the counterpart of the macOS version's AVSpeechSynthesizer voices. Offline.
/// </summary>
public sealed class WindowsVoices : IVoiceEngine, IDisposable
{
    readonly MediaPlayer player = new();

    /// <summary>The Speech page of Settings, where more voices are added.</summary>
    public const string SettingsUri = "ms-settings:speech";

    public IReadOnlyList<DubVoice> Voices(string languageCode)
    {
        var wanted = languageCode.Split('-');
        return SpeechSynthesizer.AllVoices
            .Where(v =>
            {
                var parts = v.Language.Split('-');
                if (!string.Equals(parts[0], wanted[0], StringComparison.OrdinalIgnoreCase)) return false;
                var region = parts.Length > 1 ? parts[^1].ToUpperInvariant() : "";
                return languageCode switch
                {
                    "zh-Hant" => region is "TW" or "HK",
                    "zh-Hans" => region == "CN",
                    _ => wanted.Length < 2 || string.Equals(region, wanted[^1], StringComparison.OrdinalIgnoreCase),
                };
            })
            .Select(v => new DubVoice(v.Id, Name(v.DisplayName), v.Language, RegionName(v.Language),
                v.Gender == global::Windows.Media.SpeechSynthesis.VoiceGender.Male ? Gender.Male : Gender.Female))
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<float[]> SpeakAsync(string text, string voiceId, double rate, CancellationToken cancel)
    {
        var bytes = await SynthesizeAsync(text, voiceId, rate, cancel);
        var (samples, channels, sampleRate) = AudioInput.ParseWav(bytes, "speech");
        var mono = samples;
        if (channels > 1)
        {
            mono = new float[samples.Length / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++) sum += samples[i * channels + c];
                mono[i] = sum / channels;
            }
        }
        return sampleRate == Dubber.SampleRate ? mono : AudioInput.Resample(mono, sampleRate, Dubber.SampleRate);
    }

    /// <summary>Plays a line with the voice through the default output, for previewing.</summary>
    public async Task PreviewAsync(string text, string voiceId)
    {
        var bytes = await SynthesizeAsync(text, voiceId, 1, CancellationToken.None);
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
        player.Play();
    }

    static async Task<byte[]> SynthesizeAsync(string text, string voiceId, double rate, CancellationToken cancel)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId) is { } voice) synthesizer.Voice = voice;
        synthesizer.Options.SpeakingRate = Math.Clamp(rate, 0.5, 6);
        synthesizer.Options.AppendedSilence = SpeechAppendedSilence.Min;
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancel);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask(cancel);
        reader.ReadBytes(bytes);
        return bytes;
    }

    public void Dispose() => player.Dispose();

    /// <summary>"Microsoft Zira" → "Zira", like the macOS voice names.</summary>
    static string Name(string displayName) =>
        displayName.StartsWith("Microsoft ", StringComparison.Ordinal) ? displayName["Microsoft ".Length..] : displayName;

    /// <summary>
    /// English region names for the regions Windows has voices for (the app runs with invariant globalization, so .NET
    /// has no region names); other regions show their own code.
    /// </summary>
    static string? RegionName(string language)
    {
        var parts = language.Split('-');
        if (parts.Length < 2) return null;
        var code = parts[^1].ToUpperInvariant();
        return Regions.GetValueOrDefault(code, code);
    }

    static readonly Dictionary<string, string> Regions = new()
    {
        ["AE"] = "United Arab Emirates", ["AR"] = "Argentina", ["AT"] = "Austria", ["AU"] = "Australia", ["BE"] = "Belgium",
        ["BR"] = "Brazil", ["CA"] = "Canada", ["CH"] = "Switzerland", ["CL"] = "Chile", ["CN"] = "China", ["CO"] = "Colombia",
        ["CZ"] = "Czechia", ["DE"] = "Germany", ["EG"] = "Egypt", ["ES"] = "Spain", ["FR"] = "France", ["GB"] = "United Kingdom",
        ["HK"] = "Hong Kong", ["ID"] = "Indonesia", ["IE"] = "Ireland", ["IL"] = "Israel", ["IN"] = "India", ["IT"] = "Italy",
        ["JP"] = "Japan", ["KR"] = "South Korea", ["MX"] = "Mexico", ["MY"] = "Malaysia", ["NL"] = "Netherlands",
        ["NZ"] = "New Zealand", ["PH"] = "Philippines", ["PL"] = "Poland", ["PT"] = "Portugal", ["RU"] = "Russia",
        ["SA"] = "Saudi Arabia", ["SE"] = "Sweden", ["SG"] = "Singapore", ["TH"] = "Thailand", ["TR"] = "Türkiye",
        ["TW"] = "Taiwan", ["UA"] = "Ukraine", ["US"] = "United States", ["VN"] = "Vietnam", ["ZA"] = "South Africa",
    };
}
