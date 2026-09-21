// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GitHub.TeamApp.Tests;

public class GitHubDashboardTests
{
    [Theory]
    [InlineData("ship")]
    [InlineData("issues")]
    public async Task LoadsEveryPageAndKeepsLinkedIssuePullRequests(string mode)
    {
        var cursors = new ConcurrentQueue<string>();
        using var http = Client((request, body) =>
        {
            var after = body["variables"].Text("after");
            cursors.Enqueue(after);
            var node = mode == "issues" ? IssueNode(after.Length == 0 ? 1 : 2) : PrNode(after.Length == 0 ? 1 : 2);
            return Response(node, mode == "issues", after.Length == 0);
        });
        var result = await Dashboard(http).LoadAsync([Account()], Prefs(mode), TestContext.Current.CancellationToken);

        Assert.Equal(["", "cursor-1"], cursors);
        Assert.Equal(2, result["counts"].Number(mode == "issues" ? "issues" : "total"));
        var items = result["lanes"].Objects().SelectMany(lane => lane["items"].Objects()).ToArray();
        Assert.Equal([1, 2], items.Select(item => item[mode == "issues" ? "issue" : "pr"].Number("number")).Order());
        if (mode == "issues")
        {
            Assert.All(items, item => Assert.Equal(["OPEN", "MERGED"],
                item["issue"]!["linkedPullRequests"].Objects().Select(pr => pr.Text("state"))));
        }
        else
        {
            Assert.All(items, item => Assert.Equal("13.5", item["pr"].Text("milestone")));
        }
        Assert.Empty(result["errors"].Strings());
    }

    [Fact]
    public async Task DeduplicatesUrlsButPreservesHostScopedErrorsAndFocusIdentity()
    {
        using var http = Client((request, body) =>
        {
            if (request.Headers.Authorization?.Parameter == "denied")
            {
                return Error("Forbidden", HttpStatusCode.Forbidden);
            }
            var node = PrNode(1);
            node["url"] = $"https://{(request.RequestUri!.Host == "api.github.com" ? "github.com" : request.RequestUri.Host)}/microsoft/aspire/pull/1";
            return Response(node);
        });
        var result = await Dashboard(http).LoadAsync(
            [Account("dotcom"), Account("denied"), Account("enterprise", "ghe.example.com"), Account("denied", "other.example.com")],
            Prefs("review"), TestContext.Current.CancellationToken);

        Assert.Equal(2, result["counts"].Number("total"));
        Assert.Equal(["https://ghe.example.com/microsoft/aspire/pull/1", "https://github.com/microsoft/aspire/pull/1"],
            result["attention"]!["focus"].Objects().Select(card => card["pr"].Text("url")).Order());
        Assert.Equal(["microsoft/aspire (other.example.com): GitHub API 403 Forbidden: Forbidden"], result["errors"].Strings());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("limit")]
    [InlineData("failure")]
    public async Task ReportsIncompletePaginationWithoutLosingCompletedPages(string failure)
    {
        var count = 0;
        using var http = Client((request, body) =>
        {
            count++;
            if (failure == "failure" && count == 2)
            {
                return Error("service unavailable", HttpStatusCode.ServiceUnavailable);
            }
            var response = ResponseBody([PrNode(count)], hasNext: true);
            var page = response["data"]!["repository"]!["pullRequests"]!["pageInfo"]!;
            page["endCursor"] = failure == "missing" ? null : failure == "limit" ? $"page-{count}" : "cursor-1";
            return JsonResponse(response);
        });
        var prefs = Prefs("ship");
        prefs["maxOpenItems"] = 10000;
        var result = await Dashboard(http).LoadAsync([Account()], prefs, TestContext.Current.CancellationToken);

        Assert.Equal(failure switch { "missing" => 1, "limit" => GitHubDashboard.MaxPages, "failure" => 1, _ => 2 }, result["counts"].Number("total"));
        var error = Assert.Single(result["errors"].Strings());
        Assert.Equal("microsoft/aspire (github.com): " + (failure switch
        {
            "limit" => $"Reached the {GitHubDashboard.MaxPages}-page safety limit; the queue is incomplete.",
            "failure" => "GitHub API 503 Service Unavailable: service unavailable",
            _ => "GitHub returned a missing or repeated pagination cursor; the queue is incomplete."
        }), error);
    }

    [Fact]
    public async Task NullRepositoryIsAnExplicitErrorNotAnEmptySuccess()
    {
        using var http = Client((_, _) => JsonResponse(JsonNode.Parse("""{"data":{"repository":null}}""")!.AsObject()));
        var result = await Dashboard(http).LoadAsync([Account()], Prefs("review"), TestContext.Current.CancellationToken);
        Assert.Equal(0, result["counts"].Number("total"));
        Assert.Equal(["microsoft/aspire (github.com): Repository is unavailable or this account does not have access."], result["errors"].Strings());
    }

    [Fact]
    public async Task InvalidTimestampsAreIsolatedToTheirRepository()
    {
        using var http = Client((_, body) =>
        {
            var node = PrNode(1);
            if (body["variables"].Text("name") == "invalid")
            {
                node["updatedAt"] = "not a timestamp";
            }
            return Response(node);
        });
        var account = Account() with { Repos = ["microsoft/aspire", "microsoft/invalid"] };
        var result = await Dashboard(http).LoadAsync([account], Prefs("review"), TestContext.Current.CancellationToken);
        Assert.Equal(1, result["counts"].Number("total"));
        Assert.Equal(["microsoft/invalid (github.com): GitHub returned an item without valid createdAt/updatedAt timestamps."], result["errors"].Strings());
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfPublishingAnEmptyDashboard()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = Client((_, _) =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dashboard(http).LoadAsync([Account()], Prefs("review"), cancellation.Token));
    }

    [Fact]
    public async Task NoCredentialsReturnsAuthenticationMessage()
    {
        using var http = Client((_, _) => throw new InvalidOperationException("No HTTP requests expected."));
        var result = await Dashboard(http).LoadAsync([], Prefs("review"), TestContext.Current.CancellationToken);
        Assert.False(result.Flag("authenticated"));
        Assert.Equal("No active GitHub account. Enable an account in the Accounts tab so the app can read your review queue.", result.Text("message"));
    }

    [Fact]
    public void DefaultsDoNotSelectRepositoriesReleaseOrTeamMembers()
    {
        Assert.Empty(DashboardConstants.DefaultRepos);
        Assert.Empty(DashboardConstants.DefaultEmuRepos);
        Assert.Empty(DashboardConstants.CoreTeamMemberAliasSuffixes);
        Assert.Equal("", DashboardConstants.CurrentRelease);
        var model = new ReviewModel();
        var prs = new[] { Normalized(1), Normalized(2, "outsider"), Normalized(3, "someone_microsoft") };
        Assert.Empty(model.CreateDeveloperPullRequestCounts(prs));
        Assert.Empty(model.ComputeCommunityItems(prs));
        Assert.Equal(3, model.ComputeFocusItems(model.CreateAttentionBuckets(prs, "octo")).Count);
        Assert.All(prs, pr => Assert.DoesNotContain(model.CreateAttentionSignals(pr),
            signal => signal.Text("label").StartsWith("release ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EmptyRepositorySelectionNeverExpandsToDefaults()
    {
        using var http = Client((_, _) => throw new InvalidOperationException("No provider queries expected."));
        var result = await Dashboard(http).LoadAsync([Account() with { Repos = [] }], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Flag("authenticated"));
        Assert.Empty(result["repos"].Strings());
        Assert.Equal(0, result["counts"].Number("total"));
        Assert.Empty(result["notifications"].Objects());
        Assert.Empty(result["errors"].Strings());
    }

    [Theory]
    [InlineData(false, "token", "octo")]
    [InlineData(true, "", "octo")]
    [InlineData(true, "token", "")]
    public async Task UnusableAccountsNeverQueryProviders(bool active, string token, string login)
    {
        using var http = Client((_, _) => throw new InvalidOperationException("No provider queries expected."));
        var account = Account(token) with { Active = active, Login = login };
        var result = await Dashboard(http).LoadAsync([account], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.False(result.Flag("authenticated"));
    }

    [Theory]
    [InlineData("review")]
    [InlineData("ship")]
    [InlineData("issues")]
    public async Task QueriesOnlySelectedRepositoryAndAccountAcrossSnapshots(string mode)
    {
        var requests = new List<string>();
        using var http = Client((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://ghe.example.com/api/graphql", request.RequestUri!.AbsoluteUri);
            var repository = $"{body["variables"].Text("owner")}/{body["variables"].Text("name")}";
            requests.Add($"{request.Headers.Authorization!.Parameter}:{repository}");
            var node = mode == "issues" ? IssueNode(1) : ApprovedNode(1);
            node["url"] = $"https://ghe.example.com/{repository}/{(mode == "issues" ? "issues" : "pull")}/1";
            return Response(node, mode == "issues");
        });
        var dashboard = Dashboard(http);
        foreach (var repository in new[] { "contoso/widgets", "fabrikam/tools" })
        {
            var selected = Account(repository, "ghe.example.com") with { Repos = [repository] };
            var result = await dashboard.LoadAsync([selected, Account("inactive") with { Active = false }],
                Prefs(mode), TestContext.Current.CancellationToken);
            Assert.Equal([repository], result["repos"].Strings());
            Assert.Equal(["octo"], result["viewers"].Strings());
            var card = Assert.Single(result["lanes"].Objects().SelectMany(lane => lane["items"].Objects()));
            Assert.Equal(repository, card[mode == "issues" ? "issue" : "pr"].Text("repository"));
            Assert.All(result["notifications"].Objects(), notification => Assert.Equal(repository, notification.Text("repository")));
            Assert.Empty(result["errors"].Strings());
        }
        Assert.Equal(["contoso/widgets:contoso/widgets", "fabrikam/tools:fabrikam/tools"], requests);
    }

    [Theory]
    [InlineData("https://github.com/microsoft/aspire-extra/pull/2")]
    [InlineData("https://github.com/other/repo/pull/2")]
    [InlineData("https://ghe.example.com/microsoft/aspire/pull/2")]
    public async Task ForeignProviderItemsNeverEnterCountsClassificationOrNotifications(string foreignUrl)
    {
        var foreign = ApprovedNode(2);
        foreign["url"] = foreignUrl;
        using var http = Client((_, _) => JsonResponse(ResponseBody([ApprovedNode(1), foreign])));
        var result = await Dashboard(http).LoadAsync([Account()], Prefs("review"), TestContext.Current.CancellationToken);
        Assert.Equal(1, result["counts"].Number("total"));
        Assert.Equal(1, Assert.Single(result["attention"]!["focus"].Objects())["pr"].Number("number"));
        Assert.Equal(1, Assert.Single(result["notifications"].Objects()).Number("number"));
        Assert.Equal(["microsoft/aspire (github.com): GitHub returned an item outside the selected repository."], result["errors"].Strings());
    }

    [Fact]
    public async Task ForeignRepositoryResponseIsReportedRatherThanRelabeled()
    {
        using var http = Client((_, _) =>
        {
            var body = ResponseBody([PrNode(1)]);
            body["data"]!["repository"]!["nameWithOwner"] = "other/repo";
            return JsonResponse(body);
        });
        var result = await Dashboard(http).LoadAsync([Account()], Prefs("review"), TestContext.Current.CancellationToken);
        Assert.Equal(0, result["counts"].Number("total"));
        Assert.Equal(["microsoft/aspire (github.com): GitHub returned a different repository than requested."], result["errors"].Strings());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedItemsAreRestrictedToTheSelectedRepositoryAndHost(bool issues)
    {
        var node = issues ? IssueNode(1) : PrNode(1);
        var links = new[] { ("microsoft/aspire", "github.com"), ("other/repo", "github.com"), ("microsoft/aspire", "ghe.example.com") }
            .Select((scope, index) => new JsonObject
            {
                ["number"] = index + 10,
                ["title"] = index == 0 ? "Local fix" : "Regression for 13.5",
                ["state"] = "OPEN",
                ["url"] = $"https://{scope.Item2}/{scope.Item1}/{(issues ? "pull" : "issues")}/{index + 10}",
                ["repository"] = new JsonObject { ["nameWithOwner"] = scope.Item1 },
                ["labels"] = index == 0 ? Connection() : Connection(new JsonObject { ["name"] = "regression" })
            }).ToArray();
        node["milestone"] = null;
        node[issues ? "closedByPullRequestsReferences" : "closingIssuesReferences"] = Connection(links);
        using var http = Client((_, _) => Response(node, issues));
        var result = await Dashboard(http).LoadAsync([Account()], Prefs(issues ? "issues" : "review"), TestContext.Current.CancellationToken);
        var card = Assert.Single(result["lanes"].Objects().SelectMany(lane => lane["items"].Objects()));
        var item = card[issues ? "issue" : "pr"]!;
        Assert.Equal(10, Assert.Single(item[issues ? "linkedPullRequests" : "linkedIssues"].Objects()).Number("number"));
        Assert.DoesNotContain(card["signals"].Objects(), signal => signal.Text("label") is "regression" or "release 13.5");
    }

    [Fact]
    public void ModelDoesNotClassifyFromForeignLinkedIssues()
    {
        var pr = Normalized(1);
        pr["milestone"] = null;
        pr["linkedIssues"] = new JsonArray(new JsonObject
        {
            ["repository"] = "other/repo",
            ["title"] = "Fix 13.5",
            ["labels"] = JsonData.Array(["regression"])
        });
        Assert.DoesNotContain(Model().CreateAttentionSignals(pr), signal => signal.Text("label") is "regression" or "release 13.5");
    }

    [Fact]
    public async Task TeamPreferencesAreExplicitAndDoNotLeakBetweenSnapshots()
    {
        using var http = Client((_, _) => JsonResponse(ResponseBody(
            [PrNode(1, "alice"), PrNode(2, "bob/copilot"), PrNode(3, "alice_microsoft")])));
        var dashboard = Dashboard(http);
        var prefs = Prefs("review");
        prefs["teamMembers"] = JsonData.Array([" Alice ", "alice", ""]);
        var first = await dashboard.LoadAsync([Account()], prefs, TestContext.Current.CancellationToken);
        Assert.Equal("Alice", Assert.Single(first["attention"]!["developerCounts"].Objects()).Text("actor"));
        Assert.Equal([2, 3], first["attention"]!["community"].Objects().Select(card => card["pr"].Number("number")).Order());
        prefs["teamMembers"] = JsonData.Array(["bob"]);
        var second = await dashboard.LoadAsync([Account()], prefs, TestContext.Current.CancellationToken);
        Assert.Equal("bob", Assert.Single(second["attention"]!["developerCounts"].Objects()).Text("actor"));
        Assert.Equal([1, 3], second["attention"]!["community"].Objects().Select(card => card["pr"].Number("number")).Order());
        prefs["teamMembers"] = new JsonArray();
        var empty = await dashboard.LoadAsync([Account()], prefs, TestContext.Current.CancellationToken);
        Assert.Empty(empty["attention"]!["developerCounts"].Objects());
        Assert.Empty(empty["attention"]!["community"].Objects());
        Assert.Equal(3, empty["attention"]!["focus"].Objects().Count());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("", true)]
    public async Task ShipWithoutReleaseIncludesEveryMilestoneAndUnmilestonedWork(string? release, bool showDrafts)
    {
        var ready = ApprovedNode(1);
        ready["milestone"] = null;
        var progress = PrNode(2);
        progress["milestone"]!["title"] = "next";
        var blocked = PrNode(3);
        blocked["mergeable"] = "CONFLICTING";
        var draft = PrNode(4);
        draft["isDraft"] = true;
        using var http = Client((_, _) => JsonResponse(ResponseBody([ready, progress, blocked, draft])));
        var prefs = new JsonObject { ["mode"] = "ship", ["showDrafts"] = showDrafts };
        if (release is not null)
        {
            prefs["release"] = release;
        }
        var result = await Dashboard(http).LoadAsync([Account()], prefs, TestContext.Current.CancellationToken);
        Assert.Equal("", result.Text("release"));
        Assert.Equal(["ready", "in-progress", "blocked"], result["lanes"].Objects().Select(lane => lane.Text("id")));
        var cards = result["lanes"].Objects().SelectMany(lane => lane["items"].Objects()).ToArray();
        Assert.Equal(showDrafts ? [1, 2, 3, 4] : new[] { 1, 2, 3 }, cards.Select(card => card["pr"].Number("number")).Order());
        Assert.Equal("No milestone", cards.Single(card => card["pr"].Number("number") == 1).Text("reason"));
        Assert.Equal("Milestone next", cards.Single(card => card["pr"].Number("number") == 2).Text("reason"));
        Assert.All(cards, card => Assert.DoesNotContain(card["signals"].Objects(),
            signal => signal.Text("label").StartsWith("release ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("14.2", "14.2", true)]
    [InlineData("14.2", "14x2", false)]
    [InlineData("14.2", "13.5", false)]
    [InlineData("", "13.5", false)]
    public void ReleaseSignalsUseOnlyConfiguredRelease(string release, string milestone, bool expected)
    {
        var pr = Normalized(1);
        pr["milestone"] = milestone;
        var model = new ReviewModel(prefs: new JsonObject { ["release"] = release });
        Assert.Equal(expected, model.CreateAttentionSignals(pr).Any(signal => signal.Text("label") == $"release {release}"));
        Assert.Equal(expected, model.CreateIssueSignals(pr).Any(signal => signal.Text("label") == $"release {release}"));
    }

    [Theory]
    [InlineData("microsoft/aspire", false)]
    [InlineData("contoso/widgets", true)]
    public void ConversationResolutionComesFromBranchProtectionNotRepositoryName(string repository, bool required)
    {
        var node = PrNode(1);
        node["baseRef"] = required ? new JsonObject { ["branchProtectionRule"] = new JsonObject { ["requiresConversationResolution"] = true } } : null;
        var pr = GitHubDashboard.NormalizePr(repository, node, ["octo"], false);
        Assert.Equal(required, pr["review"].Flag("requiresConversationResolution"));
    }

    [Fact]
    public async Task ReviewQueueRanksFromReadyForReviewAndCountsHiddenDrafts()
    {
        var oldDraft = PrNode(1);
        oldDraft["createdAt"] = Ago(28);
        oldDraft["readyForReviewEvents"] = Connection(new JsonObject { ["createdAt"] = Ago(1) });
        var longerWait = PrNode(2);
        longerWait["createdAt"] = Ago(3);
        var draft = PrNode(3, "octo");
        draft["isDraft"] = true;
        using var http = Client((_, _) => JsonResponse(ResponseBody([oldDraft, longerWait, draft])));
        var prefs = Prefs("review");
        prefs["reviewLimit"] = 1;
        var result = await Dashboard(http).LoadAsync([Account()], prefs, TestContext.Current.CancellationToken);

        var queue = Assert.Single(result["lanes"].Objects());
        Assert.Equal("review-queue", queue.Text("id"));
        Assert.Equal(2, queue.Number("cappedTotal"));
        Assert.Equal(2, Assert.Single(queue["items"].Objects())["pr"].Number("number"));
        Assert.Equal(2, result["counts"].Number("prs"));
        Assert.Equal(3, result["counts"].Number("total"));
        Assert.Equal(1, result["counts"].Number("drafts"));
        Assert.Equal(3, Assert.Single(result["attention"]!["buckets"].Objects()
            .Single(bucket => bucket.Text("label") == "My draft PRs")["items"].Objects())["pr"].Number("number"));
    }

    [Fact]
    public async Task AttentionRetainsDebtPastCapAndExplainsOwnBlockedWork()
    {
        var fresh = PrNode(1);
        var uncappedFresh = PrNode(2);
        uncappedFresh["updatedAt"] = Ago(2);
        var debt = PrNode(3);
        debt["updatedAt"] = Ago(20);
        var mine = PrNode(4, "octo");
        mine["mergeable"] = "CONFLICTING";
        using var http = Client((_, _) => JsonResponse(ResponseBody([fresh, uncappedFresh, debt, mine])));
        var prefs = Prefs("review");
        prefs["reviewLimit"] = 1;
        var result = await Dashboard(http).LoadAsync([Account()], prefs, TestContext.Current.CancellationToken);
        var attention = result["attention"]!;
        Assert.Equal(3, attention.Number("focusTotal"));
        Assert.True(attention.Flag("focusMixed"));
        Assert.Equal([1, 3], attention["focus"].Objects().Select(card => card["pr"].Number("number")));
        Assert.Equal([false, true], attention["focus"].Objects().Select(card => card.Flag("reviewDebt")));
        var personal = Assert.Single(attention["forMe"].Objects());
        Assert.Equal("Resolve conflicts", personal.Text("action"));
        Assert.Equal(4, personal["pr"].Number("number"));
        var exclusion = Assert.Single(attention["focusExclusions"].Objects());
        Assert.Equal("The author needs to rebase before reviewers can finish it.", exclusion.Text("reason"));
        Assert.Equal("Merge conflicts", exclusion["signals"].Objects().First().Text("label"));
    }

    [Fact]
    public async Task IssuesIncludeAllFocusAndResidualLanes()
    {
        var regression = IssueNode(1);
        regression["labels"] = Connection(new JsonObject { ["name"] = "regression" });
        var cti = IssueNode(2);
        cti["title"] = "[AspireE2E] validate workload";
        var afscrome = IssueNode(3);
        afscrome["author"]!["login"] = "afscrome";
        var assigned = IssueNode(4);
        assigned["assignees"] = Connection(new JsonObject { ["login"] = "octo" });
        var triage = IssueNode(5);
        var active = IssueNode(6);
        active["labels"] = Connection(new JsonObject { ["name"] = "area-dashboard" });
        using var http = Client((_, _) => JsonResponse(ResponseBody([regression, cti, afscrome, assigned, triage, active], issues: true)));
        var result = await Dashboard(http).LoadAsync([Account()], Prefs("issues"), TestContext.Current.CancellationToken);

        Assert.Equal(["focus-regression", "focus-my-issues", "triage", "active"],
            result["lanes"].Objects().Select(lane => lane.Text("id")));
        Assert.Equal([1, 2, 3, 4, 5, 6], result["lanes"].Objects().SelectMany(lane => lane["items"].Objects()).Select(item => item["issue"].Number("number")).Order());
        Assert.Equal([2, 3, 5], result["lanes"].Objects().Single(lane => lane.Text("id") == "triage")["items"].Objects()
            .Select(item => item["issue"].Number("number")).Order());
        Assert.Null(result["attention"]);
    }

    [Fact]
    public async Task ShipHonorsMilestoneDraftsAndAuthoritativeReviewGate()
    {
        var ready = ApprovedNode(1);
        var reviewRequired = ApprovedNode(2);
        reviewRequired["reviewDecision"] = "REVIEW_REQUIRED";
        var blocked = ApprovedNode(3);
        blocked["mergeable"] = "CONFLICTING";
        var wrongRelease = ApprovedNode(4);
        wrongRelease["milestone"]!["title"] = "other";
        var draft = ApprovedNode(5);
        draft["isDraft"] = true;
        using var http = Client((_, _) => JsonResponse(ResponseBody([ready, reviewRequired, blocked, wrongRelease, draft])));
        var result = await Dashboard(http).LoadAsync([Account()], Prefs("ship"), TestContext.Current.CancellationToken);
        Assert.Equal(["ready", "in-progress", "blocked"], result["lanes"].Objects().Select(lane => lane.Text("id")));
        Assert.Equal([1, 2, 3], result["lanes"].Objects().SelectMany(lane => lane["items"].Objects()).Select(item => item["pr"].Number("number")));
        Assert.Equal(2, result["counts"].Number("readyToMerge"));
    }

    [Fact]
    public void DerivesLatestHumanReviewStateAndHistoricalCounts()
    {
        var raw = PrNode(1);
        raw["reviews"] = Connection(
            Review("octo", "APPROVED", 3),
            Review("octo", "COMMENTED", 2),
            Review("reviewer", "CHANGES_REQUESTED", 1),
            Review("renovate", "APPROVED", 0.5),
            Review("copilot-pull-request-reviewer[bot]", "COMMENTED", 0.5));
        raw["reviewThreads"] = Connection(new JsonObject { ["isResolved"] = false });
        raw["reviewRequests"] = Connection(new JsonObject { ["requestedReviewer"] = new JsonObject { ["login"] = "second" } });
        var pr = GitHubDashboard.NormalizePr("microsoft/aspire", raw, ["octo", "second"], false);
        Assert.Equal("changes_requested", pr["review"].Text("state"));
        Assert.Equal(1, pr["review"].Number("approvalCount"));
        Assert.Equal(1, pr["review"].Number("commentedReviewCount"));
        Assert.Equal(1, pr["review"].Number("changesRequestedCount"));
        Assert.Equal(2, pr["review"].Number("reviewerCount"));
        Assert.True(pr["review"].Flag("copilotReviewed"));
        Assert.True(pr["review"].Flag("reviewRequestedFromViewer"));
        Assert.True(pr["review"].Flag("requiresConversationResolution"));
        Assert.False(pr["review"].Flag("viewerApproved"));
        Assert.Equal(0, pr["review"].Number("unresolvedThreadCount"));
        Assert.Equal(1, pr.Number("unresolvedThreadCount"));
        Assert.Equal(raw["commits"]!["nodes"]![0]!["commit"].Text("committedDate"), pr.Text("lastCommitAt"));
    }

    [Fact]
    public void NotificationsCoverReviewRequestsApproversAndAuthorsWithDismissals()
    {
        var requested = Normalized(1);
        requested["review"]!["reviewRequestedFromViewer"] = true;
        var approved = GitHubDashboard.NormalizePr("microsoft/aspire", ApprovedNode(2), ["octo"], false);
        var mine = Normalized(3, "octo");
        mine["checksState"] = "failure";
        mine["review"]!["state"] = "changes_requested";
        var settings = Prefs("review")["notifications"]!.AsObject();
        var notifications = GitHubDashboard.BuildNotifications([requested, approved, mine], settings, ["changes-requested:microsoft/aspire#3"]);
        Assert.Equal(["review-requested:microsoft/aspire#1", "ready-to-merge:microsoft/aspire#2", "ci-failing:microsoft/aspire#3"], notifications.Select(item => item.Text("id")));
        Assert.Equal("A PR you approved is ready to merge", notifications[1].Text("detail"));
        settings["ciFailing"] = false;
        Assert.Equal(["review-requested", "ready-to-merge", "changes-requested"],
            GitHubDashboard.BuildNotifications([requested, approved, mine], settings, []).Select(item => item.Text("kind")));
    }

    [Fact]
    public void CopilotAttributionAndPrivateRepositoriesKeepOwnershipWithoutCommunityNoise()
    {
        var raw = PrNode(1, "copilot[bot]");
        raw["author"]!["__typename"] = "Bot";
        raw["assignees"] = Connection(new JsonObject { ["login"] = "davidfowl" });
        var pr = GitHubDashboard.NormalizePr("microsoft/aspire", raw, ["davidfowl"], false);
        Assert.Equal("davidfowl/copilot", pr.Text("author"));
        Assert.True(pr.Flag("isMine"));
        Assert.Equal("davidfowl", ReviewModel.ActorIdentityKey(pr.Text("author")));
        Assert.Empty(new ReviewModel().CreateForMeItems([pr], ["davidfowl"]));
        Assert.Equal("davidfowl", Assert.Single(Model().CreateDeveloperPullRequestCounts([pr])).Text("actor"));
        var privatePr = GitHubDashboard.NormalizePr("org/private", PrNode(2, "outsider"), ["octo"], true);
        Assert.False(Model().IsCommunityPullRequest(privatePr));
        Assert.Single(new ReviewModel().ComputeFocusItems(new ReviewModel().CreateAttentionBuckets([privatePr], "octo")));
    }

    [Fact]
    public void OnlyExplicitTeamMembersGetOwnershipAndRepositoryNamesDoNotCreateSpecializedLanes()
    {
        var alias = Normalized(1, "IEvangelist_microsoft");
        var unlisted = Normalized(2, "someone_microsoft");
        var docs = Normalized(3);
        docs["repository"] = "microsoft/aspire.dev";
        docs["labels"] = JsonData.Array(["docs-from-code"]);
        var toolkit = Normalized(4);
        toolkit["repository"] = "CommunityToolkit/Aspire";
        var bot = Normalized(5, "dependabot[bot]");
        var community = Normalized(6, "community-person");
        var held = Normalized(7);
        held["labels"] = JsonData.Array(["needs-author-action"]);
        var prefs = Prefs("review");
        prefs["teamMembers"] = JsonData.Array(["davidfowl", "IEvangelist_microsoft"]);
        var model = new ReviewModel(prefs: prefs);
        var prs = new[] { alias, unlisted, docs, toolkit, bot, community, held };
        var buckets = model.CreateAttentionBuckets(prs, "octo");
        Assert.Equal([1, 4], model.ComputeFocusItems(buckets).Select(item => item.PullRequest.Number("number")).Order());
        Assert.Equal([2, 6], model.ComputeCommunityItems(prs).Select(item => item.PullRequest.Number("number")).Order());
        Assert.Equal(new[] { "IEvangelist_microsoft", "davidfowl" }.Order(),
            model.CreateDeveloperPullRequestCounts(prs).Select(item => item.Text("actor")).Order());
        Assert.Equal([3], buckets.Single(bucket => bucket.Label == "Docs").Items.Select(item => item.PullRequest.Number("number")));
        Assert.DoesNotContain(buckets, bucket => bucket.Label == "Community Toolkit");
        Assert.Equal([5], buckets.Single(bucket => bucket.Label == "Bots / automation").Items.Select(item => item.PullRequest.Number("number")));
    }

    [Theory]
    [InlineData(13, false, true)]
    [InlineData(14, true, true)]
    [InlineData(20, true, true)]
    public void ReviewDebtThresholdRetainsUnapprovedButNotApprovedWork(int age, bool debt, bool inFocus)
    {
        var now = DateTimeOffset.Parse("2026-09-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var model = Model(new FixedClock(now));
        var pr = Normalized(1);
        pr["updatedAt"] = now.AddDays(-age).ToString("O");
        Assert.Equal(debt, model.IsReviewDebt(pr));
        Assert.Equal(inFocus, model.ComputeFocusItems(model.CreateAttentionBuckets([pr], "octo")).Count == 1);
        pr["review"]!["state"] = "approved";
        pr["review"]!["lastApprovedAt"] = now.AddDays(-age).ToString("O");
        Assert.False(model.IsReviewDebt(pr));
        Assert.Equal(age <= 14, model.ComputeFocusItems(model.CreateAttentionBuckets([pr], "octo")).Count == 1);
    }

    [Fact]
    public void StalledOnlyReviewDebtIsRetainedButAuthorResponseNeedsANewerCommit()
    {
        var stalled = Normalized(1);
        stalled["updatedAt"] = Ago(20);
        stalled["review"]!["state"] = "reviewed";
        stalled["review"]!["lastReviewedAt"] = Ago(20);
        var response = (JsonObject)stalled.DeepClone();
        response["number"] = 2;
        response["review"]!["state"] = "changes_requested";
        var model = Model();
        var focus = model.ComputeFocusItems(model.CreateAttentionBuckets([stalled, response], "octo"));
        Assert.Equal("Stalled", Assert.Single(focus).BucketLabel);
        response["lastCommitAt"] = Ago(1);
        focus = model.ComputeFocusItems(model.CreateAttentionBuckets([stalled, response], "octo"));
        Assert.Equal(["Stalled", "Re-review needed"], focus.OrderBy(item => item.PullRequest.Number("number")).Select(item => item.BucketLabel));
    }

    [Theory]
    [InlineData("microsoft/aspire", 0, "", 0, "failure", true)]
    [InlineData("devdiv-microsoft/aspire-1p", 0, "", 0, "failure", true)]
    [InlineData("devdiv-microsoft/aspire-1p", 1, "GitOps/GitHubPop", 0, "failure", true)]
    [InlineData("devdiv-microsoft/aspire-1p", 1, "some proof of presence gate", 1, "failure", true)]
    [InlineData("devdiv-microsoft/aspire-1p", 2, "GitOps/GitHubPop", 0, "failure", true)]
    [InlineData("devdiv-microsoft/aspire-1p", 1, "build", 0, "failure", true)]
    public void FailingChecksAreBlockingRegardlessOfRepositoryOrCheckName(string repo, int failures, string check, int pending, string expected, bool failing)
    {
        var pr = Normalized(1);
        pr["repository"] = repo;
        pr["checks"]!["state"] = "failure";
        pr["checks"]!["failureCount"] = failures;
        pr["checks"]!["totalCount"] = failures;
        pr["checks"]!["pendingCount"] = pending;
        pr["checks"]!["failingChecks"] = JsonData.Array(check.Length == 0 ? [] : new[] { new JsonObject { ["name"] = check } });
        Assert.Equal(expected, ReviewModel.VisibleCheckState(pr));
        Assert.Equal(failing, ReviewModel.IsChecksFailing(pr));
    }

    [Fact]
    public void SignalsPrioritizeBlockersAndTagRawLabelsRatherThanAuthorizingActions()
    {
        var pr = Normalized(1);
        pr["milestone"] = null;
        pr["labels"] = JsonData.Array(["merge conflicts"]);
        var model = Model();
        var raw = Assert.Single(model.CreateAttentionSignals(pr), signal => signal.Text("label") == "merge conflicts");
        Assert.Equal("repo-label", raw.Text("kind"));
        pr["labels"] = JsonData.Array(["regression"]);
        pr["mergeableState"] = "dirty";
        pr["milestone"] = "13.5";
        pr["baseRef"] = "release/13.5";
        Assert.Equal(["regression", "merge conflicts", "release 13.5", "regression"],
            model.CreateAttentionSignals(pr).Take(4).Select(signal => signal.Text("label")));
        Assert.Equal(["needs reviewer", "fix CI", "2 unresolved"], GitHubDashboard.DedupeSignals(
            new[] { "needs reviewer", "no reviews", "fix CI", "CI failing", "2 unresolved", "Unresolved feedback" }
                .Select(label => ReviewModel.Signal(label, "warning"))).Select(signal => signal.Text("label")));
    }

    [Fact]
    public void IssueSignalsPreserveFocusPriorityAndDoNotInventLinkedPrCounts()
    {
        var issue = new JsonObject
        {
            ["title"] = "[aspiree2e] verify installer 13.5",
            ["author"] = "afscrome",
            ["labels"] = JsonData.Array(["blocking-release", "regression"]),
            ["assignees"] = new JsonArray(),
            ["updatedAt"] = Ago(1)
        };
        Assert.Equal(["Blocking release", "release 13.5", "Regression", "Needs validation", "Installer/acquisition", "blocking-release"],
            Model().CreateIssueSignals(issue).Select(signal => signal.Text("label")));
        var plain = new JsonObject { ["title"] = "Problem", ["author"] = "octo", ["labels"] = new JsonArray(), ["assignees"] = new JsonArray(), ["updatedAt"] = Ago(1) };
        Assert.Equal(["Unowned"], new ReviewModel().CreateIssueSignals(plain).Select(signal => signal.Text("label")));
    }

    // Historical fixtures use explicit team/release settings and generic repository classification.
    [Theory]
    [InlineData("baseline", "Needs review", "Needs review",
        "needs reviewer|warning|;open 2d|muted|;no reviews|warning|")]
    [InlineData("quick-win", "Quick wins;Needs review", "Needs review",
        "quick win|success|;quick win|success|;open 2d|muted|;no reviews|warning|")]
    [InlineData("approved-aging", "Approved but aging", "Approved but aging",
        "land approval|danger|;2 approvals|success|;approved 3d|danger|;open 2d|muted|;reviewed 3d|muted|")]
    [InlineData("pending-approved", "Review started", "Review started",
        "wait for CI|warning|;1 approval|success|;CI running|warning|;open 2d|muted|;reviewed 12h|muted|")]
    [InlineData("regression-conflict", "Regression;CI failing;Merge conflicts", "",
        "regression|danger|;merge conflicts|danger|;release 13.5|danger|;regression|danger|;base release/13.5|danger|;CI failing|danger|;open 2d|muted|")]
    [InlineData("re-review", "Re-review needed;Author response", "Re-review needed",
        "re-review|warning|;commit after review|warning|;open 2d|muted|;1 change request|danger|;reviewed 2d|muted|")]
    [InlineData("docs", "Docs;Needs review", "",
        "docs review|accent|;docs|accent|;open 2d|muted|;no reviews|warning|")]
    [InlineData("toolkit", "Needs review", "Needs review",
        "needs reviewer|warning|;open 2d|muted|;no reviews|warning|")]
    [InlineData("aged-community", "Aged out community;Stalled", "",
        "aged out community|warning|;aged out community|warning|;idle 20d|warning|;review debt|danger|;open 2d|muted|;no reviews|warning|")]
    [InlineData("bot", "Bots / automation", "",
        "automation|accent|;open 2d|muted|;no reviews|warning|;bot|accent|")]
    [InlineData("unresolved", "Unresolved feedback", "",
        "resolve feedback|danger|;2 unresolved|danger|;open 2d|muted|;1 reviewer \u00b7 0 approvals|accent|;reviewed 1d|muted|;1 review comment|muted|")]
    public void PreservesReviewModelFixturesWithExplicitPreferences(string scenario, string expectedBuckets, string expectedFocus, string expectedSignals)
    {
        var now = DateTimeOffset.Parse("2026-09-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var pr = Normalized(1);
        pr["createdAt"] = now.AddDays(-2).ToString("O");
        pr["updatedAt"] = now.AddDays(-1).ToString("O");
        pr["milestone"] = null;
        var review = pr["review"]!;
        switch (scenario)
        {
            case "quick-win":
                pr["additions"] = 50;
                pr["deletions"] = 5;
                pr["changedFiles"] = 2;
                pr["commitCount"] = 1;
                break;
            case "approved-aging":
            case "pending-approved":
                review["state"] = "approved";
                review["approvalCount"] = scenario == "approved-aging" ? 2 : 1;
                review["reviewerCount"] = scenario == "approved-aging" ? 2 : 1;
                review["lastApprovedAt"] = now.AddDays(scenario == "approved-aging" ? -3 : -0.5).ToString("O");
                review["lastReviewedAt"] = review["lastApprovedAt"]!.DeepClone();
                if (scenario == "pending-approved")
                {
                    pr["checks"]!["state"] = "pending";
                }
                break;
            case "regression-conflict":
                pr["labels"] = JsonData.Array(["regression"]);
                pr["mergeableState"] = "dirty";
                pr["milestone"] = "13.5";
                pr["baseRef"] = "release/13.5";
                pr["checks"]!["state"] = "failure";
                break;
            case "re-review":
                review["state"] = "changes_requested";
                review["changesRequestedCount"] = 1;
                review["reviewerCount"] = 1;
                review["lastReviewedAt"] = now.AddDays(-2).ToString("O");
                pr["lastCommitAt"] = now.AddDays(-1).ToString("O");
                break;
            case "docs":
                pr["repository"] = "microsoft/aspire.dev";
                pr["labels"] = JsonData.Array(["docs-from-code"]);
                break;
            case "toolkit":
                pr["repository"] = "CommunityToolkit/Aspire";
                break;
            case "aged-community":
                pr["author"] = "outsider";
                pr["updatedAt"] = now.AddDays(-20).ToString("O");
                break;
            case "bot":
                pr["author"] = "dependabot[bot]";
                pr["authorType"] = "Bot";
                break;
            case "unresolved":
                review["state"] = "reviewed";
                review["commentedReviewCount"] = 1;
                review["reviewerCount"] = 1;
                review["lastReviewedAt"] = now.AddDays(-1).ToString("O");
                review["unresolvedThreadCount"] = 2;
                break;
        }

        var model = Model(new FixedClock(now));
        var buckets = model.CreateAttentionBuckets([pr], "octo");
        Assert.Equal(expectedBuckets.Split(';', StringSplitOptions.RemoveEmptyEntries), buckets.Select(bucket => bucket.Label));
        Assert.Equal(expectedFocus.Split(';', StringSplitOptions.RemoveEmptyEntries), model.ComputeFocusItems(buckets).Select(item => item.BucketLabel));
        Assert.Equal(expectedSignals.Split(';', StringSplitOptions.RemoveEmptyEntries),
            model.CreateAttentionSignals(pr).Select(signal => $"{signal.Text("label")}|{signal.Text("tone")}|{signal.Text("kind")}"));
    }

    [Fact]
    public async Task AccountsChooseStrongestCredentialsAndActivateOnlyTheBestFirstRunAccount()
    {
        var commands = new ConcurrentQueue<string>();
        Task<ProcessResult> Gh(IReadOnlyList<string> args, CancellationToken ct)
        {
            commands.Enqueue(string.Join(" ", args));
            return Task.FromResult(args[1] == "status"
                ? new ProcessResult(0, """{"hosts":{"github.com":[{"login":"octo","state":"success"}]}}""", "")
                : new ProcessResult(0, "cli-token\n", ""));
        }
        var env = new Dictionary<string, string?> { ["GH_TOKEN"] = "env-token", ["COPILOT_GH_ACCOUNT_github_2E_com_other"] = "other-token" };
        using var http = AccountClient();
        var service = new AccountService(http, NullLogger<AccountService>.Instance, Gh, () => env);
        var result = await service.ResolveAsync(new JsonObject { ["accounts"] = new JsonObject() }, TestContext.Current.CancellationToken);

        Assert.Equal(["auth status --json hosts", "auth token --hostname github.com --user octo"], commands);
        Assert.Equal(["acct:github.com/octo", "acct:github.com/other"], result.Select(account => account.Id));
        Assert.Equal([true, false], result.Select(account => account.Active));
        Assert.Equal("env-token", result[0].Token);
        Assert.Equal(["env", "gh"], result[0].Metadata["sources"].Objects().Select(source => source.Text("source")));
        Assert.Equal([true, false], result[0].Metadata["sources"].Objects().Select(source => source.Flag("chosen")));
        Assert.Equal(["id", "login", "avatarUrl", "host", "enterprise", "graphql", "status", "accessible", "total", "hasReadOrg", "reason", "sources", "sourceKinds", "repos", "active"],
            result[0].Metadata.Select(pair => pair.Key));
        Assert.All(result.SelectMany(account => account.Metadata["sources"].Objects()), source =>
            Assert.Equal(["source", "label", "hash", "host", "enterprise", "status", "scopes", "accessible", "total", "hasReadOrg", "reason", "chosen"],
                source.Select(pair => pair.Key)));
    }

    [Fact]
    public async Task AccountsHonorLegacyPreferencesWithoutImplicitRepositories()
    {
        var env = new Dictionary<string, string?>
        {
            ["COPILOT_GH_ACCOUNT_github_2E_com_octo"] = "env-token",
            ["COPILOT_GH_ACCOUNT_github_2E_com_alice_5F_microsoft"] = "emu-token",
            ["COPILOT_GH_ACCOUNT_ghe_2E_example_2E_com_alice_5F_microsoft"] = "enterprise-token"
        };
        using var http = AccountClient();
        var service = new AccountService(http, NullLogger<AccountService>.Instance, NoGh, () => env);
        var result = await service.ResolveAsync(JsonNode.Parse("""
            {"accounts":{"acct:octo":{"repos":["microsoft/aspire.dev"],"active":false}}}
            """)!.AsObject(), TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Count);
        var octo = result.Single(account => account.Login == "octo");
        Assert.Equal(["microsoft/aspire.dev"], octo.Repos);
        Assert.False(octo.Active);
        Assert.All(result, account => Assert.False(account.Active));
        Assert.Equal(DashboardConstants.DefaultEmuRepos, result.Single(account => account.Id == "acct:github.com/alice_microsoft").Repos);
        Assert.Equal(DashboardConstants.DefaultRepos, result.Single(account => account.Id == "acct:ghe.example.com/alice_microsoft").Repos);
    }

    [Fact]
    public async Task LegacySingleAccountConfigurationDoesNotAutoActivateAnotherAccount()
    {
        var env = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "env-token",
            ["COPILOT_GH_ACCOUNT_github_2E_com_other"] = "other-token"
        };
        using var http = AccountClient();
        var service = new AccountService(http, NullLogger<AccountService>.Instance, NoGh, () => env);
        var accounts = await service.ResolveAsync(JsonNode.Parse("""
            {"accounts":{},"account":"acct:octo","repos":["microsoft/aspire.dev"]}
            """)!.AsObject(), TestContext.Current.CancellationToken);
        var active = Assert.Single(accounts, account => account.Active);
        Assert.Equal("octo", active.Login);
        Assert.Equal(["microsoft/aspire.dev"], active.Repos);
    }

    [Fact]
    public async Task FailedAccountsRemainVisibleAndErrorsRedactTokens()
    {
        var env = new Dictionary<string, string?>
        {
            ["COPILOT_GH_ACCOUNT_github_2E_com_failed"] = "secret-token",
            ["COPILOT_GH_ACCOUNT_github_2E_com_octo"] = "env-token"
        };
        using var http = AccountClient();
        var service = new AccountService(http, NullLogger<AccountService>.Instance, NoGh, () => env);
        var result = await service.ResolveAsync(new JsonObject(), TestContext.Current.CancellationToken);
        var failed = result.Single(account => account.Login == "failed");
        Assert.Equal("failed", failed.Metadata.Text("status"));
        Assert.Equal("GitHub API 401 Unauthorized: rejected [redacted]", failed.Metadata.Text("reason"));
        Assert.False(failed.Active);
        Assert.Equal("octo", Assert.Single(result, account => account.Active).Login);
    }

    [Fact]
    public async Task RepositoryProbeFailureDoesNotHideOtherAccounts()
    {
        using var http = Client((request, body) =>
        {
            var token = request.Headers.Authorization!.Parameter;
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(new JsonObject());
            }
            if (body.Text("query").Contains("viewer", StringComparison.Ordinal))
            {
                return JsonResponse(new JsonObject { ["data"] = new JsonObject { ["viewer"] = new JsonObject { ["login"] = token } } });
            }
            if (token == "failed")
            {
                throw new HttpRequestException("Repository service unavailable");
            }
            return JsonResponse(new JsonObject { ["data"] = new JsonObject { ["r0"] = new JsonObject { ["nameWithOwner"] = "microsoft/aspire" } } });
        });
        var env = new Dictionary<string, string?> { ["GH_TOKEN"] = "failed", ["GITHUB_TOKEN"] = "working" };
        var service = new AccountService(http, NullLogger<AccountService>.Instance, NoGh, () => env);
        var accounts = await service.ResolveAsync(JsonNode.Parse("""
            {"accounts":{"acct:github.com/failed":{"repos":["microsoft/aspire","microsoft/aspire.dev"]},
                         "acct:github.com/working":{"repos":["microsoft/aspire","microsoft/aspire.dev"],"active":true}}}
            """)!.AsObject(), TestContext.Current.CancellationToken);
        Assert.Equal(["working", "failed"], accounts.Select(account => account.Login));
        Assert.Equal(["partial", "failed"], accounts.Select(account => account.Metadata.Text("status")));
        Assert.Equal([true, false], accounts.Select(account => account.Active));
        Assert.Equal("Repository service unavailable", accounts[1].Metadata.Text("reason"));
    }

    [Fact]
    public async Task NonzeroGhStatusStillKeepsExpiredAccountMetadata()
    {
        static Task<ProcessResult> Gh(IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult(args[1] == "status"
            ? new ProcessResult(1, """{"hosts":{"github.com":[{"login":"expired","state":"failed"}]}}""", "authentication failed")
            : new ProcessResult(1, "", "secret process output"));
        using var http = Client((_, _) => throw new InvalidOperationException("No usable credentials."));
        var service = new AccountService(http, NullLogger<AccountService>.Instance, Gh, () => new Dictionary<string, string?>());
        var failed = Assert.Single(await service.ResolveAsync(new JsonObject(), TestContext.Current.CancellationToken));
        Assert.Equal("expired", failed.Login);
        Assert.Equal("", failed.Token);
        Assert.Equal("failed", failed.Metadata.Text("status"));
        Assert.Equal("GitHub CLI could not read the credential for expired on github.com. Run gh auth login --hostname github.com to sign in again.", failed.Metadata.Text("reason"));
    }

    [Fact]
    public async Task InvalidEnvironmentHostIsIsolatedAndNeverReceivesCredentials()
    {
        var env = new Dictionary<string, string?>
        {
            ["GH_HOST"] = "user@invalid.example",
            ["GH_TOKEN"] = "env-token",
            ["COPILOT_GH_ACCOUNT_github_2E_com_other"] = "other-token"
        };
        using var http = AccountClient();
        var service = new AccountService(http, NullLogger<AccountService>.Instance, NoGh, () => env);
        var accounts = await service.ResolveAsync(new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(["other", "unknown"], accounts.Select(account => account.Login));
        Assert.Equal(["ok", "failed"], accounts.Select(account => account.Metadata.Text("status")));
        Assert.Null(accounts[1].Metadata["graphql"]);
        Assert.Equal("Invalid GitHub hostname.", accounts[1].Metadata.Text("reason"));
        Assert.Equal("", accounts[1].Token);
    }

    [Fact]
    public async Task DuplicateTokensAreProbedOncePerHost()
    {
        var identities = 0;
        using var http = Client((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(new JsonObject());
            }
            if (body.Text("query").Contains("viewer", StringComparison.Ordinal))
            {
                identities++;
                return JsonResponse(new JsonObject { ["data"] = new JsonObject { ["viewer"] = new JsonObject { ["login"] = "octo" } } });
            }
            return JsonResponse(new JsonObject { ["data"] = new JsonObject { ["r0"] = new JsonObject() } });
        });
        var env = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "same-token",
            ["GITHUB_TOKEN"] = "same-token",
            ["COPILOT_GH_ACCOUNT_ghe_2E_example_2E_com_octo"] = "same-token"
        };
        var service = new AccountService(http, NullLogger<AccountService>.Instance, NoGh, () => env);
        var accounts = await service.ResolveAsync(new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(2, identities);
        Assert.Equal(["acct:ghe.example.com/octo", "acct:github.com/octo"], accounts.Select(account => account.Id).Order());
        Assert.All(accounts, account => Assert.Single(account.Metadata["sources"].Objects()));
    }

    [Theory]
    [InlineData("HTTPS://API.GITHUB.COM/foo", "acct:github.com/octo")]
    [InlineData("GHE.EXAMPLE.COM", "acct:ghe.example.com/octo")]
    public void AccountIdentityNormalizesHostAndLogin(string host, string expected) =>
        Assert.Equal(expected, AccountService.AccountId(" Octo ", host));

    private static HttpClient AccountClient() => Client((request, body) =>
    {
        var token = request.Headers.Authorization!.Parameter;
        if (token == "secret-token")
        {
            return Error("rejected secret-token", HttpStatusCode.Unauthorized);
        }
        if (request.Method == HttpMethod.Get)
        {
            var response = JsonResponse(new JsonObject());
            response.Headers.Add("x-oauth-scopes", token == "env-token" ? "repo, read:org" : "repo");
            return response;
        }
        if (body.Text("query").Contains("viewer", StringComparison.Ordinal))
        {
            return JsonResponse(new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["viewer"] = new JsonObject
                    {
                        ["login"] = token switch { "other-token" => "other", "emu-token" or "enterprise-token" => "alice_microsoft", _ => "octo" },
                        ["avatarUrl"] = "https://avatars.example.test/user.png"
                    }
                }
            });
        }
        var data = new JsonObject();
        for (var i = 0; i < 5; i++)
        {
            if (token != "cli-token" || i == 0)
            {
                data[$"r{i}"] = new JsonObject { ["nameWithOwner"] = "microsoft/aspire" };
            }
        }
        return JsonResponse(new JsonObject { ["data"] = data });
    });

    private static Task<ProcessResult> NoGh(IReadOnlyList<string> args, CancellationToken ct) =>
        Task.FromResult(new ProcessResult(0, """{"hosts":{}}""", ""));

    private static GitHubDashboard Dashboard(HttpClient http) => new(http, NullLogger<GitHubDashboard>.Instance);
    private static ReviewModel Model(TimeProvider? clock = null) => new(clock, Prefs("review"));
    private static Account Account(string token = "token", string host = "github.com") =>
        new($"acct:{host}/octo", "octo", host, token, ["microsoft/aspire"], true, new JsonObject());

    private static JsonObject Prefs(string mode) => new()
    {
        ["mode"] = mode,
        ["release"] = "13.5",
        ["teamMembers"] = JsonData.Array(["davidfowl"]),
        ["notifications"] = new JsonObject { ["reviewRequested"] = true, ["readyToMerge"] = true, ["changesRequested"] = true, ["ciFailing"] = true }
    };

    private static string Ago(double days) => DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
    private static JsonObject Review(string login, string state, double days) =>
        new() { ["author"] = new JsonObject { ["login"] = login }, ["state"] = state, ["submittedAt"] = Ago(days) };

    private static JsonObject Normalized(int number, string author = "davidfowl") =>
        GitHubDashboard.NormalizePr("microsoft/aspire", PrNode(number, author), ["octo"], false);

    private static JsonObject ApprovedNode(int number)
    {
        var node = PrNode(number);
        node["reviews"] = Connection(Review("octo", "APPROVED", 0.5));
        return node;
    }

    private static JsonObject PrNode(int number, string author = "davidfowl") => new()
    {
        ["number"] = number,
        ["title"] = $"PR {number}",
        ["url"] = $"https://github.com/microsoft/aspire/pull/{number}",
        ["state"] = "OPEN",
        ["isDraft"] = false,
        ["createdAt"] = Ago(2),
        ["updatedAt"] = Ago(1),
        ["author"] = new JsonObject { ["login"] = author, ["__typename"] = "User", ["avatarUrl"] = null },
        ["baseRefName"] = "main",
        ["baseRef"] = new JsonObject { ["branchProtectionRule"] = new JsonObject { ["requiresConversationResolution"] = true } },
        ["mergeable"] = "MERGEABLE",
        ["reviewDecision"] = null,
        ["readyForReviewEvents"] = Connection(),
        ["additions"] = 200,
        ["deletions"] = 10,
        ["changedFiles"] = 10,
        ["milestone"] = new JsonObject { ["title"] = "13.5" },
        ["labels"] = Connection(),
        ["assignees"] = Connection(),
        ["reviewRequests"] = Connection(),
        ["reviews"] = Connection(),
        ["reviewThreads"] = Connection(),
        ["closingIssuesReferences"] = Connection(),
        ["commits"] = new JsonObject
        {
            ["totalCount"] = 3,
            ["nodes"] = JsonData.Array([new JsonObject
            {
                ["commit"] = new JsonObject { ["committedDate"] = Ago(1), ["statusCheckRollup"] = new JsonObject { ["state"] = "SUCCESS" } }
            }])
        }
    };

    private static JsonObject IssueNode(int number) => new()
    {
        ["number"] = number,
        ["title"] = $"Issue {number}",
        ["url"] = $"https://github.com/microsoft/aspire/issues/{number}",
        ["createdAt"] = Ago(2),
        ["updatedAt"] = Ago(1),
        ["author"] = new JsonObject { ["login"] = "someone", ["__typename"] = "User" },
        ["milestone"] = null,
        ["labels"] = Connection(),
        ["assignees"] = Connection(),
        ["closedByPullRequestsReferences"] = Connection(new[] { "OPEN", "MERGED", "CLOSED" }.Select((state, index) => new JsonObject
        {
            ["number"] = index + 10,
            ["title"] = $"Fix {index}",
            ["url"] = $"https://github.com/microsoft/aspire/pull/{index + 10}",
            ["state"] = state,
            ["repository"] = new JsonObject { ["nameWithOwner"] = "microsoft/aspire" }
        }).ToArray())
    };

    private static JsonObject Connection(params JsonObject[] nodes) => new() { ["nodes"] = JsonData.Array(nodes) };

    private static HttpClient Client(Func<HttpRequestMessage, JsonObject, HttpResponseMessage> respond) => new(new Handler(respond));

    private static HttpResponseMessage Response(JsonObject node, bool issues = false, bool hasNext = false) =>
        JsonResponse(ResponseBody([node], issues, hasNext));

    private static JsonObject ResponseBody(JsonObject[] nodes, bool issues = false, bool hasNext = false) => new()
    {
        ["data"] = new JsonObject
        {
            ["repository"] = new JsonObject
            {
                ["isPrivate"] = false,
                [issues ? "issues" : "pullRequests"] = new JsonObject
                {
                    ["nodes"] = JsonData.Array(nodes),
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = hasNext, ["endCursor"] = hasNext ? "cursor-1" : null }
                }
            }
        }
    };

    private static HttpResponseMessage Error(string message, HttpStatusCode status) =>
        JsonResponse(new JsonObject { ["message"] = message }, status);

    private static HttpResponseMessage JsonResponse(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Handler(Func<HttpRequestMessage, JsonObject, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? new JsonObject() :
                JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            return respond(request, body);
        }
    }
}
