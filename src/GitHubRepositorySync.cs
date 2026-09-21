// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHub.TeamApp;

internal sealed class GitHubRepositorySync(
    HttpClient http, ILogger<GitHubDashboard> logger, string directory, TimeProvider clock)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromHours(6);
    private readonly string cacheDirectory = Path.Combine(Path.GetFullPath(directory), "repository-items-v1");

    internal sealed record Snapshot(List<JsonObject> Records, bool IsPrivate, int TotalOpen);
    private sealed record Stored(Snapshot Snapshot, DateTimeOffset Cursor, DateTimeOffset ReconciledAt);
    private sealed record Change(int Number, bool PullRequest, DateTimeOffset UpdatedAt);

    internal async Task<Snapshot> LoadAsync(Account account, string repository, bool issues,
        CancellationToken ct, Action<SyncProgress>? progress, int limit = 200)
    {
        _ = PreferenceStore.MaxOpenItems(new JsonObject { ["maxOpenItems"] = limit });
        var host = GitHubDashboardTransport.NormalizeHost(account.Host);
        var repo = repository.Trim().ToLowerInvariant();
        var parts = repo.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 ||
            part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) ||
            parts.Any(part => part is "." or ".."))
        {
            throw new InvalidDataException($"Invalid repo \"{repository}\".");
        }
        var identity = new JsonObject
        {
            ["host"] = host, ["repository"] = repo, ["accountId"] = account.Id,
            ["login"] = account.Login.ToLowerInvariant(), ["kind"] = issues ? "issues" : "pullRequests",
            ["maxOpenItems"] = limit
        };
        var key = DashboardCache.Presentation(identity, [account]).ToJsonString();
        var path = Path.Combine(cacheDirectory, DashboardCache.IndexFileName(key));
        // Read, fetch, and commit inside the same gate, including across dashboard instances/scopes.
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var old = await ReadAsync(path, key, host, repo, issues, limit, ct).ConfigureAwait(false);
            var start = clock.GetUtcNow();
            DateTimeOffset? providerStart = null;
            var observedFirstResponse = false;
            var initial = old is null;
            var reconcile = initial || start - old!.ReconciledAt >= ReconcileInterval;
            var records = new Dictionary<int, JsonObject>();
            var isPrivate = old?.Snapshot.IsPrivate ?? false;
            var totalOpen = old?.Snapshot.TotalOpen ?? 0;
            List<JsonObject>? inventory = null;
            if (initial)
            {
                inventory = await InventoryAsync("Downloading open items").ConfigureAwait(false);
            }
            if (old is not null)
            {
                foreach (var node in old.Snapshot.Records)
                {
                    records.Add(node.Number("number"), (JsonObject)node.DeepClone());
                }
            }

            // Read the complete delta independently of the open-item window.
            var changes = await ChangesAsync(initial ? providerStart ?? start : old!.Cursor).ConfigureAwait(false);
            if (inventory is null || changes.Any(change => change.PullRequest != issues))
            {
                inventory = await InventoryAsync(issues ? "Refreshing issue inventory" : "Refreshing PR live state").ConfigureAwait(false);
            }
            var open = inventory.Select(node => node.Number("number")).ToHashSet();
            foreach (var number in records.Keys.Where(number => !open.Contains(number)).ToArray())
            {
                records.Remove(number);
            }
            if (!issues && !reconcile)
            {
                var boundaryItems = inventory.Where(node => !node.ContainsKey("commits") &&
                    records.TryGetValue(node.Number("number"), out var cached) &&
                    cached.Text("updatedAt") == node.Text("updatedAt")).ToArray();
                for (var offset = 0; offset < boundaryItems.Length; offset += 100)
                {
                    var batch = boundaryItems.Skip(offset).Take(100).ToArray();
                    var fields = string.Join("\n", batch.Select(node =>
                        $"item{node.Number("number")}:pullRequest(number:{node.Number("number")}) {{ {GitHubDashboard.PullRequestLiveSelection} }}"));
                    var response = Repository(await QueryAsync(
                        $"query BoundaryLiveState($owner:String!, $name:String!) {{ repository(owner:$owner, name:$name) {{ nameWithOwner isPrivate {fields} }} }}",
                        null).ConfigureAwait(false));
                    foreach (var metadata in batch)
                    {
                        var number = metadata.Number("number");
                        var node = response[$"item{number}"] as JsonObject ??
                            throw new InvalidDataException($"GitHub did not return live state for #{number}; the sync is incomplete.");
                        ValidateRecord(node, host, repo, issues: false, full: false);
                        if (node.Number("number") != number)
                        {
                            throw new InvalidDataException("GitHub returned a different item than requested.");
                        }
                        inventory[inventory.IndexOf(metadata)] = node;
                    }
                }
            }
            var hydrate = new HashSet<int>();
            foreach (var node in inventory)
            {
                var number = node.Number("number");
                if (reconcile || !records.TryGetValue(number, out var cached) ||
                    cached.Text("updatedAt") != node.Text("updatedAt") ||
                    !issues && (cached.Text("headRefOid") != node.Text("headRefOid") || !node.ContainsKey("commits")))
                {
                    hydrate.Add(number);
                }
            }
            foreach (var change in changes)
            {
                if (change.PullRequest != issues)
                {
                    if (open.Contains(change.Number) &&
                        (!records.TryGetValue(change.Number, out var cached) || cached.Date("updatedAt") < change.UpdatedAt))
                    {
                        hydrate.Add(change.Number);
                    }
                }
                else
                {
                    var connection = issues ? "closedByPullRequestsReferences" : "closingIssuesReferences";
                    foreach (var (number, node) in records)
                    {
                        if (node[connection]?["nodes"].Objects().Any(link => link.Number("number") == change.Number &&
                            link["repository"].Text("nameWithOwner").Equals(repo, StringComparison.OrdinalIgnoreCase)) == true)
                        {
                            hydrate.Add(number);
                        }
                    }
                }
            }

            var numbers = inventory.Where(node => hydrate.Contains(node.Number("number"))).Select(node => node.Number("number")).ToArray();
            for (var offset = 0; offset < numbers.Length; offset += 20)
            {
                var batch = numbers.Skip(offset).Take(20).ToArray();
                var selection = issues ? GitHubDashboard.IssueSelection : GitHubDashboard.PullRequestSelection;
                var fields = string.Join("\n", batch.Select(number =>
                    $"item{number}:{(issues ? "issue" : "pullRequest")}(number:{number}) {{ {selection} }}"));
                var body = await QueryAsync(
                    $"query($owner:String!, $name:String!) {{ repository(owner:$owner, name:$name) {{ nameWithOwner isPrivate {fields} }} }}",
                    null).ConfigureAwait(false);
                var result = Repository(body);
                foreach (var number in batch)
                {
                    if (result[$"item{number}"] is not JsonObject node)
                    {
                        throw new InvalidDataException($"GitHub did not return requested item #{number}; the sync is incomplete.");
                    }
                    ValidateRecord(node, host, repo, issues, full: true);
                    if (node.Number("number") != number)
                    {
                        throw new InvalidDataException("GitHub returned a different item than requested.");
                    }
                    if (node.Text("state") == "OPEN")
                    {
                        records[number] = node;
                    }
                    else
                    {
                        records.Remove(number);
                    }
                }
                progress?.Invoke(new("Updating changed items", Math.Min(offset + batch.Length, numbers.Length),
                    numbers.Length, issues ? "issues" : "PRs", initial));
            }

            if (!issues)
            {
                foreach (var node in inventory)
                {
                    // Hydrations run after the live pass and already contain fresher volatile fields.
                    if (!hydrate.Contains(node.Number("number")) && records.TryGetValue(node.Number("number"), out var cached))
                    {
                        foreach (var (field, value) in node)
                        {
                            cached[field] = value?.DeepClone();
                        }
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            foreach (var node in records.Values)
            {
                ValidateRecord(node, host, repo, issues, full: true);
            }
            var cursor = providerStart ?? start;
            var ordered = records.Values.OrderByDescending(node => node.Date("updatedAt"))
                .ThenByDescending(node => node.Number("number")).ToList();
            if (totalOpen < ordered.Count)
            {
                throw new InvalidDataException("GitHub's open-item inventory changed during pagination; refresh to retry.");
            }
            var envelope = new JsonObject
            {
                ["version"] = 2, ["key"] = key, ["cursor"] = cursor.ToString("O"),
                ["reconciledAt"] = (reconcile ? cursor : old!.ReconciledAt).ToString("O"),
                ["isPrivate"] = isPrivate, ["totalOpen"] = totalOpen, ["records"] = JsonData.Array(ordered)
            };
            await DashboardCache.WriteFileAsync(path, DashboardCache.Presentation(envelope, [account]), ct).ConfigureAwait(false);
            progress?.Invoke(new("Items synchronized", records.Count, records.Count, issues ? "issues" : "PRs", initial));
            return new(ordered, isPrivate, totalOpen);

            async Task<JsonObject> QueryAsync(string query, string? after, int first = 0)
            {
                using var request = GitHubDashboardTransport.Request(HttpMethod.Post,
                    GitHubDashboardTransport.GraphqlUrl(host), account.Token);
                request.Content = new StringContent(new JsonObject
                {
                    ["query"] = query,
                    ["variables"] = new JsonObject { ["owner"] = parts[0], ["name"] = parts[1], ["after"] = after, ["first"] = first }
                }.ToJsonString(), Encoding.UTF8, "application/json");
                var response = await SendAsync(request).ConfigureAwait(false) as JsonObject ??
                    throw new InvalidDataException("GitHub API returned no object.");
                if (response["errors"] is JsonArray errors && errors.Count > 0)
                {
                    throw new InvalidDataException(GitHubDashboardTransport.Redact(
                        string.Join("; ", errors.Objects().Select(error => error.Text("message", "GraphQL request failed"))),
                        account.Token));
                }
                return response;
            }

            JsonObject Repository(JsonObject response)
            {
                var result = response["data"]?["repository"] as JsonObject ??
                    throw new InvalidDataException("Repository is unavailable or this account does not have access.");
                if (!result.Text("nameWithOwner").Equals(repo, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("GitHub returned a different repository than requested.");
                }
                if (result["isPrivate"] is not null)
                {
                    isPrivate = result.Flag("isPrivate");
                }
                return result;
            }

            async Task<List<JsonObject>> InventoryAsync(string phase)
            {
                var result = new Dictionary<int, JsonObject>();
                var cursors = new HashSet<string>();
                var connectionName = issues ? "issues" : "pullRequests";
                string? after = null;
                DateTimeOffset? boundary = null;
                var boundaryScan = false;
                for (var page = 0; page < GitHubDashboard.MaxPages; page++)
                {
                    var first = boundaryScan ? 100 : Math.Min(100, limit - result.Count);
                    var query = InventoryQuery(issues, boundaryScan, phase == "Downloading open items");
                    var repositoryNode = Repository(await QueryAsync(query, after, first).ConfigureAwait(false));
                    if (repositoryNode[connectionName] is not JsonObject connection ||
                        connection["nodes"] is not JsonArray nodes ||
                        connection["pageInfo"] is not JsonObject pageInfo ||
                        pageInfo["hasNextPage"] is not JsonValue next || !next.TryGetValue<bool>(out var hasNext) ||
                        connection["totalCount"] is not JsonValue totalValue || !totalValue.TryGetValue<int>(out var total) || total < 0)
                    {
                        throw new InvalidDataException("GitHub returned an invalid repository connection or pagination metadata.");
                    }
                    if (nodes.Count > first || hasNext && nodes.Count == 0)
                    {
                        throw new InvalidDataException("GitHub returned an invalid inventory page size.");
                    }
                    totalOpen = total;
                    var pastBoundary = false;
                    foreach (var value in nodes)
                    {
                        var node = value as JsonObject ?? throw new InvalidDataException("GitHub returned a malformed item.");
                        ValidateRecord(node, host, repo, issues, full: false, metadataOnly: boundaryScan || issues || initial);
                        if (boundaryScan && node.Date("updatedAt") < boundary)
                        {
                            pastBoundary = true;
                            break;
                        }
                        result[node.Number("number")] = node;
                    }
                    progress?.Invoke(new(phase, Math.Min(result.Count, limit), Math.Min(total, limit), issues ? "issues" : "PRs", initial));
                    if (!hasNext || pastBoundary)
                    {
                        return result.Values.OrderByDescending(node => node.Date("updatedAt"))
                            .ThenByDescending(node => node.Number("number")).Take(limit).ToList();
                    }
                    if (!boundaryScan && result.Count >= limit)
                    {
                        // GitHub only supports one sort field. Read lightweight boundary ties before choosing by number.
                        boundary = result.Values.Min(node => node.Date("updatedAt"));
                        boundaryScan = true;
                    }
                    after = pageInfo.Text("endCursor");
                    if (after.Length == 0 || !cursors.Add(after))
                    {
                        throw new InvalidDataException("GitHub returned a missing or repeated pagination cursor; the queue is incomplete.");
                    }
                }
                throw new InvalidDataException($"Reached the {GitHubDashboard.MaxPages}-page safety limit; the queue is incomplete.");
            }

            async Task<List<Change>> ChangesAsync(DateTimeOffset since)
            {
                var changes = new Dictionary<int, Change>();
                var pages = new HashSet<string>();
                var sinceText = Uri.EscapeDataString(since.Subtract(Overlap).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                for (var page = 1; page <= GitHubDashboard.MaxPages; page++)
                {
                    // Construct every page locally. Link headers are never followed (including cross-host redirects).
                    const int pageSize = 100;
                    var url = $"{GitHubDashboardTransport.RestUrl(host)}/repos/{repo}/issues?state=all&since={sinceText}&sort=updated&direction=asc&per_page={pageSize}&page={page}";
                    using var request = GitHubDashboardTransport.Request(HttpMethod.Get, url, account.Token);
                    if (await SendAsync(request).ConfigureAwait(false) is not JsonArray nodes)
                    {
                        throw new InvalidDataException("GitHub returned an invalid changed-items response.");
                    }
                    if (nodes.Count > pageSize)
                    {
                        throw new InvalidDataException("GitHub returned an invalid changed-items page size.");
                    }
                    var signature = new StringBuilder();
                    foreach (var value in nodes)
                    {
                        var node = value as JsonObject ?? throw new InvalidDataException("GitHub returned a malformed changed item.");
                        var number = RequiredNumber(node);
                        var pr = node["pull_request"] is JsonObject;
                        if (node["pull_request"] is not null && !pr ||
                            node.Text("state") is not ("open" or "closed") || node.Date("updated_at") is null ||
                            !ItemUrl(node.Text("html_url"), host, repo, !pr, number) ||
                            !node.Text("repository_url").Equals($"{GitHubDashboardTransport.RestUrl(host)}/repos/{repo}",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("GitHub returned a malformed or foreign changed item.");
                        }
                        changes[number] = new(number, pr, node.Date("updated_at")!.Value);
                        signature.Append(number).Append(':').Append(node.Text("updated_at")).Append(':').Append(node.Text("state")).Append(';');
                    }
                    progress?.Invoke(new("Checking changes", changes.Count, null, "items", initial));
                    if (!pages.Add(signature.ToString()))
                    {
                        throw new InvalidDataException("GitHub repeated a changed-items page; the sync is incomplete.");
                    }
                    if (nodes.Count < pageSize)
                    {
                        return changes.Values.ToList();
                    }
                }
                throw new InvalidDataException($"Reached the {GitHubDashboard.MaxPages}-page changed-items safety limit; the sync is incomplete.");
            }

            async Task<JsonNode> SendAsync(HttpRequestMessage request)
            {
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is Uri final &&
                    (final.Scheme != request.RequestUri!.Scheme || final.Authority != request.RequestUri.Authority))
                {
                    throw new InvalidDataException("GitHub redirected the request outside its API host.");
                }
                if (!observedFirstResponse)
                {
                    providerStart = response.Headers.Date;
                    observedFirstResponse = true;
                }
                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                JsonNode? body;
                try
                {
                    body = JsonNode.Parse(raw);
                }
                catch (JsonException)
                {
                    throw new InvalidDataException($"GitHub API {(int)response.StatusCode} returned invalid JSON.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(GitHubDashboardTransport.Redact(
                        $"GitHub API {(int)response.StatusCode} {response.ReasonPhrase}: {(body as JsonObject).Text("message", "Request failed")}",
                        account.Token), null, response.StatusCode);
                }
                return body ?? throw new InvalidDataException("GitHub API returned an empty response.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("GitHub returned malformed item data.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Cannot access the repository item cache.", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Stored?> ReadAsync(string path, string key, string host, string repo, bool issues, int limit, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var envelope = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject;
            if (envelope is null || envelope.Number("version") != 2 || envelope.Text("key") != key ||
                envelope.Date("cursor") is not { } cursor || envelope.Date("reconciledAt") is not { } reconciledAt ||
                envelope["isPrivate"] is not JsonValue privacy || !privacy.TryGetValue<bool>(out var isPrivate) ||
                envelope["totalOpen"] is not JsonValue count || !count.TryGetValue<int>(out var totalOpen) || totalOpen < 0 ||
                envelope["records"] is not JsonArray nodes || nodes.Count > limit || nodes.Count > totalOpen)
            {
                throw new InvalidDataException("Invalid or mismatched repository item cache envelope.");
            }
            var records = new List<JsonObject>();
            var numbers = new HashSet<int>();
            foreach (var value in nodes)
            {
                var node = value as JsonObject ?? throw new InvalidDataException("Invalid cached item.");
                ValidateRecord(node, host, repo, issues, full: true);
                if (node.Text("state") != "OPEN" || !numbers.Add(node.Number("number")))
                {
                    throw new InvalidDataException("Invalid or duplicate cached open item.");
                }
                records.Add(node);
            }
            return new(new(records, isPrivate, totalOpen), cursor, reconciledAt);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            // Do not include cache contents or parser messages: private data/credentials may be present in a corrupt file.
            logger.LogWarning("Repository item cache for {Host}/{Repository} is corrupt or incompatible; rebuilding it.", host, repo);
            return null;
        }
    }

    private static int RequiredNumber(JsonObject node) =>
        node["number"] is JsonValue value && value.TryGetValue<int>(out var number) && number > 0
            ? number : throw new InvalidDataException("GitHub returned an item without a valid number.");

    private static void ValidateRecord(JsonObject node, string host, string repo, bool issues, bool full, bool metadataOnly = false)
    {
        ValidateFieldTypes(node);
        var number = RequiredNumber(node);
        if (!ItemUrl(node.Text("url"), host, repo, issues, number))
        {
            throw new InvalidDataException("GitHub returned an item outside the selected repository.");
        }
        if (node.Date("updatedAt") is null || full && node.Date("createdAt") is null)
        {
            throw new InvalidDataException("GitHub returned an item without valid createdAt/updatedAt timestamps.");
        }
        if (metadataOnly) { return; }
        if (full && (node.Text("state") is not ("OPEN" or "CLOSED" or "MERGED") ||
            issues && node.Text("state") == "MERGED" || node["title"] is not JsonValue))
        {
            throw new InvalidDataException("GitHub returned an item without a valid state or title.");
        }
        if (!issues && (node.Text("headRefOid").Length == 0 ||
            node.Text("mergeable") is not ("MERGEABLE" or "CONFLICTING" or "UNKNOWN") ||
            !node.ContainsKey("reviewDecision") || !node.ContainsKey("baseRef") ||
            node.Text("reviewDecision") is not ("" or "APPROVED" or "CHANGES_REQUESTED" or "REVIEW_REQUIRED")))
        {
            throw new InvalidDataException("GitHub returned incomplete PR live-state fields.");
        }
        var connections = issues
            ? new[] { "labels", "assignees", "closedByPullRequestsReferences" }
            : full
                ? ["labels", "assignees", "readyForReviewEvents", "closingIssuesReferences", "reviewRequests", "reviews", "reviewThreads", "commits"]
                : ["reviewRequests", "reviews", "reviewThreads", "commits"];
        foreach (var field in connections)
        {
            if (node[field] is not JsonObject connection || connection["nodes"] is not JsonArray nodes ||
                nodes.Any(value => value is not JsonObject))
            {
                throw new InvalidDataException($"GitHub returned an invalid {field} connection.");
            }
        }
        if (full)
        {
            // Exercise the same typed reads before committing a cursor, not afterwards during rendering.
            _ = issues ? GitHubDashboard.NormalizeIssue(repo, node, [])
                : GitHubDashboard.NormalizePr(repo, node, [], false);
        }
    }

    private static bool ItemUrl(string url, string host, string repo, bool issues, int number) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.Equals($"/{repo}/{(issues ? "issues" : "pull")}/{number}", StringComparison.OrdinalIgnoreCase);

    private static void ValidateFieldTypes(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (field, value) in obj)
            {
                if (value is null) { continue; }
                var valid = field switch
                {
                    "number" or "additions" or "deletions" or "changedFiles" or "totalCount" =>
                        value is JsonValue integer && integer.TryGetValue<int>(out _),
                    "isDraft" or "isResolved" or "requiresConversationResolution" =>
                        value is JsonValue boolean && boolean.TryGetValue<bool>(out _),
                    "state" or "title" or "url" or "name" or "nameWithOwner" or "login" or "__typename" or
                        "avatarUrl" or "baseRefName" or "headRefOid" or "mergeable" or "reviewDecision" =>
                        value is JsonValue text && text.TryGetValue<string>(out _),
                    "createdAt" or "updatedAt" or "submittedAt" or "committedDate" =>
                        value is JsonValue date && date.TryGetValue<string>(out var dateText) &&
                        DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
                    "nodes" => value is JsonArray,
                    _ => true
                };
                if (!valid)
                {
                    throw new InvalidDataException($"GitHub returned an invalid {field} field.");
                }
                ValidateFieldTypes(value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                if (value is not JsonObject)
                {
                    throw new InvalidDataException("GitHub returned a malformed nested item.");
                }
                ValidateFieldTypes(value);
            }
        }
    }

    private static string InventoryQuery(bool issues, bool boundary, bool initial) =>
        "query " + (boundary ? "WindowBoundary" : initial ? "InitialOpenItemInventory" : "OpenItemInventory") + """
        ($owner:String!, $name:String!, $after:String, $first:Int!) {
          repository(owner:$owner, name:$name) {
            nameWithOwner isPrivate
        """ + "\n" + (issues ? "issues" : "pullRequests") + """
            (states:OPEN, first:$first, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
              totalCount pageInfo { hasNextPage endCursor }
              nodes {
        """ + (issues || boundary || initial ? "number url updatedAt" : GitHubDashboard.PullRequestLiveSelection) + """
              }
            }
          }
        }
        """;
}
