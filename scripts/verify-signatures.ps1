<#
.SYNOPSIS
    Refuses publication unless the installer and packaged WinClip binaries have valid, consistent Authenticode signatures.
#>
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$packages = (Resolve-Path $PackageDirectory).Path
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $packages 'WinClip.App-win-Setup.exe')
if ($signature.Status -ne 'Valid') {
    throw "Installer is not trusted-signed ($($signature.Status)). Signing-provider setup is required before publishing."
}
$publisher = $signature.SignerCertificate.Subject
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("winclip-signatures-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$portable = $null
$full = $null
try {
    $portable = [IO.Compression.ZipFile]::OpenRead((Join-Path $packages 'WinClip.App-win-Portable.zip'))
    $fullPackages = @(Get-ChildItem -LiteralPath $packages -Filter '*-full.nupkg')
    if ($fullPackages.Count -ne 1) { throw 'Expected exactly one full update package' }
    $full = [IO.Compression.ZipFile]::OpenRead($fullPackages[0].FullName)
    foreach ($name in 'WinClip.exe', 'WinClip.dll', 'WinClip.Core.dll') {
        $entries = @($portable.Entries | Where-Object Name -EQ $name)
        $updates = @($full.Entries | Where-Object Name -EQ $name)
        if ($entries.Count -ne 1 -or $updates.Count -ne 1) { throw "Missing or ambiguous packaged binary: $name" }
        $portablePath = Join-Path $temporary $name
        $updatePath = Join-Path $temporary "update-$name"
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $portablePath)
        [IO.Compression.ZipFileExtensions]::ExtractToFile($updates[0], $updatePath)
        $signed = Get-AuthenticodeSignature -LiteralPath $portablePath
        if ($signed.Status -ne 'Valid' -or $signed.SignerCertificate.Subject -ne $publisher) {
            throw "Packaged $name does not have a valid signature from the installer's publisher"
        }
        if ((Get-FileHash $portablePath).Hash -ne (Get-FileHash $updatePath).Hash) {
            throw "Portable and update packages contain different $name binaries"
        }
        Write-Host "PASS: $name has a valid signature and matches the update package"
    }
}
finally {
    if ($portable) { $portable.Dispose() }
    if ($full) { $full.Dispose() }
    # Only direct files created here are removed; no recursive traversal.
    Get-ChildItem -LiteralPath $temporary -File | Remove-Item
    Remove-Item -LiteralPath $temporary
}
Write-Host "PASS: release signatures verified ($publisher)"
