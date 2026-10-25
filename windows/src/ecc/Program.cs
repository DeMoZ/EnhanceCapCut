using System.Globalization;
using EnhanceCapCut.Draft;

// Command-line front end of the Windows version, used for automation and for checking the draft port
// against the macOS version (`ecc info` prints the same report as the Swift CLI).
//   ecc list
//   ecc info [project] [--timeline name|number]
//   ecc render [project] [--timeline name|number] [-o file] [--codec h264|hevc|hevc10] [--quality 0-100 | --bitrate Mbps]
//              [--fps N] [--aspect project|9:16|1:1|16:9] [--height N] [--lufs -14] [--no-subtitles]
//              [--from s] [--duration s]                                            (Windows only)
//   ecc subtitles [project] [--timeline name|number] --language ru-RU [--translate en,th] [--no-original] [--show en]
//                 [--keep-previous] [--srt folder] [--draft out.json] [--audio mix.wav] [--no-captions]
//                 [--voice en,th | en=<voice id>,th] [--hear th] [--original keep|lower|mute]
//                 (writes into the project unless --draft is given; models are downloaded on first use;
//                  the timeline's audio is mixed on Windows, elsewhere pass --audio;
//                  --voice speaks these translations with Windows voices (Windows only), `ecc voices <language>` lists them)
//   ecc voices <language>                                                           (Windows only)

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

    case "render":
#if WINDOWS
        Render();
#else
        Fail("ecc render runs on Windows (Media Foundation, Direct2D, DirectWrite)");
#endif
        break;

    case "subtitles":
        await Subtitles();
        break;

    case "voices":
#if WINDOWS
        if (rest.Count == 0) Fail("usage: ecc voices <language code>");
        foreach (var voice in new EnhanceCapCut.Windows.WindowsVoices().Voices(SubtitleLanguage.Find(rest[0])?.Code ?? rest[0]))
            Console.WriteLine($"{voice.Label}  [{voice.Gender?.ToString() ?? "-"}]  {voice.Id}");
#else
        Fail("ecc voices runs on Windows (Windows.Media.SpeechSynthesis)");
#endif
        break;

    default:
        Console.WriteLine("usage: ecc list | info [project] [--timeline name|number] | render [project] [options] | subtitles [project] --language ru-RU [options] | voices <language>");
        break;
}

async Task Subtitles()
{
    var languageArg = Option("--language");
    if (languageArg is null) Fail("pass the spoken language: --language ru-RU");
    var source = SubtitleLanguage.Find(languageArg!);
    if (source is null) Fail($"Unsupported language {languageArg}: " + string.Join(", ", SubtitleLanguage.All.Select(l => l.Code)));
    SubtitleLanguage Language(string code) => SubtitleLanguage.Find(code) ?? throw new ArgumentException($"Unsupported language {code}");
    var targets = (Option("--translate") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Language).ToList();
    // "en,th" or "en=<voice id>,th": voice-over languages, each with the best installed voice unless one is given.
    var voices = new Dictionary<string, string>();
    foreach (var item in (Option("--voice") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = item.Split('=', 2);
        voices[Language(parts[0]).Code] = parts.Length > 1 ? parts[1] : "";
    }
    var hear = Option("--hear") is { } h ? Language(h).Code : null;
    var originalArg = Option("--original") ?? "lower";
    if (!Enum.TryParse<OriginalAudio>(originalArg, ignoreCase: true, out var original)) Fail("--original takes keep, lower or mute");
    var visible = Option("--show") is { } show ? SubtitleLanguage.Find(show)?.Code ?? show : null;
    var srtFolder = Option("--srt");
    var draftOut = Option("--draft");
    var audioPath = Option("--audio");
    var timelineArg = Option("--timeline");
    var includeOriginal = !rest.Remove("--no-original");
    var replace = !rest.Remove("--keep-previous");
    var captions = !rest.Remove("--no-captions");
    var p = Project();
    var pt = TimelineOf(timelineArg, p);
    targets.AddRange(voices.Keys.Where(c => targets.All(t => t.Code != c)).Order(StringComparer.Ordinal).Select(Language));

    EnhanceCapCut.Subtitles.IVoiceEngine? engine = null;
    float[] audio;
    if (audioPath is not null)
    {
        audio = EnhanceCapCut.Subtitles.AudioInput.ReadWav(audioPath);
    }
    else
    {
#if WINDOWS
        Console.WriteLine("  Mixing timeline audio…");
        // Earlier voice-over must not be heard by speech recognition.
        var timeline = EnhanceCapCut.Subtitles.SubtitlePipeline.SpeechTimeline(p, pt);
        var warnings = new List<string>();
        var mix = EnhanceCapCut.Render.AudioMixer.Mix(timeline, new EnhanceCapCut.Windows.MfAudioDecoder(), warnings);
        foreach (var warning in warnings) Console.WriteLine($"warning: {warning}");
        audio = EnhanceCapCut.Subtitles.AudioInput.ToMono16k(mix, 2, EnhanceCapCut.Render.AudioMixer.SampleRate);
#else
        Fail("Mixing the timeline needs Windows (Media Foundation); pass the mix with --audio file.wav");
        return;
#endif
    }
#if WINDOWS
    if (voices.Count > 0) engine = new EnhanceCapCut.Windows.WindowsVoices();
#else
    if (voices.Count > 0) Fail("Voice-over needs Windows (Windows.Media.SpeechSynthesis)");
#endif

    var options = new EnhanceCapCut.Subtitles.SubtitlePipeline.Options(source!, includeOriginal,
        captions ? targets : targets.Where(t => voices.ContainsKey(t.Code)).ToList(), visible)
    {
        Captions = captions,
        Voices = voices,
        AudibleVoice = hear,
        // A dry run (--draft) keeps the voice-over audio next to the draft it writes.
        VoiceOverFolder = draftOut is not null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(draftOut))!, "voiceover")
            : EnhanceCapCut.Subtitles.SubtitlePipeline.VoiceOverFolder(p),
    };
    var lastStep = "";
    var lastPercent = -10;
    var output = await EnhanceCapCut.Subtitles.SubtitlePipeline.RunAsync(audio, options, (step, fraction) =>
        {
            var percent = (int)(fraction * 100);
            if (step == lastStep && percent < lastPercent + 10) return;
            if (step != lastStep) lastPercent = -10;
            lastStep = step;
            lastPercent = percent;
            Console.WriteLine($"  {step} {percent}%");
        }, engine);
    var tracks = output.Captions;
    foreach (var track in output.VoiceOver)
        Console.WriteLine($"voice-over {track.Language.Code}{(track.IsAudible ? " (audible)" : "")}: {track.Lines.Count} lines → {track.Path}");
    foreach (var track in tracks)
    {
        Console.WriteLine($"{track.Language.Code}{(track.IsVisible ? " (shown)" : "")}: {track.Cues.Count} cues");
        foreach (var cue in track.Cues.Take(3))
            Console.WriteLine($"  {F2(cue.Start)}–{F2(cue.End)} " + cue.Text.Replace("\n", " / "));
    }
    if (srtFolder is not null)
    {
        foreach (var path in EnhanceCapCut.Subtitles.SubtitlePipeline.WriteSrt(tracks, Path.GetFullPath(srtFolder),
                     pt.IsMain ? p.Name : $"{p.Name} {pt.Name}"))
            Console.WriteLine($"Wrote {path}");
    }
    byte[] Apply(byte[] draft) => EnhanceCapCut.Subtitles.SubtitlePipeline.Apply(output, draft, original, replace, p.Folder);
    if (draftOut is not null)
    {
        File.WriteAllBytes(draftOut, Apply(File.ReadAllBytes(pt.DraftPath)));
        Console.WriteLine($"Wrote {draftOut}");
    }
    else
    {
        var result = DraftSubtitleWriter.Write(p, pt, tracks.Sum(t => t.Cues.Count), Apply);
        foreach (var file in result.Files) Console.WriteLine($"Updated {file}");
        Console.WriteLine($"Backup: {result.BackupFolder}");
    }
}

#if WINDOWS
void Render()
{
    // Start from the window's saved settings so CLI renders match the last export from the app.
    var settings = EnhanceCapCut.Draft.ExportSettings.Load();
    if (Option("--codec") is { } codec)
    {
        settings.Codec = codec switch
        {
            "h264" => EnhanceCapCut.Draft.ExportSettings.VideoCodec.H264,
            "hevc" => EnhanceCapCut.Draft.ExportSettings.VideoCodec.Hevc,
            "hevc10" => EnhanceCapCut.Draft.ExportSettings.VideoCodec.Hevc10,
            _ => throw new ArgumentException($"Unknown codec {codec} (h264, hevc, hevc10)"),
        };
    }
    if (Option("--quality") is { } q) { settings.RateControl = EnhanceCapCut.Draft.ExportSettings.RateControlMode.Quality; settings.Quality = Number(q); }
    if (Option("--bitrate") is { } b) { settings.RateControl = EnhanceCapCut.Draft.ExportSettings.RateControlMode.Bitrate; settings.BitrateMbps = Number(b); }
    if (Option("--fps") is { } fps) settings.FrameRate = Number(fps);
    if (Option("--aspect") is { } aspect)
    {
        settings.Aspect = aspect switch
        {
            "project" => EnhanceCapCut.Draft.ExportSettings.AspectRatio.Project,
            "9:16" => EnhanceCapCut.Draft.ExportSettings.AspectRatio.Portrait,
            "1:1" => EnhanceCapCut.Draft.ExportSettings.AspectRatio.Square,
            "16:9" => EnhanceCapCut.Draft.ExportSettings.AspectRatio.Landscape,
            _ => throw new ArgumentException($"Unknown aspect {aspect}"),
        };
    }
    if (Option("--height") is { } height)
    {
        var h = (int)Number(height);
        if (!Enum.IsDefined(typeof(EnhanceCapCut.Draft.ExportSettings.Resolution), h)) Fail("Resolution must be 0, 720, 1080, 1440 or 2160");
        settings.OutputResolution = (EnhanceCapCut.Draft.ExportSettings.Resolution)h;
    }
    if (rest.Remove("--no-subtitles")) settings.BurnInSubtitles = false;
    if (Option("--lufs") is { } lufs) { settings.NormalizeLoudness = true; settings.TargetLUFS = Number(lufs); }
    var from = Option("--from") is { } f ? Number(f) : (double?)null;
    var length = Option("--duration") is { } d ? Number(d) : (double?)null;
    var output = Option("-o");
    var timelineArg = Option("--timeline");
    var p = Project();
    var pt = TimelineOf(timelineArg, p);
    var timeline = TimelineBuilder.Build(p, pt);
    var name = pt.IsMain ? p.Name : $"{p.Name} {pt.Name}";
    var (width, heightPx) = settings.OutputSize(timeline.CanvasWidth, timeline.CanvasHeight);
    var rate = settings.Fps(timeline.Fps);
    var path = output is not null ? Path.GetFullPath(output)
        : Path.Combine(settings.OutputFolder, settings.FileName(name, width, heightPx, rate));
    var exporter = new EnhanceCapCut.Windows.Exporter(timeline, settings, path);
    if (from is not null || length is not null)
    {
        var start = (long)((from ?? 0) * 1_000_000);
        var duration = length is { } l ? (long)(l * 1_000_000) : timeline.Duration - start;
        exporter.Range = new TimeRange(start, Math.Min(duration, timeline.Duration - start));
    }
    foreach (var item in timeline.Report.Unsupported) Console.WriteLine($"not supported: {item.Category}: {item.Name} ×{item.Count}");
    foreach (var file in timeline.Report.MissingFiles) Console.WriteLine($"missing: {file}");
    Console.WriteLine($"Rendering {name} → {path} ({width}x{heightPx} @ {rate.ToString(CultureInfo.InvariantCulture)} fps, "
        + EnhanceCapCut.Draft.ExportSettings.Title(settings.Codec) + ")");
    var last = DateTime.MinValue;
    var started = DateTime.Now;
    exporter.Run(progress =>
    {
        if ((DateTime.Now - last).TotalSeconds < 2 && progress.Fraction < 1) return;
        last = DateTime.Now;
        Console.WriteLine($"  {progress.PhaseTitle} {progress.Fraction * 100,3:0}%  {progress.FramesDone}/{progress.FramesTotal} frames");
    });
    foreach (var warning in exporter.Warnings) Console.WriteLine($"warning: {warning}");
    Console.WriteLine($"Done in {(DateTime.Now - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s: {path}");
}

static double Number(string s) => double.Parse(s, CultureInfo.InvariantCulture);
#endif
