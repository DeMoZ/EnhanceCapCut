using System.Runtime.CompilerServices;
using System.Text;

// Not under EnhanceCapCut.Draft: its Timeline, CaptionCue… would hide the core's.
namespace EnhanceCapCut.Core.Tests;

/// <summary>
/// The generated C# bindings of the Rust core (Generated/ecc_core.cs, scripts/csharp-bindings.sh) on a fixture
/// project of core/fixtures: a timeline is built and read, a frame evaluated, captions written, errors and panics
/// arrive as <see cref="CoreException"/>. The core's own tests are in core/ (cargo test); this checks the crossing.
/// </summary>
public sealed class RustCoreBindingTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "ecc-binding-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
    }

    private static string Repo([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", ".."));

    /// <summary>The fixture core/fixtures/&lt;name&gt; (one without media) as a project folder.</summary>
    private string Project(string name)
    {
        var folder = Path.Combine(_parent, name);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(Repo(), "core", "fixtures", name, "draft.fixture.json"),
                  Path.Combine(folder, "draft_content.json"));
        return folder;
    }

    private static CoreEnvironment Environment(string project) =>
        new(Path.GetDirectoryName(project)!, [Path.Combine(project, "cache")], Path.Combine(project, "backups"));

    [Fact]
    public void BuildsATimelineAndEvaluatesAFrame()
    {
        var folder = Project("captions");
        using var timeline = Timeline.Build(folder, null, Environment(folder));
        Assert.Equal(8_000_000, timeline.Duration());
        Assert.Equal(new Size(1920, 1080), timeline.Canvas());
        var layers = timeline.Layers();
        Assert.Equal(6, layers.Length);
        var text = Assert.IsType<VisualContent.Text>(layers[0].Content);
        Assert.Equal("Привет, мир", text.Spec.Content.Text);
        Assert.True(timeline.HasSubtitles());
        using (var without = timeline.WithoutSubtitles()) Assert.True(without.Layers().Length < layers.Length);

        var frame = timeline.Frame(1_000_000);
        Assert.Equal(1_000_000, frame.Time);
        Assert.Contains(frame.Layers, l => l.Index == 0 && l.Offset == 1_000_000);
        Assert.NotNull(frame.Layers[0].Text);
        var map = layers[0].TimeMap;
        Assert.Equal([map.SourceTime(0), map.SourceTime(500_000)], map.SourceTimes([0, 500_000]));

        var info = EccCore.FormatInfo(folder, null, Environment(folder));
        var expected = File.ReadAllText(Path.Combine(Repo(), "core", "fixtures", "captions", "expected", "info.txt"));
        Assert.Equal(expected, info);
    }

    [Fact]
    public void WritesCaptionsWithABackup()
    {
        var folder = Project("captions");
        var timeline = EccCore.ProjectTimeline(folder, null)!;
        var before = File.ReadAllBytes(timeline.DraftPath);
        var edits = new DraftEdits([new SubtitleTrack("en", [new CaptionCue(0.5, 2, "Hello")], false, true)], [],
                                   OriginalAudio.Lower, true);
        var applied = EccCore.ApplyEdits(edits, before, folder, new IdSource.Counting(0), 1_700_000_000_000_000);
        Assert.Contains("00000000-0000-4000-8000-000000000001", Encoding.UTF8.GetString(applied));

        var result = EccCore.WriteTimeline(folder, timeline, edits, Environment(folder).BackupRoot, false,
                                           new IdSource.Random(), 1_700_000_000_000_000);
        Assert.Equal(1ul, result.CueCount);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(result.BackupFolder, "draft_content.json")));
        using var rebuilt = Timeline.Build(folder, null, Environment(folder));
        Assert.True(rebuilt.Layers().Length > 6);
    }

    [Fact]
    public void ErrorsAndPanicsArriveAsCoreExceptions()
    {
        var missing = Path.Combine(_parent, "missing");
        Assert.Throws<CoreException.Unreadable>(() => Timeline.Build(missing, null, EccCore.DefaultEnvironment()));
        var panic = Assert.Throws<CoreException.Internal>(() => EccCore.DebugPanic("boom"));
        Assert.Equal("boom", panic.Message);
    }
}
