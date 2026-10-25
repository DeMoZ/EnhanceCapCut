using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace EnhanceCapCut.App;

/// <summary>Windows shell services the windows use: Explorer, Settings, the taskbar, window frames, dialogs.</summary>
static partial class Platform
{
    /// <summary>Opens an Explorer window with the file selected (the counterpart of "Show in Finder").</summary>
    public static void Reveal(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    public static void OpenUri(string uri) => _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(uri));

    /// <summary>Flashes the window's taskbar button until it's activated (the counterpart of requestUserAttention).</summary>
    public static void Flash(Window window)
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = WinRT.Interop.WindowNative.GetWindowHandle(window),
            Flags = 0x3 | 0xC /* FLASHW_ALL | FLASHW_TIMERNOFG */,
        };
        FlashWindowEx(ref info);
    }

    /// <summary>Compares names the way Explorer sorts them ("Clip 2" before "Clip 10").</summary>
    public static int LogicalCompare(string a, string b) => StrCmpLogicalW(a, b);

    public static IntPtr Handle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    public static double Scale(Window window) => GetDpiForWindow(Handle(window)) / 96.0;

    /// <summary>
    /// Sizes and places a window: the frame saved under <paramref name="key"/> when it still fits a display, else
    /// <paramref name="width"/> × <paramref name="height"/> (DIPs) centred. Moves and resizes are saved again.
    /// </summary>
    public static void SetUpFrame(Window window, string key, int width, int height, int minWidth, int minHeight)
    {
        var appWindow = window.AppWindow;
        var scale = Scale(window);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(minWidth * scale);
            presenter.PreferredMinimumHeight = (int)(minHeight * scale);
        }
        var saved = Preferences.Strings(key)?.Select(s => int.TryParse(s, out var v) ? v : (int?)null).ToList();
        if (saved is { Count: 4 } && saved.All(v => v is not null))
        {
            var rect = new RectInt32(saved[0]!.Value, saved[1]!.Value, saved[2]!.Value, saved[3]!.Value);
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
            if (area is not null && rect.Width >= minWidth * scale && rect.Height >= minHeight * scale)
            {
                appWindow.MoveAndResize(rect);
                Track(appWindow, key);
                return;
            }
        }
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(width * scale), display.Width);
        var h = Math.Min((int)(height * scale), display.Height);
        appWindow.MoveAndResize(new RectInt32(display.X + (display.Width - w) / 2, display.Y + (display.Height - h) / 2, w, h));
        Track(appWindow, key);
    }

    static void Track(AppWindow appWindow, string key) => appWindow.Changed += (sender, args) =>
    {
        if ((!args.DidPositionChange && !args.DidSizeChange) || sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
            return;
        Preferences.Set(key, new[] { sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height }.Select(v => v.ToString()));
    };

    /// <summary>A two-button question (NSAlert's counterpart). Returns true for the primary button.</summary>
    public static async Task<bool> Ask(Window window, string title, string message, string primary, string close,
        bool primaryIsDefault = true)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = close,
            DefaultButton = primaryIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close, XamlRoot = window.Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);
}
