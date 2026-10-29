<#
.SYNOPSIS
    Builds a double-clickable dist\WinClip.exe and adds Start Menu and Desktop shortcuts to it.

.DESCRIPTION
    The exe is a single file that needs the .NET 10 Desktop Runtime, which the .NET 10 SDK includes.
    To run it on a machine without .NET, pass -SelfContained (larger file, about 70 MB).
    Quit WinClip before publishing; a running copy locks dist\WinClip.exe.
#>
param(
    [switch]$SelfContained,
    [switch]$NoShortcuts
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $root 'dist'

if (Get-Process WinClip -ErrorAction SilentlyContinue | Where-Object Path -like "$dist*") {
    throw 'WinClip is running from dist. Quit it from the tray icon, then publish again.'
}

dotnet publish (Join-Path $root 'src\WinClip\WinClip.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained $SelfContained.IsPresent.ToString().ToLower() `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    --output $dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $dist 'WinClip.exe'
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"

if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $places = @(
        [Environment]::GetFolderPath('Programs'),   # Start Menu, so Windows search finds it
        [Environment]::GetFolderPath('Desktop')
    )
    foreach ($place in $places) {
        $link = $shell.CreateShortcut((Join-Path $place 'WinClip.lnk'))
        $link.TargetPath = $exe
        $link.WorkingDirectory = $dist
        $link.IconLocation = "$exe,0"
        $link.Description = 'Clipboard history'
        $link.Save()
        Write-Host "Shortcut: $(Join-Path $place 'WinClip.lnk')"
    }
}
