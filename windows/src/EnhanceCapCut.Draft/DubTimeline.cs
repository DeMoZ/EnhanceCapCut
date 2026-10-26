using System.Globalization;

namespace EnhanceCapCut.Draft;

public sealed class NoTimelineListException(string name) : Exception(
    $"{name} is from an older CapCut that keeps a single timeline. Open it in CapCut once, close it, and try again.");

/// <summary>
/// A dubbed copy of a timeline, added to the project as a timeline of its own: the source timeline stays as it was.
///
/// A new timeline is an entry in <c>Timelines/project.json</c>, a folder <c>Timelines/&lt;id&gt;/</c> with the draft
/// (whose root <c>id</c> is the timeline's) and its <c>template-2.tmp</c> copy, and a tab in
/// <c>timeline_layout.json</c>'s first dock (see docs/FORMAT.md). The source's plain-JSON attachments are copied along;
/// <c>draft.extra</c> (binary) is left to CapCut. A port of the macOS version's <c>DubTimeline.swift</c>; the two must
/// stay in step.
/// </summary>
public static class DubTimeline
{
    /// <summary>Files and folders next to a timeline's draft that are copied into the new timeline's folder.</summary>
    static readonly string[] Attachments = ["attachment_editing.json", "attachment_pc_common.json", "common_attachment"];

    /// <summary>In the names of dubbed timelines: <c>keep</c>, <c>lower</c>, <c>muteall</c>, <c>replacevoice</c>, <c>removevocal</c>.</summary>
    public static string NameToken(OriginalAudio original) => original switch
    {
        OriginalAudio.Keep => "keep",
        OriginalAudio.Lower => "lower",
        OriginalAudio.Mute => "muteall",
        OriginalAudio.Replace => "replacevoice",
        _ => "removevocal",
    };

    /// <summary><c>&lt;project&gt;_dub_&lt;languages&gt;_&lt;original audio&gt;_&lt;n&gt;</c>, with the first <c>n</c> from 1 that no timeline has.</summary>
    public static string Name(CapCutProject project, IEnumerable<string> languages, OriginalAudio original)
    {
        var taken = project.Timelines().Select(t => t.Name).ToHashSet();
        var name = $"{project.Name}_dub_{string.Join("-", languages)}_{NameToken(original)}";
        var n = 1;
        while (taken.Contains($"{name}_{n}")) n++;
        return $"{name}_{n}";
    }

    /// <summary>
    /// Adds a timeline named <paramref name="name"/> to the project: <paramref name="source"/>'s draft run through
    /// <paramref name="transform"/>. <c>project.json</c> and <c>timeline_layout.json</c> are backed up to
    /// <see cref="DraftSubtitleWriter.BackupRoot"/> first.
    /// </summary>
    public static (ProjectTimeline Timeline, DraftSubtitleWriter.Result Result) Create(CapCutProject project, ProjectTimeline source,
        string name, int cueCount, Func<byte[], byte[]> transform)
    {
        if (DraftSubtitleWriter.IsOpenInCapCut(project)) throw new ProjectOpenException(project.Name);
        var timelines = Path.Combine(project.Folder, "Timelines");
        var listPath = Path.Combine(timelines, "project.json");
        if (!File.Exists(listPath)) throw new NoTimelineListException(project.Name);
        var list = File.ReadAllBytes(listPath);
        var layoutPath = Path.Combine(project.Folder, "timeline_layout.json");
        var layout = File.Exists(layoutPath) ? File.ReadAllBytes(layoutPath) : null;

        var id = Lenient.NewId();
        var draft = WithRootId(transform(File.ReadAllBytes(source.DraftPath)), id);
        var newList = AddEntry(list, id, name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000)
            ?? throw new NoTimelineListException(project.Name);
        var newLayout = layout is null ? null : AddTab(layout, id, name);

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ", CultureInfo.InvariantCulture);
        var backup = Path.Combine(DraftSubtitleWriter.BackupRoot, project.Name, stamp);
        Directory.CreateDirectory(Path.Combine(backup, "Timelines"));
        File.Copy(listPath, Path.Combine(backup, "Timelines", "project.json"));
        if (layout is not null) File.Copy(layoutPath, Path.Combine(backup, "timeline_layout.json"));

        var folder = Path.Combine(timelines, id);
        Directory.CreateDirectory(folder);
        try
        {
            var sourceFolder = Path.GetDirectoryName(source.DraftPath)!;
            foreach (var item in Attachments)
            {
                var from = Path.Combine(sourceFolder, item);
                if (File.Exists(from)) File.Copy(from, Path.Combine(folder, item));
                else if (Directory.Exists(from)) CopyFolder(from, Path.Combine(folder, item));
            }
            List<string> files = [Path.Combine(folder, Path.GetFileName(source.DraftPath)), Path.Combine(folder, "template-2.tmp")];
            foreach (var path in files) File.WriteAllBytes(path, draft);
            // The list last: until then CapCut doesn't know the folder.
            WriteAtomically(listPath, newList);
            if (newLayout is not null) WriteAtomically(layoutPath, newLayout);
            return (new ProjectTimeline(id, name, files[0], IsMain: false), new DraftSubtitleWriter.Result(files, backup, cueCount));
        }
        catch
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>The draft with its root <c>id</c> (the timeline's id) replaced.</summary>
    internal static byte[] WithRootId(byte[] draft, string id)
    {
        var scanner = new JsonScanner(draft);
        var root = scanner.Root();
        var edits = new JsonEdits();
        if (scanner.Value("id", root) is { } range) edits.Replace(range, JsonText.Encode(id));
        else
        {
            var open = root.Start.Value + 1;
            edits.Replace(open..open, "\"id\":" + JsonText.Encode(id) + (scanner.Members(root).Count == 0 ? "" : ","));
        }
        return edits.AppliedTo(scanner.Bytes);
    }

    /// <summary><c>project.json</c> with an entry for the new timeline appended to <c>timelines</c>; null without that list.</summary>
    internal static byte[]? AddEntry(byte[] list, string id, string name, long now)
    {
        var scanner = new JsonScanner(list);
        if (scanner.Value("timelines", scanner.Root()) is not { } range || scanner.Bytes[range.Start.Value] != (byte)'[') return null;
        var entry = JsonText.Encode(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["create_time"] = now, ["id"] = id, ["is_marked_delete"] = false, ["name"] = name, ["update_time"] = now,
        });
        var edits = new JsonEdits();
        var close = range.End.Value - 1;
        edits.Replace(close..close, (scanner.Elements(range).Count == 0 ? "" : ",") + entry);
        return edits.AppliedTo(scanner.Bytes);
    }

    /// <summary><c>timeline_layout.json</c> with the timeline added as a tab of the first dock; unchanged when it has no dock.</summary>
    internal static byte[] AddTab(byte[] layout, string id, string name)
    {
        var scanner = new JsonScanner(layout);
        if (scanner.Value("dockItems", scanner.Root()) is not { } docks || scanner.Bytes[docks.Start.Value] != (byte)'['
            || scanner.Elements(docks) is not [var dock, ..]
            || scanner.Value("timelineIds", dock) is not { } ids || scanner.Bytes[ids.Start.Value] != (byte)'['
            || scanner.Value("timelineNames", dock) is not { } names || scanner.Bytes[names.Start.Value] != (byte)'[')
            return layout;
        var edits = new JsonEdits();
        foreach (var (range, value) in new[] { (ids, id), (names, name) })
        {
            var close = range.End.Value - 1;
            edits.Replace(close..close, (scanner.Elements(range).Count == 0 ? "" : ",") + JsonText.Encode(value));
        }
        return edits.AppliedTo(scanner.Bytes);
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".ecc-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
