<#
.SYNOPSIS
    Regenerates src\WinClip\WinClip.ico: an accent-blue clipboard glyph at the sizes Windows asks for.
    Each size is drawn separately (not downscaled) so small sizes stay crisp.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-IconPng([int]$size) {
    $s = $size / 64.0
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($s, $s)
    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x7B, 0x93, 0xFF))
    $paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xE6, 0xE8, 0xEC))
    $ink = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x1F, 0x22, 0x29)), 5
    $ink.StartCap = 'Round'; $ink.EndCap = 'Round'
    $g.FillPath($accent, (New-RoundedRect 8 7 48 54 9))
    $g.FillPath($paper, (New-RoundedRect 20 2 24 13 5))
    $g.DrawLine($ink, 19, 29, 45, 29)
    $g.DrawLine($ink, 19, 40, 45, 40)
    if ($size -ge 24) { $g.DrawLine($ink, 19, 51, 34, 51) }
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$target = Join-Path $PSScriptRoot '..\src\WinClip\WinClip.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $((Resolve-Path $target).Path) ($($out.Length) bytes)"
