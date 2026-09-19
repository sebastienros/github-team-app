# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class NativeIcon {
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(System.IntPtr icon);
}
'@
$bitmap = New-Object Drawing.Bitmap 256, 256
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$background = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(31, 36, 46))
$font = New-Object Drawing.Font "Segoe UI", 100, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
$format = New-Object Drawing.StringFormat
$format.Alignment = [Drawing.StringAlignment]::Center
$format.LineAlignment = [Drawing.StringAlignment]::Center
$handle = [IntPtr]::Zero
$icon = $null
$stream = $null
try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.FillEllipse($background, 8, 8, 240, 240)
    $graphics.DrawString("GH", $font, [Drawing.Brushes]::White, [Drawing.RectangleF]::new(0, 0, 256, 256), $format)
    $handle = $bitmap.GetHicon()
    $icon = [Drawing.Icon]::FromHandle($handle)
    $stream = [IO.File]::Create($Path)
    $icon.Save($stream)
}
finally {
    if ($stream) { $stream.Dispose() }
    if ($icon) { $icon.Dispose() }
    if ($handle -ne [IntPtr]::Zero) { [void][NativeIcon]::DestroyIcon($handle) }
    $format.Dispose()
    $font.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
