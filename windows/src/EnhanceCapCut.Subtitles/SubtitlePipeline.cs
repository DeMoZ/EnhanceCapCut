using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Timeline mix → speech recognition → cues → translations: the counterpart of the macOS version's
/// <c>SubtitlePipeline</c>. The result goes into the draft with <see cref="DraftSubtitleWriter"/> and into .srt files.
/// </summary>
public static class SubtitlePipeline
{
    public sealed record Options(SubtitleLanguage Source, bool IncludeOriginal, IReadOnlyList<SubtitleLanguage> Targets,
        string? VisibleLanguage = null);

    public delegate void Report(string step, double fraction);

    /// <summary>The models the run needs (translation only when there are targets).</summary>
    public static List<ModelFile> RequiredModels(Options options) =>
        options.Targets.Any(t => t.Code != options.Source.Code) ? [ModelStore.Whisper, .. ModelStore.Translation] : [ModelStore.Whisper];

    /// <param name="audio">The timeline's mix as 16 kHz mono (<see cref="AudioInput"/>).</param>
    public static async Task<List<SubtitleTrack>> RunAsync(float[] audio, Options options, Report report, CancellationToken cancel = default)
    {
        // 1. Models (downloaded once).
        var models = RequiredModels(options);
        if (!ModelStore.IsDownloaded(models))
            await ModelStore.EnsureAsync(models, p => report("Downloading models (one time)…", 0.15 * p), cancel);

        // 2. Speech
        var words = await SpeechRecognizer.TranscribeAsync(audio, options.Source, p => report("Transcribing…", 0.15 + 0.6 * p), cancel);
        var cues = Segmenter.Cues(words, options.Source.Code);

        // 3. Translations
        var tracks = new List<SubtitleTrack>();
        if (options.IncludeOriginal) tracks.Add(new SubtitleTrack(options.Source, cues, IsOriginal: true, IsVisible: false));
        var targets = options.Targets.Where(t => t.Code != options.Source.Code).ToList();
        if (targets.Count > 0)
        {
            using var translator = new Translator();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var baseFraction = 0.75 + 0.25 * i / targets.Count;
                var translated = translator.Translate(cues.Select(c => c.Text).ToList(), options.Source, target,
                    p => report($"Translating: {target.Name}…", baseFraction + 0.25 * p / targets.Count), cancel);
                var wrapped = cues.Zip(translated, (c, t) => c with { Text = LineWrapper.Wrap(t, target.Code) }).ToList();
                tracks.Add(new SubtitleTrack(target, wrapped, IsOriginal: false, IsVisible: false));
            }
        }
        var visible = tracks.FindIndex(t => t.Language.Code == options.VisibleLanguage);
        if (tracks.Count > 0) tracks[Math.Max(visible, 0)] = tracks[Math.Max(visible, 0)] with { IsVisible = true };
        report("Done", 1);
        return tracks;
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
