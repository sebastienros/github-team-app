// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aspire.TeamApp;

internal sealed class HealthDashboard
{
    private readonly HttpClient _http;
    private readonly ILogger<HealthDashboard> _logger;
    private readonly AzureDevOps _azure;
    private readonly TimeProvider _clock;
    private readonly Lock _historyLock = new();
    private readonly Dictionary<string, HistoryEntry> _historyCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _historyOrder = new();

    public HealthDashboard(HttpClient http, ILogger<HealthDashboard> logger)
        : this(http, logger, new AzureDevOps(), TimeProvider.System)
    {
    }

    internal HealthDashboard(HttpClient http, ILogger<HealthDashboard> logger, AzureDevOps azure, TimeProvider clock)
    {
        _http = http;
        _logger = logger;
        _azure = azure;
        _clock = clock;
    }

    public Task<JsonObject> ResolvePipelineAsync(string url, string? branch, CancellationToken ct) =>
        _azure.ResolvePipelineAsync(url, branch, ct);

    public async Task<JsonObject> LoadAsync(IReadOnlyList<Account> accounts, JsonObject prefs, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var usable = accounts.Where(a => a.Active && a.Token.Length > 0 && a.Login.Length > 0).ToArray();
        var pipelines = prefs["azurePipelines"].Objects().Select(p => (JsonObject)p.DeepClone()).ToArray();
        var repositories = usable.SelectMany(a => a.Repos).Distinct().ToArray();
        var discoverable = usable.Where(a => GitHubHost(a.Host) == "github.com")
            .SelectMany(a => a.Repos).Distinct().ToArray();
        var items = new ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal);
        var successful = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<(string? Source, string Message)>();
        var knownPipelines = new HashSet<string>(pipelines.Select(p => p.Text("id")).Where(id => id.Length > 0));
        JsonObject discovery = new() { ["pipelines"] = new JsonArray(), ["warnings"] = new JsonArray(), ["providers"] = new JsonArray() };

        async Task LoadGitHubAsync(Account account, string repository)
        {
            var source = $"{GitHubHost(account.Host)}\n{repository.ToLowerInvariant()}";
            try
            {
                var item = await LoadGitHubRepositoryAsync(account, repository, now, ct);
                successful[source] = true;
                items.TryAdd(item.Text("id"), item);
            }
            catch (Exception error) when (IsProviderFailure(error, ct))
            {
                // Never log the exception: HTTP/process diagnostics can contain credentials.
                _logger.LogWarning("A GitHub health source could not be loaded.");
                errors.Add((source, $"{repository}: {SafeGitHubError(error, account.Token)}"));
            }
        }

        async Task LoadAzureAsync(JsonObject pipeline)
        {
            JsonObject item;
            try
            {
                item = await _azure.LoadPipelineHealthAsync(pipeline, now, ct);
            }
            catch (Exception error) when (IsProviderFailure(error, ct))
            {
                _logger.LogWarning("An Azure DevOps health source could not be loaded.");
                item = AzureDevOps.UnavailableHealth(pipeline, error);
            }
            items[item.Text("id")] = item;
        }

        async Task DiscoverAsync()
        {
            discovery = await _azure.DiscoverAsync(discoverable, ct);
            foreach (var warning in discovery["warnings"].Strings())
            {
                errors.Add((null, $"Azure DevOps discovery: {warning}"));
            }
            var discovered = discovery["pipelines"].Objects()
                .Where(p => p.Text("id").Length > 0 && knownPipelines.Add(p.Text("id"))).ToArray();
            await Task.WhenAll(discovered.Select(LoadAzureAsync));
        }

        var jobs = usable.SelectMany(account => account.Repos.Select(repository => LoadGitHubAsync(account, repository))).ToList();
        jobs.AddRange(pipelines.Select(LoadAzureAsync));
        if (discoverable.Length > 0)
        {
            jobs.Add(DiscoverAsync());
        }
        await Task.WhenAll(jobs);
        ct.ThrowIfCancellationRequested();

        var ordered = AssociateSources(items.Values
            .OrderBy(item => StateRank(item.Text("state")))
            .ThenBy(item => item.Text("name"), StringComparer.CurrentCulture)
            .ThenBy(item => item.Text("id"), StringComparer.Ordinal));
        ordered = ApplyOrder(ordered, prefs["healthOrder"].Strings());
        var counts = Counts(ordered);
        var finalErrors = errors.Where(e => e.Source is null || !successful.ContainsKey(e.Source))
            .Select(e => e.Message).Distinct().Order(StringComparer.Ordinal).ToArray();
        var configured = usable.Length > 0 || pipelines.Length > 0;
        var githubCount = ordered.Count(item => item.Text("provider") == "github");
        var configuredAzure = ordered.Where(item => item.Text("provider") == "azure-devops" && !item.Flag("discovered")).ToArray();
        var unavailableAzure = configuredAzure.Count(item => item.Text("state") == "unavailable");
        var githubFailed = errors.Any(e => e.Source is not null && !successful.ContainsKey(e.Source));
        var providers = new JsonArray
        {
            (JsonNode)new JsonObject
            {
                ["provider"] = "github",
                ["status"] = usable.Length == 0 ? "not_configured" : githubFailed ? githubCount > 0 ? "partial" : "unavailable" : "available",
                ["message"] = usable.Length == 0 ? "No active GitHub account is configured." : githubFailed ? "Some GitHub health sources could not be loaded." : null,
                ["sourceCount"] = githubCount
            }
        };
        foreach (var provider in discovery["providers"].Objects())
        {
            providers.Add(provider.DeepClone());
        }
        if (pipelines.Length > 0 || discoverable.Length == 0)
        {
            providers.Add((JsonNode)new JsonObject
            {
                ["provider"] = "azure-devops",
                ["scope"] = "configured",
                ["status"] = pipelines.Length == 0 ? "not_configured"
                    : unavailableAzure == 0 ? "available"
                    : unavailableAzure == configuredAzure.Length ? "unavailable" : "partial",
                ["message"] = pipelines.Length == 0 ? "No Azure DevOps pipelines are configured."
                    : unavailableAzure > 0 ? "Some configured Azure DevOps health sources could not be loaded." : null,
                ["sourceCount"] = configuredAzure.Length
            });
        }
        return new JsonObject
        {
            ["authenticated"] = configured,
            ["message"] = configured ? null : "No health sources are configured. Enable a GitHub account or add an Azure DevOps pipeline.",
            ["viewer"] = usable.FirstOrDefault()?.Login,
            ["viewers"] = JsonData.Array(usable.Select(a => a.Login)),
            ["mode"] = "health",
            ["loading"] = false,
            ["repos"] = JsonData.Array(repositories),
            ["lanes"] = new JsonArray(),
            ["attention"] = null,
            ["notifications"] = new JsonArray(),
            ["counts"] = counts.DeepClone(),
            ["providers"] = providers.DeepClone(),
            ["health"] = new JsonObject
            {
                ["items"] = JsonData.Array(ordered),
                ["counts"] = counts,
                ["providers"] = providers,
                ["loading"] = false
            },
            ["errors"] = JsonData.Array(finalErrors),
            ["fetchedAt"] = now.ToString("O")
        };
    }

    internal async Task<JsonObject> LoadGitHubRepositoryAsync(Account account, string repository, DateTimeOffset now, CancellationToken ct)
    {
        if (!IsRepository(repository))
        {
            throw new HealthProviderException($"Invalid repo \"{repository}\"");
        }
        var parts = repository.Trim().Split('/');
        var host = GitHubHost(account.Host);
        var graphql = host == "github.com" ? "https://api.github.com/graphql" : $"https://{host}/api/graphql";
        JsonObject Variables(string? after) => new() { ["owner"] = parts[0], ["name"] = parts[1], ["after"] = after };
        var initial = await QueryAsync(graphql, account.Token, InitialQuery, Variables(null), ct);
        var repo = initial["repository"] as JsonObject
            ?? throw new HealthProviderException("Repository was not found or is not accessible");
        var branch = repo["defaultBranchRef"] as JsonObject;
        var name = repo.Text("nameWithOwner", repository);
        var url = repo.Text("url", $"https://{host}/{repository}");
        if (Uri.TryCreate(url, UriKind.Absolute, out var repositoryUri))
        {
            host = repositoryUri.Host.ToLowerInvariant();
        }
        var id = $"github:{host}/{name.ToLowerInvariant()}";
        var item = new JsonObject
        {
            ["id"] = id,
            ["provider"] = "github",
            ["name"] = name,
            ["repository"] = name,
            ["mappedRepository"] = host == "github.com" ? name : null,
            ["canOpenRepoSession"] = host == "github.com",
            ["host"] = host,
            ["branch"] = branch?["name"]?.DeepClone(),
            ["url"] = url,
            ["state"] = "unknown",
            ["latest"] = null,
            ["lastSuccessAt"] = null,
            ["daysSinceSuccess"] = null,
            ["failureStreak"] = 0,
            ["failedChecks"] = new JsonArray(),
            ["reasons"] = new JsonArray(Reason("no_default_branch", "muted", "No default-branch commit is available.")),
            ["evidence"] = new JsonArray(),
            ["historyExamined"] = 0,
            ["successSearchTruncated"] = false
        };
        if (branch?["head"] is not JsonObject head)
        {
            return item;
        }

        var history = new List<JsonObject>();
        var connection = branch["historyTarget"]?["history"];
        AppendHistory(history, connection?["nodes"]);
        var firstLength = history.Count;
        HistoryEntry? cached;
        lock (_historyLock)
        {
            _historyCache.TryGetValue(id, out cached);
        }
        bool truncated;
        if (cached?.Head == head.Text("oid"))
        {
            if (FindSuccess(history) is null)
            {
                history.AddRange(cached.Tail.Select(h => (JsonObject)h.DeepClone()));
            }
            truncated = FindSuccess(history) is null && cached.Truncated;
        }
        else
        {
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var pages = 1;
            while (FindSuccess(history) is null && connection?["pageInfo"].Flag("hasNextPage") == true && pages < 10)
            {
                var cursor = connection["pageInfo"].Text("endCursor");
                if (cursor.Length == 0 || !cursors.Add(cursor))
                {
                    throw new HealthProviderException("GitHub history pagination returned an invalid cursor");
                }
                var next = await QueryAsync(graphql, account.Token, HistoryQuery, Variables(cursor), ct);
                connection = next["repository"]?["defaultBranchRef"]?["target"]?["history"];
                AppendHistory(history, connection?["nodes"]);
                pages++;
            }
            truncated = FindSuccess(history) is null && connection?["pageInfo"].Flag("hasNextPage") == true;
            lock (_historyLock)
            {
                _historyOrder.Remove(id);
                _historyOrder.AddLast(id);
                _historyCache[id] = new(head.Text("oid"), history.Skip(firstLength).Select(h => (JsonObject)h.DeepClone()).ToArray(), truncated);
                while (_historyCache.Count > 100)
                {
                    _historyCache.Remove(_historyOrder.First!.Value);
                    _historyOrder.RemoveFirst();
                }
            }
        }

        var checkConnection = head["statusCheckRollup"]?["contexts"];
        var contexts = checkConnection?["nodes"].Objects().ToList() ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (checkConnection?["pageInfo"].Flag("hasNextPage") == true)
        {
            var cursor = checkConnection["pageInfo"].Text("endCursor");
            if (cursor.Length == 0 || !seen.Add(cursor))
            {
                throw new HealthProviderException("GitHub check context pagination returned an invalid cursor");
            }
            var variables = Variables(cursor);
            variables["oid"] = head.Text("oid");
            // Pin contexts to the captured SHA; a new default-branch head must not mix checks.
            var next = await QueryAsync(graphql, account.Token, ContextsQuery, variables, ct);
            var commit = next["repository"]?["object"];
            checkConnection = commit?["statusCheckRollup"]?["contexts"];
            if (commit.Text("oid") != head.Text("oid") || checkConnection is null)
            {
                throw new HealthProviderException("GitHub check context pagination returned an unexpected commit");
            }
            contexts.AddRange(checkConnection["nodes"].Objects());
        }
        var failedChecks = NormalizeChecks(contexts).Where(c => c.Text("state") is "failing" or "degraded").ToArray();
        var pullRequest = SelectPullRequest(head["associatedPullRequests"]?["nodes"]);
        var success = FindSuccess(history);
        var lastSuccessAt = success?.Text("committedDate");
        var state = RollupState(head["statusCheckRollup"].Text("state"));
        var streak = history.TakeWhile(h => h["statusCheckRollup"].Text("state").ToUpperInvariant() is "FAILURE" or "ERROR").Count();
        var evidence = failedChecks.Take(6).Select(check => new JsonObject
        {
            ["label"] = check.Text("name"),
            ["detail"] = Nonempty(check.Text("conclusion"), check.Text("status"), "Failure"),
            ["url"] = check["url"]?.DeepClone()
        }).ToList();
        if (pullRequest?["autoMerge"].Text("enabledAt").Length > 0)
        {
            evidence.Insert(0, new JsonObject
            {
                ["label"] = $"Auto-merged PR #{pullRequest.Number("number")}",
                ["detail"] = string.Join(" via ", new[] { pullRequest.Text("author"), pullRequest["autoMerge"].Text("enabledBy") }.Where(s => s.Length > 0)),
                ["url"] = pullRequest["url"]?.DeepClone()
            });
        }
        item["state"] = state;
        item["latest"] = new JsonObject
        {
            ["id"] = head["oid"]?.DeepClone(),
            ["at"] = head["committedDate"]?.DeepClone(),
            ["status"] = head["statusCheckRollup"]?["state"]?.DeepClone(),
            ["url"] = $"{url}/commit/{head.Text("oid")}",
            ["actor"] = head["author"]?["user"]?["login"]?.DeepClone() ?? head["author"]?["name"]?.DeepClone(),
            ["message"] = head["messageHeadline"]?.DeepClone()
        };
        item["lastSuccessAt"] = lastSuccessAt;
        item["daysSinceSuccess"] = DaysSince(lastSuccessAt, now);
        item["failureStreak"] = streak;
        item["failedChecks"] = JsonData.Array(failedChecks);
        item["linkedPullRequest"] = pullRequest;
        item["reasons"] = GitHubReasons(state, failedChecks, streak, pullRequest, lastSuccessAt, history.Count, truncated, now);
        item["evidence"] = JsonData.Array(evidence);
        item["historyExamined"] = history.Count;
        item["successSearchTruncated"] = truncated;
        return item;
    }

    internal static JsonObject Counts(IEnumerable<JsonObject> items)
    {
        var counts = new JsonObject
        {
            ["total"] = 0,
            ["healthy"] = 0,
            ["running"] = 0,
            ["degraded"] = 0,
            ["failing"] = 0,
            ["unavailable"] = 0,
            ["unknown"] = 0
        };
        foreach (var item in items)
        {
            counts["total"] = counts.Number("total") + 1;
            var state = item.Text("state");
            var key = state != "total" && counts.ContainsKey(state) ? state : "unknown";
            counts[key] = counts.Number(key) + 1;
        }
        return counts;
    }

    internal static List<JsonObject> AssociateSources(IEnumerable<JsonObject> items)
    {
        var sources = items.Select(i => (JsonObject)i.DeepClone()).ToList();
        var groups = new List<RepositoryGroup>();
        foreach (var source in sources.Where(s => s.Text("provider") == "github"))
        {
            var repository = Nonempty(source.Text("repository"), source.Text("name")).Trim();
            if (!IsRepository(repository))
            {
                continue;
            }
            var host = Nonempty(source.Text("host"), "github.com").Trim().ToLowerInvariant();
            var group = new RepositoryGroup($"repository:{host}/{repository.ToLowerInvariant()}", repository, host,
                RepositoryMatchKey(repository), RepositoryMatchKey(repository.Split('/')[1]));
            groups.Add(group);
            source["groupId"] = group.Id;
            source["groupName"] = group.Name;
            source["groupMatch"] = "canonical";
        }
        foreach (var source in sources.Where(s => s.Text("provider") != "github"))
        {
            RepositoryGroup? match = null;
            string? kind = null;
            var mapped = source.Text("mappedRepository").Trim();
            if (IsRepository(mapped))
            {
                var direct = groups.Where(g => g.Host == "github.com" && g.Name.Equals(mapped, StringComparison.OrdinalIgnoreCase)).DistinctBy(g => g.Id).ToArray();
                if (direct.Length == 1)
                {
                    match = direct[0];
                    kind = "provider";
                }
                else if (direct.Length == 0)
                {
                    match = new($"repository:github.com/{mapped.ToLowerInvariant()}", mapped, "github.com", "", "");
                    kind = "provider";
                }
            }
            if (match is null)
            {
                var keys = new[] { source["repository"].Text("name"), source.Text("name") }
                    .Select(RepositoryMatchKey).Where(k => k.Length > 0).ToArray();
                var full = groups.Where(g => keys.Contains(g.FullKey)).DistinctBy(g => g.Id).ToArray();
                var candidates = full.Length > 0 ? full : groups.Where(g => keys.Contains(g.ShortKey)).DistinctBy(g => g.Id).ToArray();
                if (candidates.Length == 1)
                {
                    match = candidates[0];
                    kind = "name";
                }
            }
            source["groupId"] = match?.Id ?? $"source:{source.Text("id")}";
            source["groupName"] = match?.Name ?? Nonempty(source["repository"].Text("name"), source.Text("name"), "Health source");
            source["groupMatch"] = kind;
        }
        return sources;
    }

    internal static List<JsonObject> ApplyOrder(IEnumerable<JsonObject> items, IEnumerable<string> order)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, index) in order.Select((id, index) => (id, index)))
        {
            rank[id] = index;
        }
        return items.OrderBy(item => rank.GetValueOrDefault(item.Text("id"), int.MaxValue))
            .GroupBy(item => item.Text("groupId", $"source:{item.Text("id")}"))
            .SelectMany(group => group).ToList();
    }

    internal static JsonArray GitHubReasons(string state, IReadOnlyList<JsonObject> failedChecks, int streak,
        JsonObject? pr, string? lastSuccess, int examined, bool truncated, DateTimeOffset now)
    {
        var reasons = new JsonArray();
        if (state == "failing" && pr?["autoMerge"].Text("enabledAt").Length > 0
            && Regex.IsMatch(pr.Text("author"), @"^dependabot(?:\[bot\])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var reason = Reason("dependabot_auto_merge", "danger",
                $"The failing head is auto-merged Dependabot PR #{pr.Number("number")}; it is a likely regression source.");
            reason["url"] = pr["url"]?.DeepClone();
            reasons.Add(reason);
        }
        if (state == "failing" && failedChecks.Count > 0)
        {
            var names = failedChecks.Take(3).Select(c => c.Text("name")).ToArray();
            var suffix = failedChecks.Count > names.Length ? $" and {failedChecks.Count - names.Length} more" : "";
            reasons.Add(Reason("failing_checks", "danger", $"{failedChecks.Count} check{Plural(failedChecks.Count)} failing: {string.Join(", ", names)}{suffix}."));
        }
        else if (state == "failing")
        {
            reasons.Add(Reason("default_branch_failing", "danger", "The default branch check rollup is failing."));
        }
        else if (state == "running")
        {
            reasons.Add(Reason("checks_running", "warning", "Default-branch validation is still running or expected."));
        }
        else if (state == "unknown")
        {
            reasons.Add(Reason("checks_unknown", "muted", "No default-branch CI rollup is available."));
        }
        if (streak > 1)
        {
            reasons.Add(Reason("commit_failure_streak", "danger", $"{streak} consecutive default-branch commits have failing validation."));
        }
        if (state != "healthy" && !string.IsNullOrEmpty(lastSuccess))
        {
            var days = DaysSince(lastSuccess, now);
            reasons.Add(Reason("last_success_age", "warning", $"Last successful default-branch validation was {days} day{Plural(days)} ago."));
        }
        else if (state != "healthy" && examined > 0)
        {
            reasons.Add(Reason("no_success_found", "warning", truncated
                ? $"No successful validation was found in the first {examined} default-branch commits."
                : $"No successful validation was found across {examined} default-branch commit{Plural(examined)}."));
        }
        return reasons;
    }

    internal static bool IsRepository(string value) => Regex.IsMatch(value.Trim(), @"^([^/\s]+)/([^/\s]+)$", RegexOptions.CultureInvariant);
    internal static string RepositoryMatchKey(string value) =>
        Regex.Replace(Regex.Replace(value.Trim(), @"\.git$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).ToLowerInvariant(), "[^a-z0-9]+", "");
    internal static JsonNode Reason(string code, string tone, string summary) => new JsonObject { ["code"] = code, ["tone"] = tone, ["summary"] = summary };
    internal static int? DaysSince(string? value, DateTimeOffset now) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? Math.Max(0, (int)Math.Floor((now - date).TotalDays)) : null;
    internal static string Nonempty(params string[] values) => values.FirstOrDefault(s => s.Length > 0) ?? "";
    internal static string Plural(int? count) => count == 1 ? "" : "s";

    private static bool IsProviderFailure(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested && error is HealthProviderException or AzureDevOpsException or HttpRequestException
            or JsonException or InvalidOperationException or FormatException or TimeoutException or OperationCanceledException;

    private static string SafeGitHubError(Exception error, string token) =>
        error is HealthProviderException ? HealthText.Redact(error.Message).Replace(token, "[redacted]", StringComparison.Ordinal)
            : error is OperationCanceledException or TimeoutException ? "The GitHub health query timed out."
            : "The GitHub health query failed.";

    private static string GitHubHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        return host is "" or "api.github.com" ? "github.com" : host;
    }

    private static int StateRank(string state) => state switch
    {
        "failing" => 0,
        "degraded" => 1,
        "running" => 2,
        "unavailable" => 3,
        "healthy" => 5,
        _ => 4
    };

    private static void AppendHistory(List<JsonObject> history, JsonNode? nodes) =>
        history.AddRange(nodes.Objects().Where(n => n.Text("oid").Length > 0));
    private static JsonObject? FindSuccess(IEnumerable<JsonObject> history) =>
        history.FirstOrDefault(h => h["statusCheckRollup"].Text("state").Equals("SUCCESS", StringComparison.OrdinalIgnoreCase));
    private static string RollupState(string value) => value.ToUpperInvariant() switch
    {
        "SUCCESS" => "healthy",
        "FAILURE" or "ERROR" => "failing",
        "PENDING" or "EXPECTED" => "running",
        _ => "unknown"
    };

    private static IEnumerable<JsonObject> NormalizeChecks(IEnumerable<JsonObject> nodes)
    {
        foreach (var node in nodes)
        {
            var type = node.Text("__typename");
            if (type is not ("CheckRun" or "StatusContext"))
            {
                continue;
            }
            var status = node.Text("status").ToUpperInvariant();
            var conclusion = node.Text("conclusion").ToUpperInvariant();
            var state = type == "StatusContext" ? RollupState(node.Text("state"))
                : status.Length > 0 && status != "COMPLETED" ? "running"
                : conclusion switch
                {
                    "SUCCESS" or "NEUTRAL" or "SKIPPED" => "healthy",
                    "CANCELLED" => "degraded",
                    "FAILURE" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE" => "failing",
                    _ => "unknown"
                };
            yield return new JsonObject
            {
                ["name"] = type == "CheckRun" ? Nonempty(node.Text("name"), "Unnamed check") : Nonempty(node.Text("context"), "Unnamed status"),
                ["state"] = state,
                ["status"] = node[type == "CheckRun" ? "status" : "state"]?.DeepClone(),
                ["conclusion"] = node[type == "CheckRun" ? "conclusion" : "state"]?.DeepClone(),
                ["url"] = node[type == "CheckRun" ? "detailsUrl" : "targetUrl"]?.DeepClone(),
                ["startedAt"] = node[type == "CheckRun" ? "startedAt" : "createdAt"]?.DeepClone(),
                ["completedAt"] = node[type == "CheckRun" ? "completedAt" : "createdAt"]?.DeepClone()
            };
        }
    }

    private static JsonObject? SelectPullRequest(JsonNode? nodes)
    {
        var candidates = nodes.Objects().Where(p => p.Number("number") > 0 && p.Text("url").Length > 0).ToArray();
        var selected = candidates.FirstOrDefault(p => p.Text("mergedAt").Length > 0) ?? candidates.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }
        return new JsonObject
        {
            ["number"] = selected["number"]?.DeepClone(),
            ["url"] = selected["url"]?.DeepClone(),
            ["mergedAt"] = selected["mergedAt"]?.DeepClone(),
            ["author"] = selected["author"]?["login"]?.DeepClone(),
            ["autoMerge"] = selected["autoMergeRequest"] is JsonObject merge ? new JsonObject
            {
                ["enabledAt"] = merge["enabledAt"]?.DeepClone(),
                ["enabledBy"] = merge["enabledBy"]?["login"]?.DeepClone(),
                ["mergeMethod"] = merge["mergeMethod"]?.DeepClone()
            } : null
        };
    }

    private async Task<JsonObject> QueryAsync(string endpoint, string token, string query, JsonObject variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        request.Headers.UserAgent.ParseAdd("aspire-team-app");
        request.Content = new StringContent(new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HealthProviderException($"GitHub API {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject
            ?? throw new HealthProviderException("GitHub returned an invalid response.");
        if (json["errors"] is JsonArray { Count: > 0 } errors)
        {
            throw new HealthProviderException(string.Join("; ", errors.Objects().Select(e => e.Text("message"))));
        }
        return json["data"] as JsonObject ?? throw new HealthProviderException("GitHub returned no health data.");
    }

    private sealed record HistoryEntry(string Head, JsonObject[] Tail, bool Truncated);
    private sealed record RepositoryGroup(string Id, string Name, string Host, string FullKey, string ShortKey);
    private sealed class HealthProviderException(string message) : Exception(message);

    // Keep query aliases and connection shapes aligned with the original canvas provider.
    private const string InitialQuery = """
        query RepositoryHealth($owner:String!, $name:String!, $after:String) {
          repository(owner:$owner, name:$name) {
            nameWithOwner url
            defaultBranchRef {
              name
              head: target { ... on Commit {
                oid committedDate messageHeadline author { user { login } name }
                statusCheckRollup { state contexts(first:100) {
                  pageInfo { hasNextPage endCursor }
                  nodes {
                    __typename
                    ... on CheckRun { name status conclusion detailsUrl startedAt completedAt }
                    ... on StatusContext { context state targetUrl createdAt }
                  }
                } }
                associatedPullRequests(first:5) { nodes {
                  number url mergedAt author { login }
                  autoMergeRequest { enabledAt enabledBy { login } mergeMethod }
                } }
              } }
              historyTarget: target { ... on Commit {
                history(first:100, after:$after) {
                  pageInfo { hasNextPage endCursor }
                  nodes { oid committedDate statusCheckRollup { state } }
                }
              } }
            }
          }
        }
        """;

    private const string HistoryQuery = """
        query RepositoryHealthHistory($owner:String!, $name:String!, $after:String!) {
          repository(owner:$owner, name:$name) {
            defaultBranchRef { target { ... on Commit {
              history(first:100, after:$after) {
                pageInfo { hasNextPage endCursor }
                nodes { oid committedDate statusCheckRollup { state } }
              }
            } } }
          }
        }
        """;

    private const string ContextsQuery = """
        query RepositoryHealthContexts($owner:String!, $name:String!, $oid:GitObjectID!, $after:String!) {
          repository(owner:$owner, name:$name) {
            object(oid:$oid) { ... on Commit {
              oid
              statusCheckRollup { contexts(first:100, after:$after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  __typename
                  ... on CheckRun { name status conclusion detailsUrl startedAt completedAt }
                  ... on StatusContext { context state targetUrl createdAt }
                }
              } }
            } }
          }
        }
        """;
}
