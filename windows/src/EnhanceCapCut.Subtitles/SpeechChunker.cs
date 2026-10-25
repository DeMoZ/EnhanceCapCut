namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Cuts a long recording into pieces at its quietest moments. Whisper gets pieces shorter than its 30 s window:
/// with DTW timestamps, Whisper.net 1.9.1 stops after the first window of a longer input (a 3-minute vlog came
/// back as its first 29 seconds). The macOS version's <c>SpeechChunker</c> does the same for Apple dictation,
/// with shorter pieces.
/// </summary>
public static class SpeechChunker
{
    /// <summary>A piece is at least this long (seconds), unless the recording is shorter…</summary>
    public const double MinPiece = 15;
    /// <summary>…and at most this long: the cut goes at the quietest moment between the two, inside Whisper's 30 s.</summary>
    public const double MaxPiece = 28;
    /// <summary>Loudness is measured in frames of this length…</summary>
    const double FrameDuration = 0.02;
    /// <summary>…and averaged over ± this much, so a cut lands in a pause rather than between two syllables.</summary>
    const double Smoothing = 0.25;

    /// <summary>Sample ranges [start, end) of the pieces of <paramref name="samples"/>, in order.</summary>
    public static List<(int Start, int End)> Pieces(float[] samples, int sampleRate)
    {
        var frame = Math.Max(1, (int)(sampleRate * FrameDuration));
        var energy = new float[(samples.Length + frame - 1) / frame];
        for (var f = 0; f < energy.Length; f++)
        {
            int from = f * frame, to = Math.Min(samples.Length, from + frame);
            var sum = 0f;
            for (var i = from; i < to; i++) sum += samples[i] * samples[i];
            energy[f] = sum / (to - from);
        }
        var frameSeconds = (double)frame / sampleRate;
        var bounds = new List<int> { 0 };
        bounds.AddRange(Cuts(energy, frameSeconds).Select(t => (int)Math.Round(t * sampleRate)));
        bounds.Add(samples.Length);
        return bounds.Zip(bounds.Skip(1)).Where(b => b.Second > b.First).ToList();
    }

    /// <summary>Cut times (seconds) for a recording with the given per-frame energy.</summary>
    public static List<double> Cuts(float[] energy, double frameDuration)
    {
        var radius = Math.Max(0, (int)(Smoothing / frameDuration));
        // Moving average through a prefix sum.
        var prefix = new double[energy.Length + 1];
        for (var i = 0; i < energy.Length; i++) prefix[i + 1] = prefix[i] + energy[i];
        double Smoothed(int i)
        {
            int lo = Math.Max(0, i - radius), hi = Math.Min(energy.Length, i + radius + 1);
            return (prefix[hi] - prefix[lo]) / (hi - lo);
        }

        int minFrames = (int)(MinPiece / frameDuration), maxFrames = (int)(MaxPiece / frameDuration);
        var cuts = new List<double>();
        var position = 0;
        while (energy.Length - position > maxFrames)
        {
            // The quietest frame; a later one wins ties, for longer pieces.
            var best = position + maxFrames;
            var bestEnergy = double.PositiveInfinity;
            for (var i = position + minFrames; i < position + maxFrames; i++)
            {
                var e = Smoothed(i);
                if (e <= bestEnergy) { bestEnergy = e; best = i; }
            }
            cuts.Add(best * frameDuration);
            position = best;
        }
        return cuts;
    }
}
