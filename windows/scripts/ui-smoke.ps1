# Starts the published app against a demo CapCut project and saves screenshots of both windows, so CI can check that
# the app starts and the windows can be compared with the macOS version by eye.
#   windows/scripts/ui-smoke.ps1 -App <folder with EnhanceCapCut.exe> -Out <folder for the PNGs>
param([Parameter(Mandatory)][string]$App, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'

# A demo project in CapCut's default drafts folder: a 9:16 timeline whose clip is missing (so the "Not rendered yet"
# section shows), with a caption track (so "Burn in captions" shows).
$projects = Join-Path $env:LOCALAPPDATA 'CapCut\User Data\Projects\com.lveditor.draft'
$demo = Join-Path $projects 'Demo Project'
New-Item -ItemType Directory -Force -Path $demo | Out-Null
$clip = (Join-Path $demo 'missing.mp4').Replace('\', '/')
@"
{"id":"D","name":"Demo Project","duration":15000000,"fps":30.0,
 "canvas_config":{"width":1080,"height":1920,"ratio":"9:16"},
 "tracks":[
  {"id":"V","type":"video","segments":[{"id":"S1","material_id":"V1","source_timerange":{"start":0,"duration":15000000},
   "target_timerange":{"start":0,"duration":15000000}}]},
  {"id":"T","type":"text","segments":[{"id":"S2","material_id":"X1","target_timerange":{"start":0,"duration":3000000}}]}],
 "materials":{
  "videos":[{"id":"V1","type":"video","path":"$clip","width":1080,"height":1920,"duration":15000000}],
  "texts":[{"id":"X1","type":"subtitle","font_size":8.0,"content":"{\"text\":\"Hello\",\"styles\":[{\"range\":[0,5],\"size\":8.0}]}"}]}}
"@ | Set-Content -Encoding utf8 (Join-Path $demo 'draft_info.json')
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# Voice-over on for English (Windows has voices) and Thai (the runner has none), so every voice row shows.
$prefs = Join-Path $env:APPDATA 'EnhanceCapCut'
New-Item -ItemType Directory -Force -Path $prefs | Out-Null
'{"subtitles.makeDub": true, "subtitles.targets": ["en", "th"]}' | Set-Content -Encoding utf8 (Join-Path $prefs 'app.json')

# Room for the whole windows (the runner's screen is 1024×768).
try { Set-DisplayResolution -Width 1920 -Height 1080 -Force -ErrorAction Stop } catch { Write-Host "Display resolution unchanged: $_" }

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    public static RECT Bounds(IntPtr hwnd) {
        RECT r;
        if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0 || r.Right <= r.Left) GetWindowRect(hwnd, out r);
        return r;
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

function Find-Window([int]$processId, [string]$title) {
    $script:found = [IntPtr]::Zero
    [Win]::EnumWindows({ param($h, $l)
        $p = 0; [void][Win]::GetWindowThreadProcessId($h, [ref]$p)
        $sb = New-Object System.Text.StringBuilder 256; [void][Win]::GetWindowText($h, $sb, 256)
        if ($p -eq $processId -and [Win]::IsWindowVisible($h) -and $sb.ToString() -eq $title) { $script:found = $h; return $false }
        return $true }, [IntPtr]::Zero) | Out-Null
    return $script:found
}

function Save-Window([IntPtr]$hwnd, [string]$path) {
    [void][Win]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 800
    $r = [Win]::Bounds($hwnd)  # DWMWA_EXTENDED_FRAME_BOUNDS, else the window rect
    $width = $r.Right - $r.Left; $height = $r.Bottom - $r.Top
    Write-Host "Window at $($r.Left),$($r.Top) size ${width}x$height"
    if ($width -le 0 -or $height -le 0) {
        # No usable bounds: the whole screen.
        $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $r.Left = $screen.Left; $r.Top = $screen.Top; $width = $screen.Width; $height = $screen.Height
    }
    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bitmap.Dispose()
}

$process = Start-Process -FilePath (Join-Path $App 'EnhanceCapCut.exe') -ArgumentList '--subtitles' -PassThru
$windows = @{ 'export' = 'Export with EnhanceCapCut'; 'subtitles' = 'Subtitles & Voice-over' }
foreach ($name in $windows.Keys) {
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $hwnd -eq [IntPtr]::Zero; $i++) {
        if ($process.HasExited) {
            $log = Join-Path $env:LOCALAPPDATA 'EnhanceCapCut\crash.log'
            if (Test-Path $log) { Get-Content $log | Write-Host }
            Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2 } -MaxEvents 5 -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Host $_.Message }
            throw "EnhanceCapCut exited with code $($process.ExitCode)"
        }
        Start-Sleep -Milliseconds 500
        $hwnd = Find-Window $process.Id $windows[$name]
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "No '$($windows[$name])' window" }
    Start-Sleep -Seconds 2  # the timeline loads in the background
    Save-Window $hwnd (Join-Path $Out "$name-1.png")
    # Then the rest of the form, a few wheel turns at a time.
    $r = [Win]::Bounds($hwnd)
    [void][Win]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2))
    for ($page = 2; $page -le 4; $page++) {
        for ($turn = 0; $turn -lt 5; $turn++) { [Win]::mouse_event(0x0800, 0, 0, -120, [IntPtr]::Zero); Start-Sleep -Milliseconds 100 }
        Save-Window $hwnd (Join-Path $Out "$name-$page.png")
    }
    Write-Host "Saved $name"
}
Stop-Process -Id $process.Id -Force
Start-Sleep -Seconds 1

# The export window while it exports (the demo timeline renders black where its clip is missing).
$process = Start-Process -FilePath (Join-Path $App 'EnhanceCapCut.exe') -ArgumentList '--auto-export' -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 500
    $hwnd = Find-Window $process.Id $windows['export']
}
if ($hwnd -eq [IntPtr]::Zero) { throw "No export window with --auto-export" }
Start-Sleep -Seconds 4
Save-Window $hwnd (Join-Path $Out 'export-progress.png')
Write-Host "Saved export-progress"
Stop-Process -Id $process.Id -Force
