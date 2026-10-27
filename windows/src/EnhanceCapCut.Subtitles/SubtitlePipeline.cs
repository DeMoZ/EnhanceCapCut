using System.Globalization;
using System.Text;
using EnhanceCapCut.Draft;
using static EnhanceCapCut.Draft.Localization;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Timeline mix → speech recognition → cues → translations → voice-over: the counterpart of the macOS version's
/// <c>SubtitlePipeline</c>. The result goes into the draft with <see cref="DraftSubtitleWriter"/> /
/// <see cref="DraftVoiceOverWriter"/> and into .srt files.
/// </summary>
public static class SubtitlePipeline
{
    /// <param name="VisibleLanguage">The language whose track stays visible; the others are added hidden. Null = the first track.</param>
    public sealed record Options(SubtitleLanguage Source, bool IncludeOriginal, IReadOnlyList<SubtitleLanguage> Targets,
        string? VisibleLanguage = null)
    {
        /// <summary>Make caption tracks. Off = only translate for the voice-over.</summary>
        public bool Captions { get; init; } = true;
        /// <summary>
        /// Voice-over languages: language code → voice id ("" = the best installed voice, <see cref="Dubber.ClonedVoiceId"/> =
        /// the original speaker's voice, cloned).
        /// </summary>
        public IReadOnlyDictionary<string, string> Voices { get; init; } = new Dictionary<string, string>();
        /// <summary>How expressive cloned voices are, 0…1 (0.5 = like the original).</summary>
        public float Exaggeration { get; init; } = 0.5f;
        /// <summary>The voice-over language left audible; the other tracks are added muted. Null = the first one.</summary>
        public string? AudibleVoice { get; init; }
        /// <summary>Where the voice-over audio files go (inside the project folder, so CapCut can reach them).</summary>
        public string? VoiceOverFolder { get; init; }
    }

    public sealed record Output(List<SubtitleTrack> Captions, List<VoiceOverTrack> VoiceOver);

    public delegate void Report(string step, double fraction);

    /// <summary>The models the run needs (translation only when there are targets, voice cloning when a voice is cloned).</summary>
    public static List<ModelFile> RequiredModels(Options options)
    {
        List<ModelFile> models = [ModelStore.Whisper];
        if (options.Targets.Any(t => t.Code != options.Source.Code)) models.AddRange(ModelStore.Translation);
        if (options.Voices.Values.Contains(Dubber.ClonedVoiceId)) models.AddRange(ModelStore.VoiceCloning);
        return models;
    }

    /// <param name="audio">The timeline's mix as 16 kHz mono (<see cref="AudioInput"/>).</param>
    /// <param name="voices">Speaks the voice-over; needed when <see cref="Options.Voices"/> has system voices.</param>
    /// <param name="originalVoice">
    /// What cloned voices copy: the timeline's voice as 24 kHz mono. Windows has no voice isolation yet, so the callers pass
    /// the mix; null = <paramref name="audio"/>, upsampled.
    /// </param>
    public static async Task<Output> RunAsync(float[] audio, Options options, Report report, IVoiceEngine? voices = null,
        CancellationToken cancel = default, float[]? originalVoice = null)
    {
        var duration = (double)audio.Length / AudioInput.SampleRate;
        var targets = options.Targets.Where(t => t.Code != options.Source.Code).ToList();
        var dubbing = targets.Where(t => options.Voices.TryGetValue(t.Code, out var v) && (v == Dubber.ClonedVoiceId || voices is not null)).ToList();
        var cloning = dubbing.Any(t => options.Voices[t.Code] == Dubber.ClonedVoiceId);
        // Progress: models up to 0.15, speech up to `speechEnd`, translations up to `translateEnd`, then the voice-over.
        var speechEnd = dubbing.Count == 0 ? 0.75 : 0.6;
        var translateEnd = dubbing.Count == 0 ? 1 : 0.7;

        // 1. Models (downloaded once).
        var models = RequiredModels(options);
        if (!ModelStore.IsDownloaded(models))
            await ModelStore.EnsureAsync(models, p => report(L("Downloading models (one time)…"), 0.15 * p), cancel);

        // 2. Speech
        var words = await SpeechRecognizer.TranscribeAsync(audio, options.Source,
            p => report(L("Transcribing…"), 0.15 + (speechEnd - 0.15) * p), cancel);
        var cues = Segmenter.Cues(words, options.Source.Code);

        // 3. Translations
        var tracks = new List<SubtitleTrack>();
        var spoken = new Dictionary<string, List<CaptionCue>>();
        if (options.IncludeOriginal && options.Captions) tracks.Add(new SubtitleTrack(options.Source, cues, IsOriginal: true, IsVisible: false));
        var translating = options.Captions ? targets : dubbing;
        if (translating.Count > 0)
        {
            using var translator = new Translator();
            for (var i = 0; i < translating.Count; i++)
            {
                var target = translating[i];
                var baseFraction = speechEnd + (translateEnd - speechEnd) * i / translating.Count;
                var translated = translator.Translate(cues.Select(c => c.Text).ToList(), options.Source, target,
                    p => report(L("Translating: {0}…", target.Title), baseFraction + (translateEnd - speechEnd) * p / translating.Count), cancel);
                spoken[target.Code] = cues.Zip(translated, (c, t) => c with { Text = t }).ToList();
                if (!options.Captions) continue;
                var wrapped = cues.Zip(translated, (c, t) => c with { Text = LineWrapper.Wrap(t, target.Code) }).ToList();
                tracks.Add(new SubtitleTrack(target, wrapped, IsOriginal: false, IsVisible: false));
            }
        }
        var visible = tracks.FindIndex(t => t.Language.Code == options.VisibleLanguage);
        if (tracks.Count > 0) tracks[Math.Max(visible, 0)] = tracks[Math.Max(visible, 0)] with { IsVisible = true };

        // 4. Voice-over
        var voiceOver = new List<VoiceOverTrack>();
        if (dubbing.Count > 0 && options.VoiceOverFolder is { } folder)
        {
            Directory.CreateDirectory(folder);
            VoiceCloner? cloner = null;
            WhisperChecker? checker = null;
            if (cloning)
            {
                report(L("Loading the voice cloning model…"), translateEnd);
                originalVoice ??= AudioInput.Resample(audio, AudioInput.SampleRate, VoiceCloner.SampleRate);
                cloner = new VoiceCloner();
                checker = new WhisperChecker();
            }
            using var clonerScope = cloner;
            using var checkerScope = checker;
            for (var i = 0; i < dubbing.Count; i++)
            {
                var language = dubbing[i];
                var baseFraction = translateEnd + (1 - translateEnd) * i / dubbing.Count;
                report(L("Voicing: {0}…", language.Title), baseFraction);
                var path = Path.Combine(folder, $"{language.Code}.wav");
                void Progress(double p) => report(L("Voicing: {0}…", language.Title), baseFraction + (1 - translateEnd) * p / dubbing.Count);
                List<(double Start, double End)> lines;
                if (options.Voices[language.Code] == Dubber.ClonedVoiceId)
                {
                    lines = await Dubber.RenderClonedAsync(cloner!, checker, spoken.GetValueOrDefault(language.Code) ?? [], language.Code,
                        originalVoice!, options.Exaggeration, duration, path, Progress, cancel);
                }
                else
                {
                    var voice = options.Voices[language.Code] is { Length: > 0 } chosen ? chosen
                        : Dubber.DefaultVoice(voices!, language)?.Id ?? throw new NoVoiceException(language);
                    lines = await Dubber.RenderAsync(voices!, spoken.GetValueOrDefault(language.Code) ?? [], voice, duration, path,
                        Progress, cancel);
                }
                var length = Math.Max(duration, lines.Count > 0 ? lines[^1].End : 0);
                voiceOver.Add(new VoiceOverTrack(language, path, length, lines, IsAudible: false));
            }
            var audible = voiceOver.FindIndex(t => t.Language.Code == options.AudibleVoice);
            voiceOver[Math.Max(audible, 0)] = voiceOver[Math.Max(audible, 0)] with { IsAudible = true };
        }
        report(L("Done"), 1);
        return new Output(tracks, voiceOver);
    }

    /// <summary>The timeline to transcribe: without voice-over (and its ducking) that earlier runs added.</summary>
    public static Timeline SpeechTimeline(CapCutProject project, ProjectTimeline timeline)
    {
        var data = DraftVoiceOverWriter.WithoutVoiceOver(File.ReadAllBytes(timeline.DraftPath));
        return TimelineBuilder.Build(EnhanceCapCut.Draft.Draft.Parse(Encoding.UTF8.GetString(data)), project.Folder);
    }

    /// <summary>A new folder for a run's voice-over audio, inside the project so CapCut can read it.</summary>
    public static string VoiceOverFolder(CapCutProject project) => Path.Combine(project.Folder, "EnhanceCapCut",
        "voiceover " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ", CultureInfo.InvariantCulture));

    /// <summary>
    /// The draft with the run's caption and voice-over tracks added. Only what the run made is touched: without
    /// captions the draft's earlier subtitles stay, without voice-over its earlier voice-over.
    /// </summary>
    public static byte[] Apply(Output output, byte[] draft, OriginalAudio original, bool replacePrevious, string? projectFolder)
    {
        var data = draft;
        if (output.Captions.Count > 0) data = DraftSubtitleWriter.Apply(output.Captions, data, replacePrevious);
        if (output.VoiceOver.Count > 0) data = DraftVoiceOverWriter.Apply(output.VoiceOver, data, original, replacePrevious, projectFolder);
        return data;
    }

    /// <summary>Writes one .srt per track into <paramref name="folder"/>, named "&lt;base&gt; &lt;code&gt;.srt".</summary>
    public static List<string> WriteSrt(IReadOnlyList<SubtitleTrack> tracks, string folder, string baseName)
    {
        Directory.CreateDirectory(folder);
        var safe = string.Concat(baseName.Select(c => "/:\\".Contains(c) ? '-' : c));
        return tracks.Select(t =>
        {
            var path = Path.Combine(folder, $"{safe} {t.Language.Code}.srt");
            File.WriteAllText(path, SrtWriter.Srt(t.Cues));
            return path;
        }).ToList();
    }
}

public sealed class NoVoiceException(SubtitleLanguage language) : Exception(
    L("No Windows voice is installed for {0}. Add one in Settings ▸ Time & language ▸ Speech (Add voices).", language.Title));
