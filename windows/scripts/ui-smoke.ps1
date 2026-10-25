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

Add-Type -AssemblyName System.Drawing
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
    $r = New-Object Win+RECT
    [void][Win]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16)  # DWMWA_EXTENDED_FRAME_BOUNDS
    $bitmap = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
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
    Save-Window $hwnd (Join-Path $Out "$name.png")
    Write-Host "Saved $name.png"
}
Stop-Process -Id $process.Id -Force
