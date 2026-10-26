using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using EnhanceCapCut.Subtitles;
using EnhanceCapCut.Windows;
using Microsoft.UI.Dispatching;

namespace EnhanceCapCut.App;

abstract record SubtitlesPhase
{
    public sealed record Idle : SubtitlesPhase;
    public sealed record Running(string Step, double Fraction) : SubtitlesPhase;
    public sealed record Done(DraftSubtitleWriter.Result Result, string Summary, IReadOnlyList<string> Srt) : SubtitlesPhase;
    public sealed record Failed(string Message) : SubtitlesPhase;
}

/// <summary>
/// State of the Subtitles &amp; Voice-over window: chosen languages and voices, the model downloads and the running job.
/// The project and timeline are the ones selected in the export window (<see cref="ExportModel"/>). The counterpart of
/// the macOS version's <c>SubtitlesModel</c>: Whisper and NLLB instead of Apple's speech and translation, Windows voices
/// instead of the system's AVSpeechSynthesizer voices.
/// </summary>
sealed class SubtitlesModel
{
    readonly DispatcherQueue ui;
    public ExportModel Export { get; }
    public event Action? Changed;

    public SubtitlesPhase Phase { get; private set; } = new SubtitlesPhase.Idle();

    public SubtitlesModel(ExportModel export, DispatcherQueue ui)
    {
        Export = export;
        this.ui = ui;
        sourceCode = SubtitleLanguage.Find(Preferences.String("subtitles.sourceLanguage") ?? "")?.Code ?? "ru";
        selectedTargets = [.. Preferences.Strings("subtitles.targets") ?? ["en"]];
        includeOriginal = Preferences.Bool("subtitles.includeOriginal", true);
        visibleLanguage = Preferences.String("subtitles.visible") ?? "";
        replacePrevious = Preferences.Bool("subtitles.replacePrevious", true);
        saveSrt = Preferences.Bool("subtitles.saveSRT", true);
        makeCaptions = Preferences.Bool("subtitles.makeCaptions", true);
        makeDub = Preferences.Bool("subtitles.makeDub");
        dubVoices = Preferences.Map("subtitles.dubVoices");
        voiceGender = Enum.TryParse<VoiceGender>(Preferences.String("subtitles.voiceGender"), out var g) ? g : VoiceGender.Female;
        originalAudio = Enum.TryParse<OriginalAudio>(Preferences.String("subtitles.originalAudio"), out var o) ? o : OriginalAudio.Lower;
        audibleVoice = Preferences.String("subtitles.audibleVoice") ?? "";
        cloneVoice = Preferences.Bool("subtitles.cloneVoice");
        expressiveness = double.TryParse(Preferences.String("subtitles.expressiveness"), System.Globalization.CultureInfo.InvariantCulture,
            out var e) ? e : 0.5;
        RefreshDownloads();
    }

    void Notify() => Changed?.Invoke();

    // MARK: Choices (saved like the macOS version's UserDefaults)

    public string SourceCode
    {
        get => sourceCode;
        set { sourceCode = value; Preferences.Set("subtitles.sourceLanguage", value); RefreshDownloads(); Notify(); }
    }
    string sourceCode;

    public bool IncludeOriginal
    {
        get => includeOriginal;
        set { includeOriginal = value; Preferences.Set("subtitles.includeOriginal", value); Notify(); }
    }
    bool includeOriginal;

    /// <summary>Language code of the track left visible; the other tracks are added hidden.</summary>
    public string VisibleLanguage
    {
        get => visibleLanguage;
        set { visibleLanguage = value; Preferences.Set("subtitles.visible", value); Notify(); }
    }
    string visibleLanguage;

    public bool ReplacePrevious
    {
        get => replacePrevious;
        set { replacePrevious = value; Preferences.Set("subtitles.replacePrevious", value); Notify(); }
    }
    bool replacePrevious;

    public bool SaveSrt
    {
        get => saveSrt;
        set { saveSrt = value; Preferences.Set("subtitles.saveSRT", value); Notify(); }
    }
    bool saveSrt;

    public bool MakeCaptions
    {
        get => makeCaptions;
        set { makeCaptions = value; Preferences.Set("subtitles.makeCaptions", value); Notify(); }
    }
    bool makeCaptions;

    public bool MakeDub
    {
        get => makeDub;
        set { makeDub = value; Preferences.Set("subtitles.makeDub", value); RefreshDownloads(); Notify(); }
    }
    bool makeDub;

    public VoiceGender VoiceGender
    {
        get => voiceGender;
        set { voiceGender = value; Preferences.Set("subtitles.voiceGender", value.ToString()); Notify(); }
    }
    VoiceGender voiceGender;

    public OriginalAudio OriginalAudio
    {
        get => originalAudio;
        set { originalAudio = value; Preferences.Set("subtitles.originalAudio", value.ToString()); Notify(); }
    }
    OriginalAudio originalAudio;

    /// <summary>Language code of the voice-over track left audible; the other tracks are added muted.</summary>
    public string AudibleVoice
    {
        get => audibleVoice;
        set { audibleVoice = value; Preferences.Set("subtitles.audibleVoice", value); Notify(); }
    }
    string audibleVoice;

    /// <summary>
    /// Speak in the original speaker's voice (<see cref="VoiceCloner"/>) where the model knows the language; Windows voices
    /// for the rest. Off = Windows voices only, as before.
    /// </summary>
    public bool CloneVoice
    {
        get => cloneVoice;
        set { cloneVoice = value; Preferences.Set("subtitles.cloneVoice", value); RefreshDownloads(); Notify(); }
    }
    bool cloneVoice;

    /// <summary>How expressive cloned voices are, 0.25…1 (0.5 = like the original).</summary>
    public double Expressiveness
    {
        get => expressiveness;
        set
        {
            expressiveness = value;
            Preferences.Set("subtitles.expressiveness", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Notify();
        }
    }
    double expressiveness;

    readonly HashSet<string> selectedTargets;
    /// <summary>Chosen voice per "language|gender" (missing = the best match).</summary>
    readonly Dictionary<string, string> dubVoices;

    public bool IsTargetSelected(SubtitleLanguage language) => selectedTargets.Contains(language.Code);

    public void Toggle(SubtitleLanguage language)
    {
        if (!selectedTargets.Remove(language.Code)) selectedTargets.Add(language.Code);
        Preferences.Set("subtitles.targets", selectedTargets.Order(StringComparer.Ordinal));
        RefreshDownloads();
        Notify();
    }

    // MARK: Derived

    public bool IsRunning => Phase is SubtitlesPhase.Running;
    public SubtitleLanguage SourceLanguage => SubtitleLanguage.Find(SourceCode) ?? SubtitleLanguage.All[0];

    /// <summary>Spoken languages, by name (every language both Whisper and NLLB handle).</summary>
    public IEnumerable<SubtitleLanguage> SourceLanguages => SubtitleLanguage.All.OrderBy(l => l.Name, StringComparer.Ordinal);
    public IEnumerable<SubtitleLanguage> TargetLanguages => SubtitleLanguage.All.Where(l => l.Code != SourceLanguage.Code);

    /// <summary>Selected targets in display order, without the source language itself.</summary>
    public List<SubtitleLanguage> EffectiveTargets => TargetLanguages.Where(IsTargetSelected).ToList();

    /// <summary>The caption tracks that will be created, in order.</summary>
    public List<SubtitleLanguage> OutputLanguages => MakeCaptions ? [.. IncludeOriginal ? [SourceLanguage] : Array.Empty<SubtitleLanguage>(), .. EffectiveTargets] : [];

    /// <summary>The voice-over tracks that will be created: every translation.</summary>
    public List<SubtitleLanguage> DubLanguages => MakeDub ? EffectiveTargets : [];

    /// <summary>The audible voice-over track: the saved choice when it's among them, else the first one.</summary>
    /// <summary>The timeline a voice-over run creates (a dubbed copy of the selected one); null without voice-over.</summary>
    public string? DubTimelineName => Export.SelectedProject is { } project && EffectiveAudibleVoice is { } heard
        ? DubTimeline.Name(project, DubLanguages.Select(l => l.Code).Where(c => c != heard).Prepend(heard), OriginalAudio)
        : null;

    public string? EffectiveAudibleVoice => DubLanguages.Any(l => l.Code == AudibleVoice) ? AudibleVoice : DubLanguages.FirstOrDefault()?.Code;

    /// <summary>The visible track: the saved choice when it's among the outputs, else the first one.</summary>
    public string? EffectiveVisibleLanguage => OutputLanguages.Any(l => l.Code == VisibleLanguage) ? VisibleLanguage : OutputLanguages.FirstOrDefault()?.Code;

    public bool CanStart => Export.Timeline is not null && !IsRunning && DownloadProgress is null
        && !(OutputLanguages.Count == 0 && DubLanguages.Count == 0);

    /// <summary>The selected project is open in CapCut, so the draft can't be written now.</summary>
    public bool ProjectIsOpen { get; private set; }

    /// <summary>Re-checks whether the selected project is open in CapCut.</summary>
    public void RefreshProjectState()
    {
        ProjectIsOpen = Export.SelectedProject is { } project && DraftSubtitleWriter.IsOpenInCapCut(project);
        if (Phase is SubtitlesPhase.Done or SubtitlesPhase.Failed) Phase = new SubtitlesPhase.Idle();
        Notify();
    }

    // MARK: Voices

    readonly WindowsVoices engine = new();
    readonly Dictionary<string, IReadOnlyList<DubVoice>> voiceCache = [];
    /// <summary>Language whose "Get Voices…" was opened, to show what to look for in Settings.</summary>
    public string? VoiceHelpLanguage { get; private set; }

    public IReadOnlyList<DubVoice> Voices(SubtitleLanguage language)
    {
        if (voiceCache.TryGetValue(language.Code, out var cached)) return cached;
        IReadOnlyList<DubVoice> list;
        try { list = engine.Voices(language.Code); }
        catch (Exception) { list = []; }
        return voiceCache[language.Code] = list;
    }

    /// <summary>Voices offered in the picker: the chosen gender, or all voices when none of that gender is installed.</summary>
    public IReadOnlyList<DubVoice> PickerVoices(SubtitleLanguage language)
    {
        var all = Voices(language);
        var matching = all.Where(v => v.Gender == VoiceGender).ToList();
        return matching.Count == 0 ? all : matching;
    }

    /// <summary>True when no voice of the chosen gender is installed for the language.</summary>
    public bool LacksGender(SubtitleLanguage language)
    {
        var all = Voices(language);
        return all.Count > 0 && all.All(v => v.Gender != VoiceGender);
    }

    string ChoiceKey(SubtitleLanguage language) => $"{language.Code}|{VoiceGender}";

    /// <summary>Voice-over languages spoken in the cloned original voice.</summary>
    public List<SubtitleLanguage> ClonedLanguages => CloneVoice ? DubLanguages.Where(l => VoiceCloner.ModelLanguage(l.Code) is not null).ToList() : [];

    public bool IsCloned(SubtitleLanguage language) => ClonedLanguages.Any(l => l.Code == language.Code);

    /// <summary>
    /// The voice used for a language: the cloned voice when it can speak the language, else the saved choice for the
    /// current gender if still installed, else the best match.
    /// </summary>
    public string VoiceID(SubtitleLanguage language) => IsCloned(language) ? Dubber.ClonedVoiceId : SystemVoiceID(language);

    public string SystemVoiceID(SubtitleLanguage language)
    {
        var list = PickerVoices(language);
        if (dubVoices.TryGetValue(ChoiceKey(language), out var saved) && list.Any(v => v.Id == saved)) return saved;
        return Dubber.DefaultVoice(engine, language, VoiceGender)?.Id ?? list.FirstOrDefault()?.Id ?? "";
    }

    public void SetVoice(string id, SubtitleLanguage language)
    {
        dubVoices[ChoiceKey(language)] = id;
        Preferences.Set("subtitles.dubVoices", dubVoices);
        Notify();
    }

    public async void Preview(SubtitleLanguage language)
    {
        try { await engine.PreviewAsync(Dubber.Sample(language), SystemVoiceID(language)); }
        catch (Exception e)
        {
            DownloadError = e.Message;
            Notify();
        }
    }

    public void GetVoices(SubtitleLanguage language)
    {
        VoiceHelpLanguage = language.Code;
        Platform.OpenUri(WindowsVoices.SettingsUri);
        Notify();
    }

    /// <summary>Re-reads the installed voices, e.g. after the user adds voices in Settings (called when the window comes back).</summary>
    public void ReloadVoices()
    {
        voiceCache.Clear();
        // The voice the user went to download has arrived: hide the hint.
        if (VoiceHelpLanguage is { } code && SubtitleLanguage.All.FirstOrDefault(l => l.Code == code) is { } lang
            && Voices(lang).Any(v => v.Gender == VoiceGender))
            VoiceHelpLanguage = null;
        Notify();
    }

    // MARK: Models

    /// <summary>Models the chosen languages need that aren't downloaded yet.</summary>
    public List<ModelFile> NotDownloaded { get; private set; } = [];
    /// <summary>0…1 while the models download.</summary>
    public double? DownloadProgress { get; private set; }
    public string? DownloadError { get; private set; }
    CancellationTokenSource? downloadCancel;

    public void RefreshDownloads()
    {
        var cloned = ClonedLanguages;
        var options = new SubtitlePipeline.Options(SourceLanguage, IncludeOriginal, EffectiveTargets)
        {
            Voices = cloned.ToDictionary(l => l.Code, _ => Dubber.ClonedVoiceId),
        };
        NotDownloaded = SubtitlePipeline.RequiredModels(options).Where(f => !ModelStore.IsDownloaded([f])).ToList();
        if (NotDownloaded.Count == 0) DownloadError = null;
    }

    /// <summary>"Whisper speech recognition (570 MB), NLLB-200 translation (0.9 GB), voice cloning (1.6 GB)".</summary>
    public string MissingModelsText => string.Join(", ", NotDownloaded.GroupBy(f => f.Folder).Select(g =>
    {
        var bytes = g.Sum(f => f.ApproximateBytes);
        var size = bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";
        var name = g.Key switch { "whisper" => "Whisper speech recognition", "nllb" => "NLLB-200 translation", _ => "voice cloning" };
        return $"{name} ({size})";
    }));

    public async Task DownloadMissing()
    {
        DownloadError = null;
        DownloadProgress = 0;
        Notify();
        var source = new CancellationTokenSource();
        downloadCancel = source;
        var lastReport = DateTime.MinValue;
        try
        {
            await Task.Run(() => ModelStore.EnsureAsync(NotDownloaded, p =>
            {
                if ((DateTime.Now - lastReport).TotalMilliseconds < 200 && p < 1) return;
                lastReport = DateTime.Now;
                ui.TryEnqueue(() =>
                {
                    DownloadProgress = p;
                    Notify();
                });
            }, source.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            DownloadError = e.Message;
        }
        DownloadProgress = null;
        RefreshDownloads();
        Notify();
    }

    public void CancelDownload() => downloadCancel?.Cancel();

    // MARK: Run

    CancellationTokenSource? cancel;

    /// <summary>A run that clones voices needs the user's confirmation that the speakers consented (the window asks).</summary>
    public bool NeedsConsent => ClonedLanguages.Count > 0;

    public void Start()
    {
        if (!CanStart || Export.Timeline is null || Export.SelectedProject is not { } project || Export.SelectedTimeline is not { } projectTimeline)
            return;
        RefreshProjectState();
        if (ProjectIsOpen)
        {
            Phase = new SubtitlesPhase.Failed(new ProjectOpenException(project.Name).Message);
            Notify();
            return;
        }
        var dub = DubLanguages;
        var voices = dub.ToDictionary(l => l.Code, VoiceID);
        var options = new SubtitlePipeline.Options(SourceLanguage, IncludeOriginal, MakeCaptions ? EffectiveTargets : dub, EffectiveVisibleLanguage)
        {
            Captions = MakeCaptions,
            Voices = voices,
            Exaggeration = (float)Expressiveness,
            AudibleVoice = EffectiveAudibleVoice,
            VoiceOverFolder = SubtitlePipeline.VoiceOverFolder(project),
        };
        var replace = ReplacePrevious;
        var original = OriginalAudio;
        var srtFolder = SaveSrt && MakeCaptions ? Export.Settings.OutputFolder : null;
        var baseName = projectTimeline.IsMain ? project.Name : $"{project.Name} {projectTimeline.Name}";
        var dubName = DubTimelineName;
        var source = new CancellationTokenSource();
        cancel = source;
        Phase = new SubtitlesPhase.Running("Preparing…", 0);
        Notify();

        void Report(string step, double fraction) => ui.TryEnqueue(() =>
        {
            if (Phase is not SubtitlesPhase.Running || cancel != source) return;
            Phase = new SubtitlesPhase.Running(step, fraction);
            Notify();
        });

        Task.Run(async () =>
        {
            // The voice-over audio goes away with a failed run, unless the draft already points at it.
            var draftWritten = false;
            void DiscardAudio()
            {
                if (draftWritten || options.VoiceOverFolder is not { } folder) return;
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            }
            try
            {
                // 1. Audio: the timeline's mix as the viewer hears it, without voice-over of earlier runs.
                Report("Mixing timeline audio…", 0);
                var timeline = SubtitlePipeline.SpeechTimeline(project, projectTimeline);
                var mix = AudioMixer.Mix(timeline, new MfAudioDecoder(), [], p => Report("Mixing timeline audio…", 0.1 * p), source.Token);
                var audio = AudioInput.ToMono16k(mix, 2, AudioMixer.SampleRate);
                // What cloned voices copy (no voice isolation on Windows yet: the mix itself).
                var originalVoice = voices.ContainsValue(Dubber.ClonedVoiceId)
                    ? AudioInput.ToMono(mix, 2, AudioMixer.SampleRate, VoiceCloner.SampleRate) : null;
                source.Token.ThrowIfCancellationRequested();

                // 2. Speech, translations, voice-over.
                var output = await SubtitlePipeline.RunAsync(audio, options, (step, fraction) => Report(step, 0.1 + 0.9 * fraction),
                    voices.Values.Any(v => v != Dubber.ClonedVoiceId) ? engine : null, source.Token, originalVoice);
                source.Token.ThrowIfCancellationRequested();

                // 3. The draft.
                var tracks = output.Captions;
                var cueCount = tracks.Sum(t => t.Cues.Count);
                byte[] Apply(byte[] draft) => SubtitlePipeline.Apply(output, draft, original, replace, project.Folder);
                // Voice-over goes to a dubbed copy of the timeline; captions alone into the timeline itself.
                DraftSubtitleWriter.Result result;
                var target = project.Name;
                if (dubName is not null && output.VoiceOver.Count > 0)
                {
                    result = DubTimeline.Create(project, projectTimeline, dubName, cueCount, Apply).Result;
                    target = $"the new timeline {dubName}";
                }
                else result = DraftSubtitleWriter.Write(project, projectTimeline, cueCount, Apply);
                draftWritten = true;
                var srt = srtFolder is not null ? SubtitlePipeline.WriteSrt(tracks, srtFolder, baseName) : [];
                var parts = new List<string>();
                if (tracks.Count > 0) parts.Add($"{result.CueCount} captions ({string.Join(", ", tracks.Select(t => t.Language.Code))})");
                if (output.VoiceOver.Count > 0) parts.Add($"voice-over ({string.Join(", ", output.VoiceOver.Select(t => t.Language.Code))})");
                var summary = $"Added {string.Join(" and ", parts)} to {target}";
                ui.TryEnqueue(() =>
                {
                    if (cancel != source) return;
                    Phase = new SubtitlesPhase.Done(result, summary, srt);
                    // The export window shows the new captions (or the new timeline) too.
                    Export.RefreshProjects(selectOpen: false);
                    Finished?.Invoke();
                    RefreshDownloads();
                    Notify();
                });
            }
            catch (OperationCanceledException)
            {
                DiscardAudio();
                ui.TryEnqueue(() =>
                {
                    if (cancel == source) Phase = new SubtitlesPhase.Idle();
                    RefreshDownloads();
                    Notify();
                });
            }
            catch (Exception e)
            {
                DiscardAudio();
                ui.TryEnqueue(() =>
                {
                    if (cancel == source) Phase = new SubtitlesPhase.Failed(e.Message);
                    RefreshDownloads();
                    Notify();
                });
            }
        });
    }

    /// <summary>The job finished (set by the app, to flash the taskbar button).</summary>
    public Action? Finished { get; set; }

    public void Cancel()
    {
        cancel?.Cancel();
        cancel = null;
        Phase = new SubtitlesPhase.Idle();
        Notify();
    }
}
