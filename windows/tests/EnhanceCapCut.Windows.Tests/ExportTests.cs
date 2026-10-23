using System.Runtime.InteropServices;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using EnhanceCapCut.Windows;

namespace EnhanceCapCut.Windows.Tests;

/// <summary>
/// End-to-end checks of the Windows renderer: encode a synthetic clip with our writer, export a draft that uses it,
/// read the result back with our readers. They need Windows (Media Foundation, Direct3D/WARP) and do nothing elsewhere.
/// </summary>
public sealed class ExportTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ecc-windows-test-" + Guid.NewGuid());

    public ExportTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    const int ClipWidth = 320, ClipHeight = 240;

    /// <summary>2 s at 30 fps: left half red, right half blue; a 440 Hz tone.</summary>
    unsafe string WriteSourceClip()
    {
        var path = Path.Combine(folder, "source.mp4");
        var settings = new ExportSettings { Codec = ExportSettings.VideoCodec.H264, Quality = 90 };
        var clock = FrameClock.For(30);
        var pixels = new byte[ClipWidth * ClipHeight * 4];
        for (var y = 0; y < ClipHeight; y++)
        for (var x = 0; x < ClipWidth; x++)
        {
            var i = (y * ClipWidth + x) * 4;
            var red = x < ClipWidth / 2;
            pixels[i] = (byte)(red ? 0 : 255);     // B
            pixels[i + 1] = 0;                     // G
            pixels[i + 2] = (byte)(red ? 255 : 0); // R
            pixels[i + 3] = 255;
        }
        var tone = new short[AudioMixer.SampleRate * 2 * 2];
        for (var i = 0; i < tone.Length / 2; i++)
            tone[i * 2] = tone[i * 2 + 1] = (short)(0.4 * 32767 * Math.Sin(2 * Math.PI * 440 * i / AudioMixer.SampleRate));
        using var writer = new MfVideoWriter(path, ClipWidth, ClipHeight, clock, settings);
        writer.WriteAudio(tone, 0, tone.Length / 2);
        fixed (byte* p = pixels)
        {
            for (var f = 0; f < 60; f++) writer.WriteVideo((IntPtr)p, ClipWidth * 4, clock.Ticks(f), clock.Ticks(f + 1) - clock.Ticks(f));
        }
        writer.Finish();
        return path;
    }

    static string DraftJson(string clip)
    {
        var path = clip.Replace("\\", "\\\\");
        return $$$$"""
    {
      "id": "D", "name": "Test", "duration": 2000000, "fps": 30,
      "canvas_config": {"width": 640, "height": 360, "ratio": "16:9"},
      "tracks": [
        {"type": "video", "segments": [
          {"id": "S1", "material_id": "V1", "extra_material_refs": ["C1"],
           "source_timerange": {"start": 0, "duration": 2000000}, "target_timerange": {"start": 0, "duration": 2000000}}]},
        {"type": "text", "segments": [
          {"id": "S2", "material_id": "X1", "target_timerange": {"start": 0, "duration": 2000000},
           "clip": {"transform": {"x": 0, "y": 0.75}, "scale": {"x": 1, "y": 1}}}]}
      ],
      "materials": {
        "videos": [{"id": "V1", "type": "video", "path": "{{{{path}}}}", "width": 320, "height": 240, "duration": 2000000}],
        "canvases": [{"id": "C1", "type": "canvas_color", "color": "#00FF00"}],
        "texts": [{"id": "X1", "type": "text", "font_size": 20, "text_color": "#FFFFFF",
                   "content": "{\"text\":\"HI\",\"styles\":[{\"range\":[0,2],\"size\":20}]}"}]
      }
    }
    """;
    }

    static (byte R, byte G, byte B) Pixel(VideoFrame frame, int x, int y)
    {
        var i = y * frame.Stride + x * 4;
        return (frame.Pixels[i + 2], frame.Pixels[i + 1], frame.Pixels[i]);
    }

    [Fact]
    public void ExportsVideoTextCanvasAndAudio()
    {
        if (!OperatingSystem.IsWindows()) return;
        var clip = WriteSourceClip();
        var timeline = TimelineBuilder.Build(EnhanceCapCut.Draft.Draft.Parse(DraftJson(clip)), folder);
        Assert.Empty(timeline.Report.MissingFiles);
        var output = Path.Combine(folder, "out.mp4");
        var settings = new ExportSettings { Codec = ExportSettings.VideoCodec.H264, Quality = 90, NormalizeLoudness = false };
        var exporter = new Exporter(timeline, settings, output);
        exporter.Run();
        Assert.True(File.Exists(output));

        using var reader = new MfVideoReader(output, 0);
        Assert.Equal(640, reader.Width);
        Assert.Equal(360, reader.Height);
        var frame = reader.FrameAt(1_000_000);
        Assert.NotNull(frame);
        // The 4:3 clip is fitted to the height (x 80…560); the canvas colour shows at the sides.
        var side = Pixel(frame, 30, 180);
        Assert.True(side.G > 200 && side.R < 60 && side.B < 60, $"canvas {side}");
        var left = Pixel(frame, 200, 200);
        Assert.True(left.R > 200 && left.G < 60 && left.B < 60, $"left half {left}");
        var right = Pixel(frame, 440, 200);
        Assert.True(right.B > 200 && right.R < 60 && right.G < 60, $"right half {right}");
        // White text near the top center (y = 0.75 half-canvas units up ≈ 45 px from the top).
        var bright = 0;
        for (var y = 20; y < 75; y++)
        for (var x = 270; x < 370; x++)
        {
            var p = Pixel(frame, x, y);
            if (p.R > 200 && p.G > 200 && p.B > 200) bright++;
        }
        Assert.True(bright > 50, $"text pixels: {bright}");

        var audio = new MfAudioDecoder().Decode(output, 0, 2_000_000);
        Assert.NotNull(audio);
        var samples = audio.Samples;
        int crossings = 0, from = 4_800, to = Math.Min(audio.Frames, 72_000);
        double sum = 0;
        for (var i = from + 1; i < to; i++)
        {
            if (samples[(i - 1) * 2] < 0 && samples[i * 2] >= 0) crossings++;
            sum += samples[i * 2] * samples[i * 2];
        }
        Assert.InRange(crossings * (double)AudioMixer.SampleRate / (to - from), 430, 450);
        Assert.True(Math.Sqrt(sum / (to - from)) > 0.1);
    }

    [Fact]
    public void RendersTextWithOutline()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var graphics = new Graphics();
        using var renderer = new TextImageRenderer(graphics);
        var material = TextMaterial.From(System.Text.Json.JsonDocument.Parse("""{"id": "X", "font_size": 15}""").RootElement)!;
        var content = TextContent.Parse("""{"text":"Hello\nworld","styles":[{"range":[0,11],"strokes":[{"width":0.08}]}]}""")!;
        using var bitmap = renderer.Render(new TextLayerSpec(material, content), 1080, 1);
        Assert.NotNull(bitmap);
        // Two lines of ~95 px type: taller than one line, wider than it is tall.
        Assert.True(bitmap.PixelSize.Height > 180, $"height {bitmap.PixelSize.Height}");
        Assert.True(bitmap.PixelSize.Width > bitmap.PixelSize.Height);
    }
}
