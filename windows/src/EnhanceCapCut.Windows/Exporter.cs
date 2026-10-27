using System.Diagnostics;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using Vortice.Direct2D1;

namespace EnhanceCapCut.Windows;

public sealed record ExportProgress(ExportProgress.Phases Phase, double Fraction, int FramesDone, int FramesTotal, TimeSpan Elapsed)
{
    public enum Phases { Audio, Loudness, Video, Finishing }

    public string PhaseTitle => Phase switch
    {
        Phases.Audio => EnhanceCapCut.Draft.Localization.L("Mixing audio"),
        Phases.Loudness => EnhanceCapCut.Draft.Localization.L("Normalizing loudness"),
        Phases.Video => EnhanceCapCut.Draft.Localization.L("Rendering video"),
        _ => EnhanceCapCut.Draft.Localization.L("Finishing"),
    };
}

/// <summary>
/// Renders a timeline to an MP4 (the counterpart of the macOS Exporter): audio mix → optional loudness
/// normalization → frame-by-frame Direct2D composition encoded with Media Foundation, audio interleaved about a
/// second ahead of the video.
/// </summary>
public sealed class Exporter(Timeline timeline, ExportSettings settings, string outputPath)
{
    /// <summary>Optional sub-range to export (µs), mainly for quick tests.</summary>
    public TimeRange? Range { get; set; }
    public List<string> Warnings { get; } = [];

    public void Run(Action<ExportProgress>? progress = null, CancellationToken cancel = default)
    {
        var clockTime = Stopwatch.StartNew();
        var source = settings.BurnInSubtitles ? timeline : timeline.WithoutSubtitles();
        var (width, height) = settings.OutputSize(source.CanvasWidth, source.CanvasHeight);
        var clock = FrameClock.For(settings.Fps(source.Fps));
        var span = Range ?? new TimeRange(0, source.Duration);
        var frameCount = clock.FrameCount(span.Duration);
        void Report(ExportProgress.Phases phase, double fraction, int done = 0) =>
            progress?.Invoke(new(phase, Math.Clamp(fraction, 0, 1), done, frameCount, clockTime.Elapsed));

        // 1. Audio.
        Report(ExportProgress.Phases.Audio, 0);
        var audioTimeline = Range is { } r ? AudioMixer.TrimmedForAudio(source, r) : source;
        var withVoiceEffects = audioTimeline.AudioClips.Count(c => !c.Voice.IsEmpty);
        if (withVoiceEffects > 0)
        {
            Warnings.Add($"Voice effects (isolate voice, reduce noise, enhance voice, voice filters) are not applied on Windows yet: {withVoiceEffects} clip(s) play unprocessed");
        }
        var mix = AudioMixer.Mix(audioTimeline, new MfAudioDecoder(), Warnings, p => Report(ExportProgress.Phases.Audio, p * 0.08), cancel);
        if (settings.NormalizeLoudness)
        {
            Report(ExportProgress.Phases.Loudness, 0.08);
            Loudness.Normalize(mix, settings.TargetLUFS);
        }
        var pcm = new short[mix.Length];
        for (var i = 0; i < mix.Length; i++) pcm[i] = (short)Math.Clamp(Math.Round(mix[i] * 32767), short.MinValue, short.MaxValue);
        var audioFrames = pcm.Length / 2;
        cancel.ThrowIfCancellationRequested();

        // 2. Writer and compositor.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath)) File.Delete(outputPath);
        using var graphics = new Graphics();
        if (graphics.IsSoftware) Warnings.Add("No usable GPU: rendered with the WARP software rasterizer (slower)");
        using var compositor = new Compositor(graphics, source, width, height, Warnings);
        using var readback = graphics.CreateBitmap(width, height, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
        var completed = false;
        try
        {
            using var writer = new MfVideoWriter(outputPath, width, height, clock, settings);
            long audioDone = 0;
            void PumpAudio(long untilFrame)
            {
                const int chunk = AudioMixer.SampleRate / 10;
                while (audioDone < Math.Min(untilFrame, audioFrames))
                {
                    var n = (int)Math.Min(chunk, audioFrames - audioDone);
                    writer.WriteAudio(pcm, audioDone, n);
                    audioDone += n;
                }
            }

            // 3. Frames.
            for (var index = 0; index < frameCount; index++)
            {
                cancel.ThrowIfCancellationRequested();
                var ticks = clock.Ticks(index);
                // Keep audio about a second ahead of video so the writer can interleave.
                PumpAudio(AudioMixer.Frames((ticks + 10_000_000) / 10));
                var frame = compositor.Render(span.Start + clock.Microseconds(index));
                readback.CopyFromBitmap(frame).CheckError();
                var mapped = readback.Map(MapOptions.Read);
                try
                {
                    writer.WriteVideo(mapped.Bits, (int)mapped.Pitch, ticks, clock.Ticks(index + 1) - ticks);
                }
                finally
                {
                    readback.Unmap();
                }
                if (index % 5 == 0 || index == frameCount - 1)
                    Report(ExportProgress.Phases.Video, 0.1 + 0.88 * (index + 1) / frameCount, index + 1);
            }
            PumpAudio(audioFrames);
            Report(ExportProgress.Phases.Finishing, 0.99, frameCount);
            writer.Finish();
            completed = true;
        }
        finally
        {
            if (!completed && File.Exists(outputPath)) File.Delete(outputPath);
        }
        Report(ExportProgress.Phases.Finishing, 1, frameCount);
    }
}
