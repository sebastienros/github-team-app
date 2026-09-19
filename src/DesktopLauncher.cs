// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace GitHub.TeamApp;

internal static class DesktopLauncher
{
    internal static string ExecutablePath(string baseDirectory, bool? isWindows = null) =>
        (isWindows ?? OperatingSystem.IsWindows())
            ? Path.Combine(baseDirectory, "GitHub Team App.exe")
            : Path.Combine(baseDirectory, "GitHub Team App.app", "Contents", "MacOS", "GitHubTeamApp");

    internal static ProcessStartInfo StartInfo(string baseDirectory, string address, int parentId, bool? isWindows = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.Port <= 0 || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            (address != $"http://127.0.0.1:{uri.Port}" && address != $"http://127.0.0.1:{uri.Port}/"))
        {
            throw new ArgumentException("The desktop window requires the app's loopback address.", nameof(address));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentId);
        var info = new ProcessStartInfo(ExecutablePath(baseDirectory, isWindows)) { UseShellExecute = false };
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add(address);
        info.ArgumentList.Add("--parent-pid");
        info.ArgumentList.Add(parentId.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    public static async Task RunAsync(string address, CancellationToken cancellationToken)
    {
        var info = StartInfo(AppContext.BaseDirectory, address, Environment.ProcessId);
        if (!File.Exists(info.FileName))
        {
            throw new FileNotFoundException(
                OperatingSystem.IsWindows()
                    ? "The Windows desktop shell is missing. Rebuild on Windows with Visual Studio 2022/2026's C++ desktop workload, Windows SDK, and CMake installed, or use --browser or --no-browser."
                    : "The macOS desktop shell is missing. Rebuild with Apple Command Line Tools installed, or use --browser or --no-browser.",
                info.FileName);
        }
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the desktop window.");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The desktop window exited with code {process.ExitCode}.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The window can exit between the status check and termination.
                }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
