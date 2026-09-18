// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHub.TeamApp;

internal delegate Task<ProcessResult> DoctorProcessRunner(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken,
    TimeSpan? timeout,
    IReadOnlyDictionary<string, string?>? environment);

internal static partial class Doctor
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);
    private static readonly IReadOnlyDictionary<string, string?> s_environment = new Dictionary<string, string?>
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"] = "no",
        ["AZURE_CORE_COLLECT_TELEMETRY"] = "no",
        // Match AzureDevOps's CLI invocation: MSAL can reject the inherited
        // non-GUID client_session value (https://github.com/ianphil/chamber/issues/419).
        ["COPILOT_AGENT_SESSION_ID"] = null
    };

    public static async Task<int> RunAsync(bool json, CancellationToken ct) =>
        await WriteReportAsync(await CheckAsync(ct).ConfigureAwait(false), json, Console.Out, ct).ConfigureAwait(false);

    public static Task<int> RunAsync(bool json, JsonObject prefs, CancellationToken ct) =>
        RunAsync(json, prefs, ct, RunProcessAsync, Console.Out, protocolProbe: null);

    internal static async Task<int> RunAsync(
        bool json, JsonObject prefs, CancellationToken ct, DoctorProcessRunner processRunner,
        TextWriter output, Func<bool?>? protocolProbe)
    {
        var result = await CheckAsync(prefs, ct, processRunner, protocolProbe).ConfigureAwait(false);
        return await WriteReportAsync(result, json, output, ct).ConfigureAwait(false);
    }

    private static async Task<int> WriteReportAsync(JsonObject result, bool json, TextWriter output, CancellationToken ct)
    {
        if (json)
        {
            await output.WriteLineAsync(result.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("GitHub Team App doctor".AsMemory(), ct).ConfigureAwait(false);
            foreach (var check in result["checks"].Objects())
            {
                var required = check.Flag("required") ? "required" : "optional";
                await output.WriteLineAsync($"[{check.Text("status").ToUpperInvariant()}] {check.Text("label")} ({required}): {check.Text("message")}".AsMemory(), ct).ConfigureAwait(false);
                if (check.Text("remediation") is { Length: > 0 } remediation)
                {
                    await output.WriteLineAsync($"  {remediation}".AsMemory(), ct).ConfigureAwait(false);
                }
            }
        }
        return result.Number("exitCode");
    }

    public static Task<JsonObject> CheckAsync(CancellationToken ct) => CheckAsync(ReadPreferencesAsync, ct);

    internal static async Task<JsonObject> CheckAsync(Func<CancellationToken, Task<JsonObject>> readPreferences, CancellationToken ct)
    {
        JsonObject prefs;
        try
        {
            prefs = await readPreferences(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return Summary(new JsonArray(Check("preferences", "Preferences", true, "failed",
                "Preferences could not be read, so required provider checks cannot be determined.",
                "Correct the preferences JSON or its file permissions in GITHUB_TEAM_APP_HOME (or the default TeamApp data directory), then rerun doctor.")));
        }
        return await CheckAsync(prefs, ct).ConfigureAwait(false);
    }

    public static Task<JsonObject> CheckAsync(JsonObject prefs, CancellationToken ct) =>
        CheckAsync(prefs, ct, RunProcessAsync, protocolProbe: null);

    internal static async Task<JsonObject> CheckAsync(
        JsonObject prefs, CancellationToken ct, DoctorProcessRunner processRunner, Func<bool?>? protocolProbe)
    {
        ct.ThrowIfCancellationRequested();
        var groups = await Task.WhenAll(
            CheckGitHubAsync(RepositoryCatalog.Selected(prefs).Text("host", "github.com"), processRunner, ct),
            CheckAzureAsync(prefs.ContainsKey("repositories") ? RepositoryCatalog.ScopePreferences(prefs) : prefs, processRunner, ct),
            CheckToolAsync("git", "Git", required: false, ["--version"],
                "Git is available. The GitHub App manages session workspaces.",
                "Install Git if the GitHub App needs to create local repository workspaces.", processRunner, ct),
            CheckToolAsync("copilot", "Copilot CLI", required: false, ["--version"],
                "Copilot CLI is available, but CLI session launching is not implemented.",
                "Optional for future CLI integration; no CLI session-launch feature is implemented.", processRunner, ct),
            CheckProtocolAsync(processRunner, protocolProbe, ct)).ConfigureAwait(false);
        var checks = new JsonArray(groups.SelectMany(group => group).Select(check => (JsonNode)check).ToArray());
        checks.Add((JsonNode)Check("dotnet-sdk", ".NET SDK", false, "info",
            "The .NET SDK is needed only to build from source, not to run a published self-contained executable."));
        checks.Add((JsonNode)Check("project-discovery", "GitHub App project discovery", false, "info",
            "No supported external local-project discovery API was found. Configure project names and repository URLs in Settings; mappings are user supplied."));
        checks.Add((JsonNode)Check("current-session", "Existing-session messaging", false, "info",
            "Unavailable: documented app-local session links navigate only. New-session links require confirmation in the GitHub App."));
        return Summary(checks);
    }

    private static async Task<JsonObject[]> CheckGitHubAsync(string host, DoctorProcessRunner runner, CancellationToken ct)
    {
        var tools = await CheckToolAsync("gh", "GitHub CLI", true, ["--version"], "GitHub CLI is installed.",
            "Install GitHub CLI from https://cli.github.com/ and ensure gh is on PATH.", runner, ct).ConfigureAwait(false);
        if (tools[0].Text("status") != "passed")
        {
            return [.. tools, Check("github-auth", "GitHub authentication", true, "failed",
                "Authentication could not be checked because GitHub CLI is unavailable.",
                $"After installing gh, run gh auth login --hostname {host}, then rerun doctor.")];
        }
        // gh auth status validates the active account without printing a token. Never copy
        // CLI stdout/stderr into the report: other tools can include credentials in errors.
        var auth = await ProbeAsync("gh", ["auth", "status", "--active", "--hostname", host], runner, ct).ConfigureAwait(false);
        return [.. tools, Check("github-auth", "GitHub authentication", true, auth.Succeeded ? "passed" : "failed",
            auth.Succeeded ? $"GitHub CLI's active {host} account is authenticated." : $"GitHub authentication could not be verified. {auth.Detail}",
            auth.Succeeded ? "" : $"Run gh auth login --hostname {host} (or refresh the active account), then rerun doctor.")];
    }

    private static async Task<JsonObject[]> CheckAzureAsync(JsonObject prefs, DoctorProcessRunner runner, CancellationToken ct)
    {
        var configured = prefs["azurePipelines"] is JsonArray { Count: > 0 };
        if (prefs["azurePipelines"] is not null and not JsonArray)
        {
            return [Check("azure-configuration", "Azure DevOps configuration", true, "failed",
                "azurePipelines must be an array.", "Correct the pipeline configuration in Settings.")];
        }
        var checks = new List<JsonObject>(await CheckToolAsync("az", "Azure CLI", configured, ["version", "--output", "json"],
            "Azure CLI is installed.", "Install Azure CLI from https://learn.microsoft.com/cli/azure/install-azure-cli if using Azure DevOps health.", runner, ct).ConfigureAwait(false));
        if (checks[0].Text("status") != "passed")
        {
            return [.. checks, Check("azure-devops-extension", "Azure DevOps extension", configured, configured ? "failed" : "warning",
                "The extension and authentication cannot be checked without Azure CLI.",
                "After installing Azure CLI, run az extension add --name azure-devops. Doctor never installs extensions.")];
        }

        var extension = await ProbeAsync("az", ["extension", "show", "--name", "azure-devops", "--query", "version", "--output", "tsv"], runner, ct).ConfigureAwait(false);
        checks.Add(Check("azure-devops-extension", "Azure DevOps extension", configured,
            extension.Succeeded ? "passed" : configured ? "failed" : "warning",
            extension.Succeeded ? "The Azure DevOps extension is installed." : $"The Azure DevOps extension is unavailable. {extension.Detail}",
            extension.Succeeded ? "" : "Run az extension add --name azure-devops if using Azure DevOps health."));
        if (!extension.Succeeded)
        {
            return checks.ToArray();
        }
        if (!configured)
        {
            checks.Add(Check("azure-auth", "Azure DevOps authentication", false, "info",
                "No Azure pipelines are configured, so organization access was not tested. Azure DevOps can use az login or a separately configured PAT."));
            return checks.ToArray();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var pipeline in prefs["azurePipelines"]!.AsArray())
        {
            index++;
            JsonObject coordinate;
            try
            {
                if (pipeline is not JsonObject source || source["url"] is not JsonValue url || !url.TryGetValue<string>(out var rawUrl))
                {
                    throw new ArgumentException("Each configured pipeline must have a URL.");
                }
                coordinate = AzureDevOps.ParsePipelineUrl(rawUrl);
            }
            catch (Exception error) when (error is ArgumentException or AzureDevOpsException)
            {
                checks.Add(Check($"azure-configuration-{index}", "Azure DevOps configuration", true, "failed",
                    "A configured pipeline URL is invalid.", "Correct the pipeline URL in Settings, then rerun doctor."));
                continue;
            }
            var organization = coordinate.Text("organization");
            var project = coordinate.Text("project");
            if (!seen.Add($"{organization}/{project}"))
            {
                continue;
            }
            // Query the configured organization rather than `az account show`: a DevOps PAT
            // can work without an Azure subscription, and subscription login does not prove
            // access to the organization's pipelines.
            var auth = await ProbeAsync("az",
                ["devops", "project", "show", "--organization", organization, "--project", project, "--output", "none", "--only-show-errors"], runner, ct).ConfigureAwait(false);
            checks.Add(Check($"azure-auth-{index}", $"Azure DevOps access ({organization}/{project})", true,
                auth.Succeeded ? "passed" : "failed",
                auth.Succeeded ? "Read access to the configured Azure DevOps project is authenticated." : $"Cannot verify access to the configured Azure DevOps project. {auth.Detail}",
                auth.Succeeded ? "" : $"Run az login or az devops login --organization {organization}, verify project read permissions, then rerun doctor."));
        }
        return checks.ToArray();
    }

    private static async Task<JsonObject[]> CheckToolAsync(
        string executable, string label, bool required, IReadOnlyList<string> arguments,
        string success, string remediation, DoctorProcessRunner runner, CancellationToken ct)
    {
        var result = await ProbeAsync(executable, arguments, runner, ct).ConfigureAwait(false);
        return [Check(executable, label, required, result.Succeeded ? "passed" : required ? "failed" : "warning",
            result.Succeeded ? success : $"The tool is unavailable or did not respond successfully. {result.Detail}",
            result.Succeeded ? "" : remediation)];
    }

    private static async Task<JsonObject[]> CheckProtocolAsync(DoctorProcessRunner runner, Func<bool?>? protocolProbe, CancellationToken ct)
    {
        bool? available;
        if (protocolProbe is not null)
        {
            available = protocolProbe();
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                available = HasMacProtocolHandler();
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                available = null;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var result = await ProbeAsync("reg.exe", ["query", @"HKCR\ghapp\shell\open\command", "/ve"], runner, ct).ConfigureAwait(false);
            available = result.Succeeded;
        }
        else if (OperatingSystem.IsLinux())
        {
            var result = await ProbeAsync("xdg-mime", ["query", "default", "x-scheme-handler/ghapp"], runner, ct).ConfigureAwait(false);
            available = result.Succeeded ? !string.IsNullOrWhiteSpace(result.StandardOutput) : null;
        }
        else
        {
            available = null;
        }
        return [Check("github-app", "GitHub App protocol handler", false, available == true ? "passed" : "warning",
            available switch
            {
                true => "A ghapp protocol handler is registered. This does not verify sign-in, projects, or session creation.",
                false => "No ghapp protocol handler is registered.",
                null => "Protocol-handler availability could not be determined on this system."
            },
            available == true ? "" : "Install and set up the GitHub Copilot app manually for session actions, and permit your browser to open ghapp links. Doctor does not open or launch anything.")];
    }

    private static async Task<ProbeResult> ProbeAsync(string executable, IReadOnlyList<string> arguments, DoctorProcessRunner runner, CancellationToken ct)
    {
        try
        {
            var result = await runner(executable, arguments, ct, s_timeout, s_environment).ConfigureAwait(false);
            return new ProbeResult(result.ExitCode == 0, $"Command exited with code {result.ExitCode}.", result.StandardOutput);
        }
        catch (Win32Exception)
        {
            return new ProbeResult(false, "The executable could not be started; check installation and PATH.", "");
        }
        catch (TimeoutException)
        {
            return new ProbeResult(false, "The diagnostic command timed out.", "");
        }
        catch (AzureDevOpsException error) when (error.Code is "az_cli_missing" or "az_cli_unsupported")
        {
            return new ProbeResult(false, error.Message, "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new ProbeResult(false, "The executable could not be accessed; check file permissions.", "");
        }
    }

    private static Task<ProcessResult> RunProcessAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken ct,
        TimeSpan? timeout, IReadOnlyDictionary<string, string?>? environment)
    {
        if (executable == "az")
        {
            var command = AzureDevOps.ResolveCliCommand(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("PATH") ?? "", File.Exists);
            return ProcessRunner.RunAsync(command.Path, [.. command.Prefix, .. arguments], ct, timeout, environment);
        }
        return ProcessRunner.RunAsync(executable, arguments, ct, timeout, environment);
    }

    private static async Task<JsonObject> ReadPreferencesAsync(CancellationToken ct)
    {
        var directory = Environment.GetEnvironmentVariable("GITHUB_TEAM_APP_HOME")
            ?? Environment.GetEnvironmentVariable("ASPIRE_TEAM_APP_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub", "TeamApp");
        return await new PreferenceStore(directory).ReadAsync(ct).ConfigureAwait(false);
    }

    private static JsonObject Check(string id, string label, bool required, string status, string message, string remediation = "") =>
        new() { ["id"] = id, ["label"] = label, ["required"] = required, ["status"] = status, ["message"] = message, ["remediation"] = remediation };

    private static JsonObject Summary(JsonArray checks)
    {
        var healthy = !checks.Objects().Any(check => check.Flag("required") && check.Text("status") == "failed");
        return new JsonObject { ["ok"] = healthy, ["exitCode"] = healthy ? 0 : 1, ["checks"] = checks };
    }

    private static bool? HasMacProtocolHandler()
    {
        // LaunchServices exposes a read-only protocol lookup. It neither launches the
        // application nor reads its private project/session database.
        // https://developer.apple.com/documentation/coreservices/1447760-lscopydefaulthandlerforurlscheme
        var scheme = CFStringCreateWithCString(0, "ghapp", 0x08000100);
        if (scheme == 0)
        {
            return null;
        }
        try
        {
            var handler = LSCopyDefaultHandlerForURLScheme(scheme);
            if (handler == 0)
            {
                return false;
            }
            CFRelease(handler);
            return true;
        }
        finally
        {
            CFRelease(scheme);
        }
    }

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint value);

    [LibraryImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static partial nint LSCopyDefaultHandlerForURLScheme(nint scheme);

    private sealed record ProbeResult(bool Succeeded, string Detail, string StandardOutput);
}
