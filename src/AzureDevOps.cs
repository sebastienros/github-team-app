// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GitHub.TeamApp;

internal sealed class AzureDevOpsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class AzureDevOps
{
    private readonly Func<IReadOnlyList<string>, bool, CancellationToken, Task<string>> _run;
    private readonly SemaphoreSlim _commands = new(6);

    public AzureDevOps() : this(ExecuteCliAsync)
    {
    }

    internal AzureDevOps(Func<IReadOnlyList<string>, bool, CancellationToken, Task<string>> run)
    {
        _run = run;
    }

    public Task<JsonObject> ResolvePipelineAsync(string url, string? branch, CancellationToken ct) =>
        ResolveAsync(ParsePipelineUrl(url), branch, ct);

    internal static JsonObject ParsePipelineUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw Error("invalid_pipeline_url", "Enter a valid Azure DevOps pipeline URL.");
        }
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0)
        {
            throw Error("invalid_pipeline_url", "Azure DevOps pipeline URLs must use HTTPS without embedded credentials.");
        }
        // Uri canonicalizes a stray '%' to '%25'; validate the original path first
        // so /project%ZZ/_build is rejected rather than silently renamed.
        var originalPath = Regex.Match(uri.OriginalString, @"^[^:]+://[^/?#]*([^?#]*)", RegexOptions.CultureInvariant).Groups[1].Value;
        if (!ValidEncoding(originalPath))
        {
            throw Error("invalid_pipeline_url", "The Azure DevOps pipeline URL contains invalid path encoding.");
        }
        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(DecodePath).ToArray();
        string organization;
        string project;
        var index = Array.IndexOf(segments, "_build");
        if (host == "dev.azure.com")
        {
            if (segments.Length < 3 || index < 2)
            {
                throw Error("invalid_pipeline_url", "The URL must include an Azure DevOps organization, project, and _build path.");
            }
            organization = segments[0];
            project = segments[1];
        }
        else if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal) && host != ".visualstudio.com")
        {
            if (segments.Length < 2 || index < 1)
            {
                throw Error("invalid_pipeline_url", "The URL must include an Azure DevOps project and _build path.");
            }
            organization = host[..^".visualstudio.com".Length];
            project = segments[0];
        }
        else
        {
            throw Error("invalid_pipeline_host", "Only dev.azure.com and visualstudio.com pipeline URLs are supported.");
        }
        ValidateCoordinates(organization, project);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            query.TryAdd(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        var definitionId = PositiveInteger(query.GetValueOrDefault("definitionId"));
        var buildId = PositiveInteger(query.GetValueOrDefault("buildId"));
        if (definitionId is null && buildId is null)
        {
            throw Error("missing_pipeline_id", "The URL must contain definitionId or buildId.");
        }
        return new JsonObject
        {
            ["organization"] = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}",
            ["organizationName"] = organization,
            ["project"] = project,
            ["definitionId"] = definitionId,
            ["buildId"] = buildId,
            ["inputUrl"] = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri
        };
    }

    internal async Task<JsonObject> LoadPipelineHealthAsync(JsonObject raw, DateTimeOffset now, CancellationToken ct)
    {
        var parsed = ParseConfiguration(raw);
        var config = await ResolveAsync(parsed, raw.Text("branch"), ct);
        if (raw.Flag("discovered"))
        {
            config["discovered"] = true;
            config["discovery"] = NormalizeDiscovery(raw["discovery"]);
        }
        var buildArgs = new List<string>
        {
            "pipelines", "build", "list", "--definition-ids", config.Number("definitionId").ToString(CultureInfo.InvariantCulture),
            "--organization", config.Text("organization"), "--project", config.Text("project"), "--branch", config.Text("branch")
        };
        var recent = await QueryAsync([.. buildArgs, "--top", "50"], ct);
        var builds = recent.Objects().ToList();
        builds.Sort(CompareBuilds);
        var latest = builds.FirstOrDefault();
        var lastSuccess = builds.FirstOrDefault(b => NormalizedResult(b) == "succeeded");
        if (lastSuccess is null)
        {
            var successes = (await QueryAsync([.. buildArgs, "--result", "succeeded", "--top", "1"], ct)).Objects().ToList();
            successes.Sort(CompareBuilds);
            lastSuccess = successes.FirstOrDefault();
        }
        var state = BuildState(latest);
        var streak = builds.Where(b => b.Text("status").Equals("completed", StringComparison.OrdinalIgnoreCase))
            .TakeWhile(b => NormalizedResult(b) != "succeeded").Count();
        var lowerBound = builds.Count >= 50 && !builds.Any(b => NormalizedResult(b) == "succeeded");
        var records = new List<JsonObject>();
        AzureDevOpsException? timelineError = null;
        if (PositiveInteger(latest?["id"]) is int buildId && state is "failing" or "degraded")
        {
            try
            {
                // Timeline is a read-only REST resource. Explicit GET avoids an accidental
                // write if Azure CLI's invoke defaults change.
                var timeline = await QueryAsync(["devops", "invoke", "--area", "build", "--resource", "Timeline",
                    "--route-parameters", $"project={config.Text("project")}", $"buildId={buildId}",
                    "--org", config.Text("organization"), "--http-method", "GET"], ct);
                records = NormalizeTimeline(timeline?["records"]);
            }
            catch (Exception exception) when (IsProviderFailure(exception, ct))
            {
                timelineError = AsError(exception);
            }
        }
        var lastSuccessAt = BuildTime(lastSuccess);
        var failed = records.Where(r => r.Flag("failed")).ToArray();
        var mapped = GitHubRepository(config["repository"]);
        config["state"] = state;
        config["mappedRepository"] = mapped;
        config["canOpenRepoSession"] = mapped is not null;
        config["latest"] = latest is null ? null : new JsonObject
        {
            ["id"] = PositiveInteger(latest["id"]),
            ["number"] = Scalar(latest["buildNumber"] ?? latest["id"]),
            ["at"] = BuildTime(latest),
            ["status"] = latest.Text("status"),
            ["result"] = latest.Text("result"),
            ["url"] = BuildUrl(config.Text("organizationName"), config.Text("project"), latest.Number("id")),
            ["sourceVersion"] = latest["sourceVersion"]?.DeepClone(),
            ["requestedFor"] = latest["requestedFor"]?["displayName"]?.DeepClone() ?? latest["requestedFor"]?["uniqueName"]?.DeepClone()
        };
        config["lastSuccessAt"] = lastSuccessAt;
        config["daysSinceSuccess"] = HealthDashboard.DaysSince(lastSuccessAt, now);
        config["failureStreak"] = streak;
        config["failureStreakLowerBound"] = lowerBound;
        config["failedRecords"] = JsonData.Array(failed);
        config["reasons"] = Reasons(state, latest, streak, lowerBound, lastSuccessAt, failed, records, timelineError, now);
        config["evidence"] = JsonData.Array(failed.Take(6).Select(record => new JsonObject
        {
            ["label"] = $"{record.Text("type")}: {record.Text("name")}",
            ["detail"] = record["errors"].Strings().FirstOrDefault() ?? record.Text("result"),
            ["url"] = latest is null ? config.Text("url") : BuildUrl(config.Text("organizationName"), config.Text("project"), latest.Number("id"))
        }));
        config["diagnosticsError"] = timelineError?.Message;
        return config;
    }

    internal static JsonObject UnavailableHealth(JsonObject raw, Exception exception)
    {
        var failure = AsError(exception);
        JsonObject? parsed = null;
        try
        {
            parsed = ParseConfiguration(raw);
        }
        catch (AzureDevOpsException)
        {
            // Preserve the original provider failure rather than a second cached-config error.
        }
        var name = HealthDashboard.Nonempty(raw.Text("name"), PositiveInteger(parsed?["definitionId"]) is int id ? $"Pipeline {id}" : "Azure DevOps pipeline");
        var branch = "refs/heads/main";
        try
        {
            branch = NormalizeBranch(raw.Text("branch"));
        }
        catch (AzureDevOpsException)
        {
            // The primary failure is shown in reasons and diagnosticsError.
        }
        var safeUrl = parsed is null ? null : PositiveInteger(parsed["definitionId"]) is int definitionId
            ? PipelineUrl(parsed.Text("organizationName"), parsed.Text("project"), definitionId)
            : BuildUrl(parsed.Text("organizationName"), parsed.Text("project"), parsed.Number("buildId"));
        return new JsonObject
        {
            ["id"] = HealthDashboard.Nonempty(raw.Text("id"), $"azdo-unavailable:{Uri.EscapeDataString(safeUrl ?? name)}"),
            ["provider"] = "azure-devops",
            ["name"] = name,
            ["url"] = safeUrl,
            ["organization"] = parsed?["organization"]?.DeepClone(),
            ["organizationName"] = parsed?["organizationName"]?.DeepClone(),
            ["project"] = parsed?["project"]?.DeepClone(),
            ["definitionId"] = parsed?["definitionId"]?.DeepClone() ?? raw["definitionId"]?.DeepClone(),
            ["branch"] = branch,
            ["repository"] = NormalizeRepository(raw["repository"]),
            ["mappedRepository"] = null,
            ["canOpenRepoSession"] = false,
            ["state"] = "unavailable",
            ["latest"] = null,
            ["lastSuccessAt"] = null,
            ["daysSinceSuccess"] = null,
            ["failureStreak"] = 0,
            ["failureStreakLowerBound"] = false,
            ["failedRecords"] = new JsonArray(),
            ["evidence"] = new JsonArray(),
            ["reasons"] = new JsonArray(HealthDashboard.Reason(failure.Code, "muted", failure.Message)),
            ["diagnosticsError"] = failure.Message,
            ["discovered"] = raw.Flag("discovered"),
            ["discovery"] = NormalizeDiscovery(raw["discovery"])
        };
    }

    internal static string? RemovalKey(string value)
    {
        var id = value.Trim();
        if (!id.StartsWith("azdo:", StringComparison.Ordinal) || id.Length > 512 || HasControl(id))
        {
            return null;
        }
        return "azp1_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string? IdFromRemovalKey(string value)
    {
        var key = value.Trim();
        if (!key.StartsWith("azp1_", StringComparison.Ordinal) || key.Length > 2048 || !Regex.IsMatch(key, "^[A-Za-z0-9_-]+$"))
        {
            return null;
        }
        try
        {
            var base64 = key[5..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var id = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64));
            return RemovalKey(id) == key ? id : null;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    private async Task<JsonObject> ResolveAsync(JsonObject parsed, string? branch, CancellationToken ct)
    {
        ValidateCoordinates(parsed.Text("organizationName"), parsed.Text("project"));
        if (!string.IsNullOrEmpty(branch))
        {
            NormalizeBranch(branch);
        }
        var definitionId = PositiveInteger(parsed["definitionId"]);
        if (definitionId is null && PositiveInteger(parsed["buildId"]) is int buildId)
        {
            var build = await QueryAsync(["pipelines", "build", "show", "--id", buildId.ToString(CultureInfo.InvariantCulture),
                "--organization", parsed.Text("organization"), "--project", parsed.Text("project")], ct);
            definitionId = PositiveInteger(build?["definition"]?["id"]);
        }
        if (definitionId is null)
        {
            throw Error("missing_pipeline_id", "The Azure DevOps build does not identify a pipeline definition.");
        }
        var definition = await QueryAsync(["pipelines", "show", "--id", definitionId.Value.ToString(CultureInfo.InvariantCulture),
            "--organization", parsed.Text("organization"), "--project", parsed.Text("project")], ct) as JsonObject;
        if (definition is null || PositiveInteger(definition["id"]) != definitionId)
        {
            throw Error("pipeline_not_found", $"Azure DevOps pipeline {definitionId} was not found.");
        }
        return NormalizeDefinition(parsed, definition, branch);
    }

    private static JsonObject ParseConfiguration(JsonObject raw)
    {
        JsonObject parsed;
        if (raw.Text("url").Length > 0)
        {
            parsed = ParsePipelineUrl(raw.Text("url"));
            if (raw["definitionId"] is not null)
            {
                var id = PositiveInteger(raw["definitionId"]) ?? throw Error("missing_pipeline_id", "The Azure DevOps pipeline definition is invalid.");
                if (PositiveInteger(parsed["definitionId"]) is int urlId && urlId != id)
                {
                    throw Error("invalid_pipeline_url", "The Azure DevOps pipeline URL and definition ID do not match.");
                }
                parsed["definitionId"] = id;
            }
            return parsed;
        }
        var organization = raw.Text("organizationName");
        var project = raw.Text("project");
        ValidateCoordinates(organization, project);
        parsed = ParsePipelineUrl(PipelineUrl(organization, project,
            PositiveInteger(raw["definitionId"]) ?? throw Error("missing_pipeline_id", "The URL must contain definitionId or buildId.")));
        if (raw.Text("organization").Length > 0 && !raw.Text("organization").TrimEnd('/').Equals(parsed.Text("organization"), StringComparison.OrdinalIgnoreCase))
        {
            throw Error("invalid_pipeline_url", "The Azure DevOps organization coordinates do not match.");
        }
        return parsed;
    }

    private static JsonObject NormalizeDefinition(JsonObject parsed, JsonObject definition, string? branch)
    {
        var id = PositiveInteger(definition["id"]) ?? throw Error("pipeline_not_found", "The Azure DevOps pipeline definition is invalid.");
        var organization = parsed.Text("organizationName");
        var project = parsed.Text("project");
        return new JsonObject
        {
            ["id"] = $"azdo:{organization.ToLowerInvariant()}/{project.ToLowerInvariant()}/{id}",
            ["provider"] = "azure-devops",
            ["url"] = PipelineUrl(organization, project, id),
            ["organization"] = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}",
            ["organizationName"] = organization,
            ["project"] = project,
            ["definitionId"] = id,
            ["name"] = HealthDashboard.Nonempty(definition.Text("name"), $"Pipeline {id}"),
            ["branch"] = NormalizeBranch(HealthDashboard.Nonempty(branch ?? "", definition["repository"].Text("defaultBranch"), "refs/heads/main")),
            ["repository"] = NormalizeRepository(definition["repository"]),
            ["discovered"] = false,
            ["discovery"] = null
        };
    }

    private static JsonObject? NormalizeRepository(JsonNode? repository)
    {
        if (repository is not JsonObject)
        {
            return null;
        }
        var result = new JsonObject();
        foreach (var key in new[] { "id", "name", "type", "url", "defaultBranch" })
        {
            result[key] = repository[key]?.DeepClone();
        }
        // Azure Git remotes can contain credentials; metadata must never persist them.
        if (Uri.TryCreate(result.Text("url"), UriKind.Absolute, out var url) && url.UserInfo.Length > 0)
        {
            result["url"] = null;
        }
        return result;
    }

    private static JsonObject? NormalizeDiscovery(JsonNode? value)
    {
        var kind = value.Text("kind");
        var repository = value.Text("repository").Trim();
        var azureRepository = value.Text("azureRepository").Trim();
        // Preserve provenance on explicitly saved legacy pipelines without enabling discovery.
        return kind is "azure-cli-default" or "official-default" && StrictRepository(repository) && ValidProject(azureRepository)
            ? Discovery(kind, repository, azureRepository, Math.Max(1, PositiveInteger(value?["pipelineCandidates"]) ?? 1)) : null;
    }

    private static JsonObject Discovery(string kind, string repository, string azureRepository, int count) =>
        new() { ["kind"] = kind, ["repository"] = repository, ["azureRepository"] = azureRepository, ["pipelineCandidates"] = Math.Max(1, count) };

    internal static string? GitHubRepository(JsonNode? repository)
    {
        var name = repository.Text("name").Trim();
        if (!repository.Text("type").Contains("github", StringComparison.OrdinalIgnoreCase) || !StrictRepository(name)
            || !Uri.TryCreate(repository.Text("url"), UriKind.Absolute, out var url)
            || url.Scheme != "https" || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || url.UserInfo.Length > 0)
        {
            return null;
        }
        var path = Regex.Replace(url.AbsolutePath, @"\.git$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim('/');
        return path.Equals(name, StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    private static List<JsonObject> NormalizeTimeline(JsonNode? nodes)
    {
        var records = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Objects())
        {
            var type = node.Text("type");
            if (type is not ("Stage" or "Job" or "Phase" or "Task") || node.Text("name").Length == 0)
            {
                continue;
            }
            var errors = node["issues"].Objects()
                .Where(i => i.Text("type").Equals("error", StringComparison.OrdinalIgnoreCase) && i.Text("message").Length > 0)
                .Select(i => HealthText.Compact(i.Text("message"))).Where(s => s.Length > 0).ToArray();
            var result = node.Text("result");
            var key = $"{type}\n{node.Text("name")}\n{result}\n{string.Join('\n', errors)}";
            if (!seen.Add(key))
            {
                continue;
            }
            records.Add(new JsonObject
            {
                ["type"] = type,
                ["name"] = node.Text("name").Trim(),
                ["state"] = node.Text("state"),
                ["result"] = result,
                ["failed"] = result.Equals("failed", StringComparison.OrdinalIgnoreCase) || errors.Length > 0,
                ["errors"] = JsonData.Array(errors),
                ["order"] = node["order"]?.DeepClone()
            });
        }
        return records.OrderBy(r => r.Number("order", int.MaxValue)).ToList();
    }

    private static JsonArray Reasons(string state, JsonObject? latest, int streak, bool lowerBound, string? lastSuccess,
        JsonObject[] failed, List<JsonObject> timeline, AzureDevOpsException? timelineError, DateTimeOffset now)
    {
        var reasons = new JsonArray();
        var failedStage = timeline.FirstOrDefault(r => r.Text("type") == "Stage" && r.Flag("failed"));
        var skipped = timeline.FirstOrDefault(r => r.Text("type") == "Stage" && r.Text("result").Equals("skipped", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(r.Text("name"), "deploy|publish|release", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if (failedStage is not null && skipped is not null)
        {
            reasons.Add(HealthDashboard.Reason("upstream_stage_blocked_deployment", "danger",
                $"{skipped.Text("name")} was skipped while {failedStage.Text("name")} failed; deployment is likely blocked upstream."));
        }
        var first = failed.FirstOrDefault(r => r.Text("type") == "Task") ?? failed.FirstOrDefault(r => r.Text("type") == "Job") ?? failed.FirstOrDefault();
        var number = Scalar(latest?["buildNumber"] ?? latest?["id"]);
        if (NormalizedResult(latest) is "canceled" or "cancelled")
        {
            reasons.Add(HealthDashboard.Reason("latest_build_canceled", "danger", $"Latest build {number} was canceled before completion."));
        }
        else if (first is not null)
        {
            var detail = first["errors"].Strings().FirstOrDefault();
            reasons.Add(HealthDashboard.Reason("failed_timeline_record", "danger", $"{first.Text("type")} {first.Text("name")} failed{(detail is null ? "" : $": {detail}")}"));
        }
        else if (state == "failing")
        {
            reasons.Add(HealthDashboard.Reason("latest_build_failed", "danger", $"Latest build {number} failed."));
        }
        else if (state == "degraded")
        {
            reasons.Add(HealthDashboard.Reason("latest_build_degraded", "warning", $"Latest build {number} partially succeeded."));
        }
        else if (state == "running")
        {
            reasons.Add(HealthDashboard.Reason("build_running", "warning", "The latest build is still running."));
        }
        else if (state == "unknown")
        {
            reasons.Add(HealthDashboard.Reason("no_builds", "muted", "No builds were found for the configured branch."));
        }
        if (streak > 1)
        {
            reasons.Add(HealthDashboard.Reason("build_failure_streak", "danger", $"{(lowerBound ? "At least " : "")}{streak} consecutive completed builds have not succeeded."));
        }
        if (state != "healthy" && !string.IsNullOrEmpty(lastSuccess))
        {
            var days = HealthDashboard.DaysSince(lastSuccess, now);
            reasons.Add(HealthDashboard.Reason("last_success_age", "warning", $"Last successful build was {days} day{HealthDashboard.Plural(days)} ago."));
        }
        else if (state != "healthy" && latest is not null)
        {
            reasons.Add(HealthDashboard.Reason("no_success_found", "warning", "No successful build was found for the configured branch."));
        }
        if (timelineError is not null)
        {
            reasons.Add(HealthDashboard.Reason("timeline_unavailable", "muted", $"Build timeline unavailable: {timelineError.Message}"));
        }
        return reasons;
    }

    private static string BuildState(JsonObject? build)
    {
        if (build is null)
        {
            return "unknown";
        }
        var status = build.Text("status").ToLowerInvariant();
        return status.Length > 0 && status != "completed" ? "running" : NormalizedResult(build) switch
        {
            "succeeded" => "healthy",
            "partiallysucceeded" => "degraded",
            "failed" or "canceled" or "cancelled" => "failing",
            "none" => "running",
            _ => "unknown"
        };
    }

    private static string NormalizedResult(JsonObject? build) => build.Text("result", "none").Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
    private static string? BuildTime(JsonObject? build) =>
        new[] { build.Text("finishTime"), build.Text("startTime"), build.Text("queueTime") }.FirstOrDefault(s => s.Length > 0);

    private static int CompareBuilds(JsonObject a, JsonObject b)
    {
        if (a.Date("queueTime") is DateTimeOffset aQueued && b.Date("queueTime") is DateTimeOffset bQueued && aQueued != bQueued)
        {
            return bQueued.CompareTo(aQueued);
        }
        var ids = (PositiveInteger(b["id"]) ?? 0).CompareTo(PositiveInteger(a["id"]) ?? 0);
        if (ids != 0)
        {
            return ids;
        }
        DateTimeOffset.TryParse(BuildTime(a), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var aTime);
        DateTimeOffset.TryParse(BuildTime(b), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var bTime);
        return bTime.CompareTo(aTime);
    }

    private static string PipelineUrl(string organization, string project, int id) =>
        $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_build?definitionId={id}";
    private static string BuildUrl(string organization, string project, int id) =>
        $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_build/results?buildId={id}";
    private static bool StrictRepository(string repository) => Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static bool HasControl(string value) => value.Any(c => c < ' ' || c == '\x7f');
    private static bool ValidOrganization(string value) => Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static bool ValidProject(string value) => value.Length is > 0 and <= 256 && !HasControl(value) && !value.Contains('/') && !value.Contains('\\');
    private static bool ValidEncoding(string value) => !Regex.IsMatch(value, "%(?![0-9a-fA-F]{2})", RegexOptions.CultureInvariant);

    private static string DecodePath(string value)
    {
        if (!ValidEncoding(value))
        {
            throw Error("invalid_pipeline_url", "The Azure DevOps pipeline URL contains invalid path encoding.");
        }
        var decoded = new StringBuilder();
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '%')
            {
                decoded.Append(value[index++]);
                continue;
            }
            var bytes = new List<byte>();
            while (index < value.Length && value[index] == '%')
            {
                bytes.Add(byte.Parse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                index += 3;
            }
            try
            {
                decoded.Append(new UTF8Encoding(false, true).GetString(bytes.ToArray()));
            }
            catch (DecoderFallbackException)
            {
                throw Error("invalid_pipeline_url", "The Azure DevOps pipeline URL contains invalid path encoding.");
            }
        }
        return decoded.ToString();
    }

    private static void ValidateCoordinates(string organization, string project)
    {
        if (!ValidOrganization(organization) || !ValidProject(project))
        {
            throw Error("invalid_pipeline_url", "The Azure DevOps organization or project is invalid.");
        }
    }

    internal static string NormalizeBranch(string? value)
    {
        var branch = value?.Trim() ?? "";
        if (branch.Length == 0)
        {
            return "refs/heads/main";
        }
        if (HasControl(branch) || branch.Length > 512)
        {
            throw Error("invalid_branch", "The Azure DevOps branch is invalid.");
        }
        return branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";
    }

    private static int? PositiveInteger(JsonNode? value) => PositiveInteger(Scalar(value));
    private static int? PositiveInteger(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    private static string Scalar(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "";

    private async Task<JsonNode?> QueryAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var output = await InvokeAsync(args, false, ct);
        try
        {
            return string.IsNullOrWhiteSpace(output) ? null : JsonNode.Parse(output);
        }
        catch (JsonException)
        {
            throw Error("azdo_query_failed", "Azure DevOps returned invalid JSON.");
        }
    }

    private async Task<string> InvokeAsync(IReadOnlyList<string> args, bool text, CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            var output = await _run(args, text, ct);
            if (output.Length > 16 * 1024 * 1024)
            {
                throw Error("azdo_query_failed", "The Azure DevOps response exceeded the size limit.");
            }
            return output;
        }
        catch (Exception exception) when (IsProviderFailure(exception, ct))
        {
            throw AsError(exception);
        }
        finally
        {
            _commands.Release();
        }
    }

    private static async Task<string> ExecuteCliAsync(IReadOnlyList<string> args, bool text, CancellationToken ct)
    {
        var (executable, prefix) = ResolveCliCommand(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("PATH") ?? "", File.Exists);
        var arguments = new List<string>(prefix);
        arguments.AddRange(args);
        arguments.Add("--only-show-errors");
        if (!text)
        {
            arguments.AddRange(["-o", "json"]);
        }
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(executable, arguments, ct, TimeSpan.FromSeconds(30),
                new Dictionary<string, string?>
                {
                    // MSAL forwards this Copilot correlation ID as client_session and can
                    // reject otherwise valid Azure CLI auth (ianphil/chamber#419).
                    ["COPILOT_AGENT_SESSION_ID"] = null,
                    ["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"] = "no"
                });
        }
        catch (Win32Exception)
        {
            throw Error("az_cli_missing", "Azure CLI is not installed or is not available on PATH.");
        }
        if (result.ExitCode != 0)
        {
            throw ClassifyCommandError(result.StandardError + "\n" + result.StandardOutput);
        }
        return result.StandardOutput;
    }

    internal static (string Path, string[] Prefix) ResolveCliCommand(bool windows, string path, Func<string, bool> exists)
    {
        if (!windows)
        {
            return ("az", []);
        }
        var batchFound = false;
        foreach (var entry in path.Split(';').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0))
        {
            foreach (var extension in new[] { ".exe", ".com" })
            {
                var candidate = Path.Combine(entry, $"az{extension}");
                if (exists(candidate))
                {
                    return (candidate, []);
                }
            }
            foreach (var extension in new[] { ".cmd", ".bat" })
            {
                if (exists(Path.Combine(entry, $"az{extension}")))
                {
                    batchFound = true;
                    // Do not use cmd.exe: %NAME% inside a legal branch argument would expand.
                    var python = Path.GetFullPath(Path.Combine(entry, "..", "python.exe"));
                    if (exists(python))
                    {
                        return (python, ["-IBm", "azure.cli"]);
                    }
                }
            }
        }
        throw batchFound
            ? Error("az_cli_unsupported", "This Azure CLI installation does not expose a safe executable entry point.")
            : Error("az_cli_missing", "Azure CLI is not installed or is not available on PATH.");
    }

    internal static AzureDevOpsException ClassifyCommandError(string detail)
    {
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (Regex.IsMatch(detail, @"azure-devops extension|az extension add --name azure-devops|pipelines.*misspelled", options))
        {
            return Error("azdo_extension_missing", "The Azure CLI azure-devops extension is not installed.");
        }
        if (Regex.IsMatch(detail, @"az login|not logged|authentication|TF400813|401\b", options))
        {
            return Error("azdo_auth_required", "Azure DevOps authentication is required. Run az login or set AZURE_DEVOPS_EXT_PAT.");
        }
        if (Regex.IsMatch(detail, @"access denied|forbidden|TF401019|VS403|403\b", options))
        {
            return Error("azdo_access_denied", "The current Azure DevOps credential cannot access this pipeline.");
        }
        if (Regex.IsMatch(detail, @"timed out|ETIMEDOUT", options))
        {
            return Error("azdo_timeout", "The Azure DevOps query timed out.");
        }
        // Unknown command output may contain tokens, request headers, or remote URLs.
        return Error("azdo_query_failed", "The Azure DevOps query failed.");
    }

    internal static AzureDevOpsException AsError(Exception exception) => exception switch
    {
        AzureDevOpsException error => Error(error.Code, HealthText.Compact(error.Message)),
        TimeoutException or OperationCanceledException => Error("azdo_timeout", "The Azure DevOps query timed out."),
        Win32Exception or FileNotFoundException => Error("az_cli_missing", "Azure CLI is not installed or is not available on PATH."),
        _ => Error("azdo_query_failed", "The Azure DevOps query failed.")
    };

    private static bool IsProviderFailure(Exception exception, CancellationToken ct) =>
        !ct.IsCancellationRequested && exception is AzureDevOpsException or Win32Exception or IOException or JsonException
            or InvalidOperationException or FormatException or TimeoutException or OperationCanceledException;
    private static AzureDevOpsException Error(string code, string message) => new(code, message);
}

internal static class HealthText
{
    public static string Compact(string value)
    {
        var compact = Regex.Replace(Redact(value), @"\s+", " ").Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }

    public static string Redact(string value)
    {
        foreach (var name in new[] { "AZURE_DEVOPS_EXT_PAT", "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } secret)
            {
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }
        value = Regex.Replace(value, @"\b(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+", "[redacted]", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b(Bearer|Basic)\s+[A-Za-z0-9+/_.=-]+", "$1 [redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(value, @"(https?://)[^/\s@]+@", "$1[redacted]@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
