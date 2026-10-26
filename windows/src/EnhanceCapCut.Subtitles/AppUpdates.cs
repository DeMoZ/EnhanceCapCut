using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace EnhanceCapCut.Subtitles;

/// <summary>A published release with a Windows build.</summary>
public sealed record AppRelease(Version Version, string ZipUrl, string? Sha256, string PageUrl);

/// <summary>
/// Updates of the Windows app from GitHub Releases, the counterpart of Sparkle in the macOS version. The release zip is
/// checked against the SHA-256 GitHub publishes for it, unpacked next to the temp folder, and copied over the app's
/// folder by a small PowerShell script once the app has quit; the script starts the new version.
/// </summary>
public static class AppUpdates
{
    public const string LatestReleaseApi = "https://api.github.com/repos/DeMoZ/EnhanceCapCut/releases/latest";
    const string ZipSuffix = "-windows-x64.zip";
    const string WorkPrefix = "EnhanceCapCut-update-";

    /// <summary>The latest release, or null when it has no Windows build.</summary>
    public static async Task<AppRelease?> LatestAsync(CancellationToken cancel = default)
    {
        using var http = Client();
        return Parse(await http.GetStringAsync(LatestReleaseApi, cancel));
    }

    /// <summary>Reads GitHub's "latest release" answer: the tag ("v0.7.0"), the Windows zip and its SHA-256 digest.</summary>
    public static AppRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(ZipSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null) continue;
            // "sha256:<hex>"; missing on assets uploaded before GitHub started computing digests.
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..].ToLowerInvariant() : null;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new AppRelease(version, url, sha, page);
        }
        return null;
    }

    /// <summary>Whether <paramref name="release"/> is newer than <paramref name="current"/> (major.minor.build).</summary>
    public static bool IsNewer(AppRelease release, Version current) => Normalize(release.Version) > Normalize(current);

    static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// Downloads the release zip, checks its SHA-256 and unpacks it into a fresh temp folder. Returns the unpacked app
    /// folder (the one with EnhanceCapCut.exe).
    /// </summary>
    public static async Task<string> DownloadAsync(AppRelease release, Action<double>? progress = null, CancellationToken cancel = default)
    {
        var work = Path.Combine(Path.GetTempPath(), WorkPrefix + release.Version);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, "update.zip");

        using (var http = Client())
        using (var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Invoke((double)done / total);
            }
        }

        if (release.Sha256 is { } expected)
        {
            await using var stream = File.OpenRead(zip);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
            if (actual != expected)
                throw new InvalidDataException($"The downloaded update doesn't match its published checksum ({actual} ≠ {expected}).");
        }

        var unpacked = Path.Combine(work, "app");
        ZipFile.ExtractToDirectory(zip, unpacked);
        File.Delete(zip);
        var exe = Directory.EnumerateFiles(unpacked, "EnhanceCapCut.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException("The update has no EnhanceCapCut.exe.");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>
    /// The PowerShell script that waits for the app (process <paramref name="processId"/>) to quit, copies the new files
    /// from <paramref name="source"/> over <paramref name="target"/> (files that only the old version had stay) and
    /// starts the app again: with <c>--update-failed</c> when a file couldn't be replaced, e.g. a running ecc.exe.
    /// </summary>
    public static string InstallScript(string source, string target, int processId)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var exe = Path.Combine(target, "EnhanceCapCut.exe");
        // The download's folder (EnhanceCapCut-update-<version>) goes away after the copy.
        var work = new DirectoryInfo(source);
        while (work.Parent is not null && !work.Name.StartsWith(WorkPrefix, StringComparison.Ordinal)) work = work.Parent;
        var cleanup = work.Name.StartsWith(WorkPrefix, StringComparison.Ordinal) ? work.FullName : source;
        return $"""
            Wait-Process -Id {processId} -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
            robocopy {Quote(source)} {Quote(target)} /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) {"{"} Start-Process -FilePath {Quote(exe)} -ArgumentList '--update-failed'; exit 1 {"}"}
            Remove-Item -LiteralPath {Quote(cleanup)} -Recurse -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath {Quote(exe)}
            """;
    }

    /// <summary>Whether the app can replace its own files in <paramref name="folder"/> (not e.g. in Program Files).</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".update-check-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EnhanceCapCut-updater");
        return http;
    }
}
