using System.Text;
using System.Text.Json;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's DubTimelineTests.swift; same cases, same expectations.

public sealed class DubTimelineTests : IDisposable
{
    const string MainId = "40332EEA-CA6F-4C3A-A039-B145ECAED041";
    readonly string root;

    /// <summary>A project with one timeline in <c>Timelines/</c>, its root draft copy and a dock with that timeline.</summary>
    public DubTimelineTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"ecc-dub-{Guid.NewGuid()}", "Trip");
        var folder = Path.Combine(root, "Timelines", MainId);
        Directory.CreateDirectory(Path.Combine(folder, "common_attachment"));
        var draft = $"{{\"id\":\"{MainId}\",\"duration\":1000000,\"tracks\":[]}}";
        foreach (var path in new[] { Path.Combine(folder, "draft_info.json"), Path.Combine(folder, "template-2.tmp"), Path.Combine(root, "draft_info.json") })
            File.WriteAllText(path, draft);
        File.WriteAllText(Path.Combine(folder, "attachment_editing.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "common_attachment", "attachment_pc_timeline.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "draft.extra"), "x");
        File.WriteAllText(Path.Combine(root, "Timelines", "project.json"),
            $"{{\"config\":{{\"color_space\":-1}},\"id\":\"P\",\"main_timeline_id\":\"{MainId}\",\"timelines\":[{{\"create_time\":1,"
            + $"\"id\":\"{MainId}\",\"is_marked_delete\":false,\"name\":\"Timeline 01\",\"update_time\":1}}],\"version\":0}}");
        File.WriteAllText(Path.Combine(root, "timeline_layout.json"),
            $"{{\"dockItems\":[{{\"dockIndex\":0,\"ratio\":1,\"timelineIds\":[\"{MainId}\"],\"timelineNames\":[\"Timeline 01\"]}}],"
            + "\"layoutOrientation\":1}");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CreatesANumberedDubTimeline()
    {
        var project = new CapCutProject(root);
        var source = project.Timelines()[0];
        var name = DubTimeline.Name(project, ["en"], OriginalAudio.Mute);
        Assert.Equal("Trip_dub_en_muteall_1", name);

        var (timeline, result) = DubTimeline.Create(project, source, name, 0,
            d => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d).Replace("\"tracks\":[]", "\"tracks\":[1]")));
        try
        {
            Assert.False(timeline.IsMain);

            // The project lists it, with the transformed draft under its own id; the source is untouched.
            var timelines = project.Timelines();
            Assert.Equal(["Timeline 01", name], timelines.Select(t => t.Name));
            var draft = JsonDocument.Parse(File.ReadAllBytes(timelines[1].DraftPath)).RootElement;
            Assert.Equal(timeline.Id, draft.GetProperty("id").GetString());
            Assert.Equal(1, draft.GetProperty("tracks")[0].GetInt32());
            var folder = Path.GetDirectoryName(timelines[1].DraftPath)!;
            Assert.Equal(File.ReadAllBytes(timelines[1].DraftPath), File.ReadAllBytes(Path.Combine(folder, "template-2.tmp")));
            Assert.True(File.Exists(Path.Combine(folder, "attachment_editing.json")));
            Assert.True(File.Exists(Path.Combine(folder, "common_attachment", "attachment_pc_timeline.json")));
            Assert.False(File.Exists(Path.Combine(folder, "draft.extra")));
            Assert.Contains("\"tracks\":[]", File.ReadAllText(source.DraftPath));

            // A tab in the dock; project.json keeps its other fields.
            Assert.Contains($"\"timelineIds\":[\"{MainId}\",\"{timeline.Id}\"],\"timelineNames\":[\"Timeline 01\",\"{name}\"]",
                File.ReadAllText(Path.Combine(root, "timeline_layout.json")));
            Assert.StartsWith("{\"config\":{\"color_space\":-1},\"id\":\"P\"", File.ReadAllText(Path.Combine(root, "Timelines", "project.json")));
            Assert.True(File.Exists(Path.Combine(result.BackupFolder, "Timelines", "project.json")));

            // The next one with the same settings gets the next number.
            Assert.Equal("Trip_dub_en_muteall_2", DubTimeline.Name(project, ["en"], OriginalAudio.Mute));
            Assert.Equal("Trip_dub_en-th_removevocal_1", DubTimeline.Name(project, ["en", "th"], OriginalAudio.RemoveVocal));
        }
        finally
        {
            Directory.Delete(result.BackupFolder, recursive: true);
        }
    }

    [Fact]
    public void OlderSingleTimelineProjectsAreRefused()
    {
        File.Delete(Path.Combine(root, "Timelines", "project.json"));
        var project = new CapCutProject(root);
        Assert.Throws<NoTimelineListException>(() => DubTimeline.Create(project, project.Timelines()[0], "x", 0, d => d));
    }
}
