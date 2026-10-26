using System.Diagnostics;
using System.Reflection;
using EnhanceCapCut.Subtitles;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EnhanceCapCut.App;

/// <summary>
/// "Check for Updates…" (the counterpart of Sparkle in the macOS version): asks GitHub for the latest release, offers
/// it, downloads and checks it (<see cref="AppUpdates"/>), then quits and lets a script put the new files in place and
/// start the app again. Also checks quietly once a day at launch; a newer version then shows up in the export window's
/// footer instead of a dialog.
/// </summary>
sealed class Updater(DispatcherQueue ui, Func<Window> owner, Func<Window, Task<bool>> confirmQuit, Action quit)
{
    /// <summary>The running version ("0.6.0"); null for a development build (no version passed to the build).</summary>
    public static Version? Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null || v.Major == 0 && v.Minor == 0 && v.Build <= 0 ? null : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>A newer release found by a check, until it's installed or dismissed.</summary>
    public AppRelease? Available { get; private set; }
    /// <summary>0…1 while the update downloads.</summary>
    public double? Progress { get; private set; }
    public event Action? Changed;

    const string LastCheckKey = "updates.lastCheck";

    /// <summary>The launch check: at most once a day, silent unless there's something new.</summary>
    public async Task CheckInBackground()
    {
        if (Current is null) return;
        var last = Preferences.String(LastCheckKey);
        if (DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
            && DateTime.UtcNow - when < TimeSpan.FromDays(1)) return;
        try
        {
            var release = await AppUpdates.LatestAsync();
            Preferences.Set(LastCheckKey, DateTime.UtcNow.ToString("o"));
            if (release is not null && AppUpdates.IsNewer(release, Current)) Set(release);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Offline or GitHub unavailable: try again next launch.
        }
    }

    /// <summary>The button: says what it finds, offers to install a newer version.</summary>
    public async Task Check()
    {
        if (Current is not { } current || Progress is not null) return;
        AppRelease? release;
        try
        {
            release = await AppUpdates.LatestAsync();
            Preferences.Set(LastCheckKey, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await Platform.Ask(owner(), "Couldn't check for updates", e.Message, "OK", "", primaryIsDefault: true);
            return;
        }
        if (release is null || !AppUpdates.IsNewer(release, current))
        {
            Set(null);
            await Platform.Ask(owner(), "You're up to date", $"EnhanceCapCut {current} is the latest version.", "OK", "");
            return;
        }
        Set(release);
        await Offer(release, current);
    }

    async Task Offer(AppRelease release, Version current)
    {
        var window = owner();
        var folder = AppContext.BaseDirectory;
        if (!AppUpdates.CanWrite(folder))
        {
            if (await Platform.Ask(window, $"EnhanceCapCut {release.Version} is available",
                    $"You have {current}. This copy can't update itself in {folder}: download the new zip from the release page, "
                    + "or move the EnhanceCapCut folder somewhere you can write (e.g. Documents).", "Open Release Page", "Later"))
                Platform.OpenUri(release.PageUrl);
            return;
        }
        if (!await Platform.Ask(window, $"EnhanceCapCut {release.Version} is available",
                $"You have {current}. Download it and restart? What's new: {release.PageUrl}", "Update and Restart", "Later"))
            return;
        if (!await confirmQuit(window)) return;
        await Install(release);
    }

    /// <summary><c>--auto-update</c> (CI's update test): installs a newer release without asking.</summary>
    public async Task CheckUnattended()
    {
        if (Current is not { } current) return;
        var release = await AppUpdates.LatestAsync();
        if (release is not null && AppUpdates.IsNewer(release, current)) await Install(release);
    }

    /// <summary>Downloads <paramref name="release"/>, then quits and leaves the rest to the install script.</summary>
    async Task Install(AppRelease release)
    {
        var folder = AppContext.BaseDirectory;
        string source;
        try
        {
            Progress = 0;
            Changed?.Invoke();
            var lastReport = DateTime.MinValue;
            source = await Task.Run(() => AppUpdates.DownloadAsync(release, p =>
            {
                if ((DateTime.Now - lastReport).TotalMilliseconds < 200 && p < 1) return;
                lastReport = DateTime.Now;
                ui.TryEnqueue(() => { Progress = p; Changed?.Invoke(); });
            }));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Progress = null;
            Changed?.Invoke();
            await Platform.Ask(owner(), "The update couldn't be downloaded", e.Message, "OK", "");
            return;
        }

        var script = Path.Combine(Path.GetTempPath(), "EnhanceCapCut-update.ps1");
        File.WriteAllText(script, AppUpdates.InstallScript(source, folder.TrimEnd(Path.DirectorySeparatorChar), Environment.ProcessId));
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
        quit();
    }

    /// <summary>Started again by the update script after a file couldn't be replaced.</summary>
    public async Task ReportFailedUpdate() =>
        await Platform.Ask(owner(), "The update wasn't installed",
            "Some files couldn't be replaced — is ecc.exe or another copy of EnhanceCapCut running? Close it and check for updates again.",
            "OK", "");

    void Set(AppRelease? release)
    {
        Available = release;
        Changed?.Invoke();
    }
}
