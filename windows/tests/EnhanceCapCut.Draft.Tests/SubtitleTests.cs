using EnhanceCapCut.Core;

namespace EnhanceCapCut.Draft.Tests;

// The Windows side of subtitles: Whisper's windows and annotations, and app updates. Segmentation, wrapping and the
// caption writer are the Rust core's (core/ecc-core, cargo test).

public class SpeechChunkerTests
{
    [Fact]
    public void CutsInThePausesWithinWhispersWindow()
    {
        // 80 s of speech at 50 frames/s with silent half-second pauses at 20 s and 45 s.
        var energy = Enumerable.Range(0, 4000).Select(i => i is >= 1000 and < 1025 or >= 2250 and < 2275 ? 0.001f : 1f).ToArray();
        var cuts = Subtitles.SpeechChunker.Cuts(energy, 0.02);
        Assert.Equal(3, cuts.Count);
        Assert.Equal(20.25, cuts[0], 0.3);
        Assert.Equal(45.25, cuts[1], 0.3);
        // No pause left: the piece is cut at the longest allowed length.
        Assert.Equal(cuts[1] + Subtitles.SpeechChunker.MaxPiece, cuts[2], 0.05);
        Assert.Empty(Subtitles.SpeechChunker.Cuts(energy[..1400], 0.02));
    }

    [Fact]
    public void PiecesCoverTheWholeRecording()
    {
        var samples = new float[16_000 * 65];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)Math.Sin(i * 0.05) * (i / 16_000 % 20 == 0 ? 0.01f : 1f);
        var pieces = Subtitles.SpeechChunker.Pieces(samples, 16_000);
        Assert.Equal(0, pieces[0].Start);
        Assert.Equal(samples.Length, pieces[^1].End);
        Assert.All(pieces.Zip(pieces.Skip(1)), p => Assert.Equal(p.First.End, p.Second.Start));
        Assert.All(pieces, p => Assert.InRange(p.End - p.Start, 1, 16_000 * Subtitles.SpeechChunker.MaxPiece + 1));
    }

    [Fact]
    public void WhisperAnnotationsAreDropped()
    {
        var words = new[] { "*смех*", "Привет,", "[звук", "мотора]", "мир", "♪", "(аплодисменты).", "пока" }
            .Select((t, i) => new TimedWord(t, i, i + 0.5)).ToList();
        Assert.Equal(["Привет,", "мир", "пока"], Subtitles.SpeechRecognizer.WithoutAnnotations(words).Select(w => w.Text));
    }
}

public class AppUpdatesTests
{
    const string Latest = """
    {"tag_name": "v0.7.0", "html_url": "https://github.com/DeMoZ/EnhanceCapCut/releases/tag/v0.7.0",
     "assets": [
       {"name": "EnhanceCapCut-0.7.0.pkg", "browser_download_url": "https://example.com/a.pkg", "digest": "sha256:AA"},
       {"name": "EnhanceCapCut-0.7.0-windows-x64.zip", "browser_download_url": "https://example.com/w.zip", "digest": "sha256:ABCDEF"},
       {"name": "appcast.xml", "browser_download_url": "https://example.com/appcast.xml"}
     ]}
    """;

    [Fact]
    public void ReadsTheWindowsBuildOfTheLatestRelease()
    {
        var release = Subtitles.AppUpdates.Parse(Latest)!;
        Assert.Equal(new Version(0, 7, 0), release.Version);
        Assert.Equal("https://example.com/w.zip", release.ZipUrl);
        Assert.Equal("abcdef", release.Sha256);
        Assert.Equal("https://github.com/DeMoZ/EnhanceCapCut/releases/tag/v0.7.0", release.PageUrl);
        // A release without a Windows zip, or with an odd tag, offers nothing.
        Assert.Null(Subtitles.AppUpdates.Parse("""{"tag_name": "v0.7.0", "assets": []}"""));
        Assert.Null(Subtitles.AppUpdates.Parse("""{"tag_name": "nightly", "assets": []}"""));
    }

    [Fact]
    public void ComparesVersions()
    {
        var release = Subtitles.AppUpdates.Parse(Latest)!;
        Assert.True(Subtitles.AppUpdates.IsNewer(release, new Version(0, 6, 0)));
        // Assembly versions carry a fourth number.
        Assert.True(Subtitles.AppUpdates.IsNewer(release, new Version(0, 6, 9, 0)));
        Assert.False(Subtitles.AppUpdates.IsNewer(release, new Version(0, 7, 0, 0)));
        Assert.False(Subtitles.AppUpdates.IsNewer(release, new Version(1, 0)));
    }

    [Fact]
    public void InstallScriptWaitsCopiesAndRestarts()
    {
        var source = Path.Combine(Path.GetTempPath(), "EnhanceCapCut-update-0.7.0", "app", "EnhanceCapCut-0.7.0-windows-x64");
        var script = Subtitles.AppUpdates.InstallScript(source, "C:/Users/O'Brien/EnhanceCapCut", 1234);
        Assert.Contains("Wait-Process -Id 1234", script);
        Assert.Contains($"robocopy '{source}' 'C:/Users/O''Brien/EnhanceCapCut' /E", script);
        Assert.Contains("--update-failed", script);
        Assert.Contains($"Remove-Item -LiteralPath '{Path.Combine(Path.GetTempPath(), "EnhanceCapCut-update-0.7.0")}'", script);
        Assert.EndsWith($"Start-Process -FilePath '{Path.Combine("C:/Users/O'Brien/EnhanceCapCut", "EnhanceCapCut.exe").Replace("'", "''")}'", script);
    }
}
