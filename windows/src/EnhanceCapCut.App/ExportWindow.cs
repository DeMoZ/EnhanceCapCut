using System.Globalization;
using EnhanceCapCut.Draft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static EnhanceCapCut.Draft.Localization;

namespace EnhanceCapCut.App;

/// <summary>"Export with EnhanceCapCut": the counterpart of the macOS version's <c>ExportView</c>, section for section.</summary>
sealed class ExportWindow : Window
{
    readonly ExportModel model;
    readonly FormView form = new();
    readonly FooterBar footer = new();
    bool updating;

    // Project
    readonly Picker<string> projectPicker = new();
    readonly ToggleButton sortToggle = new() { Content = new TextBlock { Text = "abc", FontSize = 13 }, Padding = new Thickness(8, 4, 8, 6) };
    readonly Picker<string> timelinePicker = new();
    readonly FormRow timelineRow, canvasRow, errorRow, loadingRow, emptyRow;
    readonly TextBlock canvasText = Value(), errorText = Text.Body(), emptyText = Text.Body();
    readonly Button subtitlesButton = new() { Content = L("Subtitles & Voice-over…") };

    // Not rendered yet
    readonly FormSection unsupported;
    string unsupportedSignature = "";

    // Video
    readonly Picker<ExportSettings.AspectRatio> aspectPicker = new();
    readonly Picker<ExportSettings.Resolution> resolutionPicker = new();
    readonly FormRow outputSizeRow, rateControlRow, qualityRow, bitrateRow, burnInRow;
    readonly TextBlock outputSizeText = Value();
    readonly Picker<double> frameRatePicker = new();
    readonly Picker<ExportSettings.VideoCodec> codecPicker = new();
    readonly Segmented<ExportSettings.RateControlMode> rateControl = new([
        (ExportSettings.RateControlMode.Quality, L("Constant quality")), (ExportSettings.RateControlMode.Bitrate, L("Bitrate"))]);
    readonly Slider qualitySlider = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock qualityText = new() { Width = 32, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly NumberBox bitrateBox = new() { Width = 70, Minimum = 1, Maximum = 400 };
    readonly Switch burnInSwitch = new();

    // Audio
    readonly Switch normalizeSwitch = new();
    readonly FormRow targetRow;
    readonly NumberBox lufsBox = new() { Width = 60, Minimum = -40, Maximum = -5 };

    // Output
    readonly TextBlock folderText = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 280 };
    readonly TextBox fileNameBox = new() { TextAlignment = TextAlignment.Right };
    readonly FormRow saveAsRow;
    readonly TextBlock saveAsText = Value();

    // Footer
    readonly Button exportButton = new() { Content = "Export", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly DonateButton donateButton = new();
    readonly ProgressBar progressBar = new() { Maximum = 1 };
    readonly TextBlock progressText = Text.Callout();
    readonly StackPanel warningsPanel = new() { Spacing = 2 };
    readonly TextBlock finishedText = Text.Line(), failedText = Text.Callout();
    readonly Button revealButton = new() { Content = L("Show in Explorer") };
    readonly StackPanel exportingView, finishedView;
    readonly Grid idleView, failedView;
    // "🌐 Language · How to Use · Version 0.6.0 · Check for Updates…" (as on the Mac): the version and the update found /
    // its download only in a release build.
    readonly HyperlinkButton languageLink = new() { FontSize = 12, Padding = new Thickness(2, 0, 2, 0) };
    readonly HyperlinkButton guideLink = new() { Content = L("How to Use"), FontSize = 12, Padding = new Thickness(2, 0, 2, 0) };
    readonly StackPanel footerLinks = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel updatesView = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock versionText = Text.Caption();
    readonly HyperlinkButton updatesLink = new() { FontSize = 12, Padding = new Thickness(2, 0, 2, 0) };
    readonly StackPanel finishedButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly StackPanel failedButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly StackPanel idleButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

    public ExportWindow(ExportModel model)
    {
        this.model = model;
        Title = L("Export with EnhanceCapCut");

        // Project
        var project = form.Section(L("Project"));
        var refresh = IconButton(Theme.Refresh, L("Re-detect the project open in CapCut"));
        refresh.Click += (_, _) => model.RefreshProjects(selectOpen: true);
        project.Add(L("CapCut project"), Row(projectPicker, sortToggle, refresh));
        timelineRow = project.Add(L("Timeline"), timelinePicker);
        canvasRow = project.Add(L("Canvas"), canvasText);
        errorText.Foreground = Theme.Red;
        errorRow = project.Add(errorText);
        loadingRow = project.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
        emptyText.Foreground = Theme.Secondary;
        emptyText.Text = L("No CapCut projects found in {0}.", ProjectLocator.DefaultProjectsRoot);
        emptyRow = project.Add(emptyText);
        project.Add(L("Subtitles"), subtitlesButton);
        ToolTipService.SetToolTip(subtitlesButton, L("Transcribe and translate the speech and add it to the project as captions"));
        projectPicker.Picked += folder => model.SelectedProjectFolder = folder;
        timelinePicker.Picked += id => model.SelectedTimelineID = id;
        sortToggle.Click += (_, _) => { if (!updating) model.SortProjectsByName = sortToggle.IsChecked == true; };
        subtitlesButton.Click += (_, _) => model.OpenSubtitles?.Invoke();

        unsupported = form.Section();
        unsupported.Header = Text.Label(Theme.Warning, Text.Headline(L("Not rendered yet")));
        unsupported.Footer = L("These CapCut features are in the project but this version doesn't reproduce them; "
            + "the export will look different where they're used.");

        // Video
        var video = form.Section(L("Video"));
        video.Add(L("Aspect ratio"), aspectPicker);
        video.Add(L("Resolution"), resolutionPicker);
        outputSizeRow = video.Add(L("Output size"), outputSizeText);
        video.Add(L("Frame rate"), frameRatePicker);
        video.Add(L("Codec"), codecPicker);
        rateControlRow = video.Add(L("Rate control"), rateControl);
        qualityRow = video.Add(L("Quality"), Row(qualitySlider, qualityText, stretchFirst: true), stretch: true);
        bitrateRow = video.Add(L("Bitrate"), Row(bitrateBox, Text.Body(L("Mbit/s"))));
        burnInRow = video.Add(L("Burn in captions (auto-subtitles)"), burnInSwitch);
        aspectPicker.Set(Enum.GetValues<ExportSettings.AspectRatio>().Select(a => (a, ExportSettings.Title(a))), model.Settings.Aspect);
        resolutionPicker.Set(Enum.GetValues<ExportSettings.Resolution>().Select(r => (r, ExportSettings.Title(r))), model.Settings.OutputResolution);
        codecPicker.Set(Enum.GetValues<ExportSettings.VideoCodec>().Select(c => (c, ExportSettings.Title(c))), model.Settings.Codec);
        aspectPicker.Picked += v => model.Update(s => s.Aspect = v);
        resolutionPicker.Picked += v => model.Update(s => s.OutputResolution = v);
        frameRatePicker.Picked += v => model.Update(s => s.FrameRate = v);
        codecPicker.Picked += v => model.Update(s => s.Codec = v);
        rateControl.Picked += v => model.Update(s => s.RateControl = v);
        qualitySlider.ValueChanged += (_, e) => { if (!updating) model.Update(s => s.Quality = Math.Round(e.NewValue)); };
        bitrateBox.ValueChanged += (_, e) => { if (!updating && !double.IsNaN(e.NewValue)) model.Update(s => s.BitrateMbps = e.NewValue); };
        burnInSwitch.Switched += on => model.Update(s => s.BurnInSubtitles = on);

        // Audio
        var audio = form.Section(L("Audio"));
        audio.Add(L("Normalize loudness"), normalizeSwitch);
        var stepper = new Stepper(() => ChangeLUFS(1), () => ChangeLUFS(-1));
        targetRow = audio.Add("Target", Row(lufsBox, Text.Body("LUFS"), stepper));
        normalizeSwitch.Switched += on => model.Update(s => s.NormalizeLoudness = on);
        lufsBox.ValueChanged += (_, e) => { if (!updating && !double.IsNaN(e.NewValue)) model.Update(s => s.TargetLUFS = e.NewValue); };

        // Output
        var output = form.Section(L("Output"));
        var choose = new Button { Content = L("Choose…") };
        choose.Click += async (_, _) => await ChooseFolder();
        output.Add(L("Folder"), Row(folderText, choose));
        output.Add(L("File name"), fileNameBox, stretch: true);
        output.Add(Text.Caption(L("Tokens: {project} {date} {time} {res} {codec} {fps}")));
        saveAsRow = output.Add(L("Will save as"), saveAsText);
        fileNameBox.TextChanged += (_, _) => { if (!updating) model.Update(s => s.FileNameTemplate = fileNameBox.Text); };

        // Footer
        exportButton.Click += async (_, _) => await Export();
        exportButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Enter });
        var cancel = new Button { Content = L("Cancel") };
        cancel.Click += (_, _) => model.Cancel();
        revealButton.Click += (_, _) => { if (model.State is ExportState.Finished f) Platform.Reveal(f.Path); };
        exportingView = new StackPanel { Spacing = 6, Children = { progressBar, Spread(progressText, cancel) } };
        finishedButtons.Children.Add(revealButton);
        finishedView = new StackPanel
        {
            Spacing = 6,
            Children = { Spread(Text.Label(Theme.CheckFilled, finishedText, Theme.Green), finishedButtons), warningsPanel },
        };
        failedText.MaxLines = 3;
        failedText.TextTrimming = TextTrimming.CharacterEllipsis;
        failedView = Spread(Text.Label(Theme.ErrorFilled, failedText, Theme.Red), failedButtons);
        updatesView.Children.Add(versionText);
        updatesView.Children.Add(updatesLink);
        updatesLink.Click += async (_, _) => { if (model.Updates is { } u) await u.Check(); };
        donateButton.Click += (_, _) => model.OpenDonate?.Invoke();
        languageLink.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { Theme.Icon(Theme.Globe, 12), new TextBlock { Text = Localization.Current.NativeName() } },
        };
        ToolTipService.SetToolTip(languageLink, L("Interface language"));
        var languageMenu = new MenuFlyout();
        var systemItem = new MenuFlyoutItem { Text = L("System Language") };
        systemItem.Click += (_, _) => model.SetLanguage?.Invoke(null);
        languageMenu.Items.Add(systemItem);
        languageMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var language in Enum.GetValues<Localization.Language>())
        {
            var item = new MenuFlyoutItem { Text = language.NativeName() };
            item.Click += (_, _) => model.SetLanguage?.Invoke(language);
            languageMenu.Items.Add(item);
        }
        languageLink.Click += (_, _) => languageMenu.ShowAt(languageLink);
        guideLink.Click += (_, _) => model.OpenGuide?.Invoke();
        footerLinks.Children.Add(languageLink);
        footerLinks.Children.Add(Text.Caption("·"));
        footerLinks.Children.Add(guideLink);
        footerLinks.Children.Add(updatesView);
        idleView = Spread(footerLinks, idleButtons);

        var root = new Grid { Background = Theme.Resource("SolidBackgroundFillColorBaseBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(footer, 1);
        root.Children.Add(form);
        root.Children.Add(footer);
        Content = root;

        Platform.SetUpFrame(this, "window.export", 560, 760, 520, 600);
        model.Changed += Update;
        Closed += (_, _) => model.Changed -= Update;
        Update();
    }

    void ChangeLUFS(int step) =>
        model.Update(s => s.TargetLUFS = Math.Clamp(Math.Round(s.TargetLUFS) + step, -40, -5));

    async Task ChooseFolder()
    {
        var picker = new global::Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Platform.Handle(this));
        if (await picker.PickSingleFolderAsync() is { } folder) model.Update(s => s.OutputFolder = folder.Path);
    }

    async Task Export()
    {
        if (model.Timeline is null || model.OutputPath is not { } path || model.IsExporting) return;
        if (File.Exists(path) && !await Platform.Ask(this, L("{0} already exists", Path.GetFileName(path)), L("Replace it?"), L("Replace"), L("Cancel")))
            return;
        model.StartExport();
    }

    void Update()
    {
        updating = true;
        try
        {
            UpdateProject();
            UpdateUnsupported();
            UpdateSettings();
            UpdateFooter();
            form.IsFormEnabled = !model.IsExporting;
            form.Refresh();
        }
        finally
        {
            updating = false;
        }
    }

    void UpdateProject()
    {
        projectPicker.Set(model.DisplayedProjects.Select(p => (p.Folder, p.IsOpen ? L("{0}  — open in CapCut", p.Name) : p.Name)),
            model.SelectedProjectFolder);
        sortToggle.IsChecked = model.SortProjectsByName;
        ToolTipService.SetToolTip(sortToggle, model.SortProjectsByName ? L("Sorted by name. Click for CapCut's order")
            : L("In CapCut's order. Click to sort by name"));
        timelinePicker.Set(model.Timelines.Select(t => (t.Id, t.IsMain ? L("{0}  — main", t.Name) : t.Name)), model.SelectedTimelineID);
        timelineRow.IsShown = model.Timelines.Count > 1;

        var timeline = model.Timeline;
        canvasRow.IsShown = timeline is not null;
        if (timeline is not null)
            canvasText.Text = L("{0}×{1}, {2} fps, {3}", timeline.CanvasWidth, timeline.CanvasHeight, Format.Fps(timeline.Fps),
                Format.Duration(timeline.Duration));
        errorRow.IsShown = timeline is null && model.LoadError is not null;
        errorText.Text = model.LoadError ?? "";
        loadingRow.IsShown = timeline is null && model.LoadError is null && model.SelectedProject is not null;
        emptyRow.IsShown = timeline is null && model.LoadError is null && model.SelectedProject is null;
        subtitlesButton.IsEnabled = model.SelectedProject is not null;
    }

    void UpdateUnsupported()
    {
        var report = model.Timeline?.Report;
        unsupported.IsShown = report is { IsEmpty: false };
        var signature = report is null ? "" : string.Join("\n", report.Unsupported.Select(i => i.Id + i.Count).Concat(report.MissingFiles));
        if (signature == unsupportedSignature) return;
        unsupportedSignature = signature;
        unsupported.Clear();
        if (report is null) return;
        var groups = report.Unsupported.GroupBy(i => i.Category).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var items = group.OrderByDescending(i => i.Count).Select(i => i.Count > 1 ? $"{i.Name} ×{i.Count}" : i.Name);
            var text = Text.Callout(string.Join(", ", items));
            text.Foreground = Theme.Secondary;
            unsupported.Add(new StackPanel { Spacing = 2, Children = { Text.Headline(group.Key), text } });
        }
        if (report.MissingFiles.Count > 0)
        {
            var title = Text.Headline(L("Missing media (rendered empty)"));
            title.Foreground = Theme.Red;
            var panel = new StackPanel { Spacing = 2, Children = { title } };
            foreach (var file in report.MissingFiles.Take(12))
                panel.Children.Add(new TextBlock { Text = Format.MiddleEllipsis(file, 70), FontSize = 12 });
            if (report.MissingFiles.Count > 12) panel.Children.Add(Text.Caption(L("…and {0} more", report.MissingFiles.Count - 12)));
            unsupported.Add(panel);
        }
    }

    void UpdateSettings()
    {
        var s = model.Settings;
        aspectPicker.Set(Enum.GetValues<ExportSettings.AspectRatio>().Select(a => (a, ExportSettings.Title(a))), s.Aspect);
        resolutionPicker.Set(Enum.GetValues<ExportSettings.Resolution>().Select(r => (r, ExportSettings.Title(r))), s.OutputResolution);
        outputSizeRow.IsShown = model.OutputSize is not null;
        if (model.OutputSize is { } size) outputSizeText.Text = $"{size.Width}×{size.Height}";
        var fromProject = model.Timeline is { } t ? L("From project ({0})", Format.Fps(t.Fps)) : L("From project");
        frameRatePicker.Set(new[] { (0.0, fromProject) }.Concat(new[] { 24.0, 25, 30, 60 }.Select(f => (f, Format.Fps(f)))), s.FrameRate);
        codecPicker.Set(Enum.GetValues<ExportSettings.VideoCodec>().Select(c => (c, ExportSettings.Title(c))), s.Codec);
        rateControl.Select(s.RateControl);
        qualityRow.IsShown = s.RateControl == ExportSettings.RateControlMode.Quality;
        bitrateRow.IsShown = s.RateControl == ExportSettings.RateControlMode.Bitrate;
        qualitySlider.Value = s.Quality;
        qualityText.Text = ((int)s.Quality).ToString(CultureInfo.InvariantCulture);
        if (bitrateBox.Value != s.BitrateMbps) bitrateBox.Value = s.BitrateMbps;
        burnInRow.IsShown = model.Timeline?.HasSubtitles == true;
        burnInSwitch.Set(s.BurnInSubtitles);

        normalizeSwitch.Set(s.NormalizeLoudness);
        targetRow.IsShown = s.NormalizeLoudness;
        if (lufsBox.Value != s.TargetLUFS) lufsBox.Value = s.TargetLUFS;

        folderText.Text = Format.MiddleEllipsis(Format.Home(s.OutputFolder), 48);
        ToolTipService.SetToolTip(folderText, s.OutputFolder);
        if (fileNameBox.Text != s.FileNameTemplate) fileNameBox.Text = s.FileNameTemplate;
        saveAsRow.IsShown = model.OutputPath is not null;
        saveAsText.Text = model.OutputPath is { } path ? Path.GetFileName(path) : "";
    }

    void UpdateFooter()
    {
        exportButton.IsEnabled = model.Timeline is not null && model.OutputPath is not null;
        UpdateUpdatesLink();
        switch (model.State)
        {
            case ExportState.Exporting(var progress):
                progressBar.Value = progress?.Fraction ?? 0;
                progressText.Text = ProgressText(progress);
                Show(exportingView, null);
                break;
            case ExportState.Finished(var path, var warnings, var elapsed):
                finishedText.Text = L("Exported {0} in {1}", Path.GetFileName(path), Format.Duration((long)(elapsed.TotalSeconds * 1_000_000)));
                warningsPanel.Children.Clear();
                foreach (var warning in warnings) warningsPanel.Children.Add(Text.Caption(warning, Theme.Orange));
                Show(finishedView, finishedButtons);
                break;
            case ExportState.Failed(var message):
                failedText.Text = message;
                Show(failedView, failedButtons);
                break;
            default:
                Show(idleView, idleButtons);
                break;
        }
    }

    void UpdateUpdatesLink()
    {
        var updater = model.Updates;
        updatesView.Visibility = updater is not null && Updater.Current is not null ? Visibility.Visible : Visibility.Collapsed;
        if (updater is null || Updater.Current is not { } current) return;
        versionText.Text = "· " + L("Version {0}", current) + " ·";
        if (updater.Progress is { } p)
        {
            updatesLink.Content = L("Downloading the update… {0}", p.ToString("P0", CultureInfo.CurrentCulture));
            updatesLink.IsEnabled = false;
        }
        else
        {
            updatesLink.Content = updater.Available is { } release ? L("Update to {0}…", release.Version) : L("Check for Updates…");
            updatesLink.IsEnabled = true;
        }
    }

    Panel? buttonHost;

    /// <summary>Puts <paramref name="view"/> in the footer, with the Donate and Export buttons (there's one Export button, for
    /// its Enter key) in <paramref name="buttons"/>.</summary>
    void Show(UIElement view, Panel? buttons)
    {
        // Parent isn't set before the window is shown: the button's place is tracked here.
        if (buttons is not null && buttonHost != buttons)
        {
            buttonHost?.Children.Remove(donateButton);
            buttonHost?.Children.Remove(exportButton);
            buttons.Children.Add(donateButton);
            buttons.Children.Add(exportButton);
            buttonHost = buttons;
        }
        if (footer.Content != view) footer.Content = view;
    }

    static string ProgressText(EnhanceCapCut.Windows.ExportProgress? p)
    {
        if (p is null) return L("Preparing…");
        var text = p.PhaseTitle;
        if (p.Phase == EnhanceCapCut.Windows.ExportProgress.Phases.Video) text += L(" — frame {0} of {1}", p.FramesDone, p.FramesTotal);
        if (p.Fraction > 0.12)
        {
            var remaining = p.Elapsed.TotalSeconds / p.Fraction * (1 - p.Fraction);
            text += L(", about {0} left", Format.Duration((long)(remaining * 1_000_000)));
        }
        return text;
    }

    // MARK: Layout helpers

    static TextBlock Value() => new() { Foreground = Theme.Secondary, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    internal static Button IconButton(string glyph, string tooltip)
    {
        var button = new Button { Content = Theme.Icon(glyph, 14), Padding = new Thickness(8, 7, 8, 7) };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>Controls side by side (SwiftUI's <c>HStack</c>); with <paramref name="stretchFirst"/> the first one takes the rest.</summary>
    internal static Grid Row(UIElement first, params UIElement[] rest) => Row(first, false, rest);

    internal static Grid Row(UIElement first, UIElement second, bool stretchFirst) => Row(first, stretchFirst, [second]);

    static Grid Row(UIElement first, bool stretchFirst, UIElement[] rest)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        var all = new[] { first }.Concat(rest).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 && stretchFirst ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            if (all[i] is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn((FrameworkElement)all[i], i);
            grid.Children.Add(all[i]);
        }
        return grid;
    }

    /// <summary><paramref name="leading"/> on the left, <paramref name="trailing"/> on the right (an HStack with a Spacer).</summary>
    internal static Grid Spread(UIElement leading, UIElement trailing)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (leading is FrameworkElement l) l.VerticalAlignment = VerticalAlignment.Center;
        if (trailing is FrameworkElement t) t.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn((FrameworkElement)trailing, 1);
        grid.Children.Add(leading);
        grid.Children.Add(trailing);
        return grid;
    }
}

/// <summary>Number and path formatting shared by the windows (as in the macOS version).</summary>
static class Format
{
    public static string Fps(double fps) => fps == Math.Round(fps)
        ? ((int)fps).ToString(CultureInfo.InvariantCulture) : fps.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Duration(long us)
    {
        var s = (int)Math.Round(us / 1_000_000.0, MidpointRounding.AwayFromZero);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>The user's folder as "~", like the macOS window shows paths.</summary>
    public static string Home(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    /// <summary>Shortens a long path in the middle ("C:\Users\…\Exports"), like SwiftUI's <c>.truncationMode(.middle)</c>.</summary>
    public static string MiddleEllipsis(string text, int max)
    {
        if (text.Length <= max) return text;
        var head = (max - 1) / 2;
        return text[..head] + "…" + text[^(max - 1 - head)..];
    }
}
