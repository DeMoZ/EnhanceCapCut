using System.Diagnostics;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Windows;
using Microsoft.UI.Dispatching;
using Timeline = EnhanceCapCut.Draft.Timeline;

namespace EnhanceCapCut.App;

abstract record ExportState
{
    public sealed record Idle : ExportState;
    public sealed record Exporting(ExportProgress? Progress) : ExportState;
    public sealed record Finished(string Path, IReadOnlyList<string> Warnings, TimeSpan Elapsed) : ExportState;
    public sealed record Failed(string Message) : ExportState;
}

/// <summary>
/// State of the export window: the CapCut projects, the selected project and timeline, the export settings and the
/// running export. The counterpart of the macOS version's <c>ExportModel</c>; the subtitles window works on the same
/// selection. <see cref="Changed"/> fires on the UI thread after every change.
/// </summary>
sealed class ExportModel(DispatcherQueue ui)
{
    public event Action? Changed;

    /// <summary>Projects in CapCut's order: most recently modified first.</summary>
    public List<CapCutProject> Projects { get; private set; } = [];

    /// <summary>Show the project list by name instead of CapCut's order. Only affects the list, not the default selection.</summary>
    public bool SortProjectsByName
    {
        get => sortProjectsByName;
        set
        {
            sortProjectsByName = value;
            Preferences.Set("sortProjectsByName", value);
            Notify();
        }
    }
    bool sortProjectsByName = Preferences.Bool("sortProjectsByName");

    public string? SelectedProjectFolder
    {
        get => selectedProjectFolder;
        set
        {
            if (selectedProjectFolder == value) return;
            selectedProjectFolder = value;
            ReloadTimelines(keepSelection: false);
        }
    }
    string? selectedProjectFolder;

    /// <summary>Timelines of the selected project (CapCut 9 projects can have several).</summary>
    public List<ProjectTimeline> Timelines { get; private set; } = [];

    public string? SelectedTimelineID
    {
        get => selectedTimelineID;
        set
        {
            if (selectedTimelineID == value) return;
            selectedTimelineID = value;
            LoadTimeline();
        }
    }
    string? selectedTimelineID;

    public Timeline? Timeline { get; private set; }
    public string? LoadError { get; private set; }
    public ExportSettings Settings { get; } = ExportSettings.Load();
    public ExportState State { get; private set; } = new ExportState.Idle();

    /// <summary>Opens the subtitles window (set by the app).</summary>
    public Action? OpenSubtitles { get; set; }
    /// <summary>The export finished while the window wasn't in front (set by the app).</summary>
    public Action? Finished { get; set; }

    /// <summary>Exports right away once the timeline is loaded (<c>--auto-export</c>, for scripting).</summary>
    public bool AutoExport { get; set; }

    CancellationTokenSource? cancel;
    bool autoExported;

    public IEnumerable<CapCutProject> DisplayedProjects =>
        SortProjectsByName ? Projects.OrderBy(p => p.Name, Comparer<string>.Create(Platform.LogicalCompare)) : Projects;

    public CapCutProject? SelectedProject => Projects.FirstOrDefault(p => p.Folder == SelectedProjectFolder);
    public ProjectTimeline? SelectedTimeline => Timelines.FirstOrDefault(t => t.Id == SelectedTimelineID);
    public bool IsExporting => State is ExportState.Exporting;

    public (int Width, int Height)? OutputSize => Timeline is { } t ? Settings.OutputSize(t.CanvasWidth, t.CanvasHeight) : null;
    public double? OutputFps => Timeline is { } t ? Settings.Fps(t.Fps) : null;

    public string? OutputPath => SelectedProject is { } project && OutputSize is { } size && OutputFps is { } fps
        ? Path.Combine(Settings.OutputFolder, Settings.FileName(OutputName(project), size.Width, size.Height, fps))
        : null;

    public void Notify() => Changed?.Invoke();

    /// <summary>Changes the settings, saves them and redraws.</summary>
    public void Update(Action<ExportSettings> change)
    {
        change(Settings);
        try { Settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Notify();
    }

    public void RefreshProjects(bool selectOpen)
    {
        var previous = SelectedProjectFolder;
        Projects = ProjectLocator.AllProjects();
        string? next;
        if (selectOpen && ProjectLocator.OpenProject() is { } open) next = open.Folder;
        else if (previous is not null && Projects.Any(p => p.Folder == previous)) next = previous;
        // Nothing open in CapCut: the most recently edited project (first in CapCut's order).
        else next = Projects.FirstOrDefault()?.Folder;
        SelectedProjectFolder = next;
        // Reload even when the selection didn't change: the draft or its timelines may have been edited.
        if (next == previous) ReloadTimelines(keepSelection: true);
        if (State is ExportState.Finished or ExportState.Failed) State = new ExportState.Idle();
        Notify();
    }

    /// <summary>Re-reads the project's timeline list. A newly selected project starts on its main timeline.</summary>
    void ReloadTimelines(bool keepSelection)
    {
        Timelines = SelectedProject?.Timelines() ?? [];
        var id = keepSelection && SelectedTimeline is not null ? SelectedTimelineID
            : (Timelines.FirstOrDefault(t => t.IsMain) ?? Timelines.FirstOrDefault())?.Id;
        if (id == selectedTimelineID) LoadTimeline();
        else SelectedTimelineID = id;
    }

    /// <summary>The <c>{project}</c> token: the project name, plus the timeline name for timelines other than the main one.</summary>
    string OutputName(CapCutProject project) =>
        Timelines.Count > 1 && SelectedTimeline is { IsMain: false } timeline ? $"{project.Name} {timeline.Name}" : project.Name;

    public void LoadTimeline()
    {
        Timeline = null;
        LoadError = null;
        Notify();
        if (SelectedProject is not { } project || SelectedTimeline is not { } projectTimeline) return;
        Task.Run(() =>
        {
            Timeline? timeline = null;
            string? error = null;
            try { timeline = TimelineBuilder.Build(project, projectTimeline); }
            catch (Exception e) { error = e.Message; }
            ui.TryEnqueue(() =>
            {
                if (SelectedProjectFolder != project.Folder || SelectedTimelineID != projectTimeline.Id) return;
                Timeline = timeline;
                LoadError = error;
                Notify();
                if (timeline is not null && AutoExport && !autoExported)
                {
                    autoExported = true;
                    StartExport();
                }
            });
        });
    }

    /// <summary>Starts the export to <see cref="OutputPath"/>; the window has already asked about replacing a file.</summary>
    public void StartExport()
    {
        if (Timeline is not { } timeline || OutputPath is not { } path || IsExporting) return;
        var exporter = new Exporter(timeline, Settings, path);
        var source = new CancellationTokenSource();
        cancel = source;
        State = new ExportState.Exporting(null);
        Notify();
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        Task.Run(() =>
        {
            try
            {
                exporter.Run(progress =>
                {
                    // About ten redraws a second are enough for the progress bar.
                    if (progress.Elapsed - lastReport < TimeSpan.FromMilliseconds(100) && progress.Fraction < 1) return;
                    lastReport = progress.Elapsed;
                    ui.TryEnqueue(() =>
                    {
                        if (State is not ExportState.Exporting) return;
                        State = new ExportState.Exporting(progress);
                        Notify();
                    });
                }, source.Token);
                var warnings = exporter.Warnings.ToList();
                ui.TryEnqueue(() =>
                {
                    State = new ExportState.Finished(path, warnings, clock.Elapsed);
                    Notify();
                    Finished?.Invoke();
                });
            }
            catch (OperationCanceledException)
            {
                // The exporter removes the unfinished file.
                ui.TryEnqueue(() =>
                {
                    State = new ExportState.Idle();
                    Notify();
                });
            }
            catch (Exception e)
            {
                ui.TryEnqueue(() =>
                {
                    State = new ExportState.Failed(e.Message);
                    Notify();
                });
            }
        });
    }

    public void Cancel() => cancel?.Cancel();
}
