using EnhanceCapCut.Core;
using EnhanceCapCut.Render;

namespace EnhanceCapCut.Render.Tests;

/// <summary>Port of the macOS MaskTests.</summary>
public class MaskTests
{
    static byte[] Raster(MaskShape shape, MaskGeometry g, bool inverted = false, int w = 40, int h = 20) =>
        MaskRaster.Render([new MaskRaster.ShapeLayer(shape, g, inverted)], w, h);

    static byte At(byte[] r, int x, int y, int w = 40) => r[y * w + x];

    [Fact]
    public void RectangleCenterSizeAndInvert()
    {
        // Half the frame, centered: x 10..<30, y 5..<15.
        var g = Core.Geometry(width: 0.5, height: 0.5);
        var r = Raster(MaskShape.Rectangle, g);
        Assert.Equal(255, At(r, 20, 10));
        Assert.Equal(255, At(r, 10, 5));
        Assert.Equal(0, At(r, 9, 10));
        Assert.Equal(0, At(r, 20, 15));
        var inv = Raster(MaskShape.Rectangle, g, inverted: true);
        Assert.Equal(0, At(inv, 20, 10));
        Assert.Equal(255, At(inv, 0, 0));
        // Center in half-frame units, +y up: (0.5, 0.5) is the top-right quarter's center (30, 5).
        var moved = Raster(MaskShape.Rectangle, Core.Geometry(centerX: 0.5, centerY: 0.5, width: 0.2, height: 0.2));
        Assert.Equal(255, At(moved, 30, 5));
        Assert.Equal(0, At(moved, 10, 15));
    }

    [Fact]
    public void RotationIsClockwise()
    {
        // A wide thin bar turned 90° becomes a tall one.
        var r = Raster(MaskShape.Rectangle, Core.Geometry(width: 0.9, height: 0.05, rotation: 90), w: 40, h: 40);
        Assert.Equal(255, At(r, 20, 3, w: 40));
        Assert.Equal(0, At(r, 35, 20, w: 40));
        // The line keeps what's above it; rotated clockwise by 90° that becomes the right side.
        Assert.Equal(255, At(Raster(MaskShape.Line, Core.Geometry()), 20, 2));
        Assert.Equal(0, At(Raster(MaskShape.Line, Core.Geometry()), 20, 17));
        var turned = Raster(MaskShape.Line, Core.Geometry(rotation: 90));
        Assert.Equal(0, At(turned, 2, 10));
        Assert.Equal(255, At(turned, 37, 10));
    }

    [Fact]
    public void Shapes()
    {
        var g = Core.Geometry(width: 0.5, height: 1);
        foreach (var shape in new[] { MaskShape.Circle, MaskShape.Heart, MaskShape.Star })
        {
            var r = Raster(shape, g);
            Assert.Equal(255, At(r, 20, 10));
            Assert.Equal(0, At(r, 1, 1));
        }
        // A rounded rectangle cuts its corners.
        Assert.Equal(0, At(Raster(MaskShape.Rectangle, Core.Geometry(width: 0.5, height: 1, roundCorner: 1)), 11, 1));
        Assert.Equal(255, At(Raster(MaskShape.Rectangle, Core.Geometry(width: 0.5, height: 1)), 11, 1));
        // Mirror: a band `height` tall.
        var band = Raster(MaskShape.Mirror, Core.Geometry(height: 0.2));
        Assert.Equal(255, At(band, 5, 10));
        Assert.Equal(0, At(band, 5, 4));
    }

    [Fact]
    public void FeatherIsASoftEdge()
    {
        var sharp = Raster(MaskShape.Line, Core.Geometry(), w: 40, h: 200);
        var soft = Raster(MaskShape.Line, Core.Geometry(feather: 0.5), w: 40, h: 200);
        Assert.Equal(255, At(sharp, 0, 90));
        var mid = At(soft, 0, 100);
        Assert.InRange(mid, 101, 154);
        Assert.True(At(soft, 0, 95) > At(soft, 0, 105));
    }

    [Fact]
    public void UnionAndBitmaps()
    {
        var left = new MaskRaster.ShapeLayer(MaskShape.Rectangle, Core.Geometry(centerX: -0.5, width: 0.5, height: 1), false);
        // A 2×1 brush frame: left hidden, right shown.
        var brush = new MaskRaster.BitmapLayer([0, 255], 2, 1, false);
        var r = MaskRaster.Render([left, brush], 40, 20);
        Assert.Equal(255, At(r, 5, 10));
        Assert.Equal(255, At(r, 38, 10));
        Assert.InRange(At(r, 20, 10), 100, 160);
        Assert.Equal(MaskRaster.MaxSide, MaskRaster.RasterSize(1920, 1080).Width);
        Assert.Equal(640, MaskRaster.RasterSize(640, 480).Width);
    }

    [Fact]
    public void TimelineBuildsMasks()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-masks-{Guid.NewGuid()}");
        try
        {
            var strokes = Path.Combine(dir, "adjust_mask", "B1");
            Directory.CreateDirectory(strokes);
            File.WriteAllBytes(Path.Combine(strokes, "2000000"), [0, 255]);
            File.WriteAllText(Path.Combine(strokes, "data.json"), """{"data": [{"width": 2, "height": 1, "pts": 2000000}]}""");
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            var escaped = photo.Replace("\\", "\\\\");
            var root = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##/adjust_mask/";
            var json = $$$"""
            {"tracks": [{"type": "video", "segments": [
               {"id": "S0", "material_id": "V", "target_timerange": {"start": 0, "duration": 1000000},
                "extra_material_refs": ["M1", "M2", "M3", "M4"],
                "common_keyframes": [
                  {"property_type": "KFTypeCommonMaskPositionX", "material_id": "C1",
                   "keyframe_list": [{"time_offset": 0, "values": [0.1]}, {"time_offset": 1000000, "values": [0.3]}]},
                  {"property_type": "KFTypeCommonMaskPositionX", "material_id": "C2",
                   "keyframe_list": [{"time_offset": 0, "values": [0.9]}]}]}]}],
             "materials": {
               "videos": [{"id": "V", "type": "photo", "path": "{{{escaped}}}"}],
               "common_mask": [
                 {"id": "M1", "constant_material_id": "C1", "name": "Rectangle", "resource_type": "rectangle",
                  "config": {"width": 0.2, "height": 0.4, "centerX": 0, "centerY": 0.5, "rotation": 30, "feather": 0.1,
                             "roundCorner": 0.2, "invert": true}},
                 {"id": "M2", "constant_material_id": "C9", "name": "Brush", "resource_type": "custom",
                  "loader_work_space": "{{{root}}}B1", "config": {}},
                 {"id": "M3", "name": "Brush", "resource_type": "custom", "loader_work_space": "", "config": {}},
                 {"id": "M4", "name": "Wave", "resource_type": "wave", "config": {"expansion": 0.2}}]}}
            """;
            // The core reads the masks (and evaluates their keyframes per frame); this side draws them.
            var timeline = Core.Timeline(json, dir);
            var masks = Assert.Single(timeline.Layers()).Masks;
            Assert.Equal(2, masks.Length);
            Assert.Equal(new ClipMaskKind.Shape(MaskShape.Rectangle), masks[0].Kind);
            Assert.True(masks[0].Inverted);
            Assert.Equal(Core.Geometry(0, 0.5, 0.2, 0.4, 30, 0.1, 0.2), masks[0].Geometry);
            var geometries = Assert.Single(timeline.Frame(500_000).Layers).Masks;
            Assert.Equal(0.2, geometries[0].CenterX, 9);
            var brush = Assert.IsType<ClipMaskKind.Brush>(masks[1].Kind);
            Assert.Equal(Path.GetFullPath(strokes), Path.GetFullPath(brush.Directory));

            // The brush frame, stretched over the clip, and redrawn only when it changes.
            var frames = new ClipMaskFrames([masks[1]]);
            var first = frames.FrameAt(0, [], 4, 2)!.Value;
            Assert.Equal(4, first.Width);
            Assert.Equal(0, first.Pixels[0]);
            Assert.Equal(255, first.Pixels[3]);
            Assert.True(first.Changed);
            Assert.False(frames.FrameAt(40_000, [], 4, 2)!.Value.Changed);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
