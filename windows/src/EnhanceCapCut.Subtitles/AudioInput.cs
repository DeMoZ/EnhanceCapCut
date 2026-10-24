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

    static float[] Resample(float[] input, int from, int to)
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
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x52494646) throw new InvalidDataException($"{path} is not a WAV file");
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
                return ToMono16k(samples, channels, rate);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException($"{path} has no audio data");
    }
}
