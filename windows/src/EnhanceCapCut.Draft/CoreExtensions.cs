using System.Diagnostics;

namespace EnhanceCapCut.Core;

// C# conveniences on the Rust core's generated types (Generated/ecc_core.cs,
// openspec/changes/archive/2026-11-26-rust-core stage 4), the counterpart of the macOS CoreExtensions.swift: this
// machine's folders, project lookup, dates, small predicates the renderers ask. No core logic here — that lives in
// core/ and is reached through the generated functions.

/// <summary>Extension members of the generated records and objects.</summary>
public static class CoreExtensions
{
    extension(CoreEnvironment)
    {
        /// <summary>The folders the app and the CLI use: this machine's defaults (<c>ECC_PROJECTS</c> overrides CapCut's
        /// projects folder, for the tests and the CLI).</summary>
        public static CoreEnvironment Current
        {
            get
            {
                var env = EccCore.DefaultEnvironment();
                return Environment.GetEnvironmentVariable("ECC_PROJECTS") is { Length: > 0 } root ? env with { ProjectsRoot = root } : env;
            }
        }
    }

    extension(CapCutProject project)
    {
        /// <summary>When the draft (or the folder) last changed, UTC.</summary>
        public DateTime Modified => DateTimeOffset.FromUnixTimeMilliseconds(project.ModifiedMs ?? 0).UtcDateTime;

        /// <summary>CapCut 9 projects can hold several timelines (<c>Timelines/project.json</c>); others have the root
        /// draft only.</summary>
        public ProjectTimeline[] Timelines() => EccCore.ProjectTimelines(project.Path);

        /// <summary>A timeline by name (any case), id or 1-based number; the main one for null.</summary>
        public ProjectTimeline? Timeline(string? nameOrNumber) => EccCore.ProjectTimeline(project.Path, nameOrNumber);

        /// <summary>The project is open in CapCut: its lock file is there while CapCut runs (a crash leaves stale ones).</summary>
        public bool IsOpenInCapCut => EccCore.IsOpenInCapcut(project.Path, CapCutApp.IsRunning);
    }

    extension(Timeline)
    {
        /// <summary>Builds <paramref name="timeline"/> of the project (the main one for null) with this machine's folders.</summary>
        public static Timeline Build(CapCutProject project, ProjectTimeline? timeline = null) =>
            EnhanceCapCut.Core.Timeline.Build(project.Path, timeline, CoreEnvironment.Current);
    }

    extension(Timeline timeline)
    {
        /// <summary>The draft's canvas width in pixels.</summary>
        public int CanvasWidth => (int)timeline.Canvas().Width;
        /// <summary>The draft's canvas height in pixels.</summary>
        public int CanvasHeight => (int)timeline.Canvas().Height;
    }

    extension(TimeRange range)
    {
        public long End => range.Start + range.Duration;
        public bool Contains(long t) => t >= range.Start && t < range.End;
    }

    extension(VisualLayer layer)
    {
        public bool IsActive(long t) => layer.Target.Contains(t);
    }

    extension(FilterLayer filter)
    {
        public bool IsActive(long t) => filter.Target.Contains(t);

        /// <summary>True when the filter is painted after <paramref name="layer"/> (and so colours it).</summary>
        public bool Covers(VisualLayer layer) =>
            filter.RenderIndex > layer.RenderIndex || (filter.RenderIndex == layer.RenderIndex && filter.TrackIndex > layer.TrackIndex);
    }

    extension(Crop crop)
    {
        public bool IsIdentity =>
            crop.UpperLeft.X <= 0.0001 && crop.UpperLeft.Y <= 0.0001 && crop.LowerRight.X >= 0.9999 && crop.LowerRight.Y >= 0.9999;
    }

    extension(VideoEffectState state)
    {
        /// <summary>Too small to see (the thresholds of the core's <c>VideoEffectState::is_identity</c>).</summary>
        public bool IsIdentity =>
            Math.Abs(state.Zoom - 1) < 0.0005 && Math.Abs(state.Rotation) < 0.01 && state.Blur < 0.0002 && state.Flash < 0.002;
    }

    extension(VoiceEffects)
    {
        /// <summary>Only a voice separation (the other stages off).</summary>
        public static VoiceEffects Only(VoiceSeparation separation) => new(separation, null, null, null);
    }

    extension(VoiceEffects voice)
    {
        public bool IsEmpty => voice.Separation == VoiceSeparation.None && voice.Denoise is null && voice.Enhance is null
            && voice.Filter is null;
    }

    extension(TextContent content)
    {
        /// <summary>The UTF-16 range of <paramref name="style"/>'s run in the text (the core's <c>utf16_start/end</c>), or
        /// the whole text without a style.</summary>
        public (int Start, int End) Utf16Range(TextStyle? style) =>
            style is null ? (0, content.Text.Length) : ((int)style.Utf16Start, (int)Math.Max(style.Utf16Start, style.Utf16End));
    }

    extension(LutLayout layout)
    {
        /// <summary>Entries per channel.</summary>
        public int Size => layout switch
        {
            LutLayout.Grid g => (int)g.Size,
            LutLayout.Row r => (int)r.Size,
            LutLayout.Column c => (int)c.Size,
            _ => 0,
        };

        /// <summary>The pixel holding entry (r, g, b).</summary>
        public (int X, int Y) Pixel(int r, int g, int b) => layout switch
        {
            LutLayout.Grid grid => ((int)((ulong)b % grid.TilesPerRow * grid.Size) + r, (int)((ulong)b / grid.TilesPerRow * grid.Size) + g),
            LutLayout.Row row => (b * (int)row.Size + r, g),
            LutLayout.Column column => (r, b * (int)column.Size + g),
            _ => (0, 0),
        };
    }

    extension(RenderReport report)
    {
        public bool IsEmpty => report.Unsupported.Length == 0 && report.MissingFiles.Length == 0;
    }

    extension(ReportItem item)
    {
        public string Id => item.Category + "/" + item.Name;
    }

    extension(OriginalAudio original)
    {
        /// <summary>The English name, also what the app stores.</summary>
        public string RawValue => EccCore.OriginalAudioRawValue(original);
        /// <summary>The name in the interface language.</summary>
        public string Title => EccCore.OriginalAudioTitle(original);
    }

    extension(OriginalAudio)
    {
        public static OriginalAudio[] All =>
            [OriginalAudio.Keep, OriginalAudio.Lower, OriginalAudio.Mute, OriginalAudio.Replace, OriginalAudio.RemoveVocal];

        public static OriginalAudio? FromRawValue(string raw) => EccCore.OriginalAudioFromRawValue(raw);
    }
}

/// <summary>Projects in CapCut's folder.</summary>
public static class ProjectLocator
{
    public static string DefaultProjectsRoot => CoreEnvironment.Current.ProjectsRoot;

    /// <summary>All projects that have a draft, most recently modified first.</summary>
    public static CapCutProject[] AllProjects(string? root = null) => EccCore.AllProjects(root ?? DefaultProjectsRoot);

    /// <summary>The project CapCut has open, if any.</summary>
    public static CapCutProject? OpenProject(string? root = null) => EccCore.OpenProject(root ?? DefaultProjectsRoot);

    /// <summary>A project by path (<c>~</c> allowed, a draft file's path too) or by folder name.</summary>
    public static CapCutProject? Resolve(string nameOrPath, string? root = null) =>
        EccCore.ResolveProject(nameOrPath, root ?? DefaultProjectsRoot);
}

/// <summary>CapCut itself, which the core can't see: whether it runs decides if a project's lock file counts.</summary>
public static class CapCutApp
{
    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("CapCut");
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }
    }
}

/// <summary>Writing a subtitles run into a project through the core: given whether CapCut runs, the clock and random
/// ids (the counterpart of the macOS DraftWriter).</summary>
public static class DraftWriter
{
    /// <summary>Where the originals of rewritten drafts are kept.</summary>
    public static string BackupRoot => CoreEnvironment.Current.BackupRoot;

    internal static long NowUs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    /// <summary>A run's tracks as the core's draft edits: captions, then voice-over.</summary>
    public static DraftEdits Edits(IEnumerable<SubtitleTrack> captions, IEnumerable<VoiceOverTrack> voiceOver, OriginalAudio original,
        bool replacePrevious) => new([.. captions], [.. voiceOver], original, replacePrevious);

    /// <summary>Writes <paramref name="edits"/> into the timeline's draft and the copies CapCut keeps of it, after a
    /// backup. Throws <see cref="CoreException.ProjectOpen"/> while the project is open in CapCut.</summary>
    public static WriteResult Write(DraftEdits edits, CapCutProject project, ProjectTimeline timeline) =>
        EccCore.WriteTimeline(project.Path, timeline, edits, BackupRoot, CapCutApp.IsRunning, new IdSource.Random(), NowUs);

    /// <summary>Adds a timeline named <paramref name="name"/>: <paramref name="source"/>'s draft with
    /// <paramref name="edits"/> applied.</summary>
    public static DubResult CreateDub(DraftEdits edits, CapCutProject project, ProjectTimeline source, string name) =>
        EccCore.CreateDubTimeline(project.Path, source, name, edits, BackupRoot, CapCutApp.IsRunning, new IdSource.Random(), NowUs);

    /// <summary>The draft with <paramref name="edits"/> applied (in memory; the CLI's <c>--draft-out</c>).</summary>
    public static byte[] Apply(DraftEdits edits, byte[] draft, string? projectPath) =>
        EccCore.ApplyEdits(edits, draft, projectPath, new IdSource.Random(), NowUs);

    /// <summary><c>&lt;project&gt;_dub_&lt;languages&gt;_&lt;original audio&gt;_&lt;n&gt;</c>, with the first <c>n</c> no
    /// timeline has.</summary>
    public static string DubName(CapCutProject project, IEnumerable<string> languages, OriginalAudio original) =>
        EccCore.DubTimelineName(project.Path, [.. languages], original);
}
