# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Source = (Join-Path $PSScriptRoot "../icons/octo-1024.png")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$original = [Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
try {
    if ($original.Width -ne 1024 -or $original.Height -ne 1024) {
        throw "The approved Octo source must be a 1024 x 1024 PNG."
    }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $png = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($original, [Drawing.Rectangle]::new(0, 0, $size, $size))
            $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
            $images.Add($png.ToArray())
        }
        finally {
            $png.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
    # ICO directory entries point at complete PNG representations (supported since Vista).
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($Path))
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write([byte[]]$image) }
    }
    finally { $writer.Dispose() }
}
finally { $original.Dispose() }
