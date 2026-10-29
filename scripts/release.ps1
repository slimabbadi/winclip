<#
.SYNOPSIS
    Builds a self-contained Velopack installer, portable zip and update package. Does not upload anything.
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $root "artifacts/release-$Version"
$publish = Join-Path $output 'publish'
$packages = Join-Path $output 'packages'
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output. Use a new version or remove the previous build first." }

Push-Location $root
try {
    dotnet tool restore
    $code = $LASTEXITCODE
    if ($code -ne 0) { exit $code }

    dotnet publish src/WinClip/WinClip.csproj -c Release -r win-x64 --self-contained true `
        "-p:Version=$Version" -p:DebugType=none -o $publish
    $code = $LASTEXITCODE
    if ($code -ne 0) { exit $code }

    # App binaries must never share the WinClip data directory: uninstall removes the install directory.
    dotnet tool run vpk -- pack --packId WinClip.App --packVersion $Version --packDir $publish `
        --packTitle WinClip --packAuthors slimabbadi --mainExe WinClip.exe `
        --icon src/WinClip/WinClip.ico --runtime win-x64 --delta None --outputDir $packages
    $code = $LASTEXITCODE
    if ($code -ne 0) { exit $code }

    # Hash the distributable assets vpk uploads, rather than its local bookkeeping files.
    $assets = Get-Content -LiteralPath (Join-Path $packages 'assets.win.json') -Raw | ConvertFrom-Json
    $assets | Sort-Object RelativeFileName | ForEach-Object {
        $name = $_.RelativeFileName
        $hash = (Get-FileHash -LiteralPath (Join-Path $packages $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    } | Set-Content -LiteralPath (Join-Path $packages 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release files: $packages"
}
finally {
    Pop-Location
}
