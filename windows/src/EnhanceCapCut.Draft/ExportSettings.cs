using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnhanceCapCut.Draft;

/// <summary>
/// Export parameters chosen in the window (the C# counterpart of the macOS version's ExportSettings.swift).
/// Codecs are the ones Windows encodes natively through Media Foundation; ProRes has no Windows encoder.
/// </summary>
public sealed class ExportSettings
{
    public enum AspectRatio { Project, Portrait, Square, Landscape }
    public enum Resolution { Project = 0, P720 = 720, P1080 = 1080, P1440 = 1440, P2160 = 2160 }
    public enum VideoCodec { H264, Hevc, Hevc10 }
    public enum RateControlMode { Quality, Bitrate }

    public AspectRatio Aspect { get; set; } = AspectRatio.Project;
    public Resolution OutputResolution { get; set; } = Resolution.Project;
    public VideoCodec Codec { get; set; } = VideoCodec.Hevc10;
    public RateControlMode RateControl { get; set; } = RateControlMode.Quality;
    /// <summary>0...100, mapped to the encoder's quality setting (CRF-like: constant quality, variable size).</summary>
    public double Quality { get; set; } = 65;
    /// <summary>Megabits per second, used when <see cref="RateControl"/> is <see cref="RateControlMode.Bitrate"/>.</summary>
    public double BitrateMbps { get; set; } = 16;
    /// <summary>0 = from project.</summary>
    public double FrameRate { get; set; }
    /// <summary>Burn CapCut captions (auto-subtitles) into the picture. Plain text clips are always rendered.</summary>
    public bool BurnInSubtitles { get; set; } = true;
    public bool NormalizeLoudness { get; set; }
    public double TargetLUFS { get; set; } = -14;
    public string OutputFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    /// <summary>Tokens: {project} {date} {time} {res} {codec} {fps}.</summary>
    public string FileNameTemplate { get; set; } = "{project}_{date}_{time}";

    // Raw values shared with the macOS version's settings.
    static readonly Dictionary<AspectRatio, string> AspectNames = new()
    {
        [AspectRatio.Project] = "project", [AspectRatio.Portrait] = "9:16",
        [AspectRatio.Square] = "1:1", [AspectRatio.Landscape] = "16:9",
    };
    static readonly Dictionary<VideoCodec, string> CodecNames = new()
    {
        [VideoCodec.H264] = "h264", [VideoCodec.Hevc] = "hevc", [VideoCodec.Hevc10] = "hevc10",
    };

    public static string Title(AspectRatio a) => a == AspectRatio.Project ? Localization.L("From project") : AspectNames[a];
    public static string Title(Resolution r) => r switch
    {
        Resolution.Project => Localization.L("From project"),
        Resolution.P2160 => "4K (2160p)",
        _ => $"{(int)r}p",
    };
    public static string Title(VideoCodec c) => c switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC (8-bit)",
        _ => "HEVC (10-bit)",
    };
    public static string RawName(VideoCodec c) => CodecNames[c];
    public string FileExtension => "mp4";

    // MARK: Derived values

    /// <summary>Output size in pixels (even numbers, as encoders require).</summary>
    public (int Width, int Height) OutputSize(int canvasWidth, int canvasHeight)
    {
        var ratio = Aspect switch
        {
            AspectRatio.Portrait => 9.0 / 16.0,
            AspectRatio.Square => 1,
            AspectRatio.Landscape => 16.0 / 9.0,
            _ => (double)canvasWidth / Math.Max(canvasHeight, 1),
        };
        // Keep the canvas's short side, or use the chosen one.
        double shortSide = OutputResolution == Resolution.Project ? Math.Min(canvasWidth, canvasHeight) : (int)OutputResolution;
        double w, h;
        if (ratio >= 1) { h = shortSide; w = shortSide * ratio; } else { w = shortSide; h = shortSide / ratio; }
        static int Even(double v) => (int)Math.Max(2, Math.Round(v / 2, MidpointRounding.AwayFromZero) * 2);
        return (Even(w), Even(h));
    }

    public double Fps(double project) => FrameRate > 0 ? FrameRate : project > 0 ? project : 30;

    static readonly char[] Unsafe = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    public string FileName(string project, int width, int height, double fps, DateTime? date = null)
    {
        // The invariant culture always uses Gregorian dates, regardless of the system calendar (e.g. Buddhist in Thailand).
        var d = date ?? DateTime.Now;
        var name = FileNameTemplate
            .Replace("{project}", project)
            .Replace("{date}", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", d.ToString("HHmmss", CultureInfo.InvariantCulture))
            .Replace("{res}", $"{width}x{height}")
            .Replace("{codec}", CodecNames[Codec])
            .Replace("{fps}", fps == Math.Round(fps) ? ((int)fps).ToString(CultureInfo.InvariantCulture)
                : fps.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (var c in Unsafe) name = name.Replace(c, '-');
        if (string.IsNullOrWhiteSpace(name)) name = project;
        return name + "." + FileExtension;
    }

    // MARK: Persistence

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EnhanceCapCut", "settings.json");

    /// <summary>
    /// Settings saved by an older version may lack newer keys or hold values this version doesn't know:
    /// each key falls back to its default on its own.
    /// </summary>
    public static ExportSettings Parse(string json)
    {
        var s = new ExportSettings();
        JsonElement c;
        try
        {
            using var doc = JsonDocument.Parse(json);
            c = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return s;
        }
        if (c.ValueKind != JsonValueKind.Object) return s;
        if (c.Str("aspect") is { } a && AspectNames.FirstOrDefault(p => p.Value == a) is { Value: not null } aspect) s.Aspect = aspect.Key;
        if (c.Get("resolution") is { ValueKind: JsonValueKind.Number } r && r.TryGetInt32(out var res)
            && Enum.IsDefined(typeof(Resolution), res)) s.OutputResolution = (Resolution)res;
        if (c.Str("codec") is { } k && CodecNames.FirstOrDefault(p => p.Value == k) is { Value: not null } codec) s.Codec = codec.Key;
        if (c.Str("rateControl") is { } rc) s.RateControl = rc == "bitrate" ? RateControlMode.Bitrate : RateControlMode.Quality;
        if (c.Get("quality") is { ValueKind: JsonValueKind.Number } q) s.Quality = q.GetDouble();
        if (c.Get("bitrateMbps") is { ValueKind: JsonValueKind.Number } b) s.BitrateMbps = b.GetDouble();
        if (c.Get("frameRate") is { ValueKind: JsonValueKind.Number } f) s.FrameRate = f.GetDouble();
        if (c.Bool("burnInSubtitles") is { } burn) s.BurnInSubtitles = burn;
        if (c.Bool("normalizeLoudness") is { } normalize) s.NormalizeLoudness = normalize;
        if (c.Get("targetLUFS") is { ValueKind: JsonValueKind.Number } t) s.TargetLUFS = t.GetDouble();
        if (c.Str("outputFolder") is { } folder) s.OutputFolder = folder;
        if (c.Str("fileNameTemplate") is { } template) s.FileNameTemplate = template;
        return s;
    }

    public string ToJson() => new JsonObject
    {
        ["aspect"] = AspectNames[Aspect],
        ["resolution"] = (int)OutputResolution,
        ["codec"] = CodecNames[Codec],
        ["rateControl"] = RateControl == RateControlMode.Bitrate ? "bitrate" : "quality",
        ["quality"] = Quality,
        ["bitrateMbps"] = BitrateMbps,
        ["frameRate"] = FrameRate,
        ["burnInSubtitles"] = BurnInSubtitles,
        ["normalizeLoudness"] = NormalizeLoudness,
        ["targetLUFS"] = TargetLUFS,
        ["outputFolder"] = OutputFolder,
        ["fileNameTemplate"] = FileNameTemplate,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static ExportSettings Load(string? path = null)
    {
        try { return Parse(File.ReadAllText(path ?? DefaultPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new ExportSettings(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }
}
