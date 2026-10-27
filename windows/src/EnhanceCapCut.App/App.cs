using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Microsoft.Windows.AppLifecycle;

namespace EnhanceCapCut.App;

/// <summary>
/// The app: one instance, the export window on every launch (a second launch brings it back and re-detects the project
/// open in CapCut, like the macOS Services entry), the subtitles, donate and guide windows on demand (the guide also at the
/// first launch), and the same questions as
/// the macOS version before quitting during an export or a subtitles run.
///
/// Command line (for testing and scripting, like the macOS <c>--args -project … -autoExport YES</c>):
/// <c>EnhanceCapCut.exe [--project &lt;name&gt;] [--auto-export] [--subtitles]</c> (the last one opens the subtitles window too);
/// <c>--update-failed</c> is passed by the update script (<see cref="Updater"/>); <c>--auto-update</c> installs a newer
/// version without asking (CI's update test, with <c>ECC_UPDATES_URL</c>).
/// </summary>
sealed partial class App : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider metadata = new();
    readonly string[] args;
    ExportModel? model;
    ExportWindow? exportWindow;
    SubtitlesModel? subtitles;
    SubtitlesWindow? subtitlesWindow;
    DonateModel? donate;
    DonateWindow? donateWindow;
    GuideWindow? guideWindow;
    bool quitting;

    App(string[] args)
    {
        this.args = args;
        UnhandledException += (_, e) => Log(e.Exception);
    }

    /// <summary>Where a crash is described: <c>%LOCALAPPDATA%\EnhanceCapCut\crash.log</c>.</summary>
    public static string CrashLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EnhanceCapCut", "crash.log");

    static void Log(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}\n\n");
        }
        catch (IOException)
        {
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        if (RedirectToRunningInstance()) return 0;
        Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(args);
        });
        return 0;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var ui = DispatcherQueue.GetForCurrentThread();
        model = new ExportModel(ui) { AutoExport = args.Contains("--auto-export") };
        model.OpenSubtitles = ShowSubtitlesWindow;
        model.OpenDonate = ShowDonateWindow;
        model.OpenGuide = ShowGuideWindow;
        model.Finished = () =>
        {
            if (exportWindow is not null) Platform.Flash(exportWindow);
            if (!model.AutoExport) OfferDonationOncePerDay();
        };
        var updater = new Updater(ui, () => exportWindow!, ConfirmQuit, Quit);
        updater.Changed += model.Notify;
        model.Updates = updater;
        AppInstance.GetCurrent().Activated += (_, _) => ui.TryEnqueue(ShowExportWindow);
        ShowExportWindow();
        if (args.Contains("--subtitles")) ShowSubtitlesWindow();
        if (!model.AutoExport) ShowGuideOnFirstLaunch();
        // Started again by the update script: either the new version or, when files couldn't be replaced, the old one.
        if (args.Contains("--update-failed")) ui.TryEnqueue(async () => await updater.ReportFailedUpdate());
        else if (args.Contains("--auto-update")) _ = updater.CheckUnattended();
        else _ = updater.CheckInBackground();
    }

    /// <summary>Shows the export window and re-detects the project open in CapCut.</summary>
    void ShowExportWindow()
    {
        if (model is null) return;
        if (!model.IsExporting)
        {
            model.RefreshProjects(selectOpen: true);
            var i = Array.IndexOf(args, "--project");
            if (i >= 0 && i + 1 < args.Length && model.Projects.FirstOrDefault(p => p.Name == args[i + 1]) is { } project)
                model.SelectedProjectFolder = project.Folder;
        }
        if (exportWindow is null)
        {
            exportWindow = new ExportWindow(model);
            HandleClose(exportWindow);
        }
        Bring(exportWindow);
    }

    /// <summary>Shows the "Subtitles &amp; Voice-over" window for the project selected in the export window.</summary>
    void ShowSubtitlesWindow()
    {
        if (model is null) return;
        if (subtitles is null)
        {
            subtitles = new SubtitlesModel(model, DispatcherQueue.GetForCurrentThread());
            subtitles.Finished = () => { if (subtitlesWindow is not null) Platform.Flash(subtitlesWindow); };
        }
        if (subtitlesWindow is null)
        {
            subtitlesWindow = new SubtitlesWindow(subtitles);
            HandleClose(subtitlesWindow);
        }
        subtitles.RefreshProjectState();
        Bring(subtitlesWindow);
    }

    /// <summary>Shows the "Support EnhanceCapCut" window, downloading the ways to donate again.</summary>
    void ShowDonateWindow()
    {
        donate ??= new DonateModel();
        if (donateWindow is null)
        {
            donateWindow = new DonateWindow(donate);
            HandleClose(donateWindow);
        }
        donate.Load();
        Bring(donateWindow);
    }

    /// <summary>Shows the "How to Use EnhanceCapCut" window.</summary>
    void ShowGuideWindow()
    {
        if (guideWindow is null)
        {
            guideWindow = new GuideWindow();
            HandleClose(guideWindow);
        }
        Bring(guideWindow);
    }

    /// <summary>The guide opens by itself once, at the first launch (as on the Mac).</summary>
    void ShowGuideOnFirstLaunch()
    {
        if (Preferences.Bool("guideShown")) return;
        Preferences.Set("guideShown", true);
        ShowGuideWindow();
    }

    /// <summary>After the first export of each day, offers to donate (as on the Mac).</summary>
    void OfferDonationOncePerDay()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (Preferences.String("donateOfferDay") == today) return;
        Preferences.Set("donateOfferDay", today);
        ShowDonateWindow();
    }

    static void Bring(Window window)
    {
        window.AppWindow.Show();
        window.Activate();
        SetForegroundWindow(Platform.Handle(window));
    }

    /// <summary>
    /// Closing a window hides it, so a running export or subtitles job (and the subtitles window's choices) survive;
    /// closing the last visible window quits, after asking when something is still running.
    /// </summary>
    void HandleClose(Window window) => window.AppWindow.Closing += async (sender, e) =>
    {
        if (quitting) return;
        e.Cancel = true;
        var others = new Window?[] { exportWindow, subtitlesWindow, donateWindow, guideWindow }.Where(w => w is not null && w != window && w.AppWindow.IsVisible);
        if (others.Any())
        {
            sender.Hide();
            return;
        }
        if (await ConfirmQuit(window)) Quit();
    };

    async Task<bool> ConfirmQuit(Window window)
    {
        if (subtitles?.IsRunning == true)
        {
            if (!await Platform.Ask(window, "Subtitles are being created", "Quitting cancels them.", "Quit", "Keep Working", primaryIsDefault: false))
                return false;
            subtitles.Cancel();
        }
        if (model?.IsExporting == true)
        {
            if (!await Platform.Ask(window, "An export is in progress", "Quitting cancels it.", "Quit", "Keep Exporting", primaryIsDefault: false))
                return false;
            model.Cancel();
        }
        return true;
    }

    void Quit()
    {
        quitting = true;
        subtitlesWindow?.Close();
        donateWindow?.Close();
        guideWindow?.Close();
        exportWindow?.Close();
        Exit();
    }

    /// <summary>
    /// A second launch hands its activation to the running instance (which shows its export window) and exits. The
    /// redirect is awaited with a COM-aware wait so the STA thread keeps pumping, as the Windows App SDK docs describe.
    /// </summary>
    static bool RedirectToRunningInstance()
    {
        var key = AppInstance.FindOrRegisterForKey("EnhanceCapCut");
        if (key.IsCurrent) return false;
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AllowSetForegroundWindow(key.ProcessId);
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset);
        Task.Run(async () =>
        {
            try { await key.RedirectActivationToAsync(activation); }
            finally { done.Set(); }
        });
        CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        return true;
    }

    public IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
}
