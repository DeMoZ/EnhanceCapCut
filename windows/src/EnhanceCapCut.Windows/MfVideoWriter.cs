using System.Runtime.InteropServices;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using Vortice.MediaFoundation;

namespace EnhanceCapCut.Windows;

/// <summary>
/// Writes an MP4 with Media Foundation's Sink Writer: H.264 or HEVC video (the system's encoders, hardware where
/// available; the writer converts our RGB32 frames itself) and 48 kHz stereo AAC audio, tagged BT.709.
/// </summary>
public sealed class MfVideoWriter : IDisposable
{
    readonly IMFSinkWriter writer;
    readonly int videoStream, audioStream;
    readonly int width, height;
    bool finished;

    /// <summary>AAC at 192 kbit/s (the highest rate Windows' AAC encoder offers), in bytes per second.</summary>
    const uint AacBytesPerSecond = 24_000;

    /// <summary>
    /// "Quality" 0…100 as bits per pixel per frame: the hardware encoders don't share a constant-quality mode,
    /// so the slider sets an average bitrate. 65 on a 1080×1920 30 fps H.264 export ≈ 7.7 Mbit/s.
    /// </summary>
    public static double BitsPerPixel(double quality) => 0.03 + 0.22 * Math.Pow(Math.Clamp(quality, 0, 100) / 100, 2);
    /// <summary>HEVC needs about this fraction of H.264's bitrate for the same quality.</summary>
    public const double HevcEfficiency = 0.7;

    public static uint Bitrate(ExportSettings settings, int width, int height, double fps)
    {
        if (settings.RateControl == ExportSettings.RateControlMode.Bitrate) return (uint)(settings.BitrateMbps * 1_000_000);
        var bits = BitsPerPixel(settings.Quality) * width * height * fps;
        if (settings.Codec != ExportSettings.VideoCodec.H264) bits *= HevcEfficiency;
        return (uint)Math.Clamp(bits, 500_000, 400_000_000);
    }

    public MfVideoWriter(string path, int width, int height, FrameClock clock, ExportSettings settings)
    {
        MF.Startup();
        this.width = width;
        this.height = height;
        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);

        var frameSize = MF.Pack((uint)width, (uint)height);
        // MF_MT_FRAME_RATE is frames per second as numerator/denominator.
        var frameRate = MF.Pack((uint)clock.Denominator, (uint)clock.Numerator);
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, settings.Codec == ExportSettings.VideoCodec.H264 ? MF.H264 : MF.Hevc);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, Bitrate(settings, width, height, clock.Fps));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, MF.Progressive);
            output.Set(MediaTypeAttributeKeys.FrameSize, frameSize);
            output.Set(MediaTypeAttributeKeys.FrameRate, frameRate);
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, MF.Pack(1, 1));
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, settings.Codec switch
            {
                ExportSettings.VideoCodec.H264 => MF.H264High,
                ExportSettings.VideoCodec.Hevc => MF.HevcMain,
                _ => MF.HevcMain10,
            });
            output.Set(MediaTypeAttributeKeys.VideoPrimaries, MF.PrimariesBT709);
            output.Set(MediaTypeAttributeKeys.TransferFunction, MF.TransferBT709);
            output.Set(MediaTypeAttributeKeys.YuvMatrix, MF.MatrixBT709);
            output.Set(MediaTypeAttributeKeys.VideoNominalRange, MF.NominalRange16To235);
            videoStream = writer.AddStream(output);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, MF.Rgb32);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, MF.Progressive);
            input.Set(MediaTypeAttributeKeys.FrameSize, frameSize);
            input.Set(MediaTypeAttributeKeys.FrameRate, frameRate);
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, MF.Pack(1, 1));
            // Positive stride: rows top-down.
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));
            try
            {
                writer.SetInputMediaType(videoStream, input, null);
            }
            catch (SharpGen.Runtime.SharpGenException e)
            {
                throw new InvalidOperationException(
                    $"This PC has no {ExportSettings.Title(settings.Codec)} encoder for {width}×{height} ({e.Message.Trim()}). "
                    + "Try H.264 or HEVC (8-bit).", e);
            }
        }

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            output.Set(MediaTypeAttributeKeys.Subtype, MF.Aac);
            output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioMixer.SampleRate);
            output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
            output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AacBytesPerSecond);
            audioStream = writer.AddStream(output);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            input.Set(MediaTypeAttributeKeys.Subtype, MF.Pcm);
            input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioMixer.SampleRate);
            input.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
            input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
            input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)AudioMixer.SampleRate * 4);
            writer.SetInputMediaType(audioStream, input, null);
        }
        writer.BeginWriting();
    }

    /// <summary>Writes one frame from top-down BGRA rows starting at <paramref name="pixels"/>.</summary>
    public unsafe void WriteVideo(IntPtr pixels, int pitch, long timeTicks, long durationTicks)
    {
        var rowBytes = width * 4;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(rowBytes * height);
        buffer.Lock(out var data, out _, out _);
        try
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy((byte*)pixels + (long)y * pitch, (byte*)data + (long)y * rowBytes, rowBytes, rowBytes);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = rowBytes * height;
        Write(videoStream, buffer, timeTicks, durationTicks);
    }

    /// <summary>Writes interleaved 16-bit stereo PCM frames starting at timeline frame <paramref name="firstFrame"/>.</summary>
    public void WriteAudio(short[] pcm, long firstFrame, int frames)
    {
        if (frames <= 0) return;
        var bytes = frames * 4;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out var data, out _, out _);
        try { Marshal.Copy(pcm, (int)(firstFrame * 2), data, frames * 2); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = bytes;
        const long ticksPerSecond = 10_000_000;
        Write(audioStream, buffer, firstFrame * ticksPerSecond / AudioMixer.SampleRate, frames * ticksPerSecond / AudioMixer.SampleRate);
    }

    void Write(int stream, IMFMediaBuffer buffer, long time, long duration)
    {
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        writer.WriteSample(stream, sample);
    }

    /// <summary>Finishes the file (writes the MP4 index).</summary>
    public void Finish()
    {
        if (finished) return;
        finished = true;
        writer.Finalize();
    }

    public void Dispose() => writer.Dispose();
}
