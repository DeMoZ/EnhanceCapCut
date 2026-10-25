using EnhanceCapCut.Draft;
using EnhanceCapCut.Subtitles;
using EnhanceCapCut.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EnhanceCapCut.App;

/// <summary>
/// "Subtitles &amp; Voice-over": transcribes the timeline, translates it and adds the result to the CapCut project as
/// caption tracks and as voice-over audio tracks spoken by Windows voices. The counterpart of the macOS version's
/// <c>SubtitlesView</c>, section for section.
/// </summary>
sealed class SubtitlesWindow : Window
{
    readonly SubtitlesModel model;
    readonly ExportModel export;
    readonly FormView form = new();
    readonly FooterBar footer = new();

    // Project
    readonly Picker<string> projectPicker = new();
    readonly Picker<string> timelinePicker = new();
    readonly FormRow timelineRow, openRow, errorRow, loadingRow;
    readonly TextBlock errorText = Text.Body();

    // Create
    readonly OutputToggle captionsToggle, dubToggle;
    readonly Picker<string> sourcePicker = new();

    // Languages
    readonly FormRow originalRow, visibleRow;
    readonly Switch originalSwitch = new();
    readonly AdaptiveGrid chips = new();
    readonly Dictionary<string, Chip> chipViews = [];
    readonly Picker<string> visiblePicker = new();

    // Voice-over
    readonly FormSection voiceSection;
    readonly Segmented<VoiceGender> genderPicker = new([(VoiceGender.Female, "Female"), (VoiceGender.Male, "Male")]);
    string voiceSignature = "";
    readonly Picker<string> audiblePicker = new();
    readonly Segmented<OriginalAudio> originalAudio = new([(OriginalAudio.Keep, "Keep"), (OriginalAudio.Lower, "Lower"), (OriginalAudio.Mute, "Mute")]);

    // Download
    readonly FormSection downloadSection;
    readonly TextBlock missingText = Text.Body();
    readonly FormRow missingRow, downloadErrorRow;
    readonly TextBlock downloadErrorText = Text.Caption("", Theme.Red);
    readonly Button downloadButton = new() { Content = "Download" };
    readonly Button cancelDownloadButton = new() { Content = "Cancel" };
    readonly StackPanel downloadingView = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly TextBlock downloadingText = Text.Caption("", Theme.Primary);

    // Options
    readonly Switch replaceSwitch = new(), srtSwitch = new();
    readonly FormRow srtRow;

    // Footer
    readonly Button createButton = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly ProgressBar progressBar = new() { Maximum = 1 };
    readonly TextBlock stepText = Text.Callout(), summaryText = Text.Body(), failedText = Text.Callout();
    readonly Button showBackup = new() { Content = "Show Backup" }, showSrt = new() { Content = "Show .srt Files" };
    readonly StackPanel runningView, doneView;
    readonly Grid idleView, failedView;
    readonly StackPanel doneButtons = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel failedButtons = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel idleButtons = new() { Orientation = Orientation.Horizontal };

    public SubtitlesWindow(SubtitlesModel model)
    {
        this.model = model;
        export = model.Export;
        Title = "Subtitles & Voice-over";

        // Project
        var project = form.Section("Project");
        var refresh = ExportWindow.IconButton(Theme.Refresh, "Re-read the project list");
        refresh.Click += (_, _) =>
        {
            export.RefreshProjects(selectOpen: false);
            model.RefreshProjectState();
        };
        project.Add("CapCut project", ExportWindow.Row(projectPicker, refresh));
        timelineRow = project.Add("Timeline", timelinePicker);
        var openText = Text.Callout("This project is open in CapCut. Close it (back to CapCut's home screen) before creating "
            + "subtitles or voice-over, and open it again afterwards — otherwise CapCut overwrites them when it saves.");
        openText.Foreground = Theme.Orange;
        openRow = project.Add(Text.Label(Theme.Warning, openText, Theme.Orange));
        errorText.Foreground = Theme.Red;
        errorRow = project.Add(errorText);
        loadingRow = project.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
        projectPicker.Picked += folder => export.SelectedProjectFolder = folder;
        timelinePicker.Picked += id => export.SelectedTimelineID = id;

        // Create
        var create = form.Section("Create");
        captionsToggle = new OutputToggle("Subtitles", Theme.Captions, () => model.MakeCaptions = !model.MakeCaptions);
        dubToggle = new OutputToggle("Voice-over", Theme.Waveform, () => model.MakeDub = !model.MakeDub);
        var toggles = new Grid { ColumnSpacing = 8 };
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(dubToggle, 1);
        toggles.Children.Add(captionsToggle);
        toggles.Children.Add(dubToggle);
        create.Add(toggles);
        create.Add("Spoken language", sourcePicker);
        create.Footer = "Speech recognition, translation and voices run on this PC (Whisper, NLLB-200 and Windows voices). "
            + "Voice-over speaks every translation.";
        sourcePicker.Picked += code => model.SourceCode = code;

        // Languages
        var languages = form.Section("Languages");
        originalRow = languages.Add("", originalSwitch);
        originalSwitch.Switched += on => model.IncludeOriginal = on;
        var translateTo = Text.Callout("Translate to:");
        translateTo.Foreground = Theme.Secondary;
        languages.Add(new StackPanel { Spacing = 6, Children = { translateTo, chips } });
        foreach (var language in SubtitleLanguage.All)
        {
            var native = language.NativeName.Equals(language.Name, StringComparison.OrdinalIgnoreCase) ? null : language.NativeName;
            var chip = new Chip(language.Name, native, language.Code, () => model.Toggle(language));
            chipViews[language.Code] = chip;
            chips.Children.Add(chip);
        }
        visibleRow = languages.Add("Shown on screen", visiblePicker);
        ToolTipService.SetToolTip(visiblePicker, "Each language becomes its own caption track. The others are added hidden: "
            + "show them with the track's eye button in CapCut.");
        visiblePicker.Picked += code => model.VisibleLanguage = code;

        // Voice-over
        voiceSection = form.Section("Voice-over");
        genderPicker.Picked += g => model.VoiceGender = g;
        ToolTipService.SetToolTip(audiblePicker, "Each language becomes its own audio track. The others are added muted: "
            + "unmute them with the track's speaker button in CapCut.");
        audiblePicker.Picked += code => model.AudibleVoice = code;
        ToolTipService.SetToolTip(originalAudio, "While the voice-over speaks: leave the project's own sound as it is, lower it by "
            + "15 dB, or silence it. Done with volume keyframes on the clips, which you can adjust in CapCut.");
        originalAudio.Picked += o => model.OriginalAudio = o;
        var openSettings = new HyperlinkButton { Content = "Open", FontSize = 12, Padding = new Thickness(2, 0, 2, 0) };
        openSettings.Click += (_, _) => Platform.OpenUri(WindowsVoices.SettingsUri);
        voiceSection.FooterContent(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { Text.Caption("More voices: Settings ▸ Time & language ▸ Speech ▸ Add voices."), openSettings },
        });

        // Download
        downloadSection = form.Section();
        missingRow = downloadSection.Add(Text.Label(Theme.Download, missingText));
        downloadErrorText.IsTextSelectionEnabled = true;
        downloadErrorRow = downloadSection.Add(downloadErrorText);
        var showModels = new Button { Content = "Show Folder" };
        ToolTipService.SetToolTip(showModels, ModelStore.Root);
        showModels.Click += (_, _) =>
        {
            Directory.CreateDirectory(ModelStore.Root);
            Platform.Reveal(ModelStore.Root);
        };
        var recheck = ExportWindow.IconButton(Theme.Refresh, "Check again");
        recheck.Click += (_, _) =>
        {
            model.RefreshDownloads();
            Update();
        };
        downloadButton.Click += async (_, _) => await model.DownloadMissing();
        cancelDownloadButton.Click += (_, _) => model.CancelDownload();
        downloadingView.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        downloadingView.Children.Add(downloadingText);
        downloadingView.Children.Add(cancelDownloadButton);
        downloadingText.VerticalAlignment = VerticalAlignment.Center;
        var leading = new Grid { Children = { downloadButton, downloadingView } };
        downloadSection.Add(ExportWindow.Spread(leading, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { showModels, recheck } }));

        // Options
        var options = form.Section("Options");
        var replaceRow = options.Add("Replace what was added here before", replaceSwitch);
        ToolTipService.SetToolTip(replaceRow, "Removes the caption and voice-over tracks an earlier run added to this timeline (only the "
            + "kinds created now), and its volume changes. Your own tracks stay.");
        srtRow = options.Add("Also save .srt files to the export folder", srtSwitch);
        options.Footer = $"The project file is backed up before it's changed ({DraftSubtitleWriter.BackupRoot}).";
        replaceSwitch.Switched += on => model.ReplacePrevious = on;
        srtSwitch.Switched += on => model.SaveSrt = on;

        // Footer
        createButton.Click += (_, _) => model.Start();
        createButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Enter });
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => model.Cancel();
        showBackup.Click += (_, _) => { if (model.Phase is SubtitlesPhase.Done d) Platform.Reveal(d.Result.BackupFolder); };
        showSrt.Click += (_, _) => { if (model.Phase is SubtitlesPhase.Done { Srt.Count: > 0 } d) Platform.Reveal(d.Srt[0]); };
        runningView = new StackPanel { Spacing = 6, Children = { progressBar, ExportWindow.Spread(stepText, cancel) } };
        summaryText.MaxLines = 2;
        summaryText.TextTrimming = TextTrimming.CharacterEllipsis;
        doneView = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                ExportWindow.Spread(Text.Label(Theme.CheckFilled, summaryText, Theme.Green), doneButtons),
                Text.Caption("Open the project in CapCut to see the result."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { showBackup, showSrt } },
            },
        };
        failedText.MaxLines = 4;
        failedText.TextTrimming = TextTrimming.CharacterEllipsis;
        failedText.IsTextSelectionEnabled = true;
        failedView = ExportWindow.Spread(Text.Label(Theme.ErrorFilled, failedText, Theme.Red), failedButtons);
        idleView = ExportWindow.Spread(new Grid(), idleButtons);

        var root = new Grid { Background = Theme.Resource("SolidBackgroundFillColorBaseBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(footer, 1);
        root.Children.Add(form);
        root.Children.Add(footer);
        Content = root;

        Platform.SetUpFrame(this, "window.subtitles", 520, 720, 480, 560);
        model.Changed += Update;
        export.Changed += OnExportChanged;
        // Voices added in Settings and a project closed in CapCut show up when the window comes back.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            model.ReloadVoices();
            model.RefreshProjectState();
        };
        Update();
    }

    string? lastProject;

    void OnExportChanged()
    {
        if (export.SelectedProjectFolder != lastProject)
        {
            lastProject = export.SelectedProjectFolder;
            model.RefreshProjectState();
            return;
        }
        Update();
    }

    void Update()
    {
        UpdateProject();
        UpdateCreate();
        UpdateVoices();
        UpdateDownload();
        replaceSwitch.Set(model.ReplacePrevious);
        srtSwitch.Set(model.SaveSrt);
        srtRow.IsShown = model.MakeCaptions;
        UpdateFooter();
        form.IsFormEnabled = !model.IsRunning;
        form.Refresh();
    }

    void UpdateProject()
    {
        projectPicker.Set(export.DisplayedProjects.Select(p => (p.Folder, p.IsOpen ? $"{p.Name}  — open in CapCut" : p.Name)),
            export.SelectedProjectFolder);
        timelinePicker.Set(export.Timelines.Select(t => (t.Id, t.IsMain ? $"{t.Name}  — main" : t.Name)), export.SelectedTimelineID);
        timelineRow.IsShown = export.Timelines.Count > 1;
        openRow.IsShown = model.ProjectIsOpen;
        errorRow.IsShown = !model.ProjectIsOpen && export.LoadError is not null;
        errorText.Text = export.LoadError ?? "";
        loadingRow.IsShown = !model.ProjectIsOpen && export.LoadError is null && export.Timeline is null && export.SelectedProject is not null;
    }

    void UpdateCreate()
    {
        captionsToggle.IsOn = model.MakeCaptions;
        dubToggle.IsOn = model.MakeDub;
        sourcePicker.Set(model.SourceLanguages.Select(l => (l.Code, l.Name)), model.SourceCode);

        originalRow.IsShown = model.MakeCaptions;
        originalRow.Label!.Text = $"Original subtitles ({model.SourceLanguage.Name})";
        originalSwitch.Set(model.IncludeOriginal);
        foreach (var language in SubtitleLanguage.All)
        {
            var chip = chipViews[language.Code];
            chip.Visibility = language.Code == model.SourceLanguage.Code ? Visibility.Collapsed : Visibility.Visible;
            chip.IsOn = model.IsTargetSelected(language);
        }
        chips.InvalidateMeasure();
        var outputs = model.OutputLanguages;
        visibleRow.IsShown = outputs.Count > 1;
        visiblePicker.Set(outputs.Select(l => (l.Code, l.Name)), model.EffectiveVisibleLanguage ?? "");
    }

    void UpdateVoices()
    {
        var dub = model.DubLanguages;
        voiceSection.IsShown = dub.Count > 0;
        genderPicker.Select(model.VoiceGender);
        originalAudio.Select(model.OriginalAudio);
        audiblePicker.Set(dub.Select(l => (l.Code, l.Name)), model.EffectiveAudibleVoice ?? "");

        // The per-language rows depend on the installed voices: the section is rebuilt only when what they show changes.
        var signature = string.Join("\n", dub.Select(l => $"{l.Code}|{model.VoiceGender}|{model.VoiceID(l)}|{model.VoiceHelpLanguage}|"
            + string.Join(",", model.Voices(l).Select(v => v.Id))));
        if (signature == voiceSignature) return;
        voiceSignature = signature;
        voiceSection.Clear();
        voiceSection.Add("Voice", genderPicker);
        foreach (var language in dub) AddVoiceRows(language);
        if (dub.Count > 1) voiceSection.Add("Heard", audiblePicker);
        voiceSection.Add("Original audio", originalAudio);
    }

    void AddVoiceRows(SubtitleLanguage language)
    {
        var voices = model.PickerVoices(language);
        if (voices.Count == 0)
        {
            var none = Text.Body("No voice installed");
            none.Foreground = Theme.Orange;
            var get = new Button { Content = "Get Voices…", FontSize = 12, Padding = new Thickness(8, 2, 8, 3) };
            get.Click += (_, _) => model.GetVoices(language);
            voiceSection.Add(language.Name, ExportWindow.Row(none, get));
        }
        else
        {
            var picker = new Picker<string>();
            // When the chosen gender isn't available, say which gender this voice is.
            var lacks = model.LacksGender(language);
            picker.Set(voices.Select(v => (v.Id, lacks ? $"{v.Label} · {v.Gender?.ToString().ToLowerInvariant() ?? "voice"}" : v.Label)),
                model.VoiceID(language));
            picker.Picked += id => model.SetVoice(id, language);
            var preview = new Button
            {
                Content = Theme.Icon(Theme.Speaker, 14), Padding = new Thickness(6, 4, 6, 4),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(preview, "Preview voice");
            preview.Click += (_, _) => model.Preview(language);
            voiceSection.Add(language.Name, ExportWindow.Row(picker, preview));
        }
        if (model.LacksGender(language))
        {
            var other = voices.FirstOrDefault()?.Gender?.ToString().ToLowerInvariant() ?? "another";
            var gender = model.VoiceGender.ToString().ToLowerInvariant();
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(Text.Caption($"No {gender} {language.Name} voice is installed — the {other} voice will speak.", Theme.Orange));
            if (model.VoiceHelpLanguage == language.Code)
            {
                panel.Children.Add(Text.Caption($"In Settings ▸ Time & language ▸ Speech, choose Add voices and look for a {gender} "
                    + $"{language.Name} voice. Many languages have none."));
            }
            else
            {
                var more = new HyperlinkButton { Content = "Look for more voices…", FontSize = 12, Padding = new Thickness(0) };
                more.Click += (_, _) => model.GetVoices(language);
                panel.Children.Add(more);
            }
            voiceSection.Add(panel);
        }
    }

    void UpdateDownload()
    {
        var missing = model.NotDownloaded;
        downloadSection.IsShown = missing.Count > 0 || model.DownloadError is not null;
        missingRow.IsShown = missing.Count > 0;
        missingText.Text = "One-time download needed for offline recognition and translation: " + model.MissingModelsText;
        downloadErrorRow.IsShown = model.DownloadError is not null;
        downloadErrorText.Text = model.DownloadError ?? "";
        var downloading = model.DownloadProgress is { } p;
        downloadingView.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        downloadingText.Text = $"Downloading… {(int)((model.DownloadProgress ?? 0) * 100)}%";
        downloadButton.Visibility = !downloading && missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateFooter()
    {
        createButton.Content = CreateTitle();
        createButton.IsEnabled = model.CanStart && !model.ProjectIsOpen;
        switch (model.Phase)
        {
            case SubtitlesPhase.Running(var step, var fraction):
                progressBar.Value = fraction;
                stepText.Text = step;
                Show(runningView, null);
                break;
            case SubtitlesPhase.Done(_, var summary, var srt):
                summaryText.Text = summary;
                showSrt.Visibility = srt.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                Show(doneView, doneButtons);
                break;
            case SubtitlesPhase.Failed(var message):
                failedText.Text = message;
                Show(failedView, failedButtons);
                break;
            default:
                Show(idleView, idleButtons);
                break;
        }
    }

    Panel? buttonHost;

    void Show(UIElement view, Panel? buttons)
    {
        // Parent isn't set before the window is shown: the button's place is tracked here.
        if (buttons is not null && buttonHost != buttons)
        {
            buttonHost?.Children.Remove(createButton);
            buttons.Children.Add(createButton);
            buttonHost = buttons;
        }
        if (footer.Content != view) footer.Content = view;
    }

    string CreateTitle()
    {
        if (!model.MakeCaptions && !model.MakeDub) return "Choose Subtitles or Voice-over";
        var what = new[] { model.MakeCaptions ? "Subtitles" : null, model.MakeDub ? "Voice-over" : null }.OfType<string>();
        var codes = model.OutputLanguages.Concat(model.DubLanguages).Select(l => l.Code).Distinct().ToList();
        if (codes.Count == 0 || (model.MakeDub && model.DubLanguages.Count == 0 && !model.MakeCaptions)) return "Choose a Language";
        return $"Create {string.Join(" & ", what)} ({string.Join(", ", codes)})";
    }
}
