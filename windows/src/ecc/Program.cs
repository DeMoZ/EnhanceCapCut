using System.Globalization;
using EnhanceCapCut.Draft;

// Command-line front end of the Windows version, used for automation and for checking the draft port
// against the macOS version (`ecc info` prints the same report as the Swift CLI).
//   ecc list
//   ecc info [project] [--timeline name|number]

var rest = new List<string>(args);
var command = rest.Count == 0 ? "help" : rest[0];
if (rest.Count > 0) rest.RemoveAt(0);

static void Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
}

string? Option(string name)
{
    var i = rest.IndexOf(name);
    if (i < 0 || i + 1 >= rest.Count) return null;
    var value = rest[i + 1];
    rest.RemoveRange(i, 2);
    return value;
}

CapCutProject Project()
{
    if (rest.FirstOrDefault(a => !a.StartsWith('-')) is { } arg)
    {
        var project = ProjectLocator.Resolve(arg);
        if (project is null) Fail($"Project not found: {arg}");
        return project!;
    }
    var open = ProjectLocator.OpenProject();
    if (open is null) Fail("No project is open in CapCut; pass a name");
    return open!;
}

// `--timeline` picks one of the project's timelines by name or 1-based number; the main one by default.
ProjectTimeline TimelineOf(string? arg, CapCutProject project)
{
    var all = project.Timelines();
    if (arg is null) return all.FirstOrDefault(t => t.IsMain) ?? all[0];
    if (all.FirstOrDefault(t => string.Equals(t.Name, arg, StringComparison.OrdinalIgnoreCase) || t.Id == arg) is { } named) return named;
    if (int.TryParse(arg, out var n) && n >= 1 && n <= all.Count) return all[n - 1];
    Fail($"No timeline {arg} in {project.Name}: " + string.Join(", ", all.Select(t => t.Name)));
    return null!;
}

static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
static string Seconds(long us) => F2(us / 1_000_000.0) + "s";
// Swift prints whole doubles as "30.0".
static string SwiftDouble(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15
    ? ((long)v).ToString(CultureInfo.InvariantCulture) + ".0" : v.ToString(CultureInfo.InvariantCulture);
// The first `count` characters (grapheme clusters, as Swift counts them).
static string Prefix(string s, int count)
{
    var e = StringInfo.GetTextElementEnumerator(s);
    var length = 0;
    for (var i = 0; i < count && e.MoveNext(); i++) length = e.ElementIndex + ((string)e.Current).Length;
    return s[..length];
}

switch (command)
{
    case "list":
        foreach (var p in ProjectLocator.AllProjects())
            Console.WriteLine($"{(p.IsOpen ? "*" : " ")} {p.Name}\t{p.Modified.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        break;

    case "info":
    {
        var timelineArg = Option("--timeline");
        var p = Project();
        var pt = TimelineOf(timelineArg, p);
        var timeline = TimelineBuilder.Build(p, pt);
        var timelines = p.Timelines();
        if (timelines.Count > 1)
        {
            Console.WriteLine("Timelines: " + string.Join(", ",
                timelines.Select(t => (t == pt ? "*" : "") + t.Name + (t.IsMain ? " (main)" : ""))));
        }
        Console.WriteLine($"Project: {p.Name}  canvas {timeline.CanvasWidth}x{timeline.CanvasHeight}  "
            + $"{SwiftDouble(timeline.Fps)} fps  duration {Seconds(timeline.Duration)}");
        Console.WriteLine($"Visual layers: {timeline.Layers.Count}, audio clips: {timeline.AudioClips.Count}");
        foreach (var layer in timeline.Layers)
        {
            var kind = layer.Content switch
            {
                VideoContent v => "video " + Path.GetFileName(v.Path),
                PhotoContent ph => "photo " + Path.GetFileName(ph.Path),
                TextContentLayer t => "text \"" + Prefix(t.Spec.Content.Text, 30).Replace("\n", " ") + "\"",
                CompoundContent c => $"compound ({c.Timeline.Layers.Count} layers)",
                _ => "?",
            };
            var speed = layer.TimeMap.IsConstant ? "x" + F2(layer.TimeMap.AverageSpeed) : "curve";
            Console.WriteLine($"  [{layer.RenderIndex}/t{layer.TrackIndex}] {Seconds(layer.Target.Start)}+{Seconds(layer.Target.Duration)} {speed} {kind}");
        }
        foreach (var clip in timeline.AudioClips)
        {
            var fx = "";
            var v = clip.Voice;
            if (v.Separation == VoiceEffects.SeparationMode.KeepVoice) fx += " [keep voice]";
            if (v.Separation == VoiceEffects.SeparationMode.RemoveVoice) fx += " [remove voice]";
            if (v.Denoise is { } d) fx += $" [denoise {F2(d)}]";
            if (v.Enhance is { } e) fx += $" [enhance {(e * 100).ToString("0", CultureInfo.InvariantCulture)}]";
            if (v.Filter is { } f)
                fx += $" [{f.Name}{(f.Strength < 1 ? " " + (f.Strength * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "")}]";
            Console.WriteLine($"  audio {Seconds(clip.Target.Start)}+{Seconds(clip.Target.Duration)} vol {F2(clip.Volume)} {Path.GetFileName(clip.Path)}{fx}");
        }
        if (timeline.Report.Unsupported.Count > 0)
        {
            Console.WriteLine("Not supported yet:");
            foreach (var item in timeline.Report.Unsupported) Console.WriteLine($"  {item.Category}: {item.Name} ×{item.Count}");
        }
        if (timeline.Report.MissingFiles.Count > 0)
        {
            Console.WriteLine("Missing media:");
            foreach (var file in timeline.Report.MissingFiles) Console.WriteLine($"  {file}");
        }
        break;
    }

    default:
        Console.WriteLine("usage: ecc list | info [project] [--timeline name|number]");
        break;
}
