namespace EnhanceCapCut.Draft;

/// <summary>A draft resolved into what the renderer needs: visual layers in paint order and audio clips.</summary>
public sealed class Timeline(string name, int canvasWidth, int canvasHeight, long duration, double fps,
    IReadOnlyList<VisualLayer> layers, IReadOnlyList<AudioClip> audioClips, RenderReport report)
{
    public string Name { get; } = name;
    /// <summary>The draft's canvas in pixels. Layout is relative, so any output size can be rendered.</summary>
    public int CanvasWidth { get; } = canvasWidth;
    public int CanvasHeight { get; } = canvasHeight;
    public long Duration { get; } = duration;
    public double Fps { get; } = fps;
    /// <summary>Bottom to top.</summary>
    public IReadOnlyList<VisualLayer> Layers { get; } = layers;
    public IReadOnlyList<AudioClip> AudioClips { get; } = audioClips;
    public RenderReport Report { get; } = report;
    /// <summary>Filters on filter tracks (each colours the layers painted below it).</summary>
    public IReadOnlyList<FilterLayer> Filters { get; init; } = [];

    /// <summary>The same timeline without caption (auto-subtitle) clips.</summary>
    public Timeline WithoutSubtitles() => new(Name, CanvasWidth, CanvasHeight, Duration, Fps,
        Layers.Where(l => l.Content is not TextContentLayer { Spec.Material.Type: "subtitle" }).ToList(), AudioClips, Report)
    {
        Filters = Filters,
    };

    public bool HasSubtitles => Layers.Any(l => l.Content is TextContentLayer { Spec.Material.Type: "subtitle" });
}

/// <summary>What a visual layer shows.</summary>
public abstract record VisualContent;
public sealed record VideoContent(string Path) : VisualContent;
public sealed record PhotoContent(string Path) : VisualContent;
public sealed record TextContentLayer(TextLayerSpec Spec) : VisualContent;
public sealed record CompoundContent(Timeline Timeline) : VisualContent;
/// <summary>A sticker clip the renderer draws itself (<paramref name="Name"/> = its name in CapCut).</summary>
public sealed record StickerContent(string Name, ClockSticker Clock) : VisualContent;

/// <param name="Animations">Intro / outro / loop animations.</param>
/// <param name="Glow">The approximated look of the text's CapCut text effect, if any (<see cref="TextEffectLook"/>).</param>
public sealed record TextLayerSpec(TextMaterial Material, TextContent Content, TextAnimations? Animations = null,
    TextGlow? Glow = null)
{
    public TextAnimations Animations { get; init; } = Animations ?? TextAnimations.None;
}

/// <summary>Layer properties that can be keyframed.</summary>
public enum LayerProperty { PositionX, PositionY, ScaleX, ScaleY, Rotation, Alpha, Volume }

public static class LayerProperties
{
    static readonly Dictionary<string, LayerProperty> ByType = new()
    {
        ["KFTypePositionX"] = LayerProperty.PositionX,
        ["KFTypePositionY"] = LayerProperty.PositionY,
        ["KFTypeScaleX"] = LayerProperty.ScaleX,
        ["KFTypeScaleY"] = LayerProperty.ScaleY,
        ["KFTypeRotation"] = LayerProperty.Rotation,
        ["KFTypeAlpha"] = LayerProperty.Alpha,
        ["KFTypeVolume"] = LayerProperty.Volume,
    };

    /// <summary>The property for a keyframe group's <c>property_type</c>, or null.</summary>
    public static LayerProperty? FromType(string type) => ByType.TryGetValue(type, out var p) ? p : null;
}

/// <param name="Rotation">Degrees, clockwise.</param>
/// <param name="PositionX">Half-canvas units.</param>
/// <param name="PositionY">Half-canvas units, +y up.</param>
public sealed record LayerTransform(double ScaleX, double ScaleY, double Rotation, double PositionX, double PositionY,
    bool FlipH, bool FlipV, double Alpha);

public sealed class VisualLayer(string id, VisualContent content, int trackIndex, int renderIndex, bool isMainTrack,
    TimeRange target, TimeMap timeMap, Crop crop, Clip clip, bool uniformScale,
    IReadOnlyDictionary<LayerProperty, KeyframeTrack> keyframes, CanvasMaterial? canvas, Cutout? cutout = null,
    ChromaKey? chromaKey = null, IReadOnlyList<ClipMask>? masks = null)
{
    public string Id { get; } = id;
    public VisualContent Content { get; } = content;
    public int TrackIndex { get; } = trackIndex;
    public int RenderIndex { get; } = renderIndex;
    /// <summary>The first video track: its clips get the canvas background.</summary>
    public bool IsMainTrack { get; } = isMainTrack;
    public TimeRange Target { get; } = target;
    public TimeMap TimeMap { get; } = timeMap;
    public Crop Crop { get; } = crop;
    public Clip Clip { get; } = clip;
    public bool UniformScale { get; } = uniformScale;
    public IReadOnlyDictionary<LayerProperty, KeyframeTrack> Keyframes { get; } = keyframes;
    public CanvasMaterial? Canvas { get; } = canvas;
    /// <summary>"Remove background": CapCut's mask frames, applied as the clip's alpha.</summary>
    public Cutout? Cutout { get; } = cutout;
    /// <summary>The clip's own filters, in order.</summary>
    public IReadOnlyList<ColorFilter> Filters { get; set; } = [];
    /// <summary>"Chroma key", applied to the clip's frames before the cutout.</summary>
    public ChromaKey? ChromaKey { get; } = chromaKey;
    /// <summary>Clip masks (shapes, brush); what any of them keeps is shown.</summary>
    public IReadOnlyList<ClipMask> Masks { get; } = masks ?? [];
    /// <summary>Video effects on the clip that we render, in order.</summary>
    public IReadOnlyList<VideoEffect> Effects { get; set; } = [];
    /// <summary>The transition into the next clip on this track.</summary>
    public LayerTransition? TransitionOut { get; set; }

    public bool IsActive(long t) => t >= Target.Start && t < Target.End;

    /// <summary>Clip transform at timeline time <paramref name="t"/>, with keyframes applied.</summary>
    public LayerTransform Transform(long t)
    {
        var offset = t - Target.Start;
        double? Kf(LayerProperty p) => Keyframes.TryGetValue(p, out var track) ? track.Value(offset) : null;
        var sx = Kf(LayerProperty.ScaleX) ?? Clip.Scale.X;
        var sy = Kf(LayerProperty.ScaleY) ?? Clip.Scale.Y;
        if (UniformScale)
        {
            if (Keyframes.ContainsKey(LayerProperty.ScaleX)) sy = sx;
            else if (Keyframes.ContainsKey(LayerProperty.ScaleY)) sx = sy;
        }
        return new(sx, sy, Kf(LayerProperty.Rotation) ?? Clip.Rotation,
            Kf(LayerProperty.PositionX) ?? Clip.Transform.X, Kf(LayerProperty.PositionY) ?? Clip.Transform.Y,
            Clip.FlipHorizontal, Clip.FlipVertical, Kf(LayerProperty.Alpha) ?? Clip.Alpha);
    }
}

/// <summary>CapCut's "Remove background" result for a clip: a folder of mask frames.</summary>
/// <param name="Directory">The <c>mask</c> folder: one raw 8-bit frame per file, named by its source time in µs.</param>
/// <param name="Inverted">"Invert": keep the background, remove the subject.</param>
public sealed record Cutout(string Directory, bool Inverted = false)
{
    /// <summary>
    /// The mask folder in a matting result folder: <c>&lt;folder&gt;/&lt;customID&gt;/&lt;n&gt;/mask</c> for a custom
    /// cutout, <c>&lt;folder&gt;/&lt;n&gt;/mask</c> for the auto cutout (<c>n</c> = 5 and 2 so far). Only folders with
    /// frames count.
    /// </summary>
    public static string? Locate(string folder, string? customId)
    {
        static string? Masks(string root)
        {
            if (!System.IO.Directory.Exists(root)) return null;
            return System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal)
                .Select(d => System.IO.Path.Combine(d, "mask"))
                .FirstOrDefault(m => System.IO.Directory.Exists(m) && System.IO.Directory.EnumerateFileSystemEntries(m).Any());
        }
        if (customId is null) return Masks(folder);
        return customId.Length == 0 ? null : Masks(System.IO.Path.Combine(folder, customId));
    }
}

/// <param name="Volume">Linear gain; CapCut allows values above 1.</param>
public sealed record AudioClip(string Path, TimeRange Target, TimeMap TimeMap, double Volume, long FadeIn, long FadeOut,
    bool PreservePitch)
{
    /// <summary>"Isolate voice" / "Reduce noise" / "Enhance voice", applied to the source before the mix.</summary>
    public VoiceEffects Voice { get; init; } = new();
    /// <summary>Volume keyframes as a multiplier of <see cref="Volume"/>, offsets from the clip start (null = constant).</summary>
    public KeyframeTrack? VolumeEnvelope { get; init; }
}

/// <summary>CapCut's per-clip voice processing.</summary>
public sealed record VoiceEffects
{
    public enum SeparationMode
    {
        None = 0,
        /// <summary>"Remove vocal": keep only the background (music, ambience).</summary>
        RemoveVoice = 1,
        /// <summary>"Keep vocal": keep only the voice.</summary>
        KeepVoice = 2,
    }

    public SeparationMode Separation { get; init; } = SeparationMode.None;
    /// <summary>"Reduce noise" strength 0...1, null when off.</summary>
    public double? Denoise { get; init; }
    /// <summary>"Enhance voice" ambient sound level 0...1 (<c>ambient_sound_level</c> / 100), null when off.</summary>
    public double? Enhance { get; init; }
    /// <summary>A voice filter ("Booming", "Old Hollywood"…), applied after the other stages.</summary>
    public VoiceFilter? Filter { get; init; }

    public bool IsEmpty => Separation == SeparationMode.None && Denoise is null && Enhance is null && Filter is null;
}

/// <summary>One of CapCut's "Voice filters": its cached processor graph and the strength slider.</summary>
/// <param name="Directory">The cached effect directory holding <c>mdsp_preset.json</c> (and any audio it loops).</param>
/// <param name="Strength">0...1.</param>
public sealed record VoiceFilter(string Name, string Directory, double Strength = 1)
{
    /// <summary>
    /// Roots of CapCut's effect cache. On Windows assumed to mirror the Mac layout under <c>%LOCALAPPDATA%\CapCut</c>;
    /// on macOS (where the tests and the comparison with the Swift version run) the sandboxed and direct-download apps'.
    /// </summary>
    public static IReadOnlyList<string> CacheRoots
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return [System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CapCut", "User Data", "Cache", "effect")];
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return
            [
                System.IO.Path.Combine(home, "Library/Containers/com.lemon.lvoverseas/Data/Movies/CapCut/User Data/Cache/effect"),
                System.IO.Path.Combine(home, "Movies/CapCut/User Data/Cache/effect"),
            ];
        }
    }

    /// <summary>
    /// The cached directory for an effect: <paramref name="path"/> (or a folder in it) when it holds a preset,
    /// otherwise the first <c>&lt;cache&gt;/&lt;resourceID&gt;/&lt;hash&gt;</c> that does.
    /// </summary>
    public static string? Locate(string? path, string resourceID, IReadOnlyList<string>? cacheRoots = null)
    {
        static bool HasPreset(string dir) => File.Exists(System.IO.Path.Combine(dir, "mdsp_preset.json"));
        static string? Search(string folder)
        {
            if (HasPreset(folder)) return folder;
            if (!System.IO.Directory.Exists(folder)) return null;
            return System.IO.Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal).FirstOrDefault(HasPreset);
        }
        if (path is not null && Search(path) is { } dir) return dir;
        if (resourceID.Length == 0) return null;
        return (cacheRoots ?? CacheRoots).Select(root => Search(System.IO.Path.Combine(root, resourceID))).FirstOrDefault(d => d is not null);
    }
}

public sealed class RenderReport
{
    public sealed record Item(string Category, string Name, int Count)
    {
        public string Id => Category + "/" + Name;
    }

    /// <summary>Effects, transitions, stickers, filters... present in the project but not rendered.</summary>
    public List<Item> Unsupported { get; set; } = [];
    /// <summary>Media files referenced by the project that don't exist on disk (rendered as empty).</summary>
    public List<string> MissingFiles { get; set; } = [];

    public bool IsEmpty => Unsupported.Count == 0 && MissingFiles.Count == 0;
}
