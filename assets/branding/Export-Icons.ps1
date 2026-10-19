#Requires -Version 7.0
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
                'rect' {
                    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
                    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($element.fill))
                    try {
                        $x = [single]$element.x; $y = [single]$element.y
                        $w = [single]$element.width; $h = [single]$element.height
                        $d = [single]$element.rx * 2
                        $path.AddArc($x, $y, $d, $d, 180, 90)
                        $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
                        $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
                        $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
                        $path.CloseFigure()
                        $graphics.FillPath($brush, $path)
                    } finally { $brush.Dispose(); $path.Dispose() }
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
                    } finally { $brush.Dispose() }
                }
                { $_ -in 'svg', 'title', 'desc', 'g' } {}
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
