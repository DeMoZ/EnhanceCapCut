namespace EnhanceCapCut.Draft;

/// <summary>
/// One of CapCut's "Filters" (<c>materials.effects</c>, <c>type</c> "filter"): colour lookup tables read from the
/// filter's package in CapCut's effect cache, applied in order at the filter's intensity.
/// </summary>
/// <param name="Luts">The lookup tables, in the order the package applies them: <c>.cube</c> files or PNG lookup images
/// (see <see cref="Layout"/>).</param>
/// <param name="Intensity">The "intensity" slider (<c>value</c>), 0…1.</param>
public sealed record ColorFilter(string Name, IReadOnlyList<string> Luts, double Intensity = 1)
{
    /// <summary>The largest lookup table we read (entries per channel).</summary>
    public const int MaxSize = 64;

    /// <summary>
    /// How a PNG lookup image holds its N×N×N table. In every layout red grows to the right and green downwards
    /// inside a tile, and blue picks the tile.
    /// </summary>
    public enum LayoutKind
    {
        /// <summary>√N × √N tiles of N×N pixels, blue tile by tile, row by row (512×512 for N = 64).</summary>
        Grid,
        /// <summary>N tiles of N×N side by side (N² × N).</summary>
        Row,
        /// <summary>N tiles of N×N stacked (N × N²).</summary>
        Column,
    }

    public sealed record Layout(LayoutKind Kind, int Size, int TilesPerRow = 0)
    {
        /// <summary>The pixel holding entry (r, g, b).</summary>
        public (int X, int Y) Pixel(int r, int g, int b) => Kind switch
        {
            LayoutKind.Grid => (b % TilesPerRow * Size + r, b / TilesPerRow * Size + g),
            LayoutKind.Row => (b * Size + r, g),
            _ => (r, b * Size + g),
        };
    }

    /// <summary>The layout of a lookup image of this size, or null when it isn't one.</summary>
    public static Layout? LayoutFor(int width, int height)
    {
        static bool Valid(int n) => n >= 2 && n <= MaxSize;
        if (width == height)
        {
            // N = k², side = N·k = k³.
            for (var k = 2; k <= 8; k++)
            {
                if (k * k * k == width && Valid(k * k)) return new(LayoutKind.Grid, k * k, k);
            }
            return null;
        }
        if (height * height == width && Valid(height)) return new(LayoutKind.Row, height);
        if (width * width == height && Valid(width)) return new(LayoutKind.Column, width);
        return null;
    }

    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The width and height in a PNG file's header, or null when it isn't a PNG.</summary>
    public static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var header = new byte[24];
            if (file.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24 || !header.AsSpan(0, 8).SequenceEqual(PngSignature))
                return null;
            static int Int(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];
            return (Int(header, 16), Int(header, 20));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The <c>LUT_3D_SIZE</c> of a <c>.cube</c> file, or null when it has none or it's out of range.</summary>
    public static int? CubeSize(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "LUT_3D_SIZE" && int.TryParse(parts[1], out var n) && n >= 2 && n <= MaxSize)
                    return n;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The lookup table in a filter package: the first <c>.cube</c> or lookup-shaped PNG, by path, preferring files
    /// whose name mentions "lut".
    /// </summary>
    public static string? LutFile(string package) => LutFiles(package).FirstOrDefault();

    /// <summary>Every <c>.cube</c> or lookup-shaped PNG in a folder, those whose name mentions "lut" first, then by path.</summary>
    public static IReadOnlyList<string> LutFiles(string package)
    {
        if (!System.IO.Directory.Exists(package)) return [];
        var candidates = new List<(int Rank, string Path, string File)>();
        foreach (var file in System.IO.Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
        {
            var usable = System.IO.Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".cube" => CubeSize(file) is not null,
                ".png" => PngSize(file) is { } size && LayoutFor(size.Width, size.Height) is not null,
                _ => false,
            };
            if (!usable) continue;
            var rank = System.IO.Path.GetFileName(file).Contains("lut", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var relative = System.IO.Path.GetRelativePath(package, file).Replace('\\', '/');
            candidates.Add((rank, "/" + relative, file));
        }
        return candidates.OrderBy(c => c.Rank).ThenBy(c => c.Path, StringComparer.Ordinal).Select(c => c.File).ToList();
    }

    /// <summary>The parts of a package (<c>config.json</c> → <c>effect.Link[]</c>), in the order they're applied (<c>zorder</c>).</summary>
    public static IReadOnlyList<string> Features(string package)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(package, "config.json")));
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !json.RootElement.TryGetProperty("effect", out var effect) || effect.ValueKind != System.Text.Json.JsonValueKind.Object
                || !effect.TryGetProperty("Link", out var links) || links.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            var parts = new List<(double Z, int Index, string Path)>();
            var i = 0;
            foreach (var link in links.EnumerateArray())
            {
                i++;
                if (link.ValueKind != System.Text.Json.JsonValueKind.Object || link.Str("path") is not { Length: > 0 } path) continue;
                parts.Add((link.Num("zorder") ?? 0, i, System.IO.Path.Combine(package, path.TrimEnd('/'))));
            }
            return parts.OrderBy(p => p.Z).ThenBy(p => p.Index).Select(p => p.Path).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The lookup table a package part uses: a table in it whose path without the extension (<c>image/filter</c>) one
    /// of its <c>material/*.material</c> files names. Parts often carry unused tables, so unreferenced ones don't count.
    /// </summary>
    public static string? ReferencedLutFile(string feature)
    {
        var materialDir = System.IO.Path.Combine(feature, "material");
        if (!System.IO.Directory.Exists(materialDir)) return null;
        var materials = System.IO.Directory.EnumerateFiles(materialDir, "*.material").Select(File.ReadAllBytes).ToList();
        if (materials.Count == 0) return null;
        return LutFiles(feature).FirstOrDefault(file =>
        {
            var relative = System.IO.Path.GetRelativePath(feature, file).Replace('\\', '/');
            var name = System.Text.Encoding.UTF8.GetBytes(relative[..^System.IO.Path.GetExtension(relative).Length]);
            return materials.Any(m => m.AsSpan().IndexOf(name) >= 0);
        });
    }

    /// <summary>A package's lookup tables in the order it applies them: the table each part's materials use; for a
    /// package without such references, its first table (<see cref="LutFile"/>).</summary>
    public static IReadOnlyList<string> PackageLutFiles(string package)
    {
        var referenced = Features(package).Select(ReferencedLutFile).OfType<string>().ToList();
        if (referenced.Count > 0) return referenced;
        return LutFile(package) is { } lut ? [lut] : [];
    }

    /// <summary>
    /// The lookup tables for a filter: in <paramref name="path"/> (the cached package), otherwise in the first
    /// <c>&lt;cache&gt;/&lt;resourceID&gt;/&lt;hash&gt;</c> that has any. Empty when none is found.
    /// </summary>
    public static IReadOnlyList<string> Locate(string? path, string resourceID, IReadOnlyList<string>? cacheRoots = null)
    {
        if (path is not null && System.IO.Directory.Exists(path) && PackageLutFiles(path) is { Count: > 0 } luts) return luts;
        if (resourceID.Length == 0) return [];
        foreach (var root in cacheRoots ?? VoiceFilter.CacheRoots)
        {
            var folder = System.IO.Path.Combine(root, resourceID);
            if (!System.IO.Directory.Exists(folder)) continue;
            foreach (var entry in System.IO.Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
            {
                if (PackageLutFiles(entry) is { Count: > 0 } found) return found;
            }
        }
        return [];
    }
}

/// <summary>A filter on a filter track: it colours everything painted below it (<see cref="RenderIndex"/>) while it's active.</summary>
public sealed record FilterLayer(string Id, TimeRange Target, int TrackIndex, int RenderIndex, ColorFilter Filter)
{
    public bool IsActive(long t) => t >= Target.Start && t < Target.End;

    /// <summary>True when the filter is painted after <paramref name="layer"/> (and so colours it).</summary>
    public bool Covers(VisualLayer layer) =>
        RenderIndex > layer.RenderIndex || (RenderIndex == layer.RenderIndex && TrackIndex > layer.TrackIndex);
}
