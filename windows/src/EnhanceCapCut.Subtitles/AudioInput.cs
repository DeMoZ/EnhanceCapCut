using System.Buffers.Binary;

namespace EnhanceCapCut.Subtitles;

/// <summary>Turns the timeline's mix into what Whisper expects: 16 kHz mono float.</summary>
public static class AudioInput
{
    public const int SampleRate = 16_000;

    /// <summary>Interleaved samples at any rate → 16 kHz mono (channel average, windowed-sinc low-pass resampling).</summary>
    public static float[] ToMono16k(float[] interleaved, int channels, int sampleRate)
    {
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return sampleRate == SampleRate ? mono : Resample(mono, sampleRate, SampleRate);
    }

    /// <summary>Mono resampling with a windowed-sinc low-pass.</summary>
    public static float[] Resample(float[] input, int from, int to)
    {
        const int halfTaps = 16;
        var ratio = (double)from / to;
        var cutoff = Math.Min(1.0, (double)to / from) * 0.95;
        var output = new float[(long)(input.Length / ratio)];
        for (var i = 0; i < output.Length; i++)
        {
            var center = i * ratio;
            var first = (int)Math.Floor(center) - halfTaps + 1;
            double sum = 0, weights = 0;
            for (var k = first; k < first + 2 * halfTaps; k++)
            {
                if (k < 0 || k >= input.Length) continue;
                var x = (k - center) * cutoff;
                var sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                var window = 0.5 + 0.5 * Math.Cos(Math.PI * (k - center) / halfTaps);
                var w = sinc * Math.Max(0, window);
                sum += input[k] * w;
                weights += w;
            }
            output[i] = weights > 0 ? (float)(sum / weights) : 0;
        }
        return output;
    }

    /// <summary>Reads a PCM (16/24/32-bit) or float WAV file as 16 kHz mono.</summary>
    public static float[] ReadWav(string path)
    {
        var (samples, channels, rate) = ParseWav(File.ReadAllBytes(path), path);
        return ToMono16k(samples, channels, rate);
    }

    /// <summary>The interleaved samples of a PCM (16/24/32-bit) or float WAV file, with its channel count and rate.</summary>
    public static (float[] Samples, int Channels, int SampleRate) ParseWav(byte[] bytes, string name = "audio")
    {
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x52494646) throw new InvalidDataException($"{name} is not a WAV file");
        int channels = 0, rate = 0, bits = 0, format = 0, pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
            var body = pos + 8;
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14));
                if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 24));
            }
            else if (id == "data" && channels > 0)
            {
                var length = Math.Min(size < 0 ? int.MaxValue : size, bytes.Length - body);
                var step = bits / 8;
                var samples = new float[length / step];
                for (var i = 0; i < samples.Length; i++)
                {
                    var s = bytes.AsSpan(body + i * step);
                    samples[i] = (format, bits) switch
                    {
                        (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                        (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                        (1, 24) => ((s[0] | s[1] << 8 | s[2] << 16) << 8 >> 8) / 8388608f,
                        (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                        _ => throw new InvalidDataException($"Unsupported WAV format {format}/{bits} bit"),
                    };
                }
                return (samples, channels, rate);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException($"{name} has no audio data");
    }

    /// <summary>Writes mono samples as a 16-bit PCM WAV file.</summary>
    public static void WriteWav(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var file = File.Create(path);
        var header = new byte[44];
        var data = samples.Length * 2;
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), data);
        file.Write(header);
        var buffer = new byte[data];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), (short)Math.Clamp(Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue));
        file.Write(buffer);
    }
}
