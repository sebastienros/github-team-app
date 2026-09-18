// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Xunit;

namespace GitHub.TeamApp.Tests;

public class DoctorTests
{
    [Fact]
    public async Task OptionalToolsDoNotFailDoctorAndCommandsCannotInstallOrPrompt()
    {
        var calls = new ConcurrentBag<string>();
        var result = await Doctor.CheckAsync(new JsonObject(), TestContext.Current.CancellationToken,
            (file, args, ct, timeout, environment) =>
            {
                calls.Add($"{file} {string.Join(' ', args)}");
                Assert.Equal(TimeSpan.FromSeconds(20), timeout);
                Assert.Equal("no", environment!["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"]);
                Assert.Equal("1", environment["GH_PROMPT_DISABLED"]);
                return file == "gh" ? Task.FromResult(new ProcessResult(0, "authenticated", "")) : throw new Win32Exception("Missing tool");
            },
            protocolProbe: () => false);

        Assert.True(result.Flag("ok"));
        Assert.Equal(0, result.Number("exitCode"));
        Assert.Equal(new[] { "az version --output json", "copilot --version", "gh --version", "gh auth status --active --hostname github.com", "git --version" },
            calls.Order(StringComparer.Ordinal));
        Assert.Equal("warning", Check(result, "github-app").Text("status"));
        Assert.Equal("warning", Check(result, "az").Text("status"));
        Assert.Equal("info", Check(result, "dotnet-sdk").Text("status"));
    }

    [Fact]
    public async Task InstalledButUnauthenticatedGitHubFailsWithActionableRemediation()
    {
        var result = await Doctor.CheckAsync(new JsonObject(), TestContext.Current.CancellationToken,
            (file, args, ct, timeout, environment) =>
                Task.FromResult(new ProcessResult(file == "gh" && args[0] == "auth" ? 1 : 0, "do-not-report-token", "secret-error")),
            protocolProbe: () => true);

        Assert.False(result.Flag("ok"));
        Assert.Equal(1, result.Number("exitCode"));
        Assert.Equal("passed", Check(result, "gh").Text("status"));
        var authentication = Check(result, "github-auth");
        Assert.Equal("failed", authentication.Text("status"));
        Assert.Equal("GitHub authentication could not be verified. Command exited with code 1.", authentication.Text("message"));
        Assert.Equal("Run gh auth login --hostname github.com (or refresh the active account), then rerun doctor.", authentication.Text("remediation"));
    }

    [Fact]
    public async Task MissingGitHubAndTimeoutsAreReportedAsRequiredFailures()
    {
        var result = await Doctor.CheckAsync(new JsonObject(), TestContext.Current.CancellationToken,
            (file, args, ct, timeout, environment) =>
                file == "gh" ? throw new Win32Exception() : throw new TimeoutException(),
            protocolProbe: () => null);

        Assert.Equal(1, result.Number("exitCode"));
        Assert.Equal("failed", Check(result, "gh").Text("status"));
        Assert.Equal("failed", Check(result, "github-auth").Text("status"));
        Assert.Equal("warning", Check(result, "git").Text("status"));
        Assert.Equal("The tool is unavailable or did not respond successfully. The diagnostic command timed out.", Check(result, "git").Text("message"));
        Assert.Equal("Protocol-handler availability could not be determined on this system.", Check(result, "github-app").Text("message"));
    }

    [Fact]
    public async Task AzureToolsBecomeRequiredForConfiguredPipelines()
    {
        var result = await Doctor.CheckAsync(AzurePreferences(), TestContext.Current.CancellationToken,
            (file, args, ct, timeout, environment) =>
                Task.FromResult(new ProcessResult(file == "az" ? 1 : 0, "", "")),
            protocolProbe: () => true);

        Assert.Equal(1, result.Number("exitCode"));
        Assert.True(Check(result, "az").Flag("required"));
        Assert.Equal("failed", Check(result, "az").Text("status"));
        Assert.Equal("failed", Check(result, "azure-devops-extension").Text("status"));
    }

    [Fact]
    public async Task AzureAuthenticationChecksConfiguredProjectNotSubscriptionOrCredentials()
    {
        var calls = new ConcurrentBag<string>();
        var prefs = AzurePreferences();
        prefs["azurePipelines"]!.AsArray().Add(prefs["azurePipelines"]![0]!.DeepClone());
        var result = await Doctor.CheckAsync(prefs, TestContext.Current.CancellationToken,
            (file, args, ct, timeout, environment) =>
            {
                if (file == "az")
                {
                    calls.Add(string.Join(' ', args));
                }
                return Task.FromResult(new ProcessResult(0, "success", ""));
            }, protocolProbe: () => true);

        Assert.Equal(0, result.Number("exitCode"));
        Assert.Equal(new[]
        {
            "devops project show --organization https://dev.azure.com/dnceng --project internal --output none --only-show-errors",
            "extension show --name azure-devops --query version --output tsv",
            "version --output json"
        }, calls.Order(StringComparer.Ordinal));
        Assert.Equal("passed", Check(result, "azure-auth-1").Text("status"));
    }

    [Fact]
    public async Task InstalledAzureWithoutOrganizationAccessFails()
    {
        var result = await Doctor.CheckAsync(AzurePreferences(), TestContext.Current.CancellationToken,
            (file, args, ct, timeout, environment) =>
                Task.FromResult(new ProcessResult(file == "az" && args[0] == "devops" ? 1 : 0, "", "")),
            protocolProbe: () => true);

        Assert.Equal(1, result.Number("exitCode"));
        Assert.Equal("passed", Check(result, "az").Text("status"));
        Assert.Equal("passed", Check(result, "azure-devops-extension").Text("status"));
        Assert.Equal("failed", Check(result, "azure-auth-1").Text("status"));
        Assert.Equal("Run az login or az devops login --organization https://dev.azure.com/dnceng, verify project read permissions, then rerun doctor.",
            Check(result, "azure-auth-1").Text("remediation"));
    }

    [Fact]
    public async Task MalformedAzureConfigurationFailsInsteadOfSilentlySkippingAuthentication()
    {
        var prefs = AzurePreferences();
        prefs["azurePipelines"]![0]!["url"] = "https://attacker.test/dnceng/internal";
        var result = await Doctor.CheckAsync(prefs, TestContext.Current.CancellationToken, SuccessfulProcess, protocolProbe: () => true);
        Assert.Equal(1, result.Number("exitCode"));
        Assert.Equal("failed", Check(result, "azure-configuration-1").Text("status"));
    }

    [Fact]
    public async Task CancellationPropagatesRatherThanBecomingAWarning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Doctor.CheckAsync(new JsonObject(), cancellation.Token, SuccessfulProcess, protocolProbe: () => true));
    }

    [Fact]
    public async Task UnreadablePreferencesAreARequiredDiagnosticFailure()
    {
        var report = await Doctor.CheckAsync(_ => throw new System.Text.Json.JsonException("Untrusted file content"), TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Number("exitCode"));
        Assert.False(report.Flag("ok"));
        var check = Assert.Single(report["checks"].Objects());
        Assert.Equal("preferences", check.Text("id"));
        Assert.Equal("failed", check.Text("status"));
        Assert.True(check.Flag("required"));
        Assert.Equal("Preferences could not be read, so required provider checks cannot be determined.", check.Text("message"));
    }

    [Fact]
    public async Task JsonOutputHasTheSameChecksAndExitCodeAsWebDoctor()
    {
        using var writer = new StringWriter();
        var exitCode = await Doctor.RunAsync(true, new JsonObject(), TestContext.Current.CancellationToken, SuccessfulProcess, writer, protocolProbe: () => true);
        var output = JsonNode.Parse(writer.ToString())!.AsObject();
        var expected = await Doctor.CheckAsync(new JsonObject(), TestContext.Current.CancellationToken, SuccessfulProcess, protocolProbe: () => true);
        Assert.Equal(0, exitCode);
        Assert.Equal(exitCode, output.Number("exitCode"));
        Assert.True(JsonNode.DeepEquals(expected, output));
    }

    [Fact]
    public async Task TextOutputIncludesEveryCheckAndRemediation()
    {
        using var writer = new StringWriter();
        var exitCode = await Doctor.RunAsync(false, new JsonObject(), TestContext.Current.CancellationToken, SuccessfulProcess, writer, protocolProbe: () => false);
        var expected = await Doctor.CheckAsync(new JsonObject(), TestContext.Current.CancellationToken, SuccessfulProcess, protocolProbe: () => false);
        var lines = new List<string> { "GitHub Team App doctor" };
        foreach (var check in expected["checks"].Objects())
        {
            lines.Add($"[{check.Text("status").ToUpperInvariant()}] {check.Text("label")} ({(check.Flag("required") ? "required" : "optional")}): {check.Text("message")}");
            if (check.Text("remediation").Length > 0)
            {
                lines.Add($"  {check.Text("remediation")}");
            }
        }
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Join(Environment.NewLine, lines) + Environment.NewLine, writer.ToString());
    }

    private static JsonObject Check(JsonObject report, string id) => Assert.Single(report["checks"].Objects(), check => check.Text("id") == id);

    private static JsonObject AzurePreferences() => JsonNode.Parse("""
        {"azurePipelines":[{"url":"https://dev.azure.com/dnceng/internal/_build?definitionId=1602"}]}
        """)!.AsObject();

    private static Task<ProcessResult> SuccessfulProcess(
        string file, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout, IReadOnlyDictionary<string, string?>? environment) =>
        Task.FromResult(new ProcessResult(0, "installed", ""));
}
