using System.Numerics;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// Builds the composited frame of a timeline at increasing times (port of the macOS FrameCompositor).
///
/// Each frame takes two passes: first every active layer's content is brought up to date in its own bitmap (video
/// frames uploaded and turned upright, nested compound clips rendered, text rasterized once per clip); then one
/// Direct2D pass draws the canvas background and the layers in paint order with their <see cref="Layout"/>
/// placement, crop and opacity.
/// </summary>
public sealed class Compositor : IDisposable
{
    readonly Graphics graphics;
    readonly Timeline timeline;
    readonly ImageLoader photos;
    readonly bool ownsPhotos;
    readonly TextImageRenderer text;
    readonly List<string> warnings;
    readonly Dictionary<string, VideoLayer> videos = [];
    readonly HashSet<string> failed = [];
    readonly Dictionary<string, Compositor> nested = [];
    readonly Dictionary<string, (ID2D1Bitmap1? Bitmap, float Scale)> texts = [];
    readonly Dictionary<string, CutoutLayer> cutouts = [];
    readonly ColorFilters colorFilters;
    /// <summary>Clips' frames through their own filters (two bitmaps to chain several).</summary>
    readonly Dictionary<string, (ID2D1Bitmap1 A, ID2D1Bitmap1 B)> filtered = [];
    /// <summary>A copy of <see cref="Frame"/> for filter-track filters to read from.</summary>
    ID2D1Bitmap1? frameCopy;
    readonly Dictionary<string, ChromaKeyer> chromaKeys = [];
    /// <summary>Bitmaps of the transitions on screen, by outgoing clip.</summary>
    readonly Dictionary<string, TransitionMixer> transitions = [];
    readonly StickerImageRenderer stickerRenderer;
    readonly Dictionary<string, ID2D1Bitmap1> stickers = [];
    /// <summary>False for a compound clip's frames: transparent where nothing is drawn, like in CapCut (cut-out clips
    /// inside it show the layers below).</summary>
    readonly bool opaque;

    public int Width { get; }
    public int Height { get; }
    /// <summary>The last rendered frame (premultiplied BGRA, drawable).</summary>
    public ID2D1Bitmap1 Frame { get; }

    public Compositor(Graphics graphics, Timeline timeline, int width, int height, List<string> warnings,
        ImageLoader? photos = null, bool opaque = true)
    {
        this.opaque = opaque;
        this.graphics = graphics;
        this.timeline = timeline;
        this.warnings = warnings;
        Width = width;
        Height = height;
        ownsPhotos = photos is null;
        this.photos = photos ?? new ImageLoader(graphics);
        text = new TextImageRenderer(graphics);
        colorFilters = new ColorFilters(graphics);
        stickerRenderer = new StickerImageRenderer(graphics);
        Frame = graphics.CreateBitmap(width, height, BitmapOptions.Target);
    }

    /// <summary>A video clip's decoder and its frame as a bitmap, turned upright.</summary>
    sealed class VideoLayer(MfVideoReader reader, ID2D1Bitmap1 stored, ID2D1Bitmap1? upright) : IDisposable
    {
        public MfVideoReader Reader { get; } = reader;
        public ID2D1Bitmap1 Stored { get; } = stored;
        public ID2D1Bitmap1? Upright { get; } = upright;
        public int Version { get; set; } = -1;
        public ID2D1Bitmap1 Image => Upright ?? Stored;

        public void Dispose()
        {
            Reader.Dispose();
            Stored.Dispose();
            Upright?.Dispose();
        }
    }

    /// <summary>"Remove background": the clip's mask frames, the current one as an A8 bitmap, and the cut-out frame.</summary>
    sealed class CutoutLayer(CutoutMask mask) : IDisposable
    {
        public CutoutMask Mask { get; } = mask;
        public ID2D1Bitmap1? MaskBitmap { get; set; }
        public long? MaskTime { get; set; }
        public ID2D1Bitmap1? Result { get; set; }

        public void Dispose()
        {
            MaskBitmap?.Dispose();
            Result?.Dispose();
        }
    }

    /// <summary>Renders timeline time <paramref name="t"/> (µs) into <see cref="Frame"/>.</summary>
    public ID2D1Bitmap1 Render(long t)
    {
        // Both clips of a transition stay on screen for its whole range.
        var mixing = timeline.Layers.Where(l => l.TransitionOut?.Contains(t) == true)
            .SelectMany(l => new[] { l.Id, l.TransitionOut!.Next.Id }).ToHashSet();
        var active = timeline.Layers.Where(l => l.IsActive(t) || mixing.Contains(l.Id)).ToList();
        ReleaseInactive(active.Select(l => l.Id).ToHashSet());

        var images = new List<(VisualLayer Layer, ID2D1Bitmap1 Image, float? TextScale)>();
        foreach (var layer in active)
        {
            if (Content(layer, t) is { } content) images.Add((layer, Colored(layer, content.Image), content.TextScale));
        }
        var main = images.FirstOrDefault(i => i.Layer.IsMainTrack);
        var mixed = MixTransitions(images, t);
        images.RemoveAll(i => mixed.Incoming.Contains(i.Layer.Id));

        // Filter-track filters colour what's painted below them: draw in runs between them.
        var filters = timeline.Filters.Where(f => f.IsActive(t) && colorFilters.CanApply([f.Filter]))
            .OrderBy(f => f.RenderIndex).ThenBy(f => f.TrackIndex).ToList();
        var next = 0;
        var first = true;
        while (first || next < images.Count || filters.Count > 0)
        {
            var run = images.Skip(next).TakeWhile(i => filters.Count == 0 || filters[0].Covers(i.Layer)).ToList();
            var clear = first;
            graphics.DrawInto(Frame, dc =>
            {
                if (clear)
                {
                    dc.Clear(new Color4(0, 0, 0, opaque ? 1 : 0));
                    if (main.Layer?.Canvas is { } canvas) Background(dc, canvas, main.Image);
                }
                foreach (var (layer, image, textScale) in run)
                {
                    if (mixed.Plates.TryGetValue(layer.Id, out var plate)) dc.DrawBitmap(plate, 1, InterpolationMode.NearestNeighbor);
                    else Place(dc, layer, image, textScale, t);
                }
            });
            first = false;
            next += run.Count;
            if (filters.Count == 0) break;
            frameCopy ??= graphics.CreateBitmap(Width, Height, BitmapOptions.Target);
            frameCopy.CopyFromBitmap(Frame).CheckError();
            colorFilters.Apply(frameCopy, Frame, filters[0].Filter);
            filters.RemoveAt(0);
        }
        return Frame;
    }

    /// <summary>Each clip in a transition mixed with the next one, drawn later in the outgoing clip's place; the
    /// incoming clips are then not drawn on their own.</summary>
    (Dictionary<string, ID2D1Bitmap1> Plates, HashSet<string> Incoming) MixTransitions(
        List<(VisualLayer Layer, ID2D1Bitmap1 Image, float? TextScale)> images, long t)
    {
        var plates = new Dictionary<string, ID2D1Bitmap1>();
        var incoming = new HashSet<string>();
        foreach (var (layer, image, textScale) in images)
        {
            if (layer.TransitionOut is not { } transition || !transition.Contains(t)) continue;
            var next = images.FirstOrDefault(i => ReferenceEquals(i.Layer, transition.Next));
            if (next.Layer is null) continue;
            if (!transitions.TryGetValue(layer.Id, out var mixer))
            {
                mixer = new TransitionMixer(graphics, Width, Height);
                transitions[layer.Id] = mixer;
            }
            DrawPlate(mixer.A, layer, image, textScale, t);
            DrawPlate(mixer.B, next.Layer, next.Image, next.TextScale, t);
            mixer.Mix(transition.Kind, transition.Progress(t));
            plates[layer.Id] = mixer.Result;
            incoming.Add(next.Layer.Id);
        }
        return (plates, incoming);
    }

    /// <summary>A clip placed on the frame, over its canvas background (and black, when opaque) on the main track.</summary>
    void DrawPlate(ID2D1Bitmap1 target, VisualLayer layer, ID2D1Bitmap1 image, float? textScale, long t) =>
        graphics.DrawInto(target, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, layer.IsMainTrack && opaque ? 1 : 0));
            if (layer.IsMainTrack && layer.Canvas is { } canvas) Background(dc, canvas, image);
            Place(dc, layer, image, textScale, t);
        });

    /// <summary><paramref name="image"/> through the clip's own filters, in order.</summary>
    ID2D1Bitmap1 Colored(VisualLayer layer, ID2D1Bitmap1 image)
    {
        if (layer.Filters.Count == 0 || !colorFilters.CanApply(layer.Filters)) return image;
        var size = image.PixelSize;
        if (!filtered.TryGetValue(layer.Id, out var pair) || pair.A.PixelSize.Width != size.Width || pair.A.PixelSize.Height != size.Height)
        {
            if (filtered.Remove(layer.Id, out var old)) { old.A.Dispose(); old.B.Dispose(); }
            pair = (graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target),
                graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target));
            filtered[layer.Id] = pair;
        }
        var source = image;
        foreach (var filter in layer.Filters)
        {
            var target = source == pair.A ? pair.B : pair.A;
            if (colorFilters.Apply(source, target, filter)) source = target;
        }
        return source;
    }

    // MARK: Content

    (ID2D1Bitmap1 Image, float? TextScale)? Content(VisualLayer layer, long t)
    {
        var offset = t - layer.Target.Start;
        switch (layer.Content)
        {
            case VideoContent video:
            {
                if (Video(layer, video.Path) is not { } v) return null;
                var frame = v.Reader.FrameAt(layer.TimeMap.SourceTime(offset));
                if (frame is null) return null;
                if (frame.Version != v.Version)
                {
                    v.Stored.CopyFromMemory(frame.Pixels, (uint)frame.Stride).CheckError();
                    v.Version = frame.Version;
                    if (v.Upright is { } upright) TurnUpright(v.Stored, upright, v.Reader.Rotation);
                }
                return (CutOut(layer, v.Image, layer.TimeMap.SourceTime(offset)), null);
            }
            case PhotoContent photo:
                return photos.Load(photo.Path) is { } bitmap ? (CutOut(layer, bitmap, 0), null) : null;
            case CompoundContent compound:
            {
                if (!nested.TryGetValue(layer.Id, out var inner))
                {
                    // Render the nested canvas at the same pixel density as this one.
                    var (w, h) = Layout.NestedSize(timeline, compound.Timeline, Width);
                    inner = new Compositor(graphics, compound.Timeline, w, h, warnings, photos, opaque: false);
                    nested[layer.Id] = inner;
                }
                var time = layer.TimeMap.SourceTime(offset);
                return (CutOut(layer, inner.Render(time), time), null);
            }
            case TextContentLayer textLayer:
            {
                if (!texts.TryGetValue(layer.Id, out var cached))
                {
                    var scale = Layout.TextRasterScale(layer);
                    cached = (text.Render(textLayer.Spec, Width, scale), scale);
                    texts[layer.Id] = cached;
                }
                return cached.Bitmap is { } rendered ? (rendered, cached.Scale) : null;
            }
            case StickerContent sticker:
            {
                // Drawn every frame at the size it's shown at; placed like text, at 1 / scale.
                var scale = Layout.StickerRasterScale(layer);
                var pixels = (int)Math.Ceiling(sticker.Clock.Diameter * Math.Min(Width, Height) * scale);
                pixels = Math.Max(pixels, 4);
                if (!stickers.TryGetValue(layer.Id, out var drawn) || drawn.PixelSize.Width != pixels)
                {
                    drawn?.Dispose();
                    drawn = graphics.CreateBitmap(pixels, pixels, BitmapOptions.Target);
                    stickers[layer.Id] = drawn;
                }
                stickerRenderer.Render(sticker.Clock, drawn, layer.TimeMap.SourceTime(offset));
                return (drawn, (float)(pixels / (sticker.Clock.Diameter * Math.Min(Width, Height))));
            }
            default:
                return null;
        }
    }

    /// <summary>"Chroma key", before the cutout.</summary>
    ID2D1Bitmap1 ChromaKeyed(VisualLayer layer, ID2D1Bitmap1 image)
    {
        if (layer.ChromaKey is not { } key) return image;
        if (!chromaKeys.TryGetValue(layer.Id, out var keyer) || keyer.Key != key)
        {
            keyer?.Dispose();
            keyer = new ChromaKeyer(graphics, key);
            chromaKeys[layer.Id] = keyer;
        }
        return keyer.Apply(image);
    }

    /// <summary>"Chroma key", then "Remove background": <paramref name="image"/> with CapCut's mask at source time <paramref name="t"/> as its alpha.</summary>
    ID2D1Bitmap1 CutOut(VisualLayer layer, ID2D1Bitmap1 image, long t)
    {
        image = ChromaKeyed(layer, image);
        if (layer.Cutout is not { } cutout) return image;
        if (!cutouts.TryGetValue(layer.Id, out var c))
        {
            if (CutoutMask.Open(cutout) is not { } opened) return image;
            c = new CutoutLayer(opened);
            cutouts[layer.Id] = c;
        }
        var size = image.PixelSize;
        var time = CutoutMask.Nearest(c.Mask.Times, t);
        if (c.MaskTime != time)
        {
            if (c.Mask.FrameAt(time, (double)size.Width / Math.Max(size.Height, 1)) is not { } frame) return image;
            if (c.MaskBitmap is not { } existing || existing.PixelSize.Width != frame.Width || existing.PixelSize.Height != frame.Height)
            {
                c.MaskBitmap?.Dispose();
                c.MaskBitmap = graphics.CreateMaskBitmap(frame.Width, frame.Height);
            }
            c.MaskBitmap.CopyFromMemory(frame.Pixels, (uint)frame.Width).CheckError();
            c.MaskTime = time;
        }
        if (c.Result is not { } result || result.PixelSize.Width != size.Width || result.PixelSize.Height != size.Height)
        {
            c.Result?.Dispose();
            c.Result = graphics.CreateBitmap(size.Width, size.Height, BitmapOptions.Target);
        }
        var mask = c.MaskBitmap!;
        graphics.DrawInto(c.Result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            // FillOpacityMask needs aliased drawing; the mask is stretched over the whole frame.
            dc.AntialiasMode = AntialiasMode.Aliased;
            using var brush = dc.CreateBitmapBrush(image);
            dc.FillOpacityMask(mask, brush, new Vortice.RawRectF(0, 0, size.Width, size.Height),
                new Vortice.RawRectF(0, 0, mask.PixelSize.Width, mask.PixelSize.Height));
            dc.AntialiasMode = AntialiasMode.PerPrimitive;
        });
        return c.Result;
    }

    VideoLayer? Video(VisualLayer layer, string path)
    {
        if (videos.TryGetValue(layer.Id, out var existing)) return existing;
        if (failed.Contains(layer.Id)) return null;
        try
        {
            var reader = new MfVideoReader(path, layer.TimeMap.SourceStart);
            if (reader.IsHdr) Warn($"{Path.GetFileName(path)}: HDR video is converted without tone mapping (colours differ from CapCut)");
            var stored = graphics.CreateBitmap(reader.Width, reader.Height);
            var turned = reader.Rotation is 90 or 270;
            var upright = reader.Rotation == 0 ? null
                : graphics.CreateBitmap(turned ? reader.Height : reader.Width, turned ? reader.Width : reader.Height, BitmapOptions.Target);
            var v = new VideoLayer(reader, stored, upright);
            videos[layer.Id] = v;
            return v;
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or IOException)
        {
            failed.Add(layer.Id);
            Warn($"{Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>Draws a stored frame into its upright bitmap, turned clockwise by <paramref name="rotation"/> degrees.</summary>
    void TurnUpright(ID2D1Bitmap1 stored, ID2D1Bitmap1 upright, int rotation)
    {
        var size = stored.PixelSize;
        var turn = Matrix3x2.CreateTranslation(-size.Width / 2f, -size.Height / 2f)
            * Matrix3x2.CreateRotation(rotation * MathF.PI / 180)
            * Matrix3x2.CreateTranslation(upright.PixelSize.Width / 2f, upright.PixelSize.Height / 2f);
        graphics.DrawInto(upright, dc =>
        {
            dc.Transform = turn;
            dc.DrawBitmap(stored, 1, InterpolationMode.NearestNeighbor);
        });
    }

    void ReleaseInactive(HashSet<string> active)
    {
        foreach (var id in videos.Keys.Where(id => !active.Contains(id)).ToList()) { videos[id].Dispose(); videos.Remove(id); }
        foreach (var id in nested.Keys.Where(id => !active.Contains(id)).ToList()) { nested[id].Dispose(); nested.Remove(id); }
        foreach (var id in texts.Keys.Where(id => !active.Contains(id)).ToList()) { texts[id].Bitmap?.Dispose(); texts.Remove(id); }
        foreach (var id in cutouts.Keys.Where(id => !active.Contains(id)).ToList()) { cutouts[id].Dispose(); cutouts.Remove(id); }
        foreach (var id in transitions.Keys.Where(id => !active.Contains(id)).ToList()) { transitions[id].Dispose(); transitions.Remove(id); }
        foreach (var id in filtered.Keys.Where(id => !active.Contains(id)).ToList())
        {
            filtered[id].A.Dispose();
            filtered[id].B.Dispose();
            filtered.Remove(id);
        }
        foreach (var id in chromaKeys.Keys.Where(id => !active.Contains(id)).ToList()) { chromaKeys[id].Dispose(); chromaKeys.Remove(id); }
        foreach (var id in stickers.Keys.Where(id => !active.Contains(id)).ToList()) { stickers[id].Dispose(); stickers.Remove(id); }
    }

    void Warn(string message)
    {
        if (!warnings.Contains(message)) warnings.Add(message);
    }

    // MARK: Layout

    void Place(ID2D1DeviceContext dc, VisualLayer layer, ID2D1Bitmap1 image, float? textScale, long t)
    {
        var size = image.PixelSize;
        float x0 = 0, y0 = 0, x1 = size.Width, y1 = size.Height;
        if (textScale is null && Layout.CropRect(layer.Crop, size.Width, size.Height) is { } crop)
            (x0, y0, x1, y1) = crop;
        float w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return;
        var transform = layer.Transform(t);
        dc.Transform = Layout.Placement(transform, w, h, Width, Height, textScale);
        var opacity = (float)Math.Clamp(transform.Alpha, 0, 1);
        if (opacity <= 0) return;
        dc.DrawBitmap(image, new Vortice.RawRectF(0, 0, w, h), opacity, InterpolationMode.HighQualityCubic,
            new Vortice.RawRectF(x0, y0, x1, y1), null);
        dc.Transform = Matrix3x2.Identity;
    }

    void Background(ID2D1DeviceContext dc, CanvasMaterial canvas, ID2D1Bitmap1 source)
    {
        switch (canvas.Type)
        {
            case "canvas_blur":
            {
                var size = source.PixelSize;
                var cover = Layout.Cover(size.Width, size.Height, Width, Height);
                var fill = cover.M11;
                // Blur in the source's pixels so the radius comes out right after scaling; clamp the edges first so
                // they don't darken.
                using var border = new Border(dc) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp };
                border.SetInput(0, source, true);
                using var blur = new GaussianBlur(dc)
                {
                    StandardDeviation = Layout.CanvasBlurRadius(canvas.Blur, Width, Height) / Math.Max(fill, 1e-3f),
                    BorderMode = BorderMode.Hard,
                };
                blur.SetInput(0, border.Output, true);
                dc.Transform = cover;
                dc.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, size.Width, size.Height), AntialiasMode.Aliased);
                dc.DrawImage(blur.Output, InterpolationMode.Linear);
                dc.PopAxisAlignedClip();
                dc.Transform = Matrix3x2.Identity;
                break;
            }
            case "canvas_image":
            {
                if (photos.Load(canvas.Image) is not { } image) return;
                var size = image.PixelSize;
                dc.Transform = Layout.Cover(size.Width, size.Height, Width, Height);
                dc.DrawBitmap(image, 1, InterpolationMode.HighQualityCubic);
                dc.Transform = Matrix3x2.Identity;
                break;
            }
            default:
            {
                if (canvas.Color.Length == 0) return;
                var c = Rgba.FromHex(canvas.Color);
                using var brush = dc.CreateSolidColorBrush(new Color4(c.R, c.G, c.B, c.A));
                dc.FillRectangle(new Vortice.RawRectF(0, 0, Width, Height), brush);
                break;
            }
        }
    }

    public void Dispose()
    {
        foreach (var v in videos.Values) v.Dispose();
        foreach (var n in nested.Values) n.Dispose();
        foreach (var (bitmap, _) in texts.Values) bitmap?.Dispose();
        foreach (var c in cutouts.Values) c.Dispose();
        foreach (var m in transitions.Values) m.Dispose();
        foreach (var (a, b) in filtered.Values) { a.Dispose(); b.Dispose(); }
        colorFilters.Dispose();
        frameCopy?.Dispose();
        foreach (var k in chromaKeys.Values) k.Dispose();
        foreach (var b in stickers.Values) b.Dispose();
        stickerRenderer.Dispose();
        text.Dispose();
        if (ownsPhotos) photos.Dispose();
        Frame.Dispose();
    }
}
