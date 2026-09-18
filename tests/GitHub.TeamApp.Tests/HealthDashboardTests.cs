// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GitHub.TeamApp.Tests;

public class HealthDashboardTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.Parse("2026-08-06T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GitHubPaginatesHistoryAndPinnedContextsAndExplainsDependabotAutoMerge()
    {
        var requests = new List<JsonObject>();
        using var http = Http(async body =>
        {
            requests.Add((JsonObject)body.DeepClone());
            await Task.CompletedTask;
            if (body.Text("query").Contains("RepositoryHealthHistory", StringComparison.Ordinal))
            {
                return Data(new JsonObject
                {
                    ["defaultBranchRef"] = new JsonObject
                    {
                        ["target"] = new JsonObject { ["history"] = Connection([Commit("green", "SUCCESS", "2026-08-01T12:00:00Z")]) }
                    }
                });
            }
            if (body.Text("query").Contains("RepositoryHealthContexts", StringComparison.Ordinal))
            {
                return Data(new JsonObject
                {
                    ["object"] = new JsonObject
                    {
                        ["oid"] = "head",
                        ["statusCheckRollup"] = new JsonObject { ["contexts"] = Connection([Check("Check 101", "FAILURE")]) }
                    }
                });
            }
            var repository = GitHubRepository("microsoft/aspire", "FAILURE");
            var branch = repository["defaultBranchRef"]!;
            branch["historyTarget"]!["history"] = Connection([Commit("head", "FAILURE"), Commit("previous", "ERROR")], "history-2");
            branch["head"]!["statusCheckRollup"]!["contexts"] = Connection([Check("Build Windows", "SUCCESS")], "contexts-2");
            branch["head"]!["associatedPullRequests"]!["nodes"] = new JsonArray(new JsonObject
            {
                ["number"] = 1871,
                ["url"] = "https://github.com/microsoft/aspire/pull/1871",
                ["mergedAt"] = "2026-08-06T11:59:00Z",
                ["author"] = new JsonObject { ["login"] = "dependabot[bot]" },
                ["autoMergeRequest"] = new JsonObject
                {
                    ["enabledAt"] = "2026-08-06T11:00:00Z",
                    ["enabledBy"] = new JsonObject { ["login"] = "policy-service[bot]" },
                    ["mergeMethod"] = "SQUASH"
                }
            });
            return Data(repository);
        });
        var dashboard = Dashboard(http);
        var item = await dashboard.LoadGitHubRepositoryAsync(Account("microsoft/aspire"), "microsoft/aspire", s_now, CancellationToken);

        Assert.Equal(new string?[] { null, "history-2", "contexts-2" }, requests.Select(r => r["variables"]?["after"]?.GetValue<string>()));
        Assert.Equal("head", requests[2]["variables"].Text("oid"));
        Assert.Equal("failing", item.Text("state"));
        Assert.Equal(2, item.Number("failureStreak"));
        Assert.Equal(5, item.Number("daysSinceSuccess"));
        Assert.Equal(3, item.Number("historyExamined"));
        Assert.Equal(["Check 101"], item["failedChecks"].Objects().Select(c => c.Text("name")));
        Assert.Equal("policy-service[bot]", item["linkedPullRequest"]!["autoMerge"].Text("enabledBy"));
        Assert.Equal(["dependabot_auto_merge", "failing_checks", "commit_failure_streak", "last_success_age"], Codes(item));
        Assert.Equal("Auto-merged PR #1871", item["evidence"].Objects().First().Text("label"));
        Assert.True(item.Flag("canOpenRepoSession"));
    }

    [Fact]
    public async Task GitHubCachesOlderHistoryButRefreshesCurrentRollups()
    {
        var initial = 0;
        var history = 0;
        using var http = Http(body =>
        {
            if (body.Text("query").Contains("RepositoryHealthHistory", StringComparison.Ordinal))
            {
                history++;
                return Task.FromResult(Data(new JsonObject
                {
                    ["defaultBranchRef"] = new JsonObject
                    {
                        ["target"] = new JsonObject { ["history"] = Connection([Commit("older", "FAILURE")]) }
                    }
                }));
            }
            initial++;
            var repo = GitHubRepository("microsoft/aspire", initial < 3 ? "FAILURE" : "SUCCESS");
            repo["defaultBranchRef"]!["historyTarget"]!["history"] = Connection([Commit("head", initial < 3 ? "FAILURE" : "SUCCESS")], "older");
            return Task.FromResult(Data(repo));
        });
        var dashboard = Dashboard(http);
        var first = await dashboard.LoadGitHubRepositoryAsync(Account("microsoft/aspire"), "microsoft/aspire", s_now, CancellationToken);
        var second = await dashboard.LoadGitHubRepositoryAsync(Account("microsoft/aspire"), "microsoft/aspire", s_now, CancellationToken);
        var third = await dashboard.LoadGitHubRepositoryAsync(Account("microsoft/aspire"), "microsoft/aspire", s_now, CancellationToken);
        Assert.Equal(3, initial);
        Assert.Equal(1, history);
        Assert.Equal(2, first.Number("historyExamined"));
        Assert.Equal(2, second.Number("historyExamined"));
        Assert.Null(second["lastSuccessAt"]);
        Assert.Equal(1, third.Number("historyExamined"));
        Assert.Equal("healthy", third.Text("state"));
        Assert.Equal(0, third.Number("failureStreak"));
    }

    [Fact]
    public async Task GitHubBoundsHistoryAtTenPages()
    {
        var queries = 0;
        using var http = Http(body =>
        {
            var page = ++queries;
            if (body.Text("query").Contains("RepositoryHealthHistory", StringComparison.Ordinal))
            {
                return Task.FromResult(Data(new JsonObject
                {
                    ["defaultBranchRef"] = new JsonObject
                    {
                        ["target"] = new JsonObject { ["history"] = Connection([Commit($"commit-{page}", "FAILURE")], $"cursor-{page}") }
                    }
                }));
            }
            var repo = GitHubRepository("org/repo", "FAILURE");
            repo["defaultBranchRef"]!["historyTarget"]!["history"] = Connection([Commit("head", "FAILURE")], "cursor-1");
            return Task.FromResult(Data(repo));
        });
        var item = await Dashboard(http).LoadGitHubRepositoryAsync(Account("org/repo"), "org/repo", s_now, CancellationToken);
        Assert.Equal(10, queries);
        Assert.Equal(10, item.Number("historyExamined"));
        Assert.True(item.Flag("successSearchTruncated"));
        Assert.Equal("No successful validation was found in the first 10 default-branch commits.",
            item["reasons"].Objects().Last().Text("summary"));
    }

    [Theory]
    [InlineData("wrong-head", null, "GitHub check context pagination returned an unexpected commit")]
    [InlineData("head", "again", "GitHub check context pagination returned an invalid cursor")]
    public async Task GitHubRejectsInvalidContextPagination(string oid, string? nextCursor, string message)
    {
        using var http = Http(body =>
        {
            if (body.Text("query").Contains("RepositoryHealthContexts", StringComparison.Ordinal))
            {
                return Task.FromResult(Data(new JsonObject
                {
                    ["object"] = new JsonObject
                    {
                        ["oid"] = oid,
                        ["statusCheckRollup"] = new JsonObject { ["contexts"] = Connection([], nextCursor) }
                    }
                }));
            }
            var repo = GitHubRepository("org/repo", "SUCCESS");
            repo["defaultBranchRef"]!["head"]!["statusCheckRollup"]!["contexts"] = Connection([], "again");
            return Task.FromResult(Data(repo));
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            Dashboard(http).LoadGitHubRepositoryAsync(Account("org/repo"), "org/repo", s_now, CancellationToken));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task GitHubDistinguishesPendingSkippedCanceledAndLegacyChecks()
    {
        var repository = GitHubRepository("org/repo", "PENDING");
        var running = Check("Still running", "FAILURE");
        running["status"] = "IN_PROGRESS";
        repository["defaultBranchRef"]!["head"]!["statusCheckRollup"]!["contexts"] = Connection(
        [
            running, Check("Skipped", "SKIPPED"), Check("Neutral", "NEUTRAL"), Check("Canceled", "CANCELLED"),
            new JsonObject { ["__typename"] = "StatusContext", ["context"] = "legacy", ["state"] = "ERROR", ["targetUrl"] = "https://example.com" }
        ]);
        using var http = Http(_ => Task.FromResult(Data(repository)));
        var item = await Dashboard(http).LoadGitHubRepositoryAsync(Account("org/repo"), "org/repo", s_now, CancellationToken);
        Assert.Equal("running", item.Text("state"));
        Assert.Equal(["Canceled", "legacy"], item["failedChecks"].Objects().Select(c => c.Text("name")));
        Assert.Equal(["degraded", "failing"], item["failedChecks"].Objects().Select(c => c.Text("state")));
        Assert.Equal("checks_running", Codes(item)[0]);
    }

    [Fact]
    public async Task GitHubHandlesMissingDefaultBranchAndEnterpriseWithoutDotcomMapping()
    {
        Uri? requested = null;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requested = request.RequestUri;
            return Task.FromResult(Response(Data(new JsonObject
            {
                ["nameWithOwner"] = "org/repo",
                ["url"] = "https://github.example.test/org/repo",
                ["defaultBranchRef"] = null
            })));
        }));
        var account = Account("org/repo") with { Host = "github.example.test" };
        var item = await Dashboard(http).LoadGitHubRepositoryAsync(account, "org/repo", s_now, CancellationToken);
        Assert.Equal("https://github.example.test/api/graphql", requested?.AbsoluteUri);
        Assert.Equal("github:github.example.test/org/repo", item.Text("id"));
        Assert.Equal("unknown", item.Text("state"));
        Assert.Null(item["mappedRepository"]);
        Assert.False(item.Flag("canOpenRepoSession"));
        Assert.Equal(["no_default_branch"], Codes(item));
    }

    [Fact]
    public void ReasonsRequireAutoMergeEvidenceBeforeBlamingDependabot()
    {
        var reasons = HealthDashboard.GitHubReasons("failing", [Check("Build", "FAILURE")], 0,
            new JsonObject { ["number"] = 10, ["author"] = "dependabot[bot]", ["autoMerge"] = null }, null, 0, false, s_now);
        Assert.Equal(["failing_checks"], reasons.Objects().Select(r => r.Text("code")));
    }

    [Fact]
    public async Task DashboardDoesNotDiscoverAzurePipelinesForGitHubRepositories()
    {
        using var http = Http(_ => Task.FromResult(Data(GitHubRepository("microsoft/aspire", "SUCCESS"))));
        var azure = Azure((_, _, _) => throw new InvalidOperationException("Unconfigured Azure providers must not be queried."));
        var result = await Dashboard(http, azure).LoadAsync([Account("microsoft/aspire")], new JsonObject(), CancellationToken);
        Assert.True(result.Flag("authenticated"));
        Assert.False(result.Flag("loading"));
        Assert.False(result["health"].Flag("loading"));
        Assert.Equal("health", result.Text("mode"));
        Assert.Equal(1, result["health"]!["counts"].Number("healthy"));
        Assert.Equal(["github"], result["health"]!["items"].Objects().Select(i => i.Text("provider")));
        Assert.Empty(result["errors"].Strings());
        Assert.Equal(["not_configured"],
            result["health"]!["providers"].Objects().Where(p => p.Text("provider") == "azure-devops").Select(p => p.Text("status")));
    }

    [Theory]
    [InlineData("github.com")]
    [InlineData("ghe.example.com")]
    public async Task DashboardQueriesOnlyScopedAccountsAndRepositoriesWithoutCachedScopeLeakage(string host)
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var repository = $"{body["variables"].Text("owner")}/{body["variables"].Text("name")}";
            requests.Add($"{request.Headers.Authorization!.Parameter}:{repository}");
            Assert.Equal(host == "github.com" ? "https://api.github.com/graphql" : $"https://{host}/api/graphql", request.RequestUri!.AbsoluteUri);
            var repo = GitHubRepository(repository, "SUCCESS");
            repo["url"] = $"https://{host}/{repository}";
            return Response(Data(repo));
        }));
        var azure = Azure((_, _, _) => throw new InvalidOperationException("No Azure discovery expected."));
        var dashboard = Dashboard(http, azure);
        foreach (var repository in new[] { "contoso/widgets", "fabrikam/tools" })
        {
            var account = Account(repository) with { Host = host, Token = repository };
            var result = await dashboard.LoadAsync(
                [account, Account("ignored/inactive") with { Active = false }, Account("ignored/no-token") with { Token = "" }],
                new JsonObject(), CancellationToken);
            var item = Assert.Single(result["health"]!["items"].Objects());
            Assert.Equal(repository, item.Text("repository"));
            Assert.Equal([repository], result["repos"].Strings());
            Assert.Equal(["octo"], result["viewers"].Strings());
            Assert.Empty(result["errors"].Strings());
        }
        Assert.Equal(["contoso/widgets:contoso/widgets", "fabrikam/tools:fabrikam/tools"], requests);
    }

    [Fact]
    public async Task EmptyRepositorySelectionAndMissingTokensDoNotMonitorAnything()
    {
        using var http = Http(_ => throw new InvalidOperationException("No GitHub queries expected."));
        var azure = Azure((_, _, _) => throw new InvalidOperationException("No Azure queries expected."));
        var dashboard = Dashboard(http, azure);
        var emptySelection = await dashboard.LoadAsync([Account()], new JsonObject(), CancellationToken);
        Assert.Empty(emptySelection["repos"].Strings());
        Assert.Empty(emptySelection["health"]!["items"].Objects());
        var noToken = await dashboard.LoadAsync([Account("contoso/widgets") with { Token = "" }], new JsonObject(), CancellationToken);
        Assert.False(noToken.Flag("authenticated"));
        Assert.Empty(noToken["health"]!["items"].Objects());
    }

    [Theory]
    [InlineData("other/repo", "https://github.com/other/repo")]
    [InlineData("contoso/widgets", "https://ghe.example.com/contoso/widgets")]
    [InlineData("contoso/widgets", "https://github.com/contoso/widgets-extra")]
    public async Task ForeignGitHubRepositoryResponsesRemainExplicitFailures(string name, string url)
    {
        var repo = GitHubRepository(name, "SUCCESS");
        repo["url"] = url;
        using var http = Http(_ => Task.FromResult(Data(repo)));
        var result = await Dashboard(http).LoadAsync([Account("contoso/widgets")], new JsonObject(), CancellationToken);
        Assert.Empty(result["health"]!["items"].Objects());
        Assert.Equal(["contoso/widgets: GitHub returned a different repository than requested"], result["errors"].Strings());
        Assert.Equal("unavailable", result["providers"].Objects().First().Text("status"));
    }

    [Fact]
    public async Task ForeignAssociatedPullRequestsCannotBecomeRegressionEvidence()
    {
        var repo = GitHubRepository("contoso/widgets", "FAILURE");
        repo["defaultBranchRef"]!["head"]!["associatedPullRequests"]!["nodes"] = JsonData.Array(
            new[] { "https://github.com/other/repo/pull/1", "https://ghe.example.com/contoso/widgets/pull/1", "https://github.com/contoso/widgets-extra/pull/1" }
                .Select(url => new JsonObject
                {
                    ["number"] = 1,
                    ["url"] = url,
                    ["mergedAt"] = "2026-08-06T11:59:00Z",
                    ["author"] = new JsonObject { ["login"] = "dependabot[bot]" },
                    ["autoMergeRequest"] = new JsonObject { ["enabledAt"] = "2026-08-06T11:00:00Z" }
                }));
        using var http = Http(_ => Task.FromResult(Data(repo)));
        var item = await Dashboard(http).LoadGitHubRepositoryAsync(Account("contoso/widgets"), "contoso/widgets", s_now, CancellationToken);
        Assert.Null(item["linkedPullRequest"]);
        Assert.DoesNotContain("dependabot_auto_merge", Codes(item));
        Assert.Empty(item["evidence"].Objects());
    }

    [Fact]
    public async Task DashboardSuppressesFailedDuplicateCredentialsAndSortsOtherErrors()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var name = body["variables"].Text("name");
            if (name == "healthy" && request.Headers.Authorization?.Parameter == "good")
            {
                return Response(Data(GitHubRepository("org/healthy", "SUCCESS")));
            }
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }));
        var accounts = new[]
        {
            Account("org/z", "org/a", "org/healthy"),
            Account("org/healthy") with { Id = "second", Token = "good" },
            Account("org/inactive") with { Active = false }
        };
        var result = await Dashboard(http).LoadAsync(accounts, new JsonObject(), CancellationToken);
        Assert.Equal(["org/a: GitHub API 403 Forbidden", "org/z: GitHub API 403 Forbidden"], result["errors"].Strings());
        Assert.Equal(1, result["health"]!["counts"].Number("total"));
        Assert.Equal(["org/z", "org/a", "org/healthy"], result["repos"].Strings());
        Assert.Equal("partial", result["providers"].Objects().First().Text("status"));
    }

    [Fact]
    public async Task DashboardKeepsConfiguredUnavailableCardsAndEmptyStateContract()
    {
        using var http = Http(_ => throw new InvalidOperationException("Unexpected GitHub query."));
        var missing = Azure((_, _, _) => throw new AzureDevOpsException("azdo_auth_required", "Run az login."));
        var dashboard = Dashboard(http, missing);
        var empty = await dashboard.LoadAsync([], new JsonObject(), CancellationToken);
        Assert.False(empty.Flag("authenticated"));
        Assert.Equal("No health sources are configured. Enable a GitHub account or add an Azure DevOps pipeline.", empty.Text("message"));
        Assert.Empty(empty["health"]!["items"].Objects());
        var configured = await dashboard.LoadAsync([], new JsonObject { ["azurePipelines"] = new JsonArray(Pipeline()) }, CancellationToken);
        var item = Assert.Single(configured["health"]!["items"].Objects());
        Assert.True(configured.Flag("authenticated"));
        Assert.Equal("unavailable", item.Text("state"));
        Assert.Equal(1602, item.Number("definitionId"));
        Assert.Equal(["azdo_auth_required"], Codes(item));
        Assert.Equal(1, configured["health"]!["counts"].Number("unavailable"));
    }

    [Fact]
    public void GroupingRequiresUniqueNamesOrExplicitProviderMappingAndPreservesSavedOrder()
    {
        var github = GitHubItem("microsoft/aspire");
        var azure = new JsonObject
        {
            ["id"] = "azdo:org/project/42",
            ["provider"] = "azure-devops",
            ["name"] = "CI",
            ["repository"] = new JsonObject { ["name"] = "microsoft-aspire" }
        };
        var docs = GitHubItem("microsoft/aspire.dev");
        var sources = HealthDashboard.AssociateSources([github, docs, azure]);
        Assert.Equal("repository:github.com/microsoft/aspire", sources[2].Text("groupId"));
        Assert.Equal("name", sources[2].Text("groupMatch"));
        var ordered = HealthDashboard.ApplyOrder(sources, ["azdo:org/project/42", docs.Text("id"), github.Text("id")]);
        Assert.Equal([azure.Text("id"), github.Text("id"), docs.Text("id")], ordered.Select(i => i.Text("id")));
        azure["repository"]!["name"] = "aspire";
        sources = HealthDashboard.AssociateSources([github, GitHubItem("contoso/aspire"), azure]);
        Assert.Equal("source:azdo:org/project/42", sources[2].Text("groupId"));
        Assert.Null(sources[2]["groupMatch"]);
        azure["mappedRepository"] = "microsoft/aspire";
        sources = HealthDashboard.AssociateSources([github, GitHubItem("contoso/aspire"), azure]);
        Assert.Equal("repository:github.com/microsoft/aspire", sources[2].Text("groupId"));
        Assert.Equal("provider", sources[2].Text("groupMatch"));
    }

    [Fact]
    public void CountsNormalizeUnexpectedStates()
    {
        var counts = HealthDashboard.Counts([new() { ["state"] = "healthy" }, new() { ["state"] = "failing" }, new() { ["state"] = "unexpected" }]);
        Assert.Equal(3, counts.Number("total"));
        Assert.Equal(1, counts.Number("healthy"));
        Assert.Equal(1, counts.Number("failing"));
        Assert.Equal(1, counts.Number("unknown"));
        Assert.Equal(0, counts.Number("running"));
        Assert.Equal(0, counts.Number("degraded"));
        Assert.Equal(0, counts.Number("unavailable"));
    }

    [Theory]
    [InlineData("https://dev.azure.com/dnceng/internal/_build?definitionId=1602", 1602, 0)]
    [InlineData("https://dev.azure.com/dnceng/internal/_build/results?buildId=3040201", 0, 3040201)]
    [InlineData("https://dnceng.visualstudio.com/internal/_build?definitionId=1602", 1602, 0)]
    public void ParsesAzurePipelineCoordinates(string url, int definition, int build)
    {
        var parsed = AzureDevOps.ParsePipelineUrl(url);
        Assert.Equal("https://dev.azure.com/dnceng", parsed.Text("organization"));
        Assert.Equal("dnceng", parsed.Text("organizationName"));
        Assert.Equal("internal", parsed.Text("project"));
        Assert.Equal(definition, parsed.Number("definitionId"));
        Assert.Equal(build, parsed.Number("buildId"));
    }

    [Theory]
    [InlineData("https://dev.azure.com.evil.example/dnceng/internal/_build?definitionId=1602", "invalid_pipeline_host")]
    [InlineData("http://dev.azure.com/dnceng/internal/_build?definitionId=1602", "invalid_pipeline_url")]
    [InlineData("https://user:secret@dev.azure.com/dnceng/internal/_build?definitionId=1602", "invalid_pipeline_url")]
    [InlineData("https://dev.azure.com/dnceng/internal/_build", "missing_pipeline_id")]
    [InlineData("https://dev.azure.com/dnceng/internal/_build?definitionId=-1", "missing_pipeline_id")]
    [InlineData("https://dev.azure.com/dnceng/internal/_build?definitionId=1.5", "missing_pipeline_id")]
    [InlineData("https://dev.azure.com/dnceng/in%2Fternal/_build?definitionId=1", "invalid_pipeline_url")]
    [InlineData("https://dev.azure.com/dnceng/in%5Cternal/_build?definitionId=1", "invalid_pipeline_url")]
    [InlineData("https://dev.azure.com/dnceng/in%00ternal/_build?definitionId=1", "invalid_pipeline_url")]
    [InlineData("https://dev.azure.com/dnceng/in%ZZternal/_build?definitionId=1", "invalid_pipeline_url")]
    [InlineData("https://dev.azure.com/dnceng/in%FFternal/_build?definitionId=1", "invalid_pipeline_url")]
    public void RejectsUntrustedAzureCoordinates(string url, string code)
    {
        Assert.Equal(code, Assert.Throws<AzureDevOpsException>(() => AzureDevOps.ParsePipelineUrl(url)).Code);
    }

    [Fact]
    public void PipelineRemovalKeysAreStable()
    {
        const string id = "azdo:dnceng/other project/1602";
        var key = AzureDevOps.RemovalKey(id);
        Assert.Equal("azp1_YXpkbzpkbmNlbmcvb3RoZXIgcHJvamVjdC8xNjAy", key);
        Assert.Equal(id, AzureDevOps.IdFromRemovalKey(key!));
        Assert.Null(AzureDevOps.IdFromRemovalKey(key + "x"));
        Assert.Null(AzureDevOps.RemovalKey("not-an-azure-pipeline"));
    }

    [Fact]
    public async Task ResolvesBuildUrlToNormalizedDefinitionAndKeepsBranchArgumentsLiteral()
    {
        var calls = new List<string[]>();
        var azure = Azure((args, _, _) =>
        {
            calls.Add(args.ToArray());
            return Output(args[1] == "build" ? new JsonObject { ["definition"] = new JsonObject { ["id"] = 1602 } } : Definition());
        });
        var pipeline = await azure.ResolvePipelineAsync("https://dev.azure.com/dnceng/internal/_build/results?buildId=3040201",
            "feature/%PATH% & stuff", CancellationToken);
        Assert.Equal("azdo:dnceng/internal/1602", pipeline.Text("id"));
        Assert.Equal("microsoft-aspire", pipeline.Text("name"));
        Assert.Equal("refs/heads/feature/%PATH% & stuff", pipeline.Text("branch"));
        Assert.Equal("https://dev.azure.com/dnceng/internal/_build?definitionId=1602", pipeline.Text("url"));
        Assert.Equal(["pipelines", "build", "show", "--id", "3040201", "--organization", "https://dev.azure.com/dnceng", "--project", "internal"], calls[0]);
        Assert.Equal(["pipelines", "show", "--id", "1602", "--organization", "https://dev.azure.com/dnceng", "--project", "internal"], calls[1]);
    }

    [Fact]
    public async Task RejectsConflictingCachedCoordinatesBeforeInvokingAzure()
    {
        var calls = 0;
        var azure = Azure((_, _, _) =>
        {
            calls++;
            return Output(null);
        });
        var pipeline = Pipeline();
        pipeline["definitionId"] = 42;
        var error = await Assert.ThrowsAsync<AzureDevOpsException>(() => azure.LoadPipelineHealthAsync(pipeline, s_now, CancellationToken));
        Assert.Equal("invalid_pipeline_url", error.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConfiguredPipelinesBoundConcurrentCliCallsWithoutDiscovery()
    {
        var definitions = Enumerable.Range(0, 8).Select(i => Definition(2000 + i, $"Repo {i} Release Production", $"repo-{i}", $"repo-{i}")).ToArray();
        var active = 0;
        var maximum = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var azure = Azure(async (args, text, ct) =>
        {
            Assert.False(text);
            Assert.Equal("pipelines", args[0]);
            if (args[1] == "build")
            {
                return new JsonArray(Build(42, "succeeded")).ToJsonString();
            }
            Assert.Equal("show", args[1]);
            var count = Interlocked.Increment(ref active);
            InterlockedExtensionsMax(ref maximum, count);
            if (count == 6)
            {
                gate.TrySetResult();
            }
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Interlocked.Decrement(ref active);
            return definitions.Single(d => d.Number("id").ToString(System.Globalization.CultureInfo.InvariantCulture) == Argument(args, "--id")).ToJsonString();
        });
        var results = await Task.WhenAll(definitions.Select(definition => azure.LoadPipelineHealthAsync(
            new JsonObject { ["url"] = $"https://dev.azure.com/contoso/product/_build?definitionId={definition.Number("id")}" },
            s_now, CancellationToken)));
        Assert.Equal(8, results.Length);
        Assert.All(results, result => Assert.Equal("healthy", result.Text("state")));
        Assert.Equal(6, maximum);
    }

    [Theory]
    [InlineData("az_cli_missing")]
    [InlineData("azdo_auth_required")]
    [InlineData("azdo_access_denied")]
    [InlineData("azdo_query_failed")]
    public async Task ConfiguredPipelineFailuresAreReportedWithoutDiscoveryFallback(string code)
    {
        var count = 0;
        var azure = Azure((args, text, _) =>
        {
            Assert.False(text);
            Assert.Equal("pipelines", args[0]);
            Assert.Equal("show", args[1]);
            Assert.Equal("1602", Argument(args, "--id"));
            Interlocked.Increment(ref count);
            throw new AzureDevOpsException(code, "Expected unavailable credential.");
        });
        using var http = Http(_ => throw new InvalidOperationException("No GitHub query expected."));
        var result = await Dashboard(http, azure).LoadAsync([], new JsonObject { ["azurePipelines"] = new JsonArray(Pipeline()) }, CancellationToken);
        var item = Assert.Single(result["health"]!["items"].Objects());
        Assert.Equal("unavailable", item.Text("state"));
        Assert.Equal([code], Codes(item));
        Assert.Equal("unavailable", result["providers"].Objects().Single(provider => provider.Text("provider") == "azure-devops").Text("status"));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contoso")]
    public async Task MissingPipelineCoordinatesNeverFallBackToAzureCliDefaults(string organization)
    {
        var azure = Azure((_, _, _) => throw new InvalidOperationException("No Azure query before validating explicit coordinates."));
        var error = await Assert.ThrowsAsync<AzureDevOpsException>(() => azure.LoadPipelineHealthAsync(
            new JsonObject { ["organizationName"] = organization, ["definitionId"] = 42 }, s_now, CancellationToken));
        Assert.Equal("invalid_pipeline_url", error.Code);
    }

    [Fact]
    public async Task DashboardLoadsOnlyExplicitPipelinesWithoutDiscoveringCuratedMirrors()
    {
        var buildLists = 0;
        var azure = Azure((args, text, _) =>
        {
            Assert.False(text);
            Assert.False(args[1] == "list");
            if (args[1] == "show")
            {
                Assert.Equal("1602", Argument(args, "--id"));
                return Output(Definition());
            }
            Interlocked.Increment(ref buildLists);
            return Output(new JsonArray(Build(42, "succeeded")));
        });
        using var http = Http(_ => Task.FromResult(Data(GitHubRepository("microsoft/aspire", "SUCCESS"))));
        var result = await Dashboard(http, azure).LoadAsync([Account("microsoft/aspire")],
            new JsonObject { ["azurePipelines"] = new JsonArray(Pipeline()) }, CancellationToken);
        var items = result["health"]!["items"].Objects().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(1, buildLists);
        Assert.All(items, item => Assert.Equal("repository:github.com/microsoft/aspire", item.Text("groupId")));
        Assert.False(items.Single(i => i.Text("id") == "azdo:dnceng/internal/1602").Flag("discovered"));
    }

    [Fact]
    public async Task DashboardUsesScopedPipelinesWithoutMutatingPreferencesOrRetainingOtherScopes()
    {
        var calls = new List<string>();
        var azure = Azure((args, text, _) =>
        {
            Assert.False(text);
            Assert.Equal("pipelines", args[0]);
            Assert.Contains(args[1], new[] { "show", "build" });
            var id = Argument(args, args[1] == "show" ? "--id" : "--definition-ids");
            calls.Add(id);
            return Output(args[1] == "show"
                ? Definition(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), $"Pipeline {id}", $"repo-{id}", $"widgets-{id}")
                : new JsonArray(Build(42, "succeeded")));
        });
        using var http = Http(_ => throw new InvalidOperationException("No GitHub queries expected."));
        var dashboard = Dashboard(http, azure);
        foreach (var id in new[] { 42, 43 })
        {
            var prefs = new JsonObject
            {
                ["azurePipelines"] = new JsonArray(new JsonObject
                {
                    ["id"] = $"azdo:dnceng/internal/{id}",
                    ["definitionId"] = id,
                    ["url"] = $"https://dev.azure.com/dnceng/internal/_build?definitionId={id}",
                    ["repositoryId"] = $"github:github.com/contoso/widgets-{id}"
                })
            };
            var original = prefs.DeepClone();
            var result = await dashboard.LoadAsync([], prefs, CancellationToken);
            Assert.True(result.Flag("authenticated"));
            Assert.Equal(id, Assert.Single(result["health"]!["items"].Objects()).Number("definitionId"));
            Assert.Empty(result["errors"].Strings());
            Assert.True(JsonNode.DeepEquals(original, prefs));
        }
        Assert.Equal(["42", "42", "43", "43"], calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPipelinesUseConfiguredCoordinatesWithoutDiscoveryEvenWithLegacyProvenance(bool legacy)
    {
        var calls = new List<string>();
        var azure = Azure((args, text, _) =>
        {
            Assert.False(text);
            Assert.Equal("pipelines", args[0]);
            Assert.Equal("https://dev.azure.com/contoso", Argument(args, "--organization"));
            Assert.Equal("product", Argument(args, "--project"));
            calls.Add(args[1]);
            if (args[1] == "show")
            {
                Assert.Equal("42", Argument(args, "--id"));
                return Output(Definition(42, "Widgets CI", "widget-id", "widgets", "refs/heads/stable"));
            }
            Assert.Equal("build", args[1]);
            Assert.Equal("42", Argument(args, "--definition-ids"));
            Assert.Equal("refs/heads/stable", Argument(args, "--branch"));
            return Output(new JsonArray(Build(101, "succeeded")));
        });
        var config = new JsonObject
        {
            ["url"] = "https://dev.azure.com/contoso/product/_build?definitionId=42",
            ["discovered"] = legacy,
            ["discovery"] = legacy ? new JsonObject
            {
                ["kind"] = "official-default",
                ["repository"] = "microsoft/aspire",
                ["azureRepository"] = "microsoft-aspire",
                ["pipelineCandidates"] = 3
            } : null
        };
        var original = config.DeepClone();
        var item = await azure.LoadPipelineHealthAsync(config, s_now, CancellationToken);
        Assert.Equal("azdo:contoso/product/42", item.Text("id"));
        Assert.Equal("healthy", item.Text("state"));
        Assert.Equal(legacy, item.Flag("discovered"));
        Assert.True(JsonNode.DeepEquals(config, original));
        Assert.Equal(["show", "build"], calls);
    }

    [Fact]
    public async Task AzureHealthIncludesBlockedDeploymentTimelineAndLastSuccessEvidence()
    {
        var calls = new List<string[]>();
        var azure = Azure((args, _, _) =>
        {
            calls.Add(args.ToArray());
            if (args[1] == "show")
            {
                return Output(Definition());
            }
            if (args[0] == "devops")
            {
                return Output(new JsonObject
                {
                    ["records"] = new JsonArray(
                        Record("Stage", "Build", "failed", 1),
                        Record("Task", "Publish Artifacts", "failed", 2, "Not found PathToPublish: artifacts/packages"),
                        Record("Task", "Publish Artifacts", "failed", 2, "Not found PathToPublish: artifacts/packages"),
                        Record("Stage", "Deploy production", "skipped", 3))
                });
            }
            return Output(new JsonArray(Build(30, "failed"), Build(29, "partiallySucceeded", "2026-08-05T12:00:00Z"), Build(28, "succeeded", "2026-08-01T12:00:00Z")));
        });
        var item = await azure.LoadPipelineHealthAsync(Pipeline(), s_now, CancellationToken);
        Assert.Equal("failing", item.Text("state"));
        Assert.Equal(5, item.Number("daysSinceSuccess"));
        Assert.Equal(2, item.Number("failureStreak"));
        Assert.Equal(["Build", "Publish Artifacts"], item["failedRecords"].Objects().Select(r => r.Text("name")));
        Assert.Equal(["upstream_stage_blocked_deployment", "failed_timeline_record", "build_failure_streak", "last_success_age"], Codes(item));
        Assert.Equal("Task Publish Artifacts failed: Not found PathToPublish: artifacts/packages", item["reasons"].Objects().ElementAt(1).Text("summary"));
        Assert.False(item.Flag("canOpenRepoSession"));
        var timeline = Assert.Single(calls, c => c[0] == "devops");
        Assert.Equal("GET", Argument(timeline, "--http-method"));
        Assert.Equal("Timeline", Argument(timeline, "--resource"));
    }

    [Theory]
    [InlineData("succeeded", "healthy", "")]
    [InlineData("failed", "failing", "latest_build_failed")]
    [InlineData("partiallySucceeded", "degraded", "latest_build_degraded")]
    [InlineData("canceled", "failing", "latest_build_canceled")]
    [InlineData("cancelled", "failing", "latest_build_canceled")]
    [InlineData("none", "running", "build_running")]
    public async Task AzureReportsEveryBuildOutcome(string result, string state, string reason)
    {
        var azure = Azure((args, _, _) =>
        {
            if (args[1] == "show")
            {
                return Output(Definition());
            }
            return Output(args[0] == "devops" ? new JsonObject { ["records"] = new JsonArray() }
                : args.Contains("--result") ? new JsonArray() : new JsonArray(Build(42, result)));
        });
        var item = await azure.LoadPipelineHealthAsync(Pipeline(), s_now, CancellationToken);
        Assert.Equal(state, item.Text("state"));
        if (reason.Length == 0)
        {
            Assert.Empty(Codes(item));
        }
        else
        {
            Assert.Equal(reason, Codes(item)[0]);
        }
    }

    [Fact]
    public async Task AzureSortsByQueueTimeThenIdRatherThanCompletionTime()
    {
        var older = Build(40, "failed", "2026-08-06T13:00:00Z");
        older["queueTime"] = "2026-08-06T10:00:00Z";
        var newer = Build(41, "succeeded", "2026-08-06T12:00:00Z");
        newer["queueTime"] = "2026-08-06T11:00:00Z";
        var azure = Azure((args, _, _) => Output(args[1] == "show" ? Definition() : new JsonArray(older.DeepClone(), newer.DeepClone())));
        var item = await azure.LoadPipelineHealthAsync(Pipeline(), s_now, CancellationToken);
        Assert.Equal(41, item["latest"].Number("id"));
        Assert.Equal("healthy", item.Text("state"));
        Assert.Equal(0, item.Number("failureStreak"));
    }

    [Fact]
    public async Task AzureSearchesOlderSuccessAndReportsBoundedFailureStreak()
    {
        var searched = false;
        var azure = Azure((args, _, _) =>
        {
            if (args[1] == "show")
            {
                return Output(Definition());
            }
            if (args[0] == "devops")
            {
                throw new AzureDevOpsException("azdo_access_denied", "Access denied.");
            }
            if (args.Contains("--result"))
            {
                searched = true;
                Assert.Equal("succeeded", Argument(args, "--result"));
                Assert.Equal("1", Argument(args, "--top"));
                return Output(new JsonArray(Build(1, "succeeded", "2026-08-01T12:00:00Z")));
            }
            return Output(JsonData.Array(Enumerable.Range(0, 50).Select(i => Build(100 - i, "failed"))));
        });
        var item = await azure.LoadPipelineHealthAsync(Pipeline(), s_now, CancellationToken);
        Assert.True(searched);
        Assert.Equal(50, item.Number("failureStreak"));
        Assert.True(item.Flag("failureStreakLowerBound"));
        Assert.Equal(5, item.Number("daysSinceSuccess"));
        Assert.Equal(["latest_build_failed", "build_failure_streak", "last_success_age", "timeline_unavailable"], Codes(item));
        Assert.Equal("At least 50 consecutive completed builds have not succeeded.", item["reasons"].Objects().ElementAt(1).Text("summary"));
        Assert.Equal("Access denied.", item.Text("diagnosticsError"));
    }

    [Theory]
    [InlineData("https://github.com/microsoft/aspire.git", "microsoft/aspire")]
    [InlineData("https://github.contoso.example/microsoft/aspire.git", null)]
    [InlineData("https://github.com/contoso/aspire.git", null)]
    [InlineData("https://user:secret@github.com/microsoft/aspire.git", null)]
    public void AzureGithubMappingsRequireMatchingDotcomUrl(string url, string? expected)
    {
        Assert.Equal(expected, AzureDevOps.GitHubRepository(new JsonObject { ["name"] = "microsoft/aspire", ["type"] = "GitHub", ["url"] = url }));
    }

    [Fact]
    public void AzureErrorClassificationNeverReturnsRawCommandOutput()
    {
        Assert.Equal("azdo_auth_required", AzureDevOps.ClassifyCommandError("TF400813 authentication failed").Code);
        Assert.Equal("azdo_access_denied", AzureDevOps.ClassifyCommandError("403 forbidden").Code);
        Assert.Equal("azdo_extension_missing", AzureDevOps.ClassifyCommandError("az extension add --name azure-devops").Code);
        Assert.Equal("azdo_timeout", AzureDevOps.ClassifyCommandError("ETIMEDOUT").Code);
        Assert.Equal("The Azure DevOps query failed.", AzureDevOps.ClassifyCommandError("secret-value unknown response").Message);
        Assert.Equal("request Bearer [redacted] https://[redacted]@example.com/ [redacted]",
            HealthText.Redact("request Bearer abc.def https://name:password@example.com/ ghp_secret123"));
    }

    [Fact]
    public void WindowsAzureCliUsesBundledPythonInsteadOfBatchExpansion()
    {
        var bin = Path.Combine(AppContext.BaseDirectory, "azure-cli", "wbin");
        var shim = Path.Combine(bin, "az.cmd");
        var python = Path.GetFullPath(Path.Combine(bin, "..", "python.exe"));
        var (path, prefix) = AzureDevOps.ResolveCliCommand(true, $"\"{bin}\"", file => file == shim || file == python);
        Assert.Equal(python, path);
        Assert.Equal(["-IBm", "azure.cli"], prefix);
        var failure = Assert.Throws<AzureDevOpsException>(() =>
            AzureDevOps.ResolveCliCommand(true, bin, file => file == shim));
        Assert.Equal("az_cli_unsupported", failure.Code);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToUnavailableHealth()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var http = Http(_ => Task.FromResult(Data(GitHubRepository("org/repo", "SUCCESS"))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Dashboard(http).LoadAsync([Account("org/repo")], new JsonObject(), cancelled.Token));
    }

    private static Account Account(params string[] repos) => new("github:octo", "octo", "github.com", "test-token", repos, true, new JsonObject());
    private static AzureDevOps Azure(Func<IReadOnlyList<string>, bool, CancellationToken, Task<string>> run) => new(run);
    private static HealthDashboard Dashboard(HttpClient http, AzureDevOps? azure = null) =>
        new(http, NullLogger<HealthDashboard>.Instance, azure ?? Azure((_, _, _) =>
            throw new InvalidOperationException("No Azure pipelines configured.")), new Clock());
    private static string[] Codes(JsonObject item) => item["reasons"].Objects().Select(r => r.Text("code")).ToArray();
    private static Task<string> Output(JsonNode? node) => Task.FromResult(node?.ToJsonString() ?? "");
    private static string Argument(IReadOnlyList<string> args, string name) => args[Array.IndexOf(args.ToArray(), name) + 1];

    private static HttpClient Http(Func<JsonObject, Task<JsonObject>> response) => new(new Handler(async (request, ct) =>
        Response(await response(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject()))));
    private static HttpResponseMessage Response(JsonObject json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
    private static JsonObject Data(JsonObject repository) => new() { ["data"] = new JsonObject { ["repository"] = repository.DeepClone() } };
    private static JsonObject Connection(IEnumerable<JsonObject> nodes, string? cursor = null) =>
        new() { ["nodes"] = JsonData.Array(nodes), ["pageInfo"] = new JsonObject { ["hasNextPage"] = cursor is not null, ["endCursor"] = cursor } };
    private static JsonObject Commit(string oid, string state, string at = "2026-08-06T12:00:00Z") =>
        new() { ["oid"] = oid, ["committedDate"] = at, ["statusCheckRollup"] = new JsonObject { ["state"] = state } };
    private static JsonObject Check(string name, string conclusion) =>
        new() { ["__typename"] = "CheckRun", ["name"] = name, ["status"] = "COMPLETED", ["conclusion"] = conclusion, ["detailsUrl"] = "https://github.com/org/repo/actions/runs/1" };
    private static JsonObject GitHubItem(string repository) => new()
    {
        ["id"] = $"github:github.com/{repository}",
        ["provider"] = "github",
        ["name"] = repository,
        ["repository"] = repository,
        ["host"] = "github.com"
    };

    private static JsonObject GitHubRepository(string repository, string state)
    {
        var head = Commit("head", state);
        head["messageHeadline"] = "Update dependency";
        head["author"] = new JsonObject { ["user"] = new JsonObject { ["login"] = "octo" } };
        head["statusCheckRollup"]!["contexts"] = Connection([]);
        head["associatedPullRequests"] = new JsonObject { ["nodes"] = new JsonArray() };
        return new JsonObject
        {
            ["nameWithOwner"] = repository,
            ["url"] = $"https://github.com/{repository}",
            ["defaultBranchRef"] = new JsonObject
            {
                ["name"] = "main",
                ["head"] = head,
                ["historyTarget"] = new JsonObject { ["history"] = Connection([Commit("head", state)]) }
            }
        };
    }

    private static JsonObject Pipeline() => new()
    {
        ["id"] = "azdo:dnceng/internal/1602",
        ["url"] = "https://dev.azure.com/dnceng/internal/_build?definitionId=1602",
        ["definitionId"] = 1602
    };

    private static JsonObject Definition(int id = 1602, string name = "microsoft-aspire", string repositoryId = "repo-id",
        string repository = "microsoft-aspire", string branch = "refs/heads/main") => new()
        {
            ["id"] = id,
            ["name"] = name,
            ["queueStatus"] = "enabled",
            ["repository"] = new JsonObject
            {
                ["id"] = repositoryId,
                ["name"] = repository,
                ["type"] = "TfsGit",
                ["url"] = $"https://dev.azure.com/dnceng/internal/_git/{repository}",
                ["defaultBranch"] = branch
            }
        };

    private static JsonObject Build(int id, string result, string at = "2026-08-06T12:00:00Z") => new()
    {
        ["id"] = id,
        ["buildNumber"] = $"20260806.{id}",
        ["status"] = "completed",
        ["result"] = result,
        ["queueTime"] = at,
        ["finishTime"] = at,
        ["sourceVersion"] = $"sha-{id}",
        ["requestedFor"] = new JsonObject { ["displayName"] = "Build Service" }
    };

    private static JsonObject Record(string type, string name, string result, int order, string? error = null) => new()
    {
        ["type"] = type,
        ["name"] = name,
        ["result"] = result,
        ["state"] = "completed",
        ["order"] = order,
        ["issues"] = error is null ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "error", ["message"] = error })
    };

    private static void InterlockedExtensionsMax(ref int maximum, int value)
    {
        var previous = Volatile.Read(ref maximum);
        while (value > previous)
        {
            var observed = Interlocked.CompareExchange(ref maximum, value, previous);
            if (observed == previous)
            {
                return;
            }
            previous = observed;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => s_now;
    }
}
