using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's ColorFilterTests.swift.

public class ColorFilterTests
{
    [Fact]
    public void LookupImageLayouts()
    {
        Assert.Equal(new ColorFilter.Layout(ColorFilter.LayoutKind.Grid, 64, 8), ColorFilter.LayoutFor(512, 512));
        Assert.Equal(new ColorFilter.Layout(ColorFilter.LayoutKind.Grid, 16, 4), ColorFilter.LayoutFor(64, 64));
        Assert.Equal(new ColorFilter.Layout(ColorFilter.LayoutKind.Row, 32), ColorFilter.LayoutFor(1024, 32));
        Assert.Equal(new ColorFilter.Layout(ColorFilter.LayoutKind.Column, 16), ColorFilter.LayoutFor(16, 256));
        Assert.Null(ColorFilter.LayoutFor(500, 500));
        Assert.Null(ColorFilter.LayoutFor(1080, 1920));
        Assert.Null(ColorFilter.LayoutFor(4096, 4096)); // N = 256 is over the limit
        var grid = ColorFilter.LayoutFor(512, 512)!;
        Assert.Equal((64 + 5, 64 + 7), grid.Pixel(5, 7, 9));
        Assert.Equal(97, ColorFilter.LayoutFor(1024, 32)!.Pixel(1, 2, 3).X);
    }

    /// <summary>A file with just a PNG signature and header (all the package search reads).</summary>
    internal static void WritePngHeader(string path, int width, int height)
    {
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0];
        File.WriteAllBytes(path, header);
    }

    [Fact]
    public void FiltersFromTheDraft()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecc-filter-{Guid.NewGuid()}");
        try
        {
            // A cached package with its lookup image somewhere inside, next to a texture that isn't one.
            var package = Path.Combine(dir, "cache", "111", "abc");
            Directory.CreateDirectory(Path.Combine(package, "res"));
            WritePngHeader(Path.Combine(package, "res", "filter.png"), 1024, 32);
            WritePngHeader(Path.Combine(package, "grain.png"), 100, 60);
            var photo = Path.Combine(dir, "p.png");
            File.WriteAllBytes(photo, [0]);
            string Esc(string p) => p.Replace("\\", "\\\\");
            var json = $$$"""
            {"tracks": [
               {"type": "video", "segments": [{"id": "S", "material_id": "V", "render_index": 0,
                 "extra_material_refs": ["F2"], "target_timerange": {"start": 0, "duration": 1000000}}]},
               {"type": "filter", "segments": [
                 {"id": "T1", "material_id": "F2", "render_index": 10001, "target_timerange": {"start": 0, "duration": 500000}},
                 {"id": "T2", "material_id": "F3", "render_index": 10000, "target_timerange": {"start": 0, "duration": 500000}}]}],
             "materials": {
               "videos": [{"id": "V", "type": "photo", "path": "{{{Esc(photo)}}}"}],
               "effects": [
                 {"id": "F1", "type": "filter", "name": "Film", "value": 0.4, "resource_id": "111",
                  "path": "/somewhere/else/Cache/effect/111/abc"},
                 {"id": "F2", "type": "filter", "name": "Clip filter", "value": 1, "resource_id": "111", "path": "{{{Esc(package)}}}"},
                 {"id": "F3", "type": "filter", "name": "Not cached", "value": 1, "resource_id": "222", "path": "/nope"}]}}
            """;
            // A stale path falls back to <cache>/<resource_id>/<hash>.
            Assert.Equal(["filter.png"], ColorFilter.Locate("/somewhere/else", "111", [Path.Combine(dir, "cache")]).Select(Path.GetFileName));
            var timeline = TimelineBuilder.Build(Draft.Parse(json), dir);
            Assert.Equal(["Clip filter"], timeline.Layers[0].Filters.Select(f => f.Name));
            Assert.Equal(["filter.png"], timeline.Layers[0].Filters[0].Luts.Select(Path.GetFileName));
            var found = timeline.Report.Unsupported.Select(i => $"{i.Category}: {i.Name}").ToHashSet();
            Assert.Contains("Filter: Not cached", found);
            Assert.DoesNotContain("Filter: Clip filter", found);
            Assert.Equal(["T1"], timeline.Filters.Select(f => f.Id)); // the uncached filter is left out

            var layer = new FilterLayer("T", new TimeRange(0, 10), 1, 10000, new ColorFilter("x", [photo]));
            Assert.True(layer.Covers(timeline.Layers[0]));
            Assert.True(layer.IsActive(5));
            Assert.False(layer.IsActive(10));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>A multi-part package (like "Shimmering B&amp;W"): parts applied by <c>zorder</c>, each with the table its
    /// materials name; tables no material uses don't count.</summary>
    [Fact]
    public void PackagePartsUseTheTablesTheirMaterialsName()
    {
        var package = Path.Combine(Path.GetTempPath(), $"ecc-package-{Guid.NewGuid()}");
        try
        {
            foreach (var part in new[] { "A/image", "A/material", "B/image", "B/material", "C/image" })
                Directory.CreateDirectory(Path.Combine(package, part));
            foreach (var path in new[] { "A/image/filter.png", "B/image/filter_bg.png", "B/image/filter_skin.png", "C/image/filter.png" })
                WritePngHeader(Path.Combine(package, path), 64, 64);
            // Binary materials name their textures; A's table is unused.
            File.WriteAllText(Path.Combine(package, "A/material/stars.material"), "\u0001image/kira-1.png\u0000");
            File.WriteAllText(Path.Combine(package, "B/material/SkinSeg.material"), "\u0001image/filter_skin.png\u0000image/filter_bg.png");
            File.WriteAllText(Path.Combine(package, "config.json"),
                """{"effect": {"Link": [{"path": "C/", "zorder": 8003}, {"path": "B/", "zorder": 8002}, {"path": "A/", "zorder": 8001}]}}""");
            string Rel(string f) => Path.GetRelativePath(package, f).Replace('\\', '/');
            // C has no materials, A's table is unused.
            Assert.Equal(["B/image/filter_bg.png"], ColorFilter.PackageLutFiles(package).Select(Rel));
            // Without the parts list, the first table of the package.
            File.Delete(Path.Combine(package, "config.json"));
            Assert.Equal(["A/image/filter.png"], ColorFilter.PackageLutFiles(package).Select(Rel));
        }
        finally
        {
            if (Directory.Exists(package)) Directory.Delete(package, true);
        }
    }
}
