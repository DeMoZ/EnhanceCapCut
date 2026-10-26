# Updates a published app from one version to another the way a user does, so CI checks the whole path: the app finds
# the newer release (served here instead of GitHub, through ECC_UPDATES_URL), downloads and checks the zip, quits, the
# install script replaces the files and the new version starts. First with a file held open, which must fail and bring
# the old version back with --update-failed, then for real.
#   windows/scripts/update-smoke.ps1 -Old <app published with -p:Version=0.0.1> -New <same with 0.0.2> -Work <folder>
param([Parameter(Mandatory)][string]$Old, [Parameter(Mandatory)][string]$New, [Parameter(Mandatory)][string]$Work)
$ErrorActionPreference = 'Stop'

$Work = New-Item -ItemType Directory -Force -Path $Work | ForEach-Object FullName
$version = (Get-Item (Join-Path $New 'EnhanceCapCut.dll')).VersionInfo.FileVersion -replace '\.0$', ''
$installed = Join-Path $Work 'installed'

# The release zip, laid out like the real one (a single EnhanceCapCut-<version>-windows-x64 folder).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$staged = Join-Path $Work "EnhanceCapCut-$version-windows-x64"
Remove-Item -Recurse -Force $staged, (Join-Path $Work 'update.zip') -ErrorAction SilentlyContinue
Copy-Item -Recurse $New $staged
$zip = Join-Path $Work 'update.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($staged, $zip, 'Fastest', $true)
$sha = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()

# GitHub's "latest release" answer and the zip, from localhost.
$port = 8765
$latest = @{
    tag_name = "v$version"; html_url = "http://localhost:$port/"
    assets = @(@{ name = "EnhanceCapCut-$version-windows-x64.zip"; browser_download_url = "http://localhost:$port/app.zip"; digest = "sha256:$sha" })
} | ConvertTo-Json -Depth 4
$server = Start-Job -ArgumentList $port, $latest, $zip -ScriptBlock {
    param($port, $latest, $zip)
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://localhost:$port/")
    $listener.Start()
    while ($true) {
        $context = $listener.GetContext()
        $response = $context.Response
        if ($context.Request.Url.AbsolutePath -eq '/app.zip') {
            $file = [System.IO.File]::OpenRead($zip)
            $response.ContentLength64 = $file.Length
            $file.CopyTo($response.OutputStream)
            $file.Dispose()
        } else {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($latest)
            $response.ContentType = 'application/json'
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        $response.Close()
    }
}
$env:ECC_UPDATES_URL = "http://localhost:$port/latest"

function Install-Old {
    Remove-Item -Recurse -Force $installed -ErrorAction SilentlyContinue
    Copy-Item -Recurse $Old $installed
}

function Get-Running {
    Get-CimInstance Win32_Process -Filter "Name = 'EnhanceCapCut.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installed, 'OrdinalIgnoreCase') }
}

# Starts the installed app with --auto-update and waits for the copy the update script starts once the first has quit.
function Update-Installed([string]$expect) {
    $first = Start-Process -FilePath (Join-Path $installed 'EnhanceCapCut.exe') -ArgumentList '--auto-update' -PassThru
    for ($i = 0; $i -lt 180; $i++) {
        Start-Sleep -Milliseconds 500
        $next = Get-Running | Where-Object ProcessId -ne $first.Id
        if ($first.HasExited -and $next) { return $next }
    }
    Receive-Job $server | Write-Host
    $log = Join-Path $env:LOCALAPPDATA 'EnhanceCapCut\crash.log'
    if (Test-Path $log) { Get-Content $log | Write-Host }
    throw "The app wasn't started again ($expect); first copy exited: $($first.HasExited)"
}

function Stop-Installed { Get-Running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Start-Sleep -Seconds 1 }

function Installed-Version { (Get-Item (Join-Path $installed 'EnhanceCapCut.dll')).VersionInfo.FileVersion -replace '\.0$', '' }

try {
    $from = (Get-Item (Join-Path $Old 'EnhanceCapCut.dll')).VersionInfo.FileVersion -replace '\.0$', ''
    Write-Host "Updating $from to $version"

    # A file that can't be replaced (as when ecc.exe is running): the old version comes back and says so.
    Install-Old
    $held = [System.IO.File]::Open((Join-Path $installed 'EnhanceCapCut.dll'), 'Open', 'Read', 'Read')
    try {
        $next = Update-Installed 'after a failed update'
        if ($next.CommandLine -notmatch '--update-failed') { throw "Started without --update-failed: $($next.CommandLine)" }
    } finally {
        Stop-Installed
        $held.Dispose()
    }
    if ((Installed-Version) -ne $from) { throw "A failed update left version $(Installed-Version)" }
    Write-Host "A held file: the update failed and $from started again with --update-failed"

    # The real thing.
    Install-Old
    $next = Update-Installed 'after the update'
    if ($next.CommandLine -match '--update-failed') { throw "The update failed: $($next.CommandLine)" }
    if ((Installed-Version) -ne $version) { throw "Version $(Installed-Version) after the update, expected $version" }
    Start-Sleep -Seconds 2
    if (Test-Path (Join-Path $env:TEMP "EnhanceCapCut-update-$version")) { throw "The downloaded update wasn't cleaned up" }
    Write-Host "Updated to $version and started it"
} finally {
    Stop-Installed
    Stop-Job $server; Remove-Job $server -Force
}
