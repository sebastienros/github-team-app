// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GitHub.TeamApp.Tests;

public sealed class DashboardCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AccountId = "acct:github.com/alice";
    private const string Secret = "test-private-credential-never-persist";

    [Fact]
    public async Task FreshHostFirstHttpStateConcurrentReadsAndEmptyRefreshReturnSuccess()
    {
        using var fixture = new Fixture();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(DashboardService).Assembly.Location);
        start.ArgumentList.Add("--no-browser");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(fixture.Store.DirectoryPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test dashboard host.");
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
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("X-Team-App-Client", Guid.NewGuid().ToString());
            async Task ReadState()
            {
                using var response = await http.GetAsync("/api/state", timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                Assert.True(response.StatusCode == HttpStatusCode.OK, text);
                var snapshot = JsonNode.Parse(text)!;
                Assert.Equal("", snapshot["dashboard"].Text("repositoryId"));
                Assert.Equal("", snapshot["prefs"].Text("selectedRepository"));
                Assert.Equal("empty", snapshot["dashboard"].Text("cacheStatus"));
            }
            await ReadState();
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => ReadState()));
            using var autoApplyBody = new StringContent("""{"enabled":false}""", Encoding.UTF8, "application/json");
            using var autoApply = await http.PostAsync("/api/auto-apply", autoApplyBody, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, autoApply.StatusCode);
            using var refreshBody = new StringContent("{}", Encoding.UTF8, "application/json");
            using var refresh = await http.PostAsync("/api/refresh", refreshBody, timeout.Token);
            var refreshed = await refresh.Content.ReadAsStringAsync(timeout.Token);
            Assert.True(refresh.StatusCode == HttpStatusCode.OK, refreshed);
            Assert.False(JsonNode.Parse(refreshed)!["prefs"].Flag("autoApplyUpdates"));
            await ReadState();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(Ct);
            await errors;
        }
    }

    [Fact]
    public async Task ColdRestartReturnsDurableContentBeforeAuthenticationAndForceDoesNotWait()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using (var first = fixture.Service())
        {
            await first.GetAsync(Guid.NewGuid(), false, Ct);
            await first.WaitForRefreshAsync(Ct);
        }
        var entered = Signal();
        var release = Signal();
        using var second = fixture.Service(async (prefs, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Accounts(prefs);
        });
        var client = Guid.NewGuid();
        var result = await second.GetAsync(client, true, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal("github.com/owner/a", result["dashboard"].Text("repositoryId"));
        Assert.Equal("cached", result["dashboard"].Text("cacheStatus"));
        Assert.True(result["dashboard"].Flag("refreshing"));
        Assert.Equal("owner/a", PullRequest(result).Text("repository"));
        Assert.Equal(AccountId, result["dashboard"]!["accounts"].Objects().Single().Text("id"));
        Assert.NotNull(second.FindPullRequest(client, PullRequest(result)));
        result["dashboard"]!["lanes"] = new JsonArray();
        Assert.NotEmpty((await second.GetAsync(client, false, Ct))["dashboard"]!["lanes"].Objects());
        release.TrySetResult();
        await second.WaitForRefreshAsync(Ct);
        Assert.Equal("live", (await second.GetAsync(client, false, Ct))["dashboard"].Text("cacheStatus"));
    }

    [Fact]
    public async Task UncachedShellAndEmptySelectionDoNotWaitForOrRequireProviders()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var release = Signal();
        using var service = fixture.Service(async (prefs, ct) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(ct);
            return Accounts(prefs);
        });
        var empty = await service.GetAsync(Guid.NewGuid(), true, Ct);
        Assert.Equal("", empty["dashboard"].Text("repositoryId"));
        Assert.Equal("empty", empty["dashboard"].Text("cacheStatus"));
        Assert.False(empty["dashboard"].Flag("refreshing"));
        Assert.Empty(empty["dashboard"]!["lanes"].Objects());
        Assert.Equal(0, calls);
        await fixture.Select("owner/a");
        service.ConfigurationChanged();
        var shell = await service.GetAsync(Guid.NewGuid(), true, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal("loading", shell["dashboard"].Text("cacheStatus"));
        Assert.True(shell["dashboard"].Flag("refreshing"));
        Assert.Empty(shell["dashboard"]!["lanes"].Objects());
        release.TrySetResult();
        await service.WaitForRefreshAsync(Ct);
    }

    [Fact]
    public async Task PersistedRepositoryAdditionReturnsLoadingAndStartsRefreshOnItsFirstResponse()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        using var service = fixture.Service(async (prefs, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Accounts(prefs);
        });
        var client = Guid.NewGuid();
        var empty = await service.GetAsync(client, false, Ct);
        Assert.Equal("empty", empty["dashboard"].Text("cacheStatus"));
        await fixture.Store.UpdateAsync(p => RepositoryCatalog.Add(p, AccountId, "owner/a"), Ct);
        service.ConfigurationChanged();
        try
        {
            var added = await service.GetAsync(client, true, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
            Assert.Equal("github.com/owner/a", added["dashboard"].Text("repositoryId"));
            Assert.Equal("github.com/owner/a", added["prefs"].Text("selectedRepository"));
            Assert.Equal("loading", added["dashboard"].Text("cacheStatus"));
            Assert.True(added["dashboard"].Flag("refreshing"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
            var subsequent = await service.GetAsync(client, false, Ct);
            Assert.True(subsequent["dashboard"].Flag("refreshing"));
            Assert.Equal(1, calls);
            Assert.Equal("", empty["prefs"].Text("selectedRepository"));
        }
        finally { release.TrySetResult(); }
        await service.WaitForRefreshAsync(Ct);
        var live = await service.GetAsync(client, false, Ct);
        Assert.Equal("live", live["dashboard"].Text("cacheStatus"));
        Assert.Equal("owner/a", PullRequest(live).Text("repository"));
    }

    [Fact]
    public async Task ConcurrentRequestsDeduplicateRefreshAndCallerCancellationDoesNotCancelIt()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        var calls = 0;
        var entered = Signal();
        var release = Signal();
        using var service = fixture.Service(load: async (accounts, prefs, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Dashboard(prefs);
        });
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await service.GetAsync(Guid.NewGuid(), true, request.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        await request.CancelAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => service.GetAsync(Guid.NewGuid(), true, Ct))).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.All(results, r => Assert.Equal("github.com/owner/a", r["dashboard"].Text("repositoryId")));
        Assert.Equal(1, calls);
        release.TrySetResult();
        await service.WaitForRefreshAsync(Ct);
        Assert.Equal("live", (await service.GetAsync(Guid.NewGuid(), false, Ct))["dashboard"].Text("cacheStatus"));
    }

    [Fact]
    public async Task SlowRepositoryCannotOverwriteNewSelectionOrItsActions()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/b");
        using (var seed = fixture.Service())
        {
            await seed.GetAsync(Guid.NewGuid(), false, Ct);
            await seed.WaitForRefreshAsync(Ct);
        }
        await fixture.Select("owner/a");
        var entered = Signal();
        var release = Signal();
        using var service = fixture.Service(load: async (accounts, prefs, ct) =>
        {
            if (RepositoryCatalog.Selected(prefs)!.Text("repository") == "owner/a")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return Dashboard(prefs);
        });
        var client = Guid.NewGuid();
        await service.GetAsync(client, false, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        var oldRefresh = service.WaitForRefreshAsync(Ct);
        await fixture.Select("owner/b");
        service.ConfigurationChanged();
        var b = await service.GetAsync(client, true, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal("owner/b", PullRequest(b).Text("repository"));
        await service.WaitForRefreshAsync(Ct);
        release.TrySetResult();
        await oldRefresh;
        var after = await service.GetAsync(client, false, Ct);
        Assert.Equal("github.com/owner/b", after["dashboard"].Text("repositoryId"));
        Assert.DoesNotContain("owner/a", after["dashboard"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.NotNull(service.FindPullRequest(client, PullRequest(after)));
        var a = Dashboard(Preferences("owner/a"));
        Assert.Null(service.FindPullRequest(client, a["lanes"]![0]!["prs"]![0]!.AsObject()));
        Assert.False(service.IsLinkedPullRequest(client, "https://github.com/owner/a/pull/1"));
    }

    [Fact]
    public async Task GenerationChangeRejectsDisplayedActionsAndOlderSameScopeRefresh()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        var release = Signal();
        var entered = Signal();
        var calls = 0;
        using var service = fixture.Service(load: async (accounts, prefs, ct) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            var result = Dashboard(prefs);
            result["label"] = $"version-{call}";
            return result;
        });
        var client = Guid.NewGuid();
        await service.GetAsync(client, false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var original = await service.GetAsync(client, true, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        var oldRefresh = service.WaitForRefreshAsync(Ct);
        Assert.NotNull(service.FindPullRequest(client, PullRequest(original)));
        service.ConfigurationChanged();
        Assert.Null(service.FindPullRequest(client, PullRequest(original)));
        Assert.Null(service.FindHealthSource(client, "source-a"));
        Assert.False(service.IsLinkedPullRequest(client, PullRequest(original).Text("url")));
        await service.GetAsync(client, true, Ct);
        await service.WaitForRefreshAsync(Ct);
        release.TrySetResult();
        await oldRefresh;
        Assert.Equal("version-3", (await service.GetAsync(client, false, Ct))["dashboard"].Text("label"));
        var prefs = await fixture.Store.ReadAsync(Ct);
        Assert.Equal("version-3", (await fixture.Cache.ReadAsync(DashboardCache.Key(prefs), Ct)).Text("label"));
    }

    [Theory]
    [InlineData("errors")]
    [InlineData("unavailable")]
    [InlineData("partial")]
    [InlineData("unauthenticated")]
    [InlineData("exception")]
    public async Task FailedRefreshRetainsEntireSuccessfulContentAndDisk(string failure)
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        var fail = false;
        using var service = fixture.Service(load: (accounts, prefs, ct) =>
        {
            var result = Dashboard(prefs);
            if (!fail) { return Task.FromResult(result); }
            result["lanes"] = new JsonArray();
            result["counts"] = new JsonObject { ["prs"] = 0 };
            result["fetchedAt"] = "2026-09-19T00:00:00Z";
            switch (failure)
            {
                case "errors": result["errors"] = new JsonArray("truncated page"); break;
                case "unavailable": result["health"] = JsonNode.Parse("""{"items":[{"state":"unavailable"}]}"""); break;
                case "partial": result["providers"] = JsonNode.Parse("""[{"status":"partial"}]"""); break;
                case "unauthenticated": result["authenticated"] = false; break;
                case "exception": throw new HttpRequestException(Secret);
            }
            return Task.FromResult(result);
        });
        var client = Guid.NewGuid();
        await service.GetAsync(client, false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var before = await service.GetAsync(client, false, Ct);
        var prefs = await fixture.Store.ReadAsync(Ct);
        var path = fixture.Cache.SnapshotPath(DashboardCache.Key(prefs));
        var disk = await File.ReadAllTextAsync(path, Ct);
        fail = true;
        var immediate = await service.GetAsync(client, true, Ct);
        Assert.Equal("owner/a", PullRequest(immediate).Text("repository"));
        await service.WaitForRefreshAsync(Ct);
        var after = await service.GetAsync(client, false, Ct);
        Assert.True(JsonNode.DeepEquals(before["dashboard"]!["lanes"], after["dashboard"]!["lanes"]));
        Assert.True(JsonNode.DeepEquals(before["dashboard"]!["counts"], after["dashboard"]!["counts"]));
        Assert.Equal(before["dashboard"].Text("fetchedAt"), after["dashboard"].Text("fetchedAt"));
        Assert.False(after["dashboard"].Flag("refreshing"));
        Assert.NotEmpty(after["dashboard"].Text("refreshError"));
        Assert.DoesNotContain(Secret, after.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(disk, await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task PartialFirstRefreshIsNotPersistedAsComplete()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using var service = fixture.Service(load: (accounts, prefs, ct) =>
        {
            var data = Dashboard(prefs);
            data["errors"] = new JsonArray("page missing");
            return Task.FromResult(data);
        });
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var result = await service.GetAsync(Guid.NewGuid(), false, Ct);
        Assert.Equal("owner/a", PullRequest(result).Text("repository"));
        Assert.NotEmpty(result["dashboard"].Text("refreshError"));
        Assert.Equal("error", result["dashboard"].Text("cacheStatus"));
        Assert.False(result["dashboard"].Flag("loading"));
        Assert.Null(await fixture.Cache.ReadAsync(DashboardCache.Key(await fixture.Store.ReadAsync(Ct)), Ct));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"version":999}""")]
    [InlineData("""{"version":1,"key":"wrong","dashboard":{}}""")]
    [InlineData("""{"version":"invalid"}""")]
    public async Task MalformedCacheIsExplicitAndCanBeRebuilt(string content)
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        var prefs = await fixture.Store.ReadAsync(Ct);
        Directory.CreateDirectory(fixture.Cache.DirectoryPath);
        var path = fixture.Cache.SnapshotPath(DashboardCache.Key(prefs));
        await File.WriteAllTextAsync(path, content, Ct);
        var release = Signal();
        using var service = fixture.Service(async (p, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Accounts(p);
        });
        var result = await service.GetAsync(Guid.NewGuid(), false, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.NotEmpty(result["dashboard"].Text("refreshError"));
        Assert.Equal("loading", result["dashboard"].Text("cacheStatus"));
        Assert.Equal(content, await File.ReadAllTextAsync(path, Ct));
        release.TrySetResult();
        await service.WaitForRefreshAsync(Ct);
        Assert.NotNull(await fixture.Cache.ReadAsync(DashboardCache.Key(prefs), Ct));
    }

    [Fact]
    public async Task MetadataIsDurableWithoutCredentialsAndMalformedMetadataIsReported()
    {
        using var fixture = new Fixture();
        using (var service = fixture.Service())
        {
            var metadata = await service.DiscoverAccountsAsync(Ct);
            Assert.Equal(AccountId, metadata.Objects().Single().Text("id"));
            Assert.DoesNotContain(Secret, metadata.ToJsonString(), StringComparison.Ordinal);
        }
        using var restarted = fixture.Service((prefs, ct) => throw new InvalidOperationException("Must not resolve without a selection"));
        var result = await restarted.GetAsync(Guid.NewGuid(), false, Ct);
        Assert.Equal(AccountId, result["dashboard"]!["accounts"].Objects().Single().Text("id"));
        var path = Path.Combine(fixture.Cache.DirectoryPath, "accounts.json");
        await File.WriteAllTextAsync(path, """{"version":1,"accounts":{}}""", Ct);
        restarted.ConfigurationChanged();
        var malformed = await restarted.GetAsync(Guid.NewGuid(), false, Ct);
        Assert.NotEmpty(malformed["dashboard"].Text("refreshError"));
    }

    [Fact]
    public async Task FilesAreAtomicPrivateVersionedAndNeverContainCredentialsOrPreferences()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        await fixture.Store.UpdateAsync(p =>
        {
            p["sessionLauncher"] = new JsonObject { ["secret"] = Secret };
            p["accessToken"] = Secret;
        }, Ct);
        using var service = fixture.Service(load: (accounts, prefs, ct) =>
        {
            var result = Dashboard(prefs);
            result["token"] = Secret;
            result["diagnostic"] = $"redact {Secret}";
            result["nested"] = new JsonObject { ["authorization"] = Secret };
            return Task.FromResult(result);
        });
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var files = Directory.GetFiles(fixture.Cache.DirectoryPath);
        Assert.Equal(2, files.Length);
        Assert.Empty(Directory.GetFiles(fixture.Cache.DirectoryPath, "*.tmp"));
        foreach (var path in files)
        {
            var text = await File.ReadAllTextAsync(path, Ct);
            var document = JsonNode.Parse(text)!;
            Assert.Equal(1, document.Number("version"));
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"token\"", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sessionLauncher", text, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        await service.GetAsync(Guid.NewGuid(), true, Ct);
        await service.WaitForRefreshAsync(Ct);
        Assert.Empty(Directory.GetFiles(fixture.Cache.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task NonCryptographicFilenameIndexVerifiesTheFullCanonicalKeyOnCollisions()
    {
        using var fixture = new Fixture();
        var a = Preferences("owner/a");
        var b = Preferences("owner/b");
        var keyA = DashboardCache.Key(a);
        var keyB = DashboardCache.Key(b);
        var pathA = fixture.Cache.SnapshotPath(keyA);
        var pathB = fixture.Cache.SnapshotPath(keyB);
        Assert.Matches("^fnv1a-[0-9a-f]{32}\\.json$", Path.GetFileName(pathA));
        Assert.StartsWith("fnv1a-a430d84680aabd0b", Path.GetFileName(fixture.Cache.SnapshotPath("hello")), StringComparison.Ordinal);
        Assert.Equal(pathA, new DashboardCache(fixture.Store.DirectoryPath).SnapshotPath(keyA));
        Assert.NotEqual(pathA, pathB);
        var first = Dashboard(a);
        first["repositoryId"] = a.Text("selectedRepository");
        var second = Dashboard(b);
        second["repositoryId"] = b.Text("selectedRepository");
        await fixture.Cache.WriteAsync(keyA, first, Ct);
        await fixture.Cache.WriteAsync(keyB, second, Ct);
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(pathA, Ct))!;
        Assert.Equal(keyA, envelope.Text("key"));
        Assert.Equal("owner/a", JsonNode.Parse(envelope.Text("key"))!["identity"].Text("repository"));
        // Put B's valid envelope at A's index to simulate a filename collision.
        File.Copy(pathB, pathA, overwrite: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Cache.ReadAsync(keyA, Ct));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("filter")]
    [InlineData("account")]
    [InlineData("host")]
    public async Task ChangedViewOrIdentityNeverReusesAnotherScopeWhileProviderIsBlocked(string change)
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using (var seed = fixture.Service())
        {
            await seed.GetAsync(Guid.NewGuid(), false, Ct);
            await seed.WaitForRefreshAsync(Ct);
        }
        await fixture.Store.UpdateAsync(p =>
        {
            switch (change)
            {
                case "mode": p["mode"] = "health"; break;
                case "filter": p["showDrafts"] = true; break;
                case "account": p["repositories"]![0]!["accountId"] = "acct:github.com/bob"; break;
                case "host": p["repositories"]![0]!["host"] = "enterprise.example"; break;
            }
        }, Ct);
        var release = Signal();
        using var service = fixture.Service(async (p, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Accounts(p);
        });
        var result = await service.GetAsync(Guid.NewGuid(), true, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Empty(result["dashboard"]!["lanes"].Objects());
        Assert.Equal("loading", result["dashboard"].Text("cacheStatus"));
        if (change is "account" or "host") { Assert.Empty(result["dashboard"]!["accounts"].Objects()); }
        release.TrySetResult();
        await service.WaitForRefreshAsync(Ct);
    }

    [Fact]
    public async Task SynchronousProviderPrologueCannotBlockCacheRead()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        using var service = fixture.Service((prefs, ct) =>
        {
            entered.TrySetResult();
            release.Wait(ct);
            return Task.FromResult(Accounts(prefs));
        });
        try
        {
            var result = await service.GetAsync(Guid.NewGuid(), true, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
            Assert.True(result["dashboard"].Flag("refreshing"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        }
        finally { release.Set(); }
        await service.WaitForRefreshAsync(Ct);
    }

    [Fact]
    public async Task CredentialInvalidationDoesNotRevokeDisplayedActions()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using var service = fixture.Service();
        var client = Guid.NewGuid();
        await service.GetAsync(client, false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var snapshot = await service.GetAsync(client, false, Ct);
        service.InvalidateAccounts();
        await service.DiscoverAccountsAsync(Ct);
        Assert.NotNull(service.FindPullRequest(client, PullRequest(snapshot)));
    }

    [Fact]
    public async Task RedCiAndUnconfiguredOptionalProvidersAreSuccessfulDataNotRefreshFailures()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using var service = fixture.Service(load: (accounts, prefs, ct) =>
        {
            var data = Dashboard(prefs);
            data["health"]!["items"]![0]!["state"] = "failing";
            data["health"]!["items"]![0]!["errors"] = new JsonArray("build failed");
            data["providers"] = new JsonArray((JsonNode)new JsonObject { ["status"] = "not_configured" });
            return Task.FromResult(data);
        });
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var result = await service.GetAsync(Guid.NewGuid(), false, Ct);
        Assert.Equal("", result["dashboard"].Text("refreshError"));
        Assert.Equal("live", result["dashboard"].Text("cacheStatus"));
        Assert.NotNull(await fixture.Cache.ReadAsync(DashboardCache.Key(await fixture.Store.ReadAsync(Ct)), Ct));
    }

    [Fact]
    public void CacheKeysCanonicalizeObjectsAndIsolateHostAccountViewsAndFilters()
    {
        var prefs = Preferences("owner/a");
        var baseline = DashboardCache.Key(prefs);
        var reordered = new JsonObject();
        foreach (var pair in prefs.Reverse()) { reordered[pair.Key] = pair.Value?.DeepClone(); }
        Assert.Equal(baseline, DashboardCache.Key(reordered));
        foreach (var update in new Action<JsonObject>[]
        {
            p => p["mode"] = "health",
            p => p["showDrafts"] = true,
            p => p["release"] = "other",
            p => p["reviewLimit"] = 100,
            p => p["notifications"]!["ciFailing"] = false,
            p => p["dismissedNotifications"] = new JsonArray("dismissed"),
            p => p["repositories"]![0]!["accountId"] = "acct:github.com/bob",
            p => p["repositories"]![0]!["host"] = "enterprise.example",
            p => p["repositories"]![0]!["repository"] = "owner/b",
            p => p["accounts"]![AccountId]!["active"] = false
        })
        {
            var changed = (JsonObject)prefs.DeepClone();
            update(changed);
            Assert.NotEqual(baseline, DashboardCache.Key(changed));
        }
        var unrelated = (JsonObject)prefs.DeepClone();
        unrelated["autoApplyUpdates"] = false;
        unrelated["healthOrder"] = new JsonArray("other-order");
        unrelated["sessionLauncher"] = new JsonObject { ["command"] = "different" };
        unrelated["accounts"]!["acct:github.com/other"] = new JsonObject
        {
            ["active"] = true, ["repos"] = new JsonArray("other/repository")
        };
        unrelated["accounts"]![AccountId]!["repos"] = new JsonArray("owner/new-membership");
        unrelated["repositories"]!.AsArray().Add((JsonNode)RepositoryCatalog.Create(AccountId, "owner/other"));
        unrelated["azurePipelines"]!.AsArray().Add((JsonNode)new JsonObject
        {
            ["id"] = "other-pipeline", ["repositoryId"] = "github.com/owner/other"
        });
        Assert.Equal(baseline, DashboardCache.Key(unrelated));
        unrelated["azurePipelines"]!.AsArray().Add((JsonNode)new JsonObject
        {
            ["id"] = "selected-pipeline", ["repositoryId"] = "github.com/owner/a"
        });
        Assert.NotEqual(baseline, DashboardCache.Key(unrelated));
    }

    [Fact]
    public async Task SwitchingBackHitsCacheDespiteUnrelatedRepositoryAndAccountChanges()
    {
        using var fixture = new Fixture();
        using (var seed = fixture.Service())
        {
            foreach (var repository in new[] { "owner/a", "owner/b" })
            {
                await fixture.Select(repository);
                seed.ConfigurationChanged();
                await seed.GetAsync(Guid.NewGuid(), false, Ct);
                await seed.WaitForRefreshAsync(Ct);
            }
        }
        var release = Signal();
        using var service = fixture.Service(async (prefs, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Accounts(prefs);
        });
        var b = await service.GetAsync(Guid.NewGuid(), true, Ct);
        Assert.Equal("cached", b["dashboard"].Text("cacheStatus"));
        Assert.Equal("owner/b", PullRequest(b).Text("repository"));
        var oldRefresh = service.WaitForRefreshAsync(Ct);
        await fixture.Select("owner/a");
        await fixture.Store.UpdateAsync(p =>
        {
            p["repositories"]!.AsArray().Add((JsonNode)RepositoryCatalog.Create("acct:github.com/bob", "unrelated/repository"));
            p["accounts"]!["acct:github.com/bob"] = new JsonObject { ["active"] = true };
            p["accounts"]![AccountId]!["repos"] = new JsonArray("unrelated/membership");
        }, Ct);
        service.ConfigurationChanged();
        var a = await service.GetAsync(Guid.NewGuid(), true, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal("cached", a["dashboard"].Text("cacheStatus"));
        Assert.Equal("owner/a", PullRequest(a).Text("repository"));
        Assert.True(a["dashboard"].Flag("refreshing"));
        release.TrySetResult();
        await Task.WhenAll(oldRefresh, service.WaitForRefreshAsync(Ct));
    }

    [Fact]
    public async Task FailedSelectedAccountNeverReachesProviderAndRetainsCachedContent()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using (var seed = fixture.Service())
        {
            await seed.GetAsync(Guid.NewGuid(), false, Ct);
            await seed.WaitForRefreshAsync(Ct);
        }
        var calls = 0;
        using var service = fixture.Service((prefs, ct) =>
        {
            var account = Account(AccountId, "github.com", true);
            account.Metadata["status"] = "failed";
            return Task.FromResult<IReadOnlyList<Account>>([account]);
        }, (accounts, prefs, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Dashboard(prefs));
        });
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var result = await service.GetAsync(Guid.NewGuid(), false, Ct);
        Assert.Equal(0, calls);
        Assert.Equal("cached", result["dashboard"].Text("cacheStatus"));
        Assert.NotEmpty(result["dashboard"].Text("refreshError"));
        Assert.Equal("owner/a", PullRequest(result).Text("repository"));
    }

    [Fact]
    public async Task PreferenceNotificationsInvalidateScopeChangesButNotSessionOrAutoApplySettings()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using var service = fixture.Service();
        var client = Guid.NewGuid();
        await service.GetAsync(client, false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var shown = await service.GetAsync(client, false, Ct);
        await fixture.Store.UpdateAsync(p =>
        {
            p["autoApplyUpdates"] = false;
            p["sessionLauncher"] = new JsonObject { ["command"] = "changed" };
        }, Ct);
        await service.PreferencesChangedAsync(Ct);
        Assert.NotNull(service.FindPullRequest(client, PullRequest(shown)));
        await fixture.Store.UpdateAsync(p => p["mode"] = "issues", Ct);
        await service.PreferencesChangedAsync(Ct);
        Assert.Null(service.FindPullRequest(client, PullRequest(shown)));
        Assert.False(service.IsLinkedPullRequest(client, PullRequest(shown).Text("url")));
        await service.WaitForRefreshAsync(Ct);
    }

    [Fact]
    public async Task AccountDiscoveryAlwaysResolvesFreshStoredRepositoryMembership()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        var calls = 0;
        using var service = fixture.Service((prefs, ct) =>
        {
            Interlocked.Increment(ref calls);
            var repos = prefs["repositories"].Objects()
                .Where(r => r.Text("accountId") == AccountId).Select(r => r.Text("repository")).ToArray();
            return Task.FromResult<IReadOnlyList<Account>>([Account(AccountId, "github.com", true) with { Repos = repos }]);
        });
        var first = await service.DiscoverAccountsAsync(Ct);
        Assert.Equal(["owner/a"], first.Objects().Single()["repos"].Strings());
        await fixture.Store.UpdateAsync(p =>
            p["repositories"]!.AsArray().Add((JsonNode)RepositoryCatalog.Create(AccountId, "owner/new")), Ct);
        service.ConfigurationChanged();
        var second = await service.DiscoverAccountsAsync(Ct);
        Assert.Equal(2, calls);
        Assert.Equal(["owner/a", "owner/new"], second.Objects().Single()["repos"].Strings());
        var persisted = await fixture.Cache.ReadAccountsAsync(Ct);
        Assert.Equal(["owner/a", "owner/new"], persisted.Objects().Single()["repos"].Strings());
    }

    [Fact]
    public async Task ProvidersReceiveOnlySelectedAccountRepositoryAndPipelines()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        await fixture.Store.UpdateAsync(p =>
        {
            p["azurePipelines"] = JsonNode.Parse("""
                [{"id":"a","repositoryId":"github.com/owner/a"},{"id":"b","repositoryId":"github.com/owner/b"}]
                """);
        }, Ct);
        using var service = fixture.Service((prefs, ct) =>
        {
            IReadOnlyList<Account> accounts =
            [
                Account(AccountId, "github.com", true),
                Account("acct:github.com/bob", "github.com", true),
                Account("acct:enterprise.example/alice", "enterprise.example", true),
                Account("acct:github.com/inactive", "github.com", false)
            ];
            return Task.FromResult(accounts);
        }, (accounts, prefs, ct) =>
        {
            var selected = Assert.Single(accounts);
            Assert.Equal(AccountId, selected.Id);
            Assert.Equal(["owner/a"], selected.Repos);
            Assert.Equal("a", Assert.Single(prefs["azurePipelines"].Objects()).Text("id"));
            return Task.FromResult(Dashboard(prefs));
        });
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var result = await service.GetAsync(Guid.NewGuid(), false, Ct);
        Assert.Empty(result["dashboard"].Text("refreshError"));
        Assert.Single(result["dashboard"]!["accounts"].Objects());
        Assert.Equal(["owner/a"], result["dashboard"]!["accounts"].Objects().Single()["repos"].Strings());
        Assert.DoesNotContain("owner/unrelated", result["dashboard"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAndPollUseLatestSelectionAndRejectLateOldState()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        var entered = Signal();
        var release = Signal();
        using var service = fixture.Service(load: async (accounts, prefs, ct) =>
        {
            if (RepositoryCatalog.Selected(prefs)!.Text("repository") == "owner/a")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return Dashboard(prefs);
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var output = new EventOutput();
        var context = new DefaultHttpContext { RequestAborted = stop.Token };
        context.Response.Body = output;
        var client = Guid.NewGuid();
        var stream = service.StreamAsync(context, client);
        await output.Next("state", Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        var old = service.WaitForRefreshAsync(Ct);
        // Polling must detect a changed scope even if a caller forgot the notification.
        await fixture.Select("owner/b");
        await service.PollAsync(Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        var schedule = await output.Next("poll-schedule", Ct, d => d.Text("repositoryId") == "github.com/owner/b");
        Assert.Equal("github.com/owner/b", schedule.Text("repositoryId"));
        await service.WaitForRefreshAsync(Ct);
        var b = await output.Next("state", Ct, d => d["dashboard"].Text("repositoryId") == "github.com/owner/b");
        Assert.NotNull(service.FindPullRequest(client, PullRequest(b)));
        var checkpoint = output.Text.Length;
        release.TrySetResult();
        await old;
        Assert.DoesNotContain("owner/a", output.Text[checkpoint..], StringComparison.Ordinal);
        Assert.False(service.IsLinkedPullRequest(client, "https://github.com/owner/a/pull/1"));
        await stop.CancelAsync();
        await stream;
    }

    [Fact]
    public async Task AutoApplyOffDoesNotAuthorizeUndisplayedUpdatedActions()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        await fixture.Store.UpdateAsync(p => p["autoApplyUpdates"] = false, Ct);
        var number = 1;
        using var service = fixture.Service(load: (accounts, prefs, ct) => Task.FromResult(Dashboard(prefs, number)));
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var output = new EventOutput();
        var context = new DefaultHttpContext { RequestAborted = stop.Token };
        context.Response.Body = output;
        var client = Guid.NewGuid();
        var stream = service.StreamAsync(context, client);
        var shown = await output.Next("state", Ct);
        Assert.NotNull(service.FindPullRequest(client, PullRequest(shown)));
        number = 2;
        await service.PollAsync(Ct);
        await service.WaitForRefreshAsync(Ct);
        var update = await output.Next("update-available", Ct);
        Assert.Equal("github.com/owner/a", update.Text("repositoryId"));
        Assert.False(service.IsLinkedPullRequest(client, "https://github.com/owner/a/pull/2"));
        Assert.True(service.IsLinkedPullRequest(client, "https://github.com/owner/a/pull/1"));
        await service.GetAsync(client, false, Ct);
        Assert.True(service.IsLinkedPullRequest(client, "https://github.com/owner/a/pull/2"));
        await stop.CancelAsync();
        await stream;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SseSnapshotSummariesAlwaysIncludeScopeIncludingEmptySelection(bool selected)
    {
        using var fixture = new Fixture();
        if (selected) { await fixture.Select("owner/a"); }
        var release = Signal();
        using var service = fixture.Service(async (prefs, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Accounts(prefs);
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var output = new EventOutput();
        var context = new DefaultHttpContext { RequestAborted = stop.Token };
        context.Response.Body = output;
        var stream = service.StreamAsync(context, Guid.NewGuid());
        try
        {
            var state = await output.Next("state", Ct);
            var summary = await output.Next("snapshot", Ct);
            var id = selected ? "github.com/owner/a" : "";
            Assert.Equal(id, state["dashboard"].Text("repositoryId"));
            Assert.Equal(id, state["prefs"].Text("selectedRepository"));
            Assert.Equal(id, summary.Text("repositoryId"));
            Assert.Equal(id, summary["prefs"].Text("selectedRepository"));
        }
        finally
        {
            release.TrySetResult();
            await service.WaitForRefreshAsync(Ct);
            await stop.CancelAsync();
            await stream;
        }
    }

    [Fact]
    public async Task SseRefreshErrorsIncludeTheSelectedRepositoryScope()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        var release = Signal();
        using var service = fixture.Service(load: async (accounts, prefs, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return new JsonObject { ["authenticated"] = false };
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var output = new EventOutput();
        var context = new DefaultHttpContext { RequestAborted = stop.Token };
        context.Response.Body = output;
        var stream = service.StreamAsync(context, Guid.NewGuid());
        try
        {
            await output.Next("snapshot", Ct);
            release.TrySetResult();
            await service.WaitForRefreshAsync(Ct);
            var state = await output.Next("state", Ct);
            Assert.Equal("github.com/owner/a", state["dashboard"].Text("repositoryId"));
            Assert.False(state["dashboard"].Flag("authenticated"));
            var error = await output.Next("refresh-error", Ct);
            Assert.Equal("github.com/owner/a", error.Text("repositoryId"));
            Assert.NotEmpty(error.Text("error"));
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            await stream;
        }
    }

    [Fact]
    public async Task SlowSseClientHasBoundedQueueAndCannotAuthorizeUnsentState()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using var service = fixture.Service();
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var release = Signal();
        var entered = Signal();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var output = new EventOutput(release.Task, entered);
        var context = new DefaultHttpContext { RequestAborted = stop.Token };
        context.Response.Body = output;
        var client = Guid.NewGuid();
        var stream = service.StreamAsync(context, client);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
            for (var i = 0; i < 40; i++)
            {
                await fixture.Store.UpdateAsync(p => p["healthOrder"] = new JsonArray(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), Ct);
                await service.PreferencesChangedAsync(Ct);
            }
            Assert.False(service.IsLinkedPullRequest(client, "https://github.com/owner/a/pull/1"));
            release.TrySetResult();
            await output.Next("poll-schedule", Ct);
            Assert.InRange(output.Text.Split("event: ", StringSplitOptions.None).Length - 1, 1, 16);
            Assert.Contains("\"healthOrder\":[\"39\"]", output.Text, StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            await stream;
        }
    }

    [Fact]
    public async Task QueuedSseFramesAreDiscardedWhenSelectionChanges()
    {
        using var fixture = new Fixture();
        await fixture.Select("owner/a");
        using var service = fixture.Service();
        await service.GetAsync(Guid.NewGuid(), false, Ct);
        await service.WaitForRefreshAsync(Ct);
        var release = Signal();
        var entered = Signal();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var output = new EventOutput(release.Task, entered);
        var context = new DefaultHttpContext { RequestAborted = stop.Token };
        context.Response.Body = output;
        var stream = service.StreamAsync(context, Guid.NewGuid());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
            await service.PreferencesChangedAsync(Ct);
            await fixture.Select("owner/b");
            service.ConfigurationChanged();
            await service.GetAsync(Guid.NewGuid(), true, Ct);
            await service.WaitForRefreshAsync(Ct);
            release.TrySetResult();
            await output.Next("poll-schedule", Ct);
            var frames = output.Text.Split("\n\n").Where(f => f.StartsWith("event: ", StringComparison.Ordinal));
            foreach (var frame in frames)
            {
                var data = JsonNode.Parse(frame[(frame.IndexOf("data: ", StringComparison.Ordinal) + 6)..])!;
                Assert.Equal("github.com/owner/b", frame.StartsWith("event: state", StringComparison.Ordinal)
                    ? data["dashboard"].Text("repositoryId") : data.Text("repositoryId"));
            }
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            await stream;
        }
    }

    private static JsonObject Preferences(string repository)
    {
        var prefs = PreferenceStore.Defaults();
        var selected = RepositoryCatalog.Create(AccountId, repository);
        prefs["repositories"] = new JsonArray((JsonNode)selected);
        prefs["selectedRepository"] = selected.Text("id");
        prefs["accounts"] = new JsonObject { [AccountId] = new JsonObject { ["active"] = true } };
        return prefs;
    }

    private static IReadOnlyList<Account> Accounts(JsonObject prefs) => [Account(AccountId, "github.com", true)];

    private static Account Account(string id, string host, bool active) =>
        new(id, "alice", host, Secret, ["owner/a", "owner/b", "owner/unrelated"], active,
            new JsonObject { ["id"] = id, ["host"] = host, ["status"] = "ok", ["token"] = Secret, ["reason"] = Secret });

    private static JsonObject Dashboard(JsonObject prefs, int number = 1)
    {
        var selected = RepositoryCatalog.Selected(prefs)!;
        var repository = selected.Text("repository");
        return new JsonObject
        {
            ["authenticated"] = true,
            ["mode"] = prefs.Text("mode", "review"),
            ["fetchedAt"] = "2026-09-18T00:00:00Z",
            ["errors"] = new JsonArray(),
            ["counts"] = new JsonObject { ["prs"] = 1 },
            ["lanes"] = new JsonArray((JsonNode)new JsonObject
            {
                ["prs"] = new JsonArray((JsonNode)new JsonObject
                {
                    ["repository"] = repository,
                    ["number"] = number,
                    ["url"] = $"https://{selected.Text("host")}/{repository}/pull/{number}"
                })
            }),
            ["health"] = new JsonObject
            {
                ["items"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "source-a", ["state"] = "healthy" })
            }
        };
    }

    private static JsonObject PullRequest(JsonObject snapshot) => snapshot["dashboard"]!["lanes"]![0]!["prs"]![0]!.AsObject();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("dashboard-cache-tests-");
        public PreferenceStore Store { get; }
        public DashboardCache Cache { get; }

        public Fixture()
        {
            Store = new(_directory.FullName);
            Cache = new(_directory.FullName);
        }

        public Task Select(string repository) => Store.UpdateAsync(prefs =>
        {
            var selected = RepositoryCatalog.Create(AccountId, repository);
            var repositories = prefs["repositories"] as JsonArray ?? new JsonArray();
            if (repositories.Parent is null) { prefs["repositories"] = repositories; }
            if (!repositories.Objects().Any(r => r.Text("id") == selected.Text("id")))
            {
                repositories.Add((JsonNode)selected);
            }
            prefs["selectedRepository"] = selected.Text("id");
            prefs["accounts"] = new JsonObject { [AccountId] = new JsonObject { ["active"] = true } };
        }, Ct);

        public DashboardService Service(
            Func<JsonObject, CancellationToken, Task<IReadOnlyList<Account>>>? resolve = null,
            Func<IReadOnlyList<Account>, JsonObject, CancellationToken, Task<JsonObject>>? load = null) =>
            new(Store, resolve ?? ((prefs, ct) => Task.FromResult(Accounts(prefs))),
                load ?? ((accounts, prefs, ct) => Task.FromResult(Dashboard(prefs))), NullLogger<DashboardService>.Instance);

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class EventOutput(Task? pause = null, TaskCompletionSource? entered = null) : Stream
    {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        private readonly StringBuilder _text = new();
        private string _pending = "";
        public string Text { get { lock (_text) { return _text.ToString(); } } }

        public async Task<JsonObject> Next(string name, CancellationToken ct, Func<JsonObject, bool>? match = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                while (_pending.IndexOf("\n\n", StringComparison.Ordinal) is var index && index >= 0)
                {
                    var frame = _pending[..index];
                    _pending = _pending[(index + 2)..];
                    if (!frame.StartsWith($"event: {name}\n", StringComparison.Ordinal)) { continue; }
                    var data = JsonNode.Parse(frame[(frame.IndexOf("data: ", StringComparison.Ordinal) + 6)..])!.AsObject();
                    if (match is null || match(data)) { return data; }
                }
                _pending += await _chunks.Reader.ReadAsync(timeout.Token);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (pause is not null)
            {
                entered?.TrySetResult();
                await pause.WaitAsync(cancellationToken);
            }
            var text = Encoding.UTF8.GetString(buffer.Span);
            lock (_text) { _text.Append(text); }
            _chunks.Writer.TryWrite(text);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
