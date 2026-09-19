// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace GitHub.TeamApp.Tests;

public class DesktopLauncherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaunchesBundledExecutableWithoutShellAndUsesSeparateArguments(bool isWindows)
    {
        var directory = Path.Combine(Path.GetTempPath(), "App with spaces");
        var info = DesktopLauncher.StartInfo(directory, "http://127.0.0.1:5143", 1234, isWindows);
        Assert.Equal(isWindows
            ? Path.Combine(directory, "GitHub Team App.exe")
            : Path.Combine(directory, "GitHub Team App.app", "Contents", "MacOS", "GitHubTeamApp"), info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["--url", "http://127.0.0.1:5143", "--parent-pid", "1234"], info.ArgumentList);
    }

    [Fact]
    public void DefaultsToCurrentOperatingSystem()
    {
        var directory = Path.GetTempPath();
        Assert.Equal(DesktopLauncher.ExecutablePath(directory, OperatingSystem.IsWindows()),
            DesktopLauncher.ExecutablePath(directory));
        Assert.Equal(DesktopLauncher.ExecutablePath(directory),
            DesktopLauncher.StartInfo(directory, "http://127.0.0.1:5143", 1234).FileName);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://127.0.0.1:5143/")]
    [InlineData("http://127.0.0.1:65535")]
    public void AcceptsCanonicalLoopbackAddresses(string address)
    {
        foreach (var isWindows in new[] { false, true })
        {
            var info = DesktopLauncher.StartInfo(Path.GetTempPath(), address, int.MaxValue, isWindows);
            Assert.Equal(address, info.ArgumentList[1]);
            Assert.Equal("2147483647", info.ArgumentList[3]);
        }
    }

    [Theory]
    [InlineData("https://127.0.0.1:5143")]
    [InlineData("http://localhost:5143")]
    [InlineData("http://example.com:5143")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://user@127.0.0.1:5143")]
    [InlineData("http://127.0.0.1:5143/api/state")]
    [InlineData("http://127.0.0.1:5143/?redirect=external")]
    [InlineData("http://127.0.0.1:5143/#external")]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:65536")]
    [InlineData("http://127.1:5143")]
    [InlineData("http://2130706433:5143")]
    [InlineData("http://0x7f000001:5143")]
    [InlineData("http://127.0.0.1:05143")]
    [InlineData("HTTP://127.0.0.1:5143")]
    [InlineData(" http://127.0.0.1:5143")]
    [InlineData("http://127.0.0.1:5143 ")]
    [InlineData("http://127.0.0.1:5143/./")]
    [InlineData("http://127.0.0.1:5143/path/..")]
    [InlineData("http://127.0.0.1:5143\\")]
    [InlineData("http://[::1]:5143")]
    public void RejectsNonCanonicalDesktopAddresses(string address)
    {
        foreach (var isWindows in new[] { false, true })
        {
            Assert.Throws<ArgumentException>(() => DesktopLauncher.StartInfo(Path.GetTempPath(), address, 1234, isWindows));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RejectsInvalidParentProcessIds(int parentId)
    {
        foreach (var isWindows in new[] { false, true })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DesktopLauncher.StartInfo(Path.GetTempPath(), "http://127.0.0.1:5143", parentId, isWindows));
        }
    }

    [Theory]
    [InlineData("shutdown", false)]
    [InlineData("shutdown", true)]
    [InlineData("shutdown", null)]
    [InlineData(null, null)]
    [InlineData("unexpected-command", null)]
    [InlineData("", null)]
    [InlineData("SHUTDOWN", null)]
    public async Task ManagedHostStopsOnOneControlLineOrEof(string? command, bool? browserFirst)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(Path.GetTempPath(), $"github-team-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(DesktopLauncher).Assembly.Location);
        if (browserFirst == true) { start.ArgumentList.Add("--browser"); }
        start.ArgumentList.Add("--desktop-managed");
        if (browserFirst == false) { start.ArgumentList.Add("--browser"); }
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the managed test host.");
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("GitHub Team App: http://", StringComparison.Ordinal))
                {
                    address = line["GitHub Team App: ".Length..];
                    break;
                }
            }
            Assert.NotNull(address);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(address, timeout.Token);
            Assert.True(response.IsSuccessStatusCode);
            Assert.False(process.HasExited);
            if (command is null)
            {
                process.StandardInput.Close();
            }
            else
            {
                await process.StandardInput.WriteLineAsync(command.AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
            }
            await process.WaitForExitAsync(timeout.Token);
            var logs = await output + await errors;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Application is shutting down", logs);
            if (command is not (null or "shutdown"))
            {
                Assert.Contains("Unexpected desktop control input; shutting down.", logs);
            }
            else
            {
                Assert.DoesNotContain("Unexpected desktop control input", logs);
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }
}
