using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Vortice.WIC;

namespace EnhanceCapCut.Windows;

/// <summary>
/// CapCut filters on Direct2D: each filter's lookup table (read from CapCut's cache, intensity baked in, see
/// <see cref="ColorLut"/>) as a Direct2D 3D lookup table, applied with the LookupTable3D effect. Port of the macOS
/// <c>ColorLUT</c> + <c>CIColorCubeWithColorSpace</c>; the 8-bit frames hold sRGB-encoded values, which is what the
/// tables are made for.
/// </summary>
public sealed class ColorFilters(Graphics graphics) : IDisposable
{
    readonly Dictionary<ColorFilter, ID2D1LookupTable3D?> tables = [];
    ID2D1DeviceContext2? context2;
    ID2D1Effect? effect;

    /// <summary>The lookup table for <paramref name="filter"/>, or null when it can't be read.</summary>
    ID2D1LookupTable3D? Table(ColorFilter filter)
    {
        if (tables.TryGetValue(filter, out var cached)) return cached;
        ID2D1LookupTable3D? table = null;
        try
        {
            if (Read(filter.Lut) is { } read)
            {
                var lut = ColorLut.Blend(read.Size, read.Rgb, filter.Intensity);
                var bytes = new byte[lut.Data.Length * sizeof(float)];
                Buffer.BlockCopy(lut.Data, 0, bytes, 0, bytes.Length);
                var n = (uint)lut.Size;
                context2 ??= graphics.Context.QueryInterface<ID2D1DeviceContext2>();
                // Red is the fastest-changing axis (X), then green (rows), then blue (planes).
                table = context2.CreateLookupTable3D(BufferPrecision.PerChannel32Float, [n, n, n], bytes, (uint)bytes.Length,
                    [n * 16, n * n * 16]);
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            table = null;
        }
        tables[filter] = table;
        return table;
    }

    (int Size, float[] Rgb)? Read(string path)
    {
        if (Path.GetExtension(path).Equals(".cube", StringComparison.OrdinalIgnoreCase))
        {
            try { return ColorLut.ParseCube(File.ReadAllText(path)); }
            catch (IOException) { return null; }
        }
        // Straight (not premultiplied) BGRA, no colour conversion: lookup images are untagged sRGB.
        using var decoder = graphics.Imaging.CreateDecoderFromFileName(path);
        using var frame = decoder.GetFrame(0);
        using var converter = graphics.Imaging.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppBGRA).CheckError();
        var size = converter.Size;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        converter.CopyPixels((uint)stride, pixels);
        return ColorLut.FromPixels(pixels, size.Width, size.Height, stride, 4, bgr: true);
    }

    /// <summary>True when <paramref name="filters"/> has at least one table we can apply.</summary>
    public bool CanApply(IEnumerable<ColorFilter> filters) => filters.Any(f => Table(f) is not null);

    /// <summary>Draws <paramref name="source"/> through <paramref name="filter"/>'s table into <paramref name="target"/>
    /// (replacing its pixels). False (and nothing drawn) when the table can't be read.</summary>
    public unsafe bool Apply(ID2D1Bitmap1 source, ID2D1Bitmap1 target, ColorFilter filter)
    {
        if (Table(filter) is not { } table) return false;
        effect ??= new ID2D1Effect(graphics.Context.CreateEffect(EffectGuids.LookupTable3D));
        var pointer = table.NativePointer;
        effect.SetValue((uint)LookupTable3DProperties.Lut, PropertyType.IUnknown, &pointer, (uint)IntPtr.Size);
        effect.SetInput(0, source, true);
        graphics.DrawInto(target, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.DrawImage(effect.Output, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
        });
        effect.SetInput(0, null, true);
        return true;
    }

    public void Dispose()
    {
        foreach (var table in tables.Values) table?.Dispose();
        effect?.Dispose();
        context2?.Dispose();
    }
}
