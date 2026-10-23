using Vortice.Direct2D1;
using Vortice.WIC;

namespace EnhanceCapCut.Windows;

/// <summary>Loads photos with WIC into Direct2D bitmaps, upright (EXIF orientation applied), cached by path.</summary>
public sealed class ImageLoader(Graphics graphics) : IDisposable
{
    readonly Dictionary<string, ID2D1Bitmap1?> cache = [];

    /// <summary>The photo, or null when WIC can't decode it.</summary>
    public ID2D1Bitmap1? Load(string path)
    {
        if (cache.TryGetValue(path, out var cached)) return cached;
        ID2D1Bitmap1? bitmap = null;
        try
        {
            using var decoder = graphics.Imaging.CreateDecoderFromFileName(path);
            using var frame = decoder.GetFrame(0);
            using var converter = graphics.Imaging.CreateFormatConverter();
            IWICBitmapSource source = frame;
            IWICBitmapFlipRotator? rotator = null;
            if (Orientation(frame) is { } transform and not BitmapTransformOptions.Rotate0)
            {
                rotator = graphics.Imaging.CreateBitmapFlipRotator();
                rotator.Initialize(frame, transform);
                source = rotator;
            }
            converter.Initialize(source, PixelFormat.Format32bppPBGRA).CheckError();
            bitmap = graphics.Context.CreateBitmapFromWicBitmap(converter);
            rotator?.Dispose();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            bitmap = null;
        }
        cache[path] = bitmap;
        return bitmap;
    }

    /// <summary>The EXIF orientation tag (274) as a WIC transform; WIC's rotations are clockwise.</summary>
    static BitmapTransformOptions? Orientation(IWICBitmapFrameDecode frame)
    {
        try
        {
            using var reader = frame.MetadataQueryReader;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                object? value;
                try { value = reader.GetMetadataByName(query).Value; }
                catch (SharpGen.Runtime.SharpGenException) { continue; }
                return Convert.ToInt32(value) switch
                {
                    2 => BitmapTransformOptions.FlipHorizontal,
                    3 => BitmapTransformOptions.Rotate180,
                    4 => BitmapTransformOptions.FlipVertical,
                    5 => BitmapTransformOptions.Rotate90 | BitmapTransformOptions.FlipHorizontal,
                    6 => BitmapTransformOptions.Rotate90,
                    7 => BitmapTransformOptions.Rotate270 | BitmapTransformOptions.FlipHorizontal,
                    8 => BitmapTransformOptions.Rotate270,
                    _ => BitmapTransformOptions.Rotate0,
                };
            }
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or InvalidCastException or FormatException)
        {
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var bitmap in cache.Values) bitmap?.Dispose();
        cache.Clear();
    }
}
