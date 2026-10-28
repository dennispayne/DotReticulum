#Requires -Version 7.0

<#
.SYNOPSIS
Exports the repository branding SVG as application icons.
.DESCRIPTION
Requires Windows and System.Drawing. Writes generated assets beside the source SVG.
.EXAMPLE
pwsh -NoProfile -File assets\branding\Export-Icons.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    throw 'Icon export requires Windows System.Drawing. The SVG and exported assets are portable.'
}
Add-Type -AssemblyName System.Drawing.Common
[xml]$source = Get-Content (Join-Path $PSScriptRoot 'dotreticulum.svg') -Raw

function Get-Style([System.Xml.XmlElement]$Element, [string]$Name) {
    while ($Element) {
        if ($Element.HasAttribute($Name)) { return $Element.GetAttribute($Name) }
        $Element = $Element.ParentNode -as [System.Xml.XmlElement]
    }
    throw "Missing SVG style: $Name"
}

function Export-Png([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size * 4, $Size * 4)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $output = [System.Drawing.Bitmap]::new($Size, $Size)
    $scaled = [System.Drawing.Graphics]::FromImage($output)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($Size * 4 / 512, $Size * 4 / 512)
        foreach ($element in $source.SelectNodes('//*')) {
            switch ($element.LocalName) {
                'polygon' {
                    [System.Drawing.PointF[]]$points = @(
                        foreach ($pair in $element.points.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)) {
                            $coordinates = $pair.Split(',')
                            [System.Drawing.PointF]::new([single]$coordinates[0], [single]$coordinates[1])
                        }
                    )
                    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml((Get-Style $element 'fill')))
                    try {
                        $graphics.FillPolygon($brush, $points)
                    } finally { $brush.Dispose() }
                }
                'polyline' {
                    [System.Drawing.PointF[]]$points = @(
                        foreach ($pair in $element.points.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)) {
                            $coordinates = $pair.Split(',')
                            [System.Drawing.PointF]::new([single]$coordinates[0], [single]$coordinates[1])
                        }
                    )
                    $pen = [System.Drawing.Pen]::new(
                        [System.Drawing.ColorTranslator]::FromHtml((Get-Style $element 'stroke')),
                        [single](Get-Style $element 'stroke-width'))
                    try {
                        $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
                        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Bevel
                        $graphics.DrawLines($pen, $points)
                    } finally { $pen.Dispose() }
                }
                'line' {
                    $color = [System.Drawing.ColorTranslator]::FromHtml((Get-Style $element 'stroke'))
                    $pen = [System.Drawing.Pen]::new($color, [single](Get-Style $element 'stroke-width'))
                    try {
                        $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                        $graphics.DrawLine($pen, [single]$element.x1, [single]$element.y1, [single]$element.x2, [single]$element.y2)
                    } finally { $pen.Dispose() }
                }
                'circle' {
                    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml((Get-Style $element 'fill')))
                    try {
                        $r = [single]$element.r
                        $graphics.FillEllipse($brush, [single]$element.cx - $r, [single]$element.cy - $r, $r * 2, $r * 2)
                        $pen = [System.Drawing.Pen]::new(
                            [System.Drawing.ColorTranslator]::FromHtml((Get-Style $element 'stroke')),
                            [single](Get-Style $element 'stroke-width'))
                        try {
                            $graphics.DrawEllipse($pen, [single]$element.cx - $r, [single]$element.cy - $r, $r * 2, $r * 2)
                        } finally { $pen.Dispose() }
                    } finally { $brush.Dispose() }
                }
                { $_ -in 'svg', 'title', 'desc', 'metadata', 'g' } {}
                default { throw "Unsupported SVG element: $($element.LocalName)" }
            }
        }
        $scaled.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaled.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scaled.DrawImage($bitmap, 0, 0, $Size, $Size)
        $output.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose(); $scaled.Dispose(); $output.Dispose()
        $graphics.Dispose(); $bitmap.Dispose()
    }
}

[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'dotreticulum.png'), (Export-Png 1024))
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Export-Png $_) })
$file = [System.IO.File]::Create((Join-Path $PSScriptRoot 'dotreticulum.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }
