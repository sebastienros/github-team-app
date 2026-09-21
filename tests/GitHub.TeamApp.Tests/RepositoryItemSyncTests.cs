// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GitHub.TeamApp.Tests;

public sealed class RepositoryItemSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BaselineCatchesUpThenRestartHydratesOnlyChangedItemsWithoutEtags()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        f.Issues[2] = Node(2);
        f.Delta.Add(Change(2));
        var first = await f.Load();
        Assert.True(!first["errors"].Strings().Any(), string.Join("; ", first["errors"].Strings()) +
            "\n" + string.Join("\n", f.Requests.Select(r => r.Query)));
        Assert.Equal(2, first["counts"].Number("issues"));
        Assert.Equal(["baseline", "delta", "live", "hydrate"], f.Requests.Select(r => r.Kind));
        Assert.Equal([1, 2], Hydrated(f));
        var stored = await f.Envelope();
        Assert.Equal(f.Now.ToString("O"), stored.Text("cursor"));
        Assert.Equal(f.Now.AddMinutes(-2), Since(f.Requests.Single(r => r.Kind == "delta")));

        f.ClearRequests();
        f.Now += TimeSpan.FromMinutes(5);
        f.Issues[2]["title"] = "Updated issue";
        f.Delta.Clear();
        f.Delta.Add(Change(2));
        var second = await f.Load(); // A new dashboard and sync instance, using only the on-disk state.
        Assert.Equal(["delta", "live", "hydrate"], f.Requests.Select(r => r.Kind));
        Assert.Equal([2], Hydrated(f));
        Assert.Equal(2, second["counts"].Number("issues"));
        Assert.Contains(Items(second), item => item.Text("title") == "Updated issue");
        Assert.Equal(DateTimeOffset.Parse(stored.Text("cursor")).AddMinutes(-2),
            Since(f.Requests.Single(r => r.Kind == "delta")));

        f.ClearRequests();
        f.Delta.Clear();
        var unchanged = await f.Load();
        Assert.Equal(["delta", "live"], f.Requests.Select(r => r.Kind));
        Assert.Equal(2, unchanged["counts"].Number("issues"));
        Assert.Empty(unchanged["errors"].Strings());
    }

    [Fact]
    public async Task ReviewAndShipShareRecordsAndRefreshLiveCiReviewsAndThreadsWithoutUpdatedAtChanges()
    {
        using var f = new Fixture();
        f.Prs[7] = Node(7, pr: true);
        var before = await f.Load("review");
        Assert.Equal("success", Items(before).Single().Text("checksState"));
        f.ClearRequests();
        var pr = f.Prs[7];
        var updatedAt = pr.Text("updatedAt");
        pr["commits"]!["nodes"]![0]!["commit"]!["statusCheckRollup"]!["state"] = "FAILURE";
        pr["reviewDecision"] = "CHANGES_REQUESTED";
        pr["mergeable"] = "CONFLICTING";
        pr["reviews"] = Connection(new JsonObject
        {
            ["state"] = "CHANGES_REQUESTED", ["author"] = new JsonObject { ["login"] = "reviewer" },
            ["submittedAt"] = f.Now.ToString("O")
        });
        pr["reviewThreads"] = Connection(new JsonObject { ["isResolved"] = false });
        pr["reviewRequests"] = Connection(new JsonObject { ["requestedReviewer"] = new JsonObject { ["login"] = "alice" } });
        var after = await f.Load("ship");
        Assert.Equal(["delta", "live"], f.Requests.Select(r => r.Kind));
        var item = Assert.Single(Items(after));
        Assert.Equal(updatedAt, item.Text("updatedAt"));
        Assert.Equal("failure", item.Text("checksState"));
        Assert.Equal("CONFLICTING", item.Text("mergeable"));
        Assert.Equal("CHANGES_REQUESTED", item.Text("reviewDecision"));
        Assert.Equal(1, item.Number("unresolvedThreadCount"));
        Assert.Equal(["alice"], item["requestedReviewers"].Strings());
        Assert.Single(Directory.GetFiles(f.CacheDirectory, "*.json"));
        Assert.Empty(after["errors"].Strings());
    }

    [Fact]
    public async Task ClosedReopenedAndNewIssuesPatchTheBaseline()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        f.Issues[2] = Node(2);
        await f.Load();
        f.ClearRequests();
        f.Delta.Add(Change(1, open: false));
        f.Issues[1]["state"] = "CLOSED";
        f.Delta.Add(Change(3));
        f.Issues[3] = Node(3);
        var closed = await f.Load();
        Assert.Equal([2, 3], Items(closed).Select(i => i.Number("number")).Order());
        Assert.Equal([3], Hydrated(f));
        f.ClearRequests();
        f.Delta.Clear();
        f.Delta.Add(Change(1));
        f.Issues[1]["state"] = "OPEN";
        var reopened = await f.Load();
        Assert.Equal([1, 2, 3], Items(reopened).Select(i => i.Number("number")).Order());
        Assert.Equal([1], Hydrated(f));
    }

    [Fact]
    public async Task OpenPrInventoryPrunesDeletionsAndHydratesNewOrChangedHeads()
    {
        using var f = new Fixture();
        f.Prs[1] = Node(1, pr: true);
        f.Prs[2] = Node(2, pr: true);
        await f.Load("ship");
        f.ClearRequests();
        f.Prs.Remove(1);
        f.Prs[2]["headRefOid"] = "new-head";
        f.Prs[3] = Node(3, pr: true);
        var result = await f.Load("ship");
        Assert.Equal([2, 3], Items(result).Select(i => i.Number("number")).Order());
        Assert.Equal([2, 3], Hydrated(f));
        Assert.DoesNotContain(f.Requests, r => r.Kind == "baseline");
    }

    [Fact]
    public async Task HydrationKeepsFresherCiThanEarlierLivePassEvenWithSameTimestampAndHead()
    {
        using var f = new Fixture();
        f.Prs[1] = Node(1, pr: true);
        await f.Load("ship");
        f.Delta.Add(Change(1, pr: true));
        f.BeforeSend = request =>
        {
            if (request.Kind == "hydrate")
            {
                f.Prs[1]["commits"]!["nodes"]![0]!["commit"]!["statusCheckRollup"]!["state"] = "FAILURE";
            }
            return Task.CompletedTask;
        };
        var result = await f.Load("ship");
        Assert.Equal("failure", Items(result).Single().Text("checksState"));
        Assert.Equal("FAILURE", (await f.Envelope())["records"]![0]!["commits"]!["nodes"]![0]!["commit"]!["statusCheckRollup"].Text("state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OppositeKindChangesRefreshCachedLinkedDetails(bool issues)
    {
        using var f = new Fixture();
        var record = Node(1, pr: !issues);
        record[issues ? "closedByPullRequestsReferences" : "closingIssuesReferences"] = Connection(new JsonObject
        {
            ["number"] = 9, ["title"] = "Linked", ["url"] = $"https://github.com/owner/repo/{(issues ? "pull" : "issues")}/9",
            ["repository"] = new JsonObject { ["nameWithOwner"] = "owner/repo" }, ["state"] = "OPEN",
            ["milestone"] = null, ["labels"] = Connection()
        });
        (issues ? f.Issues : f.Prs)[1] = record;
        await f.Load(issues ? "issues" : "ship");
        f.ClearRequests();
        f.Delta.Add(Change(9, pr: issues, open: false));
        await f.Load(issues ? "issues" : "ship");
        Assert.Equal([1], Hydrated(f));
    }

    [Fact]
    public async Task EqualTimestampOverlapAndLocallyConstructedRestPaginationDoNotTrustLinks()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        await f.Load();
        var cursor = f.Now;
        f.ClearRequests();
        f.Now += TimeSpan.FromSeconds(1);
        for (var number = 2; number <= 102; number++)
        {
            var change = Change(number, open: false);
            change["updated_at"] = cursor.ToString("O");
            f.Delta.Add(change);
        }
        f.Link = "<https://attacker.example/steal>; rel=\"next\"";
        var result = await f.Load();
        var requests = f.Requests.Where(r => r.Kind == "delta").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, r =>
        {
            Assert.Equal("api.github.com", r.Uri.Host);
            Assert.Equal(cursor.AddMinutes(-2), Since(r));
            Assert.Contains("state=all", r.Uri.Query);
            Assert.Contains("sort=updated&direction=asc&per_page=100", r.Uri.Query);
        });
        Assert.Contains("page=2", requests[1].Uri.Query);
        Assert.Equal(1, result["counts"].Number("issues"));
        Assert.Empty(result["errors"].Strings());
    }

    [Fact]
    public async Task LargeBaselineBeyondOldLimitReportsActualItemCountsAndTotal()
    {
        using var f = new Fixture();
        for (var number = 1; number <= 1041; number++)
        {
            f.Issues[number] = Node(number);
        }
        var progress = new List<SyncProgress>();
        var result = await f.Load(progress: progress.Add, limit: 1041);
        Assert.Equal(1041, result["counts"].Number("issues"));
        Assert.Equal(11, f.Requests.Count(r => r.Kind == "baseline"));
        var pages = progress.Where(p => p.Phase == "Downloading open items").ToArray();
        Assert.Equal(11, pages.Length);
        Assert.All(pages, p => { Assert.Equal(1041, p.Total); Assert.True(p.Initial); Assert.Equal("issues", p.Unit); });
        Assert.Equal(100, pages[0].Done);
        Assert.Equal(1041, pages[^1].Done);
        Assert.Equal(1041, (await f.Envelope())["records"]!.AsArray().Count);
        Assert.Empty(result["errors"].Strings());
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("hydrate")]
    [InlineData("live")]
    [InlineData("cancel")]
    [InlineData("malformed")]
    public async Task FailedOrCancelledRefreshPreservesExactDiskRecordsAndCursor(string failure)
    {
        using var f = new Fixture();
        f.Prs[1] = Node(1, pr: true);
        await f.Load("ship");
        var previous = await File.ReadAllTextAsync(f.CacheFile, Ct);
        f.Delta.Add(Change(1, pr: true));
        f.Now += TimeSpan.FromMinutes(3);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Override = request =>
        {
            if (failure == "cancel" && request.Kind == "hydrate")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            if (request.Kind == (failure == "rest" ? "delta" : failure))
            {
                return Json(new JsonObject { ["message"] = "unavailable" }, HttpStatusCode.ServiceUnavailable);
            }
            return null;
        };
        if (failure == "malformed")
        {
            f.Prs[1]["reviews"] = Connection(new JsonObject { ["state"] = 42 });
        }
        if (failure == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Load("ship", ct: cancellation.Token));
        }
        else
        {
            var failed = await f.Load("ship");
            Assert.NotEmpty(failed["errors"].Strings());
        }
        Assert.Equal(previous, await File.ReadAllTextAsync(f.CacheFile, Ct));
        Assert.Empty(Directory.GetFiles(f.CacheDirectory, "*.tmp"));
        f.Override = null;
        f.Prs[1] = Node(1, pr: true);
        Assert.Empty((await f.Load("ship"))["errors"].Strings());
        Assert.NotEqual(previous, await File.ReadAllTextAsync(f.CacheFile, Ct));
    }

    [Fact]
    public async Task FailedBaselineCatchupNeverCreatesACompleteCache()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        f.Override = request => request.Kind == "delta" ? Json(new JsonArray(), HttpStatusCode.ServiceUnavailable) : null;
        Assert.NotEmpty((await f.Load())["errors"].Strings());
        Assert.False(Directory.Exists(f.CacheDirectory));
        f.Override = null;
        Assert.Empty((await f.Load())["errors"].Strings());
        Assert.True(File.Exists(f.CacheFile));
    }

    [Theory]
    [InlineData("rest-page")]
    [InlineData("hydrate-batch")]
    public async Task FailureAfterCompletedPagesOrBatchesDoesNotCommitPartialChanges(string failure)
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        await f.Load();
        var previous = await File.ReadAllTextAsync(f.CacheFile, Ct);
        f.ClearRequests();
        f.Now += TimeSpan.FromMinutes(2);
        for (var number = 2; number <= 102; number++)
        {
            f.Issues[number] = Node(number);
            f.Delta.Add(Change(number));
        }
        f.Override = request =>
            failure == "rest-page" && request.Kind == "delta" && request.Uri.Query.EndsWith("page=2", StringComparison.Ordinal) ||
            failure == "hydrate-batch" && request.Kind == "hydrate" && request.Query.Contains("item22:", StringComparison.Ordinal)
                ? Json(new JsonObject { ["message"] = "later request failed" }, HttpStatusCode.ServiceUnavailable) : null;
        Assert.NotEmpty((await f.Load())["errors"].Strings());
        Assert.Equal(previous, await File.ReadAllTextAsync(f.CacheFile, Ct));
        Assert.True(f.Requests.Count(r => r.Kind == (failure == "rest-page" ? "delta" : "hydrate")) >= 2);
        f.Override = null;
        var retry = await f.Load();
        Assert.Equal(102, retry["counts"].Number("issues"));
        Assert.Empty(retry["errors"].Strings());
    }

    [Theory]
    [InlineData("baseline-cursor")]
    [InlineData("rest-page")]
    public async Task RepeatedPaginationIsExplicitAndNeverCreatesCache(string failure)
    {
        using var f = new Fixture();
        f.Override = request =>
        {
            if (failure == "baseline-cursor" && request.Kind == "baseline")
            {
                return Json(Repository(new JsonObject
                {
                    ["nameWithOwner"] = "owner/repo",
                    ["issues"] = new JsonObject
                    {
                        ["nodes"] = JsonData.Array([Node(1)]), ["totalCount"] = 2,
                        ["pageInfo"] = new JsonObject { ["hasNextPage"] = true, ["endCursor"] = "repeated" }
                    }
                }));
            }
            if (failure == "rest-page" && request.Kind == "delta")
            {
                return Json(JsonData.Array(Enumerable.Range(1, 100).Select(n => Change(n, open: false))));
            }
            return null;
        };
        var result = await f.Load();
        Assert.Contains("repeat", Assert.Single(result["errors"].Strings()), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(f.CacheDirectory));
    }

    [Fact]
    public async Task LiveInventoryPagesBeyondOneHundredAndRejectsMissingVolatileFields()
    {
        using var f = new Fixture();
        for (var number = 1; number <= 101; number++) { f.Prs[number] = Node(number, pr: true); }
        await f.Load("ship");
        f.ClearRequests();
        Assert.Empty((await f.Load("ship"))["errors"].Strings());
        Assert.Equal(2, f.Requests.Count(r => r.Kind == "live"));
        Assert.Empty(Hydrated(f));
        var previous = await File.ReadAllTextAsync(f.CacheFile, Ct);
        f.Prs[1].Remove("mergeable");
        Assert.NotEmpty((await f.Load("ship"))["errors"].Strings());
        Assert.Equal(previous, await File.ReadAllTextAsync(f.CacheFile, Ct));
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("records")]
    public async Task CorruptOrMismatchedCacheWarnsAndRebuilds(string corruption)
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        await f.Load();
        var envelope = await f.Envelope();
        if (corruption == "key") { envelope["key"] = "other-account"; }
        if (corruption == "version") { envelope["version"] = 50; }
        if (corruption == "records") { envelope["records"] = new JsonArray(1); }
        await File.WriteAllTextAsync(f.CacheFile, corruption == "invalid-json" ? "bad json" : envelope.ToJsonString(), Ct);
        f.ClearRequests();
        var result = await f.Load();
        Assert.Contains(f.Logger.Messages, m => m.Contains("corrupt or incompatible", StringComparison.Ordinal));
        Assert.Contains(f.Requests, r => r.Kind == "baseline");
        Assert.Equal(1, result["counts"].Number("issues"));
        Assert.Empty(result["errors"].Strings());
    }

    [Fact]
    public async Task CacheIsPrivateVersionedSanitizedAndIndependentOfPresentationPreferences()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        f.Issues[1]["title"] = "Private " + Fixture.Secret;
        f.Issues[1]["credential"] = Fixture.Secret;
        await f.Load();
        var text = await File.ReadAllTextAsync(f.CacheFile, Ct);
        Assert.DoesNotContain(Fixture.Secret, text);
        Assert.DoesNotContain("\"credential\"", text);
        Assert.Contains("[redacted]", text);
        Assert.StartsWith("fnv1a-", Path.GetFileName(f.CacheFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f.CacheFile));
        }
        f.ClearRequests();
        var prefs = new JsonObject { ["mode"] = "issues", ["release"] = "other", ["showDrafts"] = true, ["reviewLimit"] = 1 };
        var dashboard = new GitHubDashboard(f.Http, f.Logger, f.Directory, f.Clock);
        await dashboard.LoadAsync([f.Account], prefs, Ct);
        Assert.DoesNotContain(f.Requests, r => r.Kind == "baseline");
        Assert.Single(Directory.GetFiles(f.CacheDirectory, "*.json"));
        Assert.Equal(2, (await f.Envelope()).Number("version"));
    }

    [Fact]
    public async Task HostAndAccountIdentityAndItemKindHaveSeparateStores()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        f.Prs[2] = Node(2, pr: true);
        await f.Load();
        await f.Load("ship");
        await f.Load(account: f.Account with { Id = "other", Login = "other" });
        await f.Load(account: f.Account with { Host = "ghe.example.com", Id = "enterprise" });
        Assert.Equal(4, Directory.GetFiles(f.CacheDirectory, "*.json").Length);
        Assert.Equal(4, f.Requests.Count(r => r.Kind == "baseline"));
        Assert.Contains(f.Requests, r => r.Uri.AbsoluteUri.StartsWith("https://ghe.example.com/api/v3/repos/owner/repo/issues?", StringComparison.Ordinal));
        Assert.Contains(f.Requests, r => r.Uri.AbsoluteUri == "https://ghe.example.com/api/graphql");
        Assert.All(f.Requests, r => Assert.Equal(Fixture.Secret, r.Token));
    }

    [Fact]
    public async Task SameKeyAcrossInstancesSerializesEntireReadFetchCommit()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        await f.Load();
        f.ClearRequests();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        f.BeforeSend = async request =>
        {
            if (request.Kind == "delta" && Interlocked.Increment(ref reads) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(Ct);
            }
        };
        var first = f.Load();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var second = f.Load();
        Assert.Equal(1, reads);
        Assert.False(second.IsCompleted);
        f.Now += TimeSpan.FromMinutes(1);
        release.SetResult();
        await first;
        await second;
        Assert.Equal(2, reads);
        Assert.Single((await f.Envelope())["records"]!.AsArray());
        Assert.Equal(f.Now.AddMinutes(-2), Since(f.Requests.Last(r => r.Kind == "delta")));
    }

    [Fact]
    public async Task SixHourReconciliationRemovesDeletedIssues()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        f.Issues[2] = Node(2);
        await f.Load();
        f.ClearRequests();
        f.Issues.Remove(1);
        f.Now += TimeSpan.FromHours(6);
        var result = await f.Load();
        Assert.Contains(f.Requests, r => r.Kind == "live");
        Assert.Equal([2], Items(result).Select(i => i.Number("number")));
        Assert.Equal(f.Now.ToString("O"), (await f.Envelope()).Text("reconciledAt"));
    }

    [Fact]
    public async Task FirstResponseProviderDateWinsAndClockFallbackIsSyncStartNotFinish()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        var start = f.Now;
        f.ServerDate = start.AddMinutes(-30);
        f.BeforeSend = request =>
        {
            if (request.Kind == "delta") { f.Now += TimeSpan.FromMinutes(10); }
            return Task.CompletedTask;
        };
        await f.Load();
        Assert.Equal(f.ServerDate.Value.ToString("O"), (await f.Envelope()).Text("cursor"));
        Assert.Equal(f.ServerDate.Value.AddMinutes(-2), Since(f.Requests.Single(r => r.Kind == "delta")));
        f.ServerDate = null;
        f.OmitDate = true;
        start = f.Now;
        await f.Load();
        Assert.Equal(start.ToString("O"), (await f.Envelope()).Text("cursor"));
    }

    [Theory]
    [InlineData("foreign-rest")]
    [InlineData("foreign-node")]
    [InlineData("foreign-repository")]
    [InlineData("null-node")]
    [InlineData("graphql-error")]
    [InlineData("missing-hydration")]
    public async Task ForeignMalformedAndPartialProviderResponsesCannotAdvanceCursor(string failure)
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        await f.Load();
        var before = await File.ReadAllTextAsync(f.CacheFile, Ct);
        f.Delta.Add(Change(1));
        f.Now += TimeSpan.FromMinutes(1);
        if (failure == "foreign-rest") { f.Delta[0]["repository_url"] = "https://api.github.com/repos/foreign/repo"; }
        if (failure == "foreign-node") { f.Issues[1]["url"] = "https://attacker.example/owner/repo/issues/1"; }
        f.Override = request =>
        {
            if (request.Kind != "hydrate") { return null; }
            return failure switch
            {
                "graphql-error" => Json(new JsonObject
                {
                    ["data"] = new JsonObject(),
                    ["errors"] = JsonData.Array([new JsonObject { ["message"] = "failed" }])
                }),
                "foreign-repository" => Json(Repository(new JsonObject { ["nameWithOwner"] = "foreign/repo" })),
                "null-node" => Json(Repository(new JsonObject { ["nameWithOwner"] = "owner/repo", ["item1"] = null })),
                "missing-hydration" => Json(Repository(new JsonObject { ["nameWithOwner"] = "owner/repo" })),
                _ => null
            };
        };
        Assert.NotEmpty((await f.Load())["errors"].Strings());
        Assert.Equal(before, await File.ReadAllTextAsync(f.CacheFile, Ct));
    }

    [Theory]
    [InlineData("issues")]
    [InlineData("ship")]
    [InlineData("review")]
    public async Task DefaultWindowHydratesExactlyNewestTwoHundredAndReportsRealTotal(string mode)
    {
        using var f = new Fixture();
        var source = mode == "issues" ? f.Issues : f.Prs;
        for (var number = 1; number <= 201; number++)
        {
            source[number] = RecentNode(number, mode != "issues");
        }
        var progress = new List<SyncProgress>();
        var result = await f.Load(mode, progress.Add);
        Assert.Empty(result["errors"].Strings());
        Assert.Equal(200, result["itemScope"].Number("limit"));
        Assert.Equal(200, result["itemScope"].Number("loaded"));
        Assert.Equal(201, result["itemScope"].Number("totalOpen"));
        Assert.True(result["itemScope"].Flag("limited"));
        Assert.Equal(Enumerable.Range(2, 200), Hydrated(f));
        Assert.Equal([100, 100], f.Requests.Where(r => r.Kind == "baseline").Select(r => r.First));
        Assert.All(f.Requests.Where(r => r.Kind == "baseline"), request =>
            Assert.DoesNotContain("reviews", request.Query, StringComparison.Ordinal));
        Assert.All(f.Requests.Where(r => r.Kind is "baseline" or "live" or "boundary"), request =>
        {
            Assert.Contains("first:$first", request.Query, StringComparison.Ordinal);
            Assert.Contains("field:UPDATED_AT, direction:DESC", request.Query, StringComparison.Ordinal);
            Assert.InRange(request.First, 1, 100);
        });
        Assert.All(f.Requests.Where(r => r.Kind == "boundary"), request =>
        {
            Assert.DoesNotContain("title", request.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("reviews", request.Query, StringComparison.Ordinal);
        });
        Assert.All(progress.Where(p => p.Total.HasValue), p => Assert.InRange(p.Total!.Value, 0, 200));
        Assert.Equal(201, (await f.Envelope()).Number("totalOpen"));
        Assert.Equal(Enumerable.Range(2, 200).Reverse(),
            (await f.Envelope())["records"].Objects().Select(n => n.Number("number")));
        foreach (var lane in result["lanes"].Objects())
        {
            var numbers = lane["items"].Objects().Select(card => (card["issue"] ?? card["pr"]).Number("number")).ToArray();
            Assert.Equal(numbers.OrderDescending(), numbers);
        }

        f.ClearRequests();
        var warm = await f.Load(mode);
        Assert.Empty(warm["errors"].Strings());
        Assert.Empty(Hydrated(f));
        Assert.Equal(result["itemScope"]!.ToJsonString(), warm["itemScope"]!.ToJsonString());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(101, 1)]
    [InlineData(201, 1)]
    [InlineData(240, 40)]
    public async Task RequestedWindowSizesBoundTheLastInventoryPageAndEveryHydration(int limit, int lastPage)
    {
        using var f = new Fixture();
        for (var number = 1; number <= 241; number++) { f.Issues[number] = RecentNode(number); }
        var result = await f.Load(limit: limit);
        Assert.Empty(result["errors"].Strings());
        Assert.Equal(limit, result["itemScope"].Number("loaded"));
        Assert.Equal(limit, Hydrated(f).Length);
        Assert.Equal(Enumerable.Range(242 - limit, limit), Hydrated(f));
        Assert.Equal(lastPage, f.Requests.Last(r => r.Kind == "baseline").First);
        Assert.Equal(limit, f.Requests.Where(r => r.Kind == "baseline").Sum(r => r.First));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundaryTimestampTiesSelectHighestNumbersBeforeFullHydration(bool pr)
    {
        using var f = new Fixture();
        var source = pr ? f.Prs : f.Issues;
        for (var number = 1; number <= 5; number++) { source[number] = Node(number, pr); }
        var mode = pr ? "ship" : "issues";
        var result = await f.Load(mode, limit: 2);
        Assert.Empty(result["errors"].Strings());
        Assert.Equal([4, 5], Hydrated(f));
        Assert.Equal([5, 4], Items(result).Select(n => n.Number("number")));
        Assert.Contains(f.Requests, r => r.Kind == "boundary");
        f.ClearRequests();
        var warm = await f.Load(mode, limit: 2);
        Assert.Empty(warm["errors"].Strings());
        Assert.Empty(Hydrated(f));
        Assert.Equal([5, 4], Items(warm).Select(n => n.Number("number")));
        if (pr) { Assert.Single(f.Requests, r => r.Kind == "boundary-live"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmWindowAdmitsNewerItemsDropsOlderOnesAndRefillsWhenItemsDisappear(bool pr)
    {
        using var f = new Fixture();
        var source = pr ? f.Prs : f.Issues;
        var mode = pr ? "ship" : "issues";
        for (var number = 1; number <= 5; number++) { source[number] = RecentNode(number, pr); }
        Assert.Empty((await f.Load(mode, limit: 2))["errors"].Strings());
        f.ClearRequests();
        source[1]["updatedAt"] = f.Now.ToString("O");
        f.Delta.Add(Change(1, pr));
        var newer = await f.Load(mode, limit: 2);
        Assert.Empty(newer["errors"].Strings());
        Assert.Equal([1, 5], Items(newer).Select(n => n.Number("number")));
        Assert.Equal([1], Hydrated(f));
        f.ClearRequests();
        f.Delta.Clear();
        source.Remove(1); // Deletion/transfer/closure need not appear in the REST feed.
        var refilled = await f.Load(mode, limit: 2);
        Assert.Empty(refilled["errors"].Strings());
        Assert.Equal([5, 4], Items(refilled).Select(n => n.Number("number")));
        Assert.Equal([4], Hydrated(f));
        Assert.Equal(4, refilled["itemScope"].Number("totalOpen"));
    }

    [Fact]
    public async Task CompleteDeltaIsIndependentOfWindowAndHydratesOnlyChangedSelectedItems()
    {
        using var f = new Fixture();
        for (var number = 1; number <= 1000; number++) { f.Issues[number] = RecentNode(number); }
        await f.Load(limit: 15);
        f.ClearRequests();
        f.Delta.Add(Change(1));
        var historical = await f.Load(limit: 15);
        Assert.Empty(historical["errors"].Strings());
        Assert.Empty(Hydrated(f));
        f.ClearRequests();
        for (var number = 2; number <= 250; number++) { f.Delta.Add(Change(number)); }
        f.Delta.Add(Change(1000));
        f.Issues[1000]["title"] = "Changed without a newer timestamp";
        var progress = new List<SyncProgress>();
        var refreshed = await f.Load(progress: progress.Add, limit: 15);
        Assert.Empty(refreshed["errors"].Strings());
        Assert.Equal(3, f.Requests.Count(r => r.Kind == "delta"));
        Assert.All(f.Requests.Where(r => r.Kind == "delta"), r => Assert.Contains("per_page=100", r.Uri.Query));
        Assert.Equal([1000], Hydrated(f));
        Assert.Contains(Items(refreshed), n => n.Text("title") == "Changed without a newer timestamp");
        Assert.Equal(15, refreshed["itemScope"].Number("loaded"));
        Assert.Equal([100, 200, 251], progress.Where(p => p.Phase == "Checking changes").Select(p => p.Done));
        Assert.All(progress.Where(p => p.Phase == "Checking changes"), p => Assert.Null(p.Total));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingAlreadyCachedChangesDoNotRehydrateAndEmptySyncPersistsTimestamp(bool pr)
    {
        using var f = new Fixture();
        var mode = pr ? "ship" : "issues";
        var source = pr ? f.Prs : f.Issues;
        source[1] = Node(1, pr);
        source[1]["updatedAt"] = f.Now.ToString("O");
        await f.Load(mode, limit: 1);
        var initial = await f.Envelope();
        f.ClearRequests();
        f.Now += TimeSpan.FromMinutes(1);
        f.Delta.Add(Change(1, pr));
        Assert.Empty((await f.Load(mode, limit: 1))["errors"].Strings());
        Assert.Empty(Hydrated(f));
        Assert.Equal(f.Now.ToString("O"), (await f.Envelope()).Text("cursor"));

        f.ClearRequests();
        f.Now += TimeSpan.FromMinutes(1);
        source[1]["title"] = "Updated old item";
        source[1]["updatedAt"] = f.Now.ToString("O");
        f.Delta[0]["updated_at"] = f.Now.ToString("O");
        var updated = await f.Load(mode, limit: 1);
        Assert.Empty(updated["errors"].Strings());
        Assert.Equal([1], Hydrated(f));
        Assert.Equal("Updated old item", Assert.Single(Items(updated)).Text("title"));
        var previous = await f.Envelope();

        f.ClearRequests();
        f.Now += TimeSpan.FromMinutes(1);
        f.Delta.Clear();
        Assert.Empty((await f.Load(mode, limit: 1))["errors"].Strings());
        Assert.Empty(Hydrated(f));
        Assert.Equal(previous.Date("cursor")!.Value.AddMinutes(-2), Since(f.Requests.Single(r => r.Kind == "delta")));
        var current = await f.Envelope();
        Assert.Equal(f.Now.ToString("O"), current.Text("cursor"));
        Assert.Equal(initial.Text("reconciledAt"), current.Text("reconciledAt"));

        f.ClearRequests();
        await f.Load(mode, limit: 1);
        Assert.Equal(current.Date("cursor")!.Value.AddMinutes(-2), Since(f.Requests.Single(r => r.Kind == "delta")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedChangesBeyondWindowAndFullRestPageAreNotSkipped(bool pr)
    {
        using var f = new Fixture();
        var mode = pr ? "ship" : "issues";
        var record = Node(1, pr);
        record[pr ? "closingIssuesReferences" : "closedByPullRequestsReferences"] = Connection(new JsonObject
        {
            ["number"] = 999, ["title"] = "Linked item",
            ["url"] = $"https://github.com/owner/repo/{(pr ? "issues" : "pull")}/999",
            ["repository"] = new JsonObject { ["nameWithOwner"] = "owner/repo" },
            ["state"] = "OPEN", ["milestone"] = null, ["labels"] = Connection()
        });
        (pr ? f.Prs : f.Issues)[1] = record;
        await f.Load(mode, limit: 1);
        f.ClearRequests();
        f.Delta.AddRange(Enumerable.Range(2, 199).Select(n => Change(n, pr, open: false)));
        f.Delta.Add(Change(999, !pr, open: false));
        var result = await f.Load(mode, limit: 1);
        Assert.Empty(result["errors"].Strings());
        Assert.Equal(3, f.Requests.Count(r => r.Kind == "delta")); // Includes the terminal empty page.
        Assert.Equal([1], Hydrated(f));
        Assert.Single(Items(result));
    }

    [Fact]
    public async Task FailureBeyondWindowCapPreservesCursorUntilEveryDeltaPageSucceeds()
    {
        using var f = new Fixture();
        f.Issues[1] = Node(1);
        await f.Load(limit: 1);
        var previous = await File.ReadAllTextAsync(f.CacheFile, Ct);
        f.Now += TimeSpan.FromMinutes(5);
        f.Delta.AddRange(Enumerable.Range(2, 201).Select(n => Change(n, open: false)));
        f.Override = request => request.Kind == "delta" && request.Uri.Query.EndsWith("page=3", StringComparison.Ordinal)
            ? Json(new JsonObject { ["message"] = "later page failed" }, HttpStatusCode.ServiceUnavailable) : null;
        Assert.NotEmpty((await f.Load(limit: 1))["errors"].Strings());
        Assert.Equal(previous, await File.ReadAllTextAsync(f.CacheFile, Ct));

        f.Override = null;
        f.ClearRequests();
        Assert.Empty((await f.Load(limit: 1))["errors"].Strings());
        Assert.Equal(3, f.Requests.Count(r => r.Kind == "delta"));
        Assert.Empty(Hydrated(f));
        Assert.Equal(f.Now.ToString("O"), (await f.Envelope()).Text("cursor"));
    }

    [Fact]
    public async Task ChangedLimitsHaveIsolatedCachesAndOldUnboundedKeysCannotBypassDefault()
    {
        using var f = new Fixture();
        for (var number = 1; number <= 205; number++) { f.Issues[number] = RecentNode(number); }
        await f.Load();
        var currentFile = f.CacheFile;
        var old = await f.Envelope();
        var identity = JsonNode.Parse(old.Text("key"))!.AsObject();
        identity.Remove("maxOpenItems");
        old["key"] = identity.ToJsonString();
        old["version"] = 1;
        old.Remove("totalOpen");
        old["records"] = JsonData.Array(f.Issues.Values);
        await DashboardCache.WriteFileAsync(Path.Combine(f.CacheDirectory, DashboardCache.IndexFileName(old.Text("key"))), old, Ct);
        File.Delete(currentFile);
        f.ClearRequests();
        var defaulted = await f.Load();
        Assert.Empty(defaulted["errors"].Strings());
        Assert.Equal(200, defaulted["itemScope"].Number("loaded"));
        Assert.Contains(f.Requests, r => r.Kind == "baseline");
        var smaller = await f.Load(limit: 3);
        var larger = await f.Load(limit: 205);
        Assert.Equal(3, smaller["itemScope"].Number("loaded"));
        Assert.Equal(205, larger["itemScope"].Number("loaded"));
        Assert.False(larger["itemScope"].Flag("limited"));
        f.ClearRequests();
        var reused = await f.Load(limit: 3);
        Assert.Equal(3, reused["itemScope"].Number("loaded"));
        Assert.DoesNotContain(f.Requests, r => r.Kind == "baseline");
        Assert.Empty(Hydrated(f));
        Assert.Equal(4, Directory.GetFiles(f.CacheDirectory, "*.json").Length);
    }

    private static JsonObject RecentNode(int number, bool pr = false)
    {
        var node = Node(number, pr);
        node["updatedAt"] = DateTimeOffset.Parse("2026-09-01T00:00:00Z").AddMinutes(number).ToString("O");
        return node;
    }

    private static IEnumerable<JsonObject> Items(JsonObject dashboard) =>
        dashboard["lanes"].Objects().SelectMany(l => l["items"].Objects())
            .Select(i => (i["issue"] ?? i["pr"])!.AsObject());

    private static int[] Hydrated(Fixture fixture) => fixture.Requests.Where(r => r.Kind == "hydrate")
        .SelectMany(r => Regex.Matches(r.Query, @"item(\d+):").Select(m => int.Parse(m.Groups[1].Value))).Order().ToArray();

    private static DateTimeOffset Since(Request request) =>
        DateTimeOffset.Parse(Uri.UnescapeDataString(Regex.Match(request.Uri.Query, @"[?&]since=([^&]+)").Groups[1].Value));

    private static JsonObject Node(int number, bool pr = false)
    {
        var node = new JsonObject
        {
            ["number"] = number, ["title"] = $"Item {number}",
            ["url"] = $"https://github.com/owner/repo/{(pr ? "pull" : "issues")}/{number}",
            ["state"] = "OPEN", ["createdAt"] = "2026-09-01T00:00:00Z", ["updatedAt"] = "2026-09-17T00:00:00Z",
            ["author"] = new JsonObject { ["login"] = "alice", ["__typename"] = "User" },
            ["milestone"] = null, ["labels"] = Connection(), ["assignees"] = Connection()
        };
        if (pr)
        {
            node["headRefOid"] = $"head-{number}";
            node["isDraft"] = false;
            node["baseRefName"] = "main";
            node["baseRef"] = new JsonObject { ["branchProtectionRule"] = new JsonObject { ["requiresConversationResolution"] = true } };
            node["mergeable"] = "MERGEABLE";
            node["reviewDecision"] = null;
            foreach (var key in new[] { "reviewRequests", "reviews", "reviewThreads", "readyForReviewEvents", "closingIssuesReferences" })
            {
                node[key] = Connection();
            }
            node["commits"] = Connection(new JsonObject
            {
                ["commit"] = new JsonObject
                {
                    ["committedDate"] = "2026-09-17T00:00:00Z", ["statusCheckRollup"] = new JsonObject { ["state"] = "SUCCESS" }
                }
            });
            node["commits"]!["totalCount"] = 1;
        }
        else
        {
            node["closedByPullRequestsReferences"] = Connection();
        }
        return node;
    }

    private static JsonObject Change(int number, bool pr = false, bool open = true)
    {
        var node = new JsonObject
        {
            ["number"] = number, ["state"] = open ? "open" : "closed", ["updated_at"] = "2026-09-18T00:00:00Z",
            ["html_url"] = $"https://github.com/owner/repo/{(pr ? "pull" : "issues")}/{number}",
            ["repository_url"] = "https://api.github.com/repos/owner/repo",
            ["node_id"] = "REST-ISSUE-NODE-ID-NOT-A-PULL-REQUEST-ID"
        };
        if (pr) { node["pull_request"] = new JsonObject(); }
        return node;
    }

    private static JsonObject Connection(params JsonObject[] nodes) => new() { ["nodes"] = JsonData.Array(nodes) };
    private static JsonObject Repository(JsonObject repository) => new() { ["data"] = new JsonObject { ["repository"] = repository } };
    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private sealed record Request(string Kind, string Query, Uri Uri, string? Token, int First);
    private sealed class Clock(Fixture fixture) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixture.Now;
    }

    private sealed class TestLogger : ILogger<GitHubDashboard>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Secret = "credential-for-sync-tests-only";
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "repository-sync-tests-" + Guid.NewGuid().ToString("N"));
        internal string CacheDirectory => Path.Combine(Directory, "repository-items-v1");
        internal string CacheFile => System.IO.Directory.GetFiles(CacheDirectory, "*.json").Single();
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T00:00:00Z");
        internal DateTimeOffset? ServerDate;
        internal bool OmitDate;
        internal string? Link;
        internal Account Account => new("acct:github.com/alice", "alice", "github.com", Secret, ["owner/repo"], true, new JsonObject());
        internal Dictionary<int, JsonObject> Issues { get; } = [];
        internal Dictionary<int, JsonObject> Prs { get; } = [];
        internal List<JsonObject> Delta { get; } = [];
        internal ConcurrentQueue<Request> Requests { get; } = [];
        internal Func<Request, HttpResponseMessage?>? Override;
        internal Func<Request, Task>? BeforeSend;
        internal TestLogger Logger { get; } = new();
        internal Clock Clock { get; }
        internal HttpClient Http { get; }

        internal Fixture()
        {
            Clock = new(this);
            Http = new(new Handler(this));
        }

        internal Task<JsonObject> Load(string mode = "issues", Action<SyncProgress>? progress = null, CancellationToken? ct = null,
            Account? account = null, int? limit = null)
        {
            var prefs = new JsonObject { ["mode"] = mode, ["showDrafts"] = true };
            if (limit.HasValue) { prefs["maxOpenItems"] = limit.Value; }
            return new GitHubDashboard(Http, Logger, Directory, Clock).LoadAsync([account ?? Account], prefs, ct ?? Ct, progress);
        }

        internal void ClearRequests() => Requests.Clear();
        internal async Task<JsonObject> Envelope() => JsonNode.Parse(await File.ReadAllTextAsync(CacheFile, Ct))!.AsObject();

        public void Dispose()
        {
            Http.Dispose();
            if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, recursive: true); }
        }

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
            {
                var body = message.Content is null ? new JsonObject() : JsonNode.Parse(await message.Content.ReadAsStringAsync(ct))!.AsObject();
                var query = body.Text("query");
                var kind = message.Method == HttpMethod.Get ? "delta" :
                    query.Contains("BoundaryLiveState", StringComparison.Ordinal) ? "boundary-live" : Regex.IsMatch(query, @"item\d+:") ? "hydrate" :
                    query.Contains("InitialOpenItemInventory", StringComparison.Ordinal) ? "baseline" :
                    query.Contains("WindowBoundary", StringComparison.Ordinal) ? "boundary" : "live";
                var request = new Request(kind, query, message.RequestUri!, message.Headers.Authorization?.Parameter, body["variables"].Number("first"));
                fixture.Requests.Enqueue(request);
                if (fixture.BeforeSend is not null) { await fixture.BeforeSend(request); }
                ct.ThrowIfCancellationRequested();
                var response = fixture.Override?.Invoke(request) ?? Respond();
                if (!fixture.OmitDate) { response.Headers.Date = fixture.ServerDate ?? fixture.Now; }
                if (fixture.Link is not null) { response.Headers.TryAddWithoutValidation("Link", fixture.Link); }
                return response;

                HttpResponseMessage Respond()
                {
                    var host = message.RequestUri!.Host == "api.github.com" ? "github.com" : message.RequestUri.Host;
                    JsonObject Copy(JsonObject node)
                    {
                        var result = (JsonObject)node.DeepClone();
                        if (host != "github.com")
                        {
                            foreach (var field in new[] { "url", "html_url" })
                            {
                                if (result[field] is not null) { result[field] = result.Text(field).Replace("github.com", host, StringComparison.Ordinal); }
                            }
                            if (result["repository_url"] is not null) { result["repository_url"] = $"https://{host}/api/v3/repos/owner/repo"; }
                        }
                        return result;
                    }
                    if (kind == "delta")
                    {
                        var page = int.Parse(Regex.Match(message.RequestUri.Query, @"[?&]page=(\d+)").Groups[1].Value);
                        var pageSize = int.Parse(Regex.Match(message.RequestUri.Query, @"[?&]per_page=(\d+)").Groups[1].Value);
                        return Json(JsonData.Array(fixture.Delta.OrderBy(n => n.Date("updated_at"))
                            .Skip((page - 1) * pageSize).Take(pageSize).Select(Copy)));
                    }
                    var repository = new JsonObject { ["nameWithOwner"] = "owner/repo", ["isPrivate"] = true };
                    if (kind is "hydrate" or "boundary-live")
                    {
                        Assert.DoesNotContain("node_id", query);
                        foreach (Match match in Regex.Matches(query, @"item(\d+):(issue|pullRequest)\(number:(\d+)\)"))
                        {
                            var number = int.Parse(match.Groups[1].Value);
                            Assert.Equal(number, int.Parse(match.Groups[3].Value));
                            repository[$"item{number}"] = Copy((match.Groups[2].Value == "issue" ? fixture.Issues : fixture.Prs)[number]);
                        }
                    }
                    else
                    {
                        var issues = Regex.IsMatch(query, @"\bissues\s*\(states");
                        var source = (issues ? fixture.Issues : fixture.Prs).Values.Where(n => n.Text("state") == "OPEN")
                            .OrderByDescending(n => n.Date("updatedAt")).ThenBy(n => n.Number("number")).ToArray();
                        var size = request.First;
                        var after = body["variables"].Text("after");
                        var offset = after.Length == 0 ? 0 : int.Parse(after);
                        var nodes = source.Skip(offset).Take(size).Select(Copy).ToArray();
                        if (kind is "live" or "baseline" or "boundary")
                        {
                            var fields = !query.Contains("commits", StringComparison.Ordinal) ? new HashSet<string> { "number", "url", "updatedAt" } :
                                new HashSet<string> { "number", "url", "updatedAt", "headRefOid", "mergeable", "reviewDecision",
                                "baseRef", "reviewRequests", "reviews", "reviewThreads", "commits" };
                            foreach (var node in nodes)
                            {
                                foreach (var field in node.Select(p => p.Key).Where(field => !fields.Contains(field)).ToArray()) { node.Remove(field); }
                            }
                        }
                        var connection = Connection(nodes);
                        connection["totalCount"] = source.Length;
                        connection["pageInfo"] = new JsonObject
                        {
                            ["hasNextPage"] = offset + size < source.Length,
                            ["endCursor"] = offset + size < source.Length ? (offset + size).ToString() : null
                        };
                        repository[issues ? "issues" : "pullRequests"] = connection;
                    }
                    return Json(Repository(repository));
                }
            }
        }
    }
}
