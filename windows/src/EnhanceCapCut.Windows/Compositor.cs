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

    public int Width { get; }
    public int Height { get; }
    /// <summary>The last rendered frame (premultiplied BGRA, drawable).</summary>
    public ID2D1Bitmap1 Frame { get; }

    public Compositor(Graphics graphics, Timeline timeline, int width, int height, List<string> warnings, ImageLoader? photos = null)
    {
        this.graphics = graphics;
        this.timeline = timeline;
        this.warnings = warnings;
        Width = width;
        Height = height;
        ownsPhotos = photos is null;
        this.photos = photos ?? new ImageLoader(graphics);
        text = new TextImageRenderer(graphics);
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

    /// <summary>Renders timeline time <paramref name="t"/> (µs) into <see cref="Frame"/>.</summary>
    public ID2D1Bitmap1 Render(long t)
    {
        var active = timeline.Layers.Where(l => l.IsActive(t)).ToList();
        ReleaseInactive(active.Select(l => l.Id).ToHashSet());

        var images = new List<(VisualLayer Layer, ID2D1Bitmap1 Image, float? TextScale)>();
        foreach (var layer in active)
        {
            if (Content(layer, t) is { } content) images.Add((layer, content.Image, content.TextScale));
        }
        var main = images.FirstOrDefault(i => i.Layer.IsMainTrack);

        graphics.DrawInto(Frame, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 1));
            if (main.Layer?.Canvas is { } canvas) Background(dc, canvas, main.Image);
            foreach (var (layer, image, textScale) in images) Place(dc, layer, image, textScale, t);
        });
        return Frame;
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
                return (v.Image, null);
            }
            case PhotoContent photo:
                return photos.Load(photo.Path) is { } bitmap ? (bitmap, null) : null;
            case CompoundContent compound:
            {
                if (!nested.TryGetValue(layer.Id, out var inner))
                {
                    // Render the nested canvas at the same pixel density as this one.
                    var (w, h) = Layout.NestedSize(timeline, compound.Timeline, Width);
                    inner = new Compositor(graphics, compound.Timeline, w, h, warnings, photos);
                    nested[layer.Id] = inner;
                }
                return (inner.Render(layer.TimeMap.SourceTime(offset)), null);
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
            default:
                return null;
        }
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
        text.Dispose();
        if (ownsPhotos) photos.Dispose();
        Frame.Dispose();
    }
}
