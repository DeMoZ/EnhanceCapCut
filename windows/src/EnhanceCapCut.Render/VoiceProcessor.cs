using EnhanceCapCut.Core;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>Separates the voice from a recording (a speech model); the result is time-aligned with the input.</summary>
public interface IVoiceIsolator
{
    /// <summary>The voice in one channel of 48 kHz audio, same length as <paramref name="channel"/>.</summary>
    float[] Isolate(float[] channel, CancellationToken cancel = default);
}

/// <summary>
/// Renders CapCut's per-clip "Isolate voice" (keep / remove vocal), "Reduce noise", "Enhance voice" and voice
/// filters — the port of the macOS VoiceProcessor, with the same stages and constants; only the voice isolation
/// differs (<see cref="IVoiceIsolator"/>: DeepFilterNet on Windows, Apple's AUSoundIsolation on the Mac):
/// - Keep vocal: the isolated voice.
/// - Remove vocal: the source minus the voice estimate, as a spectral Wiener mask (<see cref="VocalRemover"/>).
/// - Reduce noise: <c>rate × voice + (1 − rate) × source</c>; on a "Remove vocal" clip a stationary-noise
///   spectral gate (<see cref="SpectralDenoiser"/>) instead.
/// - Enhance voice: the ambience turned down to <see cref="VoiceEnhancer.AmbientGain"/>, then the enhancer's gain
///   and EQ (not on "Remove vocal" clips).
/// - Voice filter: the cached preset's graph runs last, from the clip's first source sample.
///
/// Each clip's source range plus <see cref="PaddingUs"/> of context is decoded and processed once per render.
/// Without an isolator (the model couldn't be loaded) the stages that need it are skipped and reported.
/// </summary>
public sealed class VoiceProcessor(IAudioDecoder decoder, IVoiceIsolator? isolator, List<string> warnings)
{
    public const int SampleRate = AudioMixer.SampleRate;
    /// <summary>Extra source audio around the clip so the model has context at its edges.</summary>
    public const long PaddingUs = 1_000_000;

    readonly Dictionary<(string, long, long, VoiceEffects), DecodedAudio?> cache = [];
    readonly Dictionary<string, float[][]?> loops = [];
    bool reportedMissingIsolator;

    /// <summary>Whether <paramref name="voice"/> needs the voice isolation model.</summary>
    public static bool NeedsIsolation(VoiceEffects voice) => voice.Separation != VoiceSeparation.None
        || AmbienceShare(voice) < 1;

    /// <summary>The share of the ambience (source − voice) kept on a clip without separation.</summary>
    static double AmbienceShare(VoiceEffects voice)
    {
        var ambience = 1.0;
        if (voice.Denoise is { } strength) ambience = 1 - strength;
        if (voice.Enhance is { } level) ambience = Math.Min(ambience, VoiceEnhancer.AmbientGain(level));
        return ambience;
    }

    /// <summary>The clip's processed source audio (covering its range plus padding), or null without audio.</summary>
    public DecodedAudio? Process(AudioClip clip, CancellationToken cancel = default)
    {
        var start = Math.Max(0, clip.TimeMap.SourceStart() - PaddingUs);
        var end = clip.TimeMap.SourceStart() + clip.TimeMap.SourceDuration() + PaddingUs;
        var key = (clip.Path, start, end, clip.Voice);
        if (cache.TryGetValue(key, out var cached)) return cached;
        var audio = decoder.Decode(clip.Path, start, end);
        var result = audio is null || audio.Frames == 0 ? audio : Apply(clip, audio, cancel);
        cache[key] = result;
        return result;
    }

    DecodedAudio Apply(AudioClip clip, DecodedAudio audio, CancellationToken cancel)
    {
        var voice = clip.Voice;
        var x = Planar(audio.Samples);
        var isolate = isolator is not null && NeedsIsolation(voice);
        if (NeedsIsolation(voice) && isolator is null && !reportedMissingIsolator)
        {
            reportedMissingIsolator = true;
            warnings.Add("Voice isolation model is not available: isolate voice and reduce noise are not applied");
        }
        if (isolate)
        {
            switch (voice.Separation)
            {
                case VoiceSeparation.None:
                    x = Blend(x, Isolate(x, cancel), (float)(1 - AmbienceShare(voice)));
                    break;
                case VoiceSeparation.KeepVoice:
                    x = Isolate(x, cancel);
                    break;
                case VoiceSeparation.RemoveVoice:
                    x = VocalRemover.Process(x, Isolate(x, cancel), cancel: cancel);
                    break;
            }
        }
        if (voice.Separation == VoiceSeparation.RemoveVoice && voice.Denoise is { } denoise)
            x = SpectralDenoiser.Process(x, denoise, cancel);
        if (voice.Enhance is not null && voice.Separation != VoiceSeparation.RemoveVoice)
            foreach (var c in x) VoiceEnhancer.Apply(c, SampleRate);
        if (voice.Filter is { } filter)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var graph = VoiceFilterGraph.Load(filter.Directory);
                var first = (int)Math.Clamp(AudioMixer.Frames(clip.TimeMap.SourceStart() - audio.StartUs), 0, x[0].Length);
                var tail = x.Select(c => c[first..]).ToArray();
                var processed = graph.Process(tail, SampleRate, filter.Strength, LoadLoop);
                for (var c = 0; c < x.Length && c < processed.Length; c++)
                    Array.Copy(processed[c], 0, x[c], first, Math.Min(processed[c].Length, x[c].Length - first));
            }
            catch (VoiceFilterGraph.LoadException e)
            {
                warnings.Add(e.Message);
            }
        }
        return new DecodedAudio(Interleaved(x), audio.StartUs);
    }

    float[][] Isolate(float[][] x, CancellationToken cancel)
    {
        var left = isolator!.Isolate(x[0], cancel);
        // Mono sources (both channels equal) are isolated once.
        var right = x[1].AsSpan().SequenceEqual(x[0]) ? (float[])left.Clone() : isolator.Isolate(x[1], cancel);
        return [left, right];
    }

    float[][]? LoadLoop(string path)
    {
        if (loops.TryGetValue(path, out var cached)) return cached;
        var audio = File.Exists(path) ? decoder.Decode(path, 0, long.MaxValue) : null;
        var result = audio is null || audio.Frames == 0 ? null : Planar(audio.Samples);
        loops[path] = result;
        return result;
    }

    /// <summary><c>voiceAmount × voice + (1 − voiceAmount) × source</c>.</summary>
    public static float[][] Blend(float[][] source, float[][] voice, float voiceAmount)
    {
        var dry = 1 - voiceAmount;
        return source.Select((s, c) =>
        {
            var v = voice[c];
            var o = new float[s.Length];
            for (var i = 0; i < s.Length; i++) o[i] = dry * s[i] + voiceAmount * (i < v.Length ? v[i] : 0);
            return o;
        }).ToArray();
    }

    public static float[][] Planar(float[] stereo)
    {
        var frames = stereo.Length / 2;
        float[] l = new float[frames], r = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            l[i] = stereo[i * 2];
            r[i] = stereo[i * 2 + 1];
        }
        return [l, r];
    }

    public static float[] Interleaved(float[][] x)
    {
        var frames = x[0].Length;
        var o = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            o[i * 2] = x[0][i];
            o[i * 2 + 1] = x[x.Length > 1 ? 1 : 0][i];
        }
        return o;
    }
}

/// <summary>
/// CapCut's "Enhance voice", as calibrated on the Mac (see docs/FORMAT.md): ~2.8 dB louder voice, a zero-phase low
/// cut around 68 Hz and a strong "air" boost around 13.7 kHz; the ambience share is level-dependent.
/// </summary>
public static class VoiceEnhancer
{
    public const float Gain = 1.38f;
    public const double HighPassHz = 68.0;
    public const double AirHz = 13_700.0, AirGainDB = 12.9, AirQ = 1.42;
    /// <summary>The ambience share kept at <c>ambient_sound_level</c> 75; others interpolated as <c>level^k</c>.</summary>
    public const double AmbientAt75 = 0.3;

    /// <summary>The share of the ambience kept for <paramref name="level"/> (0...1).</summary>
    public static double AmbientGain(double level)
    {
        var k = Math.Log(AmbientAt75) / Math.Log(0.75);
        return Math.Pow(Math.Clamp(level, 0, 1), k);
    }

    /// <summary>Applies the EQ and gain to one channel of a whole clip.</summary>
    public static void Apply(float[] x, double rate)
    {
        var highPass = VoiceFilterProcessing.Biquad.HighPass(HighPassHz, rate);
        highPass.Run(x);
        highPass.Run(x, reverse: true);
        VoiceFilterProcessing.Biquad.Peak(AirHz, AirGainDB, AirQ, rate).Run(x);
        for (var i = 0; i < x.Length; i++) x[i] *= Gain;
    }
}
