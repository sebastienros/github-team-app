# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet("x64", "arm64")][string]$Architecture = "x64"
)
$ErrorActionPreference = "Stop"
$source = $PSScriptRoot
$root = Split-Path (Split-Path $source -Parent) -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = Join-Path $root "obj\windows-desktop\$Architecture"
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
$cmake = if ($cmakeCommand) { $cmakeCommand.Source } else { $null }
if (-not $cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $cmake = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' | Select-Object -First 1
    }
}
if (-not $cmake) {
    throw "Install Visual Studio Build Tools with Desktop development with C++, Windows SDK, and CMake tools; or build with -p:BuildDesktopShell=false and run --browser."
}

$platform = if ($Architecture -eq "arm64") { "ARM64" } else { "x64" }
& $cmake -S $source -B $build -A $platform
if ($LASTEXITCODE -ne 0) { throw "Windows desktop CMake configuration failed ($LASTEXITCODE)." }
& $cmake --build $build --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "Windows desktop build failed ($LASTEXITCODE)." }
& $cmake --install $build --config Release --prefix $output
if ($LASTEXITCODE -ne 0) { throw "Windows desktop packaging failed ($LASTEXITCODE)." }
