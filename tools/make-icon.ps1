# Renders the Aphelion mark (an orbit with the body at its far point) into a multi-size .ico.
# Run once after changing the mark: pwsh tools/make-icon.ps1
param([string]$Out = "$PSScriptRoot/../src/Aphelion.App/Assets/aphelion.ico")

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$panel = [System.Drawing.Color]::FromArgb(255, 0x1A, 0x1F, 0x22)
$ink = [System.Drawing.Color]::FromArgb(255, 0xE6, 0xEC, 0xE8)
$orange = [System.Drawing.Color]::FromArgb(255, 0xFF, 0x5B, 0x14)

$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [Math]::Max(1, [int]($s * 0.18))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $panel), $path)
    $stroke = [Math]::Max(1.2, $s / 18.0)
    $pen = New-Object System.Drawing.Pen $ink, $stroke
    $g.DrawEllipse($pen, [single]($s * 0.12), [single]($s * 0.28), [single]($s * 0.68), [single]($s * 0.44))
    $sun = $s * 0.11
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $ink), [single]($s * 0.20 - $sun / 2), [single]($s * 0.5 - $sun / 2), [single]$sun, [single]$sun)
    $body = $s * 0.20
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $orange), [single]($s * 0.80 - $body / 2), [single]($s * 0.5 - $body / 2), [single]$body, [single]$body)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Wrote $Out"
