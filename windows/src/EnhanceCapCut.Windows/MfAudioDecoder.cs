using System.Runtime.InteropServices;
using EnhanceCapCut.Render;
using Vortice.MediaFoundation;

namespace EnhanceCapCut.Windows;

/// <summary>
/// Decodes audio with Media Foundation's source reader, which also converts to 48 kHz stereo float
/// (decoder + resampler), for <see cref="AudioMixer"/>.
/// </summary>
public sealed class MfAudioDecoder : IAudioDecoder
{
    public DecodedAudio? Decode(string path, long fromUs, long toUs)
    {
        MF.Startup();
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        reader.SetStreamSelection(MF.AllStreams, false);
        try
        {
            reader.SetStreamSelection(MF.FirstAudioStream, true);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null; // no audio stream
        }
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, MF.Float);
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioMixer.SampleRate);
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 32u);
            type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 8u);
            type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)AudioMixer.SampleRate * 8);
            reader.SetCurrentMediaType(MF.FirstAudioStream, type);
        }
        if (fromUs > 0) reader.SetCurrentPosition(MF.ToTicks(fromUs));

        var samples = new List<float>();
        long? start = null;
        while (true)
        {
            var sample = reader.ReadSample(MF.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out var timestamp);
            using (sample)
            {
                if (sample is not null)
                {
                    var time = MF.ToMicroseconds(timestamp);
                    start ??= time;
                    if (time > toUs) break;
                    Append(sample, samples);
                }
            }
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
        }
        if (start is null || samples.Count == 0) return null;
        // Seeking lands on a packet boundary at or before `fromUs`; the mixer works from the actual start.
        return new DecodedAudio(samples.ToArray(), start.Value);
    }

    static void Append(IMFSample sample, List<float> samples)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var data, out _, out var length);
        try
        {
            var floats = new float[length / 4];
            Marshal.Copy(data, floats, 0, floats.Length);
            samples.AddRange(floats);
        }
        finally
        {
            buffer.Unlock();
        }
    }
}
