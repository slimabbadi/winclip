<#
.SYNOPSIS
    Verifies installation, local-feed updating, restart, data retention and uninstall on a fresh GitHub-hosted Windows runner.
#>
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run this only on a fresh GitHub-hosted Windows runner; it uses that account''s WinClip data folder.'
}
$root = Join-Path $env:LOCALAPPDATA 'WinClip.App'
$data = Join-Path $env:LOCALAPPDATA 'WinClip'
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $data) -or (Get-Process WinClip -ErrorAction SilentlyContinue)) {
    throw 'Refusing to run: an existing installation, process or data directory was found.'
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packages = (Resolve-Path $PackageDirectory).Path
$work = Join-Path $repo 'artifacts/lifecycle'
New-Item -ItemType Directory -Force $work | Out-Null

function Invoke-Native([string]$File, [string[]]$Arguments, [int]$Seconds = 120) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -NoNewWindow
    if (-not $process.WaitForExit($Seconds * 1000)) {
        $process.Kill($true)
        throw "Timed out: $File"
    }
    $code = $process.ExitCode
    if ($code -ne 0) { throw "$File exited $code" }
}
function Assert-Running([string]$Version) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $exe = [IO.Path]::GetFullPath("$root/current/WinClip.exe")
    do {
        $app = Get-Process WinClip -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe
        $runtime = if ($app) { $app.Modules | Where-Object ModuleName -EQ 'coreclr.dll' } else { $null }
        if ($runtime) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $app) { throw 'WinClip failed to start' }
    $actual = (Get-Item "$root/current/WinClip.exe").VersionInfo.ProductVersion.Split('+')[0]
    if ($actual -ne $Version) { throw "Expected $Version, got $actual" }
    $runtime = $app.Modules | Where-Object ModuleName -EQ 'coreclr.dll'
    if (-not $runtime -or -not $runtime.FileName.StartsWith("$root\current\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'App is not using its bundled .NET runtime'
    }
    Write-Host "PASS: WinClip $Version runs using its bundled runtime"
    return $app
}

$probe = Join-Path $repo 'tests/WinClip.LifecycleProbe/bin/Release/net10.0-windows/WinClip.LifecycleProbe.exe'
$oldInstaller = Join-Path $work 'Setup-0.2.0.exe'
Invoke-WebRequest 'https://github.com/slimabbadi/winclip/releases/download/v0.2.0/WinClip.App-win-Setup.exe' -OutFile $oldInstaller -TimeoutSec 90
if ((Get-FileHash $oldInstaller -Algorithm SHA256).Hash -ne '6D9AEF697D57D10B06B4B94E69E8257D5F19FBBED569DDE37A061054F8431D56') {
    throw 'Baseline installer checksum mismatch'
}
Invoke-Native $probe @('seed')
Invoke-Native $oldInstaller @('--silent')
Start-Process "$root/current/WinClip.exe" -ArgumentList '--background' -WindowStyle Hidden | Out-Null
$old = Assert-Running '0.2.0'
Invoke-Native $probe @('verify')
# Simulate a stopped/crashed previous app before applying the staged update. Never kill a user-owned app.
$old.Kill()
if (-not $old.WaitForExit(10000)) { throw 'Baseline app did not stop' }
Invoke-Native $probe @('update', "`"$root`"", '0.2.0', "`"$packages`"")
$version = (Get-Content (Join-Path $packages 'releases.win.json') -Raw | ConvertFrom-Json).Assets[0].Version
$updated = Assert-Running $version
Invoke-Native $probe @('verify')
Invoke-Native $probe @('delete-ui')
if (-not $updated.WaitForExit(15000)) { throw 'App did not quit after deletion' }
if (Test-Path -LiteralPath $data) { throw 'Local data remained after confirmed deletion' }
Write-Host 'PASS: confirmed UI deletion removes pinned history, settings and logs and quits'
Invoke-Native $probe @('seed')
Invoke-Native "$root/Update.exe" @('--silent', 'uninstall')
if (Test-Path -LiteralPath "$root/current/WinClip.exe") { throw 'Uninstall left the app installed' }
Invoke-Native $probe @('verify')
Write-Host 'PASS: uninstall removes the app and preserves user data'
Write-Host 'PASS: lifecycle verification complete'
