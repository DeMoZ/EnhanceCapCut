using System.Text.Json;

namespace EnhanceCapCut.Draft;

// Typed view of CapCut's `draft_info.json` (draft version 360000 / new_version 187.0.0), ported from the
// macOS version's DraftModel.swift. Only the fields the renderer and the export report need are modelled;
// everything else is ignored. All times are in microseconds, as in the draft.

public sealed class Draft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Duration { get; set; }
    public double Fps { get; set; } = 30;
    public CanvasConfig Canvas { get; set; } = new();
    public List<Track> Tracks { get; set; } = [];
    public Materials Materials { get; set; } = new();
    public DraftConfig Config { get; set; } = new();

    /// <summary>Parses a draft; throws only when the text isn't a JSON object.</summary>
    public static Draft Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("The draft is not a JSON object");
        return From(doc.RootElement);
    }

    public static Draft From(JsonElement c) => new()
    {
        Id = c.Str("id") ?? "",
        Name = c.Str("name") ?? "",
        Duration = c.Int64("duration") ?? 0,
        Fps = c.Num("fps") ?? 30,
        Canvas = c.Obj("canvas_config") is { } canvas ? CanvasConfig.From(canvas) : new(),
        Tracks = c.List("tracks", Track.From),
        Materials = c.Obj("materials") is { } m ? Materials.From(m) : new(),
        Config = c.Obj("config") is { } config ? DraftConfig.From(config) : new(),
    };
}

public sealed class CanvasConfig
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string Ratio { get; set; } = "original";

    public static CanvasConfig From(JsonElement c) => new()
    {
        Width = (int)(c.Num("width") ?? 1920),
        Height = (int)(c.Num("height") ?? 1080),
        Ratio = c.Str("ratio") ?? "original",
    };
}

public sealed class DraftConfig
{
    /// <summary>The speaker toggle on the main track.</summary>
    public bool VideoMute { get; set; }

    public static DraftConfig From(JsonElement c) => new() { VideoMute = c.Bool("video_mute") ?? false };
}

public sealed record TimeRange(long Start, long Duration)
{
    public long End => Start + Duration;

    public static TimeRange From(JsonElement c) => new(c.Int64("start") ?? 0, c.Int64("duration") ?? 0);
}

public sealed class Track
{
    public string Id { get; set; } = "";
    /// <summary>"video", "audio", "text", "sticker", "effect", "filter", ...</summary>
    public string Type { get; set; } = "";
    /// <summary>Bit flags of the track header toggles. Bit 0 is "mute"; bit 1 is "hide".</summary>
    public int Attribute { get; set; }
    public int Flag { get; set; }
    public List<Segment> Segments { get; set; } = [];

    public bool IsMuted => (Attribute & 1) != 0;
    public bool IsHidden => (Attribute & 2) != 0;

    public static Track? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? Lenient.NewId(),
        Type = c.Str("type") ?? "",
        Attribute = (int)(c.Num("attribute") ?? 0),
        Flag = (int)(c.Num("flag") ?? 0),
        Segments = c.List("segments", Segment.From),
    };
}

public sealed record Point2(double X, double Y)
{
    public static Point2? From(JsonElement c) =>
        c.ValueKind != JsonValueKind.Object ? null : new(c.Num("x") ?? 0, c.Num("y") ?? 0);
}

public sealed class Clip
{
    public Point2 Scale { get; set; } = new(1, 1);
    /// <summary>Degrees, clockwise on screen.</summary>
    public double Rotation { get; set; }
    /// <summary>Offset of the layer center in half-canvas units; +y is up.</summary>
    public Point2 Transform { get; set; } = new(0, 0);
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public double Alpha { get; set; } = 1;

    public static Clip From(JsonElement c)
    {
        var flip = c.Obj("flip");
        return new()
        {
            Scale = c.Obj("scale") is { } s ? Point2.From(s)! : new(1, 1),
            Rotation = c.Num("rotation") ?? 0,
            Transform = c.Obj("transform") is { } t ? Point2.From(t)! : new(0, 0),
            FlipHorizontal = flip?.Bool("horizontal") ?? false,
            FlipVertical = flip?.Bool("vertical") ?? false,
            Alpha = c.Num("alpha") ?? 1,
        };
    }
}

public sealed class Keyframe
{
    public long TimeOffset { get; set; }
    public List<double> Values { get; set; } = [];
    public string CurveType { get; set; } = "Line";

    public static Keyframe? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        TimeOffset = c.Int64("time_offset") ?? 0,
        Values = c.Doubles("values") ?? [],
        CurveType = c.Str("curveType") ?? "Line",
    };
}

public sealed class KeyframeGroup
{
    /// <summary>e.g. "KFTypePositionX", "KFTypeScaleX", "KFTypeRotation", "KFTypeAlpha", "KFTypeVolume".</summary>
    public string PropertyType { get; set; } = "";
    public List<Keyframe> Keyframes { get; set; } = [];

    public static KeyframeGroup? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        PropertyType = c.Str("property_type") ?? "",
        Keyframes = c.List("keyframe_list", Keyframe.From),
    };
}

public sealed class Segment
{
    public string Id { get; set; } = "";
    public string MaterialId { get; set; } = "";
    public TimeRange? SourceTimerange { get; set; }
    public TimeRange TargetTimerange { get; set; } = new(0, 0);
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Reverse { get; set; }
    public bool Visible { get; set; } = true;
    public bool IsToneModify { get; set; }
    public Clip? Clip { get; set; }
    public bool UniformScale { get; set; } = true;
    public List<string> ExtraMaterialRefs { get; set; } = [];
    public int RenderIndex { get; set; }
    public int TrackRenderIndex { get; set; }
    public List<KeyframeGroup> Keyframes { get; set; } = [];
    /// <summary>"Loop" playback of a clip shorter than its segment (not supported).</summary>
    public bool IsLoop { get; set; }

    public static Segment? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? Lenient.NewId(),
        MaterialId = c.Str("material_id") ?? "",
        SourceTimerange = c.Obj("source_timerange") is { } s ? TimeRange.From(s) : null,
        TargetTimerange = c.Obj("target_timerange") is { } t ? TimeRange.From(t) : new(0, 0),
        Speed = c.Num("speed") ?? 1,
        Volume = c.Num("volume") ?? 1,
        Reverse = c.Bool("reverse") ?? false,
        Visible = c.Bool("visible") ?? true,
        IsToneModify = c.Bool("is_tone_modify") ?? false,
        Clip = c.Obj("clip") is { } clip ? Clip.From(clip) : null,
        UniformScale = c.Obj("uniform_scale")?.Bool("on") ?? true,
        ExtraMaterialRefs = c.Strings("extra_material_refs") ?? [],
        RenderIndex = (int)(c.Num("render_index") ?? 0),
        TrackRenderIndex = (int)(c.Num("track_render_index") ?? 0),
        Keyframes = c.List("common_keyframes", KeyframeGroup.From),
        IsLoop = c.Bool("is_loop") ?? false,
    };
}

// MARK: - Materials

public sealed class Materials
{
    public List<VideoMaterial> Videos { get; set; } = [];
    public List<AudioMaterial> Audios { get; set; } = [];
    public List<TextMaterial> Texts { get; set; } = [];
    public List<CanvasMaterial> Canvases { get; set; } = [];
    public List<SpeedMaterial> Speeds { get; set; } = [];
    public List<AudioFadeMaterial> AudioFades { get; set; } = [];
    public List<CombinationMaterial> Drafts { get; set; } = [];
    public List<LoudnessMaterial> Loudnesses { get; set; } = [];
    public List<VocalSeparationMaterial> VocalSeparations { get; set; } = [];
    public List<RealtimeDenoiseMaterial> RealtimeDenoises { get; set; } = [];
    public List<VocalBeautifyMaterial> VocalBeautifys { get; set; } = [];
    public List<AudioEffectMaterial> AudioEffects { get; set; } = [];
    /// <summary>Every material of every category, with just the fields used for reporting.</summary>
    public Dictionary<string, List<GenericMaterial>> All { get; set; } = [];

    public static Materials From(JsonElement c)
    {
        var m = new Materials
        {
            Videos = c.List("videos", VideoMaterial.From),
            Audios = c.List("audios", AudioMaterial.From),
            Texts = c.List("texts", TextMaterial.From),
            Canvases = c.List("canvases", CanvasMaterial.From),
            Speeds = c.List("speeds", SpeedMaterial.From),
            AudioFades = c.List("audio_fades", AudioFadeMaterial.From),
            Drafts = c.List("drafts", CombinationMaterial.From),
            Loudnesses = c.List("loudnesses", LoudnessMaterial.From),
            VocalSeparations = c.List("vocal_separations", VocalSeparationMaterial.From),
            RealtimeDenoises = c.List("realtime_denoises", RealtimeDenoiseMaterial.From),
            VocalBeautifys = c.List("vocal_beautifys", VocalBeautifyMaterial.From),
            AudioEffects = c.List("audio_effects", AudioEffectMaterial.From),
        };
        foreach (var property in c.EnumerateObject())
        {
            var items = c.List(property.Name, GenericMaterial.From);
            if (items.Count > 0) m.All[property.Name] = items;
        }
        return m;
    }
}

/// <summary>The common subset of every material: enough to name it in the "not supported yet" list.</summary>
public sealed class GenericMaterial
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>For <c>material_animations</c>: the names of the text/sticker animations it holds.</summary>
    public List<string> AnimationNames { get; set; } = [];
    /// <summary>Toggles used by audio-processing materials (<c>enable</c>, <c>is_denoise</c>, <c>choice</c>).</summary>
    public bool? Enable { get; set; }
    public bool? IsDenoise { get; set; }
    public int? Choice { get; set; }

    public static GenericMaterial? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object || c.Str("id") is not { } id) return null;
        return new()
        {
            Id = id,
            Type = c.Str("type") ?? "",
            Name = c.Str("name") ?? c.Str("effect_name") ?? c.Str("material_name") ?? "",
            Path = c.Str("path") ?? "",
            AnimationNames = c.List("animations", a => a.ValueKind != JsonValueKind.Object ? null
                : a.Str("name") ?? a.Str("type") ?? "animation"),
            Enable = c.Bool("enable"),
            IsDenoise = c.Bool("is_denoise"),
            Choice = c.Num("choice") is { } choice ? (int)choice : null,
        };
    }
}

public sealed class Crop
{
    // Normalized corners, origin at the top-left of the source frame.
    public Point2 UpperLeft { get; set; } = new(0, 0);
    public Point2 LowerRight { get; set; } = new(1, 1);

    public bool IsIdentity =>
        UpperLeft.X <= 0.0001 && UpperLeft.Y <= 0.0001 && LowerRight.X >= 0.9999 && LowerRight.Y >= 0.9999;

    public static Crop From(JsonElement c) => new()
    {
        UpperLeft = new(c.Num("upper_left_x") ?? 0, c.Num("upper_left_y") ?? 0),
        LowerRight = new(c.Num("lower_right_x") ?? 1, c.Num("lower_right_y") ?? 1),
    };
}

public sealed class VideoMaterial
{
    public string Id { get; set; } = "";
    /// <summary>"video" or "photo".</summary>
    public string Type { get; set; } = "video";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Duration { get; set; }
    public bool HasAudio { get; set; } = true;
    public bool HasSoundSeparated { get; set; }
    public string ReversePath { get; set; } = "";
    public Crop Crop { get; set; } = new();
    /// <summary>"Stabilize" level, 0 = off.</summary>
    public int StableLevel { get; set; }
    /// <summary>"Remove background" mode (<c>matting.flag</c>), 0 = off; 1 and 3 seen (custom / auto cutout).</summary>
    public int MattingFlag { get; set; }
    /// <summary>
    /// AI processing applied to the clip: <c>video_algorithm.algorithms[].type</c> plus the names of the
    /// non-null per-feature configs (e.g. "super_resolution", "deflicker").
    /// </summary>
    public List<string> Algorithms { get; set; } = [];
    /// <summary>"Retouch" (face beauty) is on.</summary>
    public bool BeautyMode { get; set; }

    /// <summary>Per-feature configs inside <c>video_algorithm</c> that change the picture when set.</summary>
    static readonly string[] AlgorithmConfigs =
    [
        "super_resolution", "deflicker", "noise_reduction", "motion_blur_config", "smart_complement_frame",
        "complement_frame_config", "ai_expression_driven", "ai_motion_driven", "mouth_shape_driver",
    ];

    public static VideoMaterial? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var algorithms = new List<string>();
        if (c.Obj("video_algorithm") is { } a)
        {
            algorithms = a.List("algorithms", i => i.ValueKind != JsonValueKind.Object ? null : i.Str("type") ?? "unknown");
            algorithms.AddRange(AlgorithmConfigs.Where(key => a.Get(key) is not null));
        }
        var stable = c.Obj("stable")?.Num("stable_level");
        var matting = c.Obj("matting")?.Num("flag");
        return new()
        {
            Id = c.Str("id") ?? "",
            Type = c.Str("type") ?? "video",
            Path = c.Str("path") ?? "",
            Name = c.Str("material_name") ?? "",
            Width = (int)(c.Num("width") ?? 0),
            Height = (int)(c.Num("height") ?? 0),
            Duration = c.Int64("duration") ?? 0,
            HasAudio = c.Bool("has_audio") ?? true,
            HasSoundSeparated = c.Bool("has_sound_separated") ?? false,
            ReversePath = c.Str("reverse_path") ?? "",
            Crop = c.Obj("crop") is { } crop ? Crop.From(crop) : new(),
            StableLevel = stable is { } s ? (int)s : 0,
            MattingFlag = matting is { } f ? (int)f : 0,
            Algorithms = algorithms,
            BeautyMode = c.Bool("is_set_beauty_mode") ?? false,
        };
    }
}

public sealed class AudioMaterial
{
    public string Id { get; set; } = "";
    /// <summary>"music", "extract_music", "record", "video_original_sound", "text_to_audio", ...</summary>
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Duration { get; set; }

    public static AudioMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Type = c.Str("type") ?? "",
        Path = c.Str("path") ?? "",
        Name = c.Str("name") ?? "",
        Duration = c.Int64("duration") ?? 0,
    };
}

public sealed class CanvasMaterial
{
    public string Id { get; set; } = "";
    /// <summary>"canvas_color", "canvas_blur" or "canvas_image".</summary>
    public string Type { get; set; } = "canvas_color";
    public string Color { get; set; } = "";
    public double Blur { get; set; }
    public string Image { get; set; } = "";

    public static CanvasMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Type = c.Str("type") ?? "canvas_color",
        Color = c.Str("color") ?? "",
        Blur = c.Num("blur") ?? 0,
        Image = c.Str("image") ?? "",
    };
}

public sealed class SpeedMaterial
{
    public string Id { get; set; } = "";
    /// <summary>0 = constant, 1 = curve.</summary>
    public int Mode { get; set; }
    public double Speed { get; set; } = 1;
    public string CurveName { get; set; } = "";
    /// <summary>x: normalized position in the segment's timeline duration, y: speed multiplier.</summary>
    public List<Point2> CurvePoints { get; set; } = [];

    public static SpeedMaterial? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var curve = c.Obj("curve_speed");
        return new()
        {
            Id = c.Str("id") ?? "",
            Mode = (int)(c.Num("mode") ?? 0),
            Speed = c.Num("speed") ?? 1,
            CurveName = curve?.Str("name") ?? "",
            CurvePoints = curve?.List("speed_points", Point2.From) ?? [],
        };
    }
}

public sealed class AudioFadeMaterial
{
    public string Id { get; set; } = "";
    public long FadeIn { get; set; }
    public long FadeOut { get; set; }

    public static AudioFadeMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        FadeIn = c.Int64("fade_in_duration") ?? 0,
        FadeOut = c.Int64("fade_out_duration") ?? 0,
    };
}

/// <summary>Per-clip "Normalize loudness": CapCut stores the measured loudness and the target.</summary>
public sealed class LoudnessMaterial
{
    public string Id { get; set; } = "";
    public bool Enable { get; set; }
    public double TargetLoudness { get; set; } = -23;
    public double? AverageLoudness { get; set; }

    /// <summary>Gain in dB that brings the clip to its target, or 0 when disabled.</summary>
    public double GainDB => Enable && AverageLoudness is { } avg && avg > -70 ? TargetLoudness - avg : 0;

    public static LoudnessMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Enable = c.Bool("enable") ?? false,
        TargetLoudness = c.Num("target_loudness") ?? -23,
        AverageLoudness = c.Obj("loudness_param")?.Num("avg_loudness"),
    };
}

/// <summary>
/// Per-clip "Isolate voice": <c>choice</c> 0 = off, 1 = "Remove vocal" (keep the background),
/// 2 = "Keep vocal". CapCut renders the result into <c>production_path</c> (obfuscated, not read by us).
/// </summary>
public sealed class VocalSeparationMaterial
{
    public string Id { get; set; } = "";
    public int Choice { get; set; }

    public static VocalSeparationMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Choice = c.Num("choice") is { } choice ? (int)choice : 0,
    };
}

/// <summary>Per-clip "Reduce noise". CapCut applies its model while playing/exporting; nothing is cached.</summary>
public sealed class RealtimeDenoiseMaterial
{
    public string Id { get; set; } = "";
    public bool IsDenoise { get; set; }
    /// <summary>Strength 0...1 (always 0.85 in the author's projects; not adjustable in the UI).</summary>
    public double Rate { get; set; } = 0.85;

    public static RealtimeDenoiseMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        IsDenoise = c.Bool("is_denoise") ?? false,
        Rate = c.Num("denoise_rate") ?? 0.85,
    };
}

/// <summary>Per-clip "Enhance voice". CapCut caches its result in <c>production_path</c> (obfuscated, not read by us).</summary>
public sealed class VocalBeautifyMaterial
{
    public string Id { get; set; } = "";
    public bool Enable { get; set; }
    /// <summary>How much of the non-voice sound is kept, 0...100 (75 by default; 100 on a few clips).</summary>
    public double AmbientSoundLevel { get; set; } = 75;

    public static VocalBeautifyMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Enable = c.Bool("enable") ?? false,
        AmbientSoundLevel = c.Num("ambient_sound_level") ?? 75,
    };
}

/// <summary>
/// Per-clip voice filter ("Voice filters" in CapCut: "Booming", "Old Hollywood", "Live House"…).
/// The effect itself is a processor graph in CapCut's effect cache (see <see cref="VoiceFilterGraph"/>).
/// </summary>
public sealed class AudioEffectMaterial
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The cached effect directory (absolute, may be stale).</summary>
    public string Path { get; set; } = "";
    /// <summary>The effect's id in CapCut's library; also the cache folder name (<c>Cache/effect/&lt;id&gt;/&lt;hash&gt;</c>).</summary>
    public string ResourceID { get; set; } = "";
    public string CategoryName { get; set; } = "";
    /// <summary>The "strength" slider, 0...1 (default 1; the only parameter the voice filters expose).</summary>
    public double Strength { get; set; } = 1;

    public static AudioEffectMaterial? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var parameters = c.List("audio_adjust_params", p => p.ValueKind != JsonValueKind.Object ? null
            : new AdjustParam(p.Str("name") ?? "", p.Num("value") ?? p.Num("default_value")));
        return new()
        {
            Id = c.Str("id") ?? "",
            Name = c.Str("name") ?? "",
            Path = c.Str("path") ?? "",
            ResourceID = c.Str("resource_id") ?? c.Str("third_resource_id") ?? c.Str("effect_id") ?? "",
            CategoryName = c.Str("category_name") ?? "",
            Strength = parameters.FirstOrDefault(p => p.Name == "strength")?.Value ?? parameters.FirstOrDefault()?.Value ?? 1,
        };
    }

    sealed record AdjustParam(string Name, double? Value);
}

/// <summary>A compound clip: a nested draft embedded in the parent draft.</summary>
public sealed class CombinationMaterial
{
    public string Id { get; set; } = "";
    public Draft? Draft { get; set; }

    public static CombinationMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Draft = c.Obj("draft") is { } d ? Draft.From(d) : null,
    };
}

public sealed class TextMaterial
{
    public string Id { get; set; } = "";
    /// <summary>"text" or "subtitle".</summary>
    public string Type { get; set; } = "text";
    /// <summary>JSON string: <c>{"text": "...", "styles": [...]}</c>.</summary>
    public string Content { get; set; } = "";
    public string FontPath { get; set; } = "";
    public double FontSize { get; set; } = 15;
    public string TextColor { get; set; } = "#FFFFFF";
    public int Alignment { get; set; } = 1;
    public double LineSpacing { get; set; } = 0.02;
    public double LetterSpacing { get; set; }
    public double LineMaxWidth { get; set; } = 0.82;
    public string BorderColor { get; set; } = "";
    public double BorderWidth { get; set; } = 0.08;
    public string BackgroundColor { get; set; } = "";
    public double BackgroundAlpha { get; set; } = 1;
    public double BackgroundRoundRadius { get; set; }
    public double BackgroundWidth { get; set; } = 0.14;
    public double BackgroundHeight { get; set; } = 0.14;
    public bool HasShadow { get; set; }
    public string ShadowColor { get; set; } = "";
    public double ShadowAlpha { get; set; } = 0.9;
    public double ShadowSmoothing { get; set; } = 0.45;
    public double ShadowDistance { get; set; } = 5;
    public double ShadowAngle { get; set; } = -45;
    public double GlobalAlpha { get; set; } = 1;
    public double BoldWidth { get; set; }
    public double ItalicDegree { get; set; }
    public bool Underline { get; set; }
    /// <summary><c>1</c> = vertical typesetting (unsupported, rendered horizontally).</summary>
    public int Typesetting { get; set; }
    /// <summary>"Curve" text (bent along an arc), not supported.</summary>
    public bool CurveEnabled { get; set; }

    public static TextMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Type = c.Str("type") ?? "text",
        Content = c.Str("content") ?? "",
        FontPath = c.Str("font_path") ?? "",
        FontSize = c.Num("font_size") ?? 15,
        TextColor = c.Str("text_color") ?? "#FFFFFF",
        Alignment = (int)(c.Num("alignment") ?? 1),
        LineSpacing = c.Num("line_spacing") ?? 0.02,
        LetterSpacing = c.Num("letter_spacing") ?? 0,
        LineMaxWidth = c.Num("line_max_width") ?? 0.82,
        BorderColor = c.Str("border_color") ?? "",
        BorderWidth = c.Num("border_width") ?? 0.08,
        BackgroundColor = c.Str("background_color") ?? "",
        BackgroundAlpha = c.Num("background_alpha") ?? 1,
        BackgroundRoundRadius = c.Num("background_round_radius") ?? 0,
        BackgroundWidth = c.Num("background_width") ?? 0.14,
        BackgroundHeight = c.Num("background_height") ?? 0.14,
        HasShadow = c.Bool("has_shadow") ?? false,
        ShadowColor = c.Str("shadow_color") ?? "",
        ShadowAlpha = c.Num("shadow_alpha") ?? 0.9,
        ShadowSmoothing = c.Num("shadow_smoothing") ?? 0.45,
        ShadowDistance = c.Num("shadow_distance") ?? 5,
        ShadowAngle = c.Num("shadow_angle") ?? -45,
        GlobalAlpha = c.Num("global_alpha") ?? 1,
        BoldWidth = c.Num("bold_width") ?? 0,
        ItalicDegree = c.Num("italic_degree") ?? 0,
        Underline = c.Bool("underline") ?? false,
        Typesetting = (int)(c.Num("typesetting") ?? 0),
        CurveEnabled = c.Obj("text_curve")?.Bool("enable") ?? false,
    };
}

/// <summary>The rich-text payload stored as a JSON string in <see cref="TextMaterial.Content"/>.</summary>
public sealed class TextContent
{
    public string Text { get; set; } = "";
    public List<TextStyle> Styles { get; set; } = [];

    public static TextContent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var c = doc.RootElement;
            if (c.ValueKind != JsonValueKind.Object) return null;
            return new() { Text = c.Str("text") ?? "", Styles = c.List("styles", TextStyle.From) };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class TextStyle
{
    /// <summary>UTF-16 offsets [start, end).</summary>
    public List<int> Range { get; set; } = [];
    public double? Size { get; set; }
    public string? FontPath { get; set; }
    /// <summary>RGB in 0...1.</summary>
    public List<double>? FillColor { get; set; }
    public double? FillAlpha { get; set; }
    public List<TextStroke> Strokes { get; set; } = [];
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    /// <summary>Set when the text uses a CapCut text-effect package (not supported yet).</summary>
    public string? EffectStylePath { get; set; }

    public static TextStyle? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var style = new TextStyle
        {
            Range = c.Ints("range") ?? [],
            Size = c.Num("size"),
            FontPath = c.Obj("font")?.Str("path"),
            Strokes = c.List("strokes", TextStroke.From),
            Bold = c.Bool("bold") ?? false,
            Italic = c.Bool("italic") ?? false,
            Underline = c.Bool("underline") ?? false,
            EffectStylePath = c.Obj("effectStyle")?.Str("path"),
        };
        if (c.Obj("fill") is { } fill)
        {
            style.FillAlpha = fill.Num("alpha");
            if (fill.Obj("content")?.Obj("solid") is { } solid)
            {
                style.FillColor = solid.Doubles("color");
                if (solid.Num("alpha") is { } a) style.FillAlpha = (style.FillAlpha ?? 1) * a;
            }
        }
        return style;
    }
}

public sealed class TextStroke
{
    /// <summary>Relative to the font size.</summary>
    public double Width { get; set; } = 0.06;
    public List<double> Color { get; set; } = [0, 0, 0];
    public double Alpha { get; set; } = 1;

    public static TextStroke? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var stroke = new TextStroke { Width = c.Num("width") ?? 0.06, Alpha = c.Num("alpha") ?? 1 };
        if (c.Obj("content")?.Obj("solid") is { } solid)
        {
            stroke.Color = solid.Doubles("color") ?? stroke.Color;
            if (solid.Num("alpha") is { } a) stroke.Alpha *= a;
        }
        return stroke;
    }
}
