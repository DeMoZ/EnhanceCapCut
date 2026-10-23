using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace EnhanceCapCut.Windows;

/// <summary>A decoded frame: BGRA (alpha ignored), top-down rows of <see cref="Stride"/> bytes.</summary>
public sealed class VideoFrame(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride => Width * 4;
    public byte[] Pixels { get; } = new byte[width * height * 4];
    /// <summary>Source time in µs.</summary>
    public long Time { get; set; }
    /// <summary>Changes whenever new pixels are copied in (lets the compositor skip re-uploading a held frame).</summary>
    public int Version { get; set; }
}

/// <summary>
/// Decodes one clip's video sequentially (the counterpart of the macOS VideoFrameSource). The renderer asks for
/// increasing source times; frames are read forward and the latest frame at or before the requested time is
/// returned. Media Foundation's video processor converts to 32-bit RGB.
/// </summary>
public sealed class MfVideoReader : IDisposable
{
    readonly IMFSourceReader reader;
    readonly int defaultStride;
    VideoFrame? current, pending, spare;
    bool finished;
    int versions;

    public int Width { get; }
    public int Height { get; }
    /// <summary>
    /// <c>MF_MT_VIDEO_ROTATION</c> of the stream (0, 90, 180, 270): the stored frames are turned this far
    /// counter-clockwise from upright (phone videos shot in portrait), so the compositor turns them back clockwise.
    /// Not yet checked against a real portrait clip on Windows.
    /// </summary>
    public int Rotation { get; }
    /// <summary>The source's transfer function (MFVideoTransFunc), to spot HDR clips.</summary>
    public uint Transfer { get; }
    public bool IsHdr => Transfer is MF.TransferHLG or MF.TransferPQ;

    /// <summary>Opens <paramref name="path"/> for reading from source time <paramref name="fromUs"/>.</summary>
    public MfVideoReader(string path, long fromUs)
    {
        MF.Startup();
        using var attributes = MediaFactory.MFCreateAttributes(1);
        // Decode (in hardware where possible) and convert to RGB32: MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING.
        attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
        reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        reader.SetStreamSelection(MF.AllStreams, false);
        reader.SetStreamSelection(MF.FirstVideoStream, true);

        using (var native = reader.GetNativeMediaType(MF.FirstVideoStream, 0))
        {
            Rotation = TryGetUInt32(native, MediaTypeAttributeKeys.VideoRotation) switch
            {
                90 => 90, 180 => 180, 270 => 270, _ => 0,
            };
            Transfer = TryGetUInt32(native, MediaTypeAttributeKeys.TransferFunction) ?? 0;
        }

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, MF.Rgb32);
            reader.SetCurrentMediaType(MF.FirstVideoStream, output);
        }
        using (var type = reader.GetCurrentMediaType(MF.FirstVideoStream))
        {
            var (w, h) = MF.Unpack(type.GetUInt64(MediaTypeAttributeKeys.FrameSize));
            Width = (int)w;
            Height = (int)h;
            // Negative = bottom-up rows (the RGB default when the attribute is missing is top-down here, as the
            // video processor writes it).
            defaultStride = TryGetUInt32(type, MediaTypeAttributeKeys.DefaultStride) is { } s ? unchecked((int)s) : Width * 4;
        }
        if (fromUs > 0) reader.SetCurrentPosition(MF.ToTicks(fromUs));
    }

    /// <summary>The frame displayed at source time <paramref name="us"/>, or null when the clip has no frames.</summary>
    public VideoFrame? FrameAt(long us)
    {
        if (pending is null && !finished) pending = ReadNext();
        while (pending is { } next && next.Time <= us)
        {
            spare = current;
            current = next;
            pending = ReadNext();
        }
        // A time before the first decoded frame shows the first frame.
        return current ?? pending;
    }

    VideoFrame? ReadNext()
    {
        while (!finished)
        {
            var sample = reader.ReadSample(MF.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var timestamp);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) finished = true;
            if (sample is null) continue;
            using (sample)
            {
                var frame = spare ?? new VideoFrame(Width, Height);
                spare = null;
                frame.Time = MF.ToMicroseconds(timestamp);
                frame.Version = ++versions;
                Copy(sample, frame);
                return frame;
            }
        }
        return null;
    }

    unsafe void Copy(IMFSample sample, VideoFrame frame)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        var rowBytes = frame.Stride;
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2D is not null)
        {
            // Lock2D gives the top row and a signed pitch whatever the buffer's orientation.
            buffer2D.Lock2D(out var scanline0, out var pitch);
            try { CopyRows((byte*)scanline0, pitch, frame, rowBytes); }
            finally { buffer2D.Unlock2D(); }
            return;
        }
        buffer.Lock(out var data, out _, out _);
        try
        {
            var top = defaultStride < 0 ? (byte*)data + (long)(Height - 1) * -defaultStride : (byte*)data;
            CopyRows(top, defaultStride, frame, rowBytes);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    static unsafe void CopyRows(byte* top, int pitch, VideoFrame frame, int rowBytes)
    {
        fixed (byte* dst = frame.Pixels)
        {
            for (var y = 0; y < frame.Height; y++)
                Buffer.MemoryCopy(top + (long)y * pitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
        }
    }

    internal static uint? TryGetUInt32(IMFAttributes attributes, Guid key)
    {
        try { return attributes.GetUInt32(key); }
        catch (SharpGen.Runtime.SharpGenException) { return null; }
    }

    public void Dispose() => reader.Dispose();
}
