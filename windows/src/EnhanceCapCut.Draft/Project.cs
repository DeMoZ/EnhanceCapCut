using System.Text.Json;
using System.Text.RegularExpressions;

namespace EnhanceCapCut.Draft;

/// <summary>A CapCut project folder on disk.</summary>
public sealed class CapCutProject
{
    public string Folder { get; }
    public string Name => System.IO.Path.GetFileName(Folder.TrimEnd('/', '\\'));
    public DateTime Modified { get; }
    /// <summary>CapCut keeps a <c>.locked</c> file in the folder of every project that is open in the editor.</summary>
    public bool IsOpen { get; }

    public string DraftPath => System.IO.Path.Combine(Folder, "draft_info.json");

    public CapCutProject(string folder)
    {
        Folder = folder;
        Modified = File.Exists(DraftPath) ? File.GetLastWriteTimeUtc(DraftPath)
            : Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : DateTime.MinValue;
        IsOpen = File.Exists(System.IO.Path.Combine(folder, ".locked"));
    }

    /// <summary>
    /// CapCut 9 projects can hold several timelines (<c>Timelines/project.json</c>), each with its own draft in
    /// <c>Timelines/&lt;id&gt;/draft_info.json</c>; the root <c>draft_info.json</c> is a copy of the main one. Projects
    /// without that file have a single timeline: the root draft.
    /// </summary>
    public List<ProjectTimeline> Timelines()
    {
        var root = System.IO.Path.Combine(Folder, "Timelines");
        List<ProjectTimeline> single = [new("", "Timeline 01", DraftPath, true)];
        JsonElement list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root, "project.json")));
            list = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return single;
        }
        if (list.ValueKind != JsonValueKind.Object) return single;
        var mainID = list.Str("main_timeline_id") ?? "";
        var found = new List<ProjectTimeline>();
        if (list.Get("timelines") is not { ValueKind: JsonValueKind.Array } entries) return single;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = e.Str("id") ?? "";
            if (id.Length == 0 || (e.Bool("is_marked_delete") ?? false)) continue;
            var isMain = id == mainID;
            var draft = System.IO.Path.Combine(root, id, "draft_info.json");
            if (!File.Exists(draft))
            {
                if (!isMain) continue;
                draft = DraftPath;
            }
            var name = e.Str("name") ?? "";
            found.Add(new(id, name.Length == 0 ? id : name, draft, isMain));
        }
        return found.Count == 0 ? single : found;
    }

    public Draft LoadDraft(ProjectTimeline? timeline = null) => DraftLoader.Load(timeline?.DraftPath ?? DraftPath);
}

/// <summary>One timeline of a project.</summary>
/// <param name="Id">The timeline's UUID; empty for projects without <c>Timelines/project.json</c>.</param>
public sealed record ProjectTimeline(string Id, string Name, string DraftPath, bool IsMain);

public static class ProjectLocator
{
    /// <summary>
    /// CapCut's default drafts folder. Assumed to mirror the Mac layout (<c>User Data/Projects/com.lveditor.draft</c>)
    /// under <c>%LOCALAPPDATA%\CapCut</c> on Windows; CapCut lets the user move it, so callers can pass another root.
    /// </summary>
    public static string DefaultProjectsRoot => OperatingSystem.IsWindows()
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CapCut", "User Data", "Projects", "com.lveditor.draft")
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Movies", "CapCut", "User Data", "Projects", "com.lveditor.draft");

    /// <summary>All projects that have a draft, most recently modified first.</summary>
    public static List<CapCutProject> AllProjects(string? root = null)
    {
        root ??= DefaultProjectsRoot;
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Where(d => !System.IO.Path.GetFileName(d).StartsWith('.') && File.Exists(System.IO.Path.Combine(d, "draft_info.json")))
            .Select(d => new CapCutProject(d))
            .OrderByDescending(p => p.Modified)
            .ToList();
    }

    /// <summary>
    /// The project currently open in CapCut. A crashed CapCut can leave stale <c>.locked</c> files,
    /// so when several projects look open the most recently modified one wins.
    /// </summary>
    public static CapCutProject? OpenProject(string? root = null) => AllProjects(root).FirstOrDefault(p => p.IsOpen);

    /// <summary>Resolves a CLI argument: a path to a project folder or draft file, or a project name.</summary>
    public static CapCutProject? Resolve(string nameOrPath, string? root = null)
    {
        var path = nameOrPath.StartsWith('~')
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + nameOrPath[1..]
            : nameOrPath;
        if (System.IO.Path.GetFileName(path) == "draft_info.json") path = System.IO.Path.GetDirectoryName(path) ?? path;
        if (File.Exists(System.IO.Path.Combine(path, "draft_info.json"))) return new CapCutProject(System.IO.Path.GetFullPath(path));
        var named = System.IO.Path.Combine(root ?? DefaultProjectsRoot, nameOrPath);
        return File.Exists(System.IO.Path.Combine(named, "draft_info.json")) ? new CapCutProject(named) : null;
    }
}

public static class DraftLoader
{
    public sealed class LoadException(string path, Exception inner)
        : Exception($"Cannot read {path}: {inner.Message}", inner);

    public static Draft Load(string draftPath)
    {
        try
        {
            return Draft.Parse(File.ReadAllText(draftPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new LoadException(draftPath, e);
        }
    }

    static readonly Regex Placeholder = new("##_draftpath_placeholder_[^#]*_##", RegexOptions.Compiled);

    /// <summary>Paths inside a draft may start with a placeholder for the project folder.</summary>
    public static string? ResolvePath(string path, string projectFolder) =>
        path.Length == 0 ? null : Placeholder.Replace(path, _ => projectFolder);
}
