// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GitHub.TeamApp;

internal sealed class GitHubDashboard(HttpClient http, ILogger<GitHubDashboard> logger)
{
    private const int MaxPages = 25;
    private const string Separator = " \u00b7 ";

    public async Task<JsonObject> LoadAsync(IReadOnlyList<Account> accounts, JsonObject prefs, CancellationToken ct)
    {
        var usable = accounts.Where(account => account.Active && account.Token.Length > 0 && account.Login.Length > 0).ToArray();
        if (usable.Length == 0)
        {
            return new JsonObject
            {
                ["authenticated"] = false,
                ["message"] = "No active GitHub account. Enable an account in the Accounts tab so the app can read your review queue."
            };
        }
        var mode = prefs.Text("mode", "review");
        var release = prefs.Text("release", DashboardConstants.CurrentRelease).Trim();
        var showDrafts = prefs.Flag("showDrafts");
        var reviewLimit = Math.Max(1, prefs.Number("reviewLimit", DashboardConstants.ReviewLimit));
        var viewers = usable.Select(account => account.Login.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var repos = usable.SelectMany(account => account.Repos).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var concurrency = new SemaphoreSlim(8);
        var jobs = usable.SelectMany(account => account.Repos.Select(repo =>
            LoadRepositoryAsync(account, repo, mode == "issues", viewers, concurrency, ct))).ToArray();
        var results = await Task.WhenAll(jobs).ConfigureAwait(false);

        // Merge after all workers settle: no shared mutable JsonNodes, and duplicate URL selection
        // follows account preference order rather than nondeterministic request completion order.
        var all = results.SelectMany(result => result.Items).DistinctBy(item => item.Text("url"))
            .OrderByDescending(item => item.Date("updatedAt")).ToArray();
        var prs = mode == "issues" ? [] : all;
        var issues = mode == "issues" ? all : [];
        var successful = results.Where(result => result.Error is null).Select(result => (result.Host, result.Repo)).ToHashSet();
        var errors = results.Where(result => result.Error is not null && !successful.Contains((result.Host, result.Repo)))
            .Select(result => result.Error!).Distinct(StringComparer.Ordinal);
        var model = new ReviewModel(prefs: prefs);
        var visible = prs.Where(pr => showDrafts || !pr.Flag("draft")).ToArray();
        var lanes = mode switch
        {
            "issues" => BucketIssues(issues, usable[0].Login, model),
            "ship" => BucketShip(prs, release, showDrafts, model),
            _ => BucketReview(prs, showDrafts, reviewLimit, model)
        };
        return new JsonObject
        {
            ["authenticated"] = true,
            ["viewer"] = usable[0].Login,
            ["viewers"] = JsonData.Array(usable.Select(account => account.Login)),
            ["mode"] = mode,
            ["release"] = release,
            ["repos"] = JsonData.Array(repos),
            ["lanes"] = JsonData.Array(lanes),
            ["attention"] = mode == "review" ? BuildAttention(prs, usable.Select(account => account.Login).ToArray(), reviewLimit, model) : null,
            ["notifications"] = JsonData.Array(BuildNotifications(prs, prefs["notifications"] as JsonObject ?? new JsonObject(),
                prefs["dismissedNotifications"].Strings().ToArray())),
            ["counts"] = new JsonObject
            {
                ["prs"] = visible.Length,
                ["total"] = prs.Length,
                ["drafts"] = prs.Count(pr => pr.Flag("draft")),
                ["issues"] = issues.Length,
                ["mine"] = prs.Count(pr => pr.Flag("isMine")),
                ["needsReview"] = prs.Count(AwaitingReview),
                ["readyToMerge"] = prs.Count(IsReadyToMerge),
                ["ciFailing"] = visible.Count(pr => pr.Text("checksState") == "failure")
            },
            ["showDrafts"] = showDrafts,
            ["reviewLimit"] = reviewLimit,
            ["errors"] = JsonData.Array(errors),
            ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private async Task<RepositoryResult> LoadRepositoryAsync(Account account, string repo, bool issues,
        HashSet<string> viewers, SemaphoreSlim concurrency, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        var host = account.Host;
        await concurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            host = GitHubDashboardTransport.NormalizeHost(host);
            var parts = repo.Split('/');
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"Invalid repo \"{repo}\".");
            }
            string? after = null;
            var cursors = new HashSet<string>();
            for (var page = 0; page < MaxPages; page++)
            {
                var response = await GitHubDashboardTransport.QueryAsync(http, host, account.Token,
                    issues ? IssueQuery : PullRequestQuery,
                    new JsonObject { ["owner"] = parts[0], ["name"] = parts[1], ["after"] = after }, ct).ConfigureAwait(false);
                var repository = response["data"]?["repository"] as JsonObject ??
                    throw new InvalidDataException("Repository is unavailable or this account does not have access.");
                if (!repository.Text("nameWithOwner", repo).Equals(repo, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("GitHub returned a different repository than requested.");
                }
                var connection = repository[issues ? "issues" : "pullRequests"] as JsonObject ??
                    throw new InvalidDataException("GitHub returned no repository connection.");
                if (connection["nodes"] is not JsonArray)
                {
                    throw new InvalidDataException("GitHub returned a repository connection without nodes.");
                }
                foreach (var node in connection["nodes"].Objects())
                {
                    if (node.Text("url").Length == 0)
                    {
                        throw new InvalidDataException("GitHub returned an item without a canonical URL.");
                    }
                    if (node.Date("createdAt") is null || node.Date("updatedAt") is null)
                    {
                        throw new InvalidDataException("GitHub returned an item without valid createdAt/updatedAt timestamps.");
                    }
                    if (!IsRepositoryUrl(node.Text("url"), host, repo))
                    {
                        throw new InvalidDataException("GitHub returned an item outside the selected repository.");
                    }
                    items.Add(issues ? NormalizeIssue(repo, node, viewers) : NormalizePr(repo, node, viewers, repository.Flag("isPrivate")));
                }
                if (connection["pageInfo"] is not JsonObject pageInfo)
                {
                    throw new InvalidDataException("GitHub returned a repository connection without pagination metadata.");
                }
                if (!pageInfo.Flag("hasNextPage"))
                {
                    return new(host, repo, items, null);
                }
                after = pageInfo.Text("endCursor");
                if (after.Length == 0 || !cursors.Add(after))
                {
                    throw new InvalidDataException("GitHub returned a missing or repeated pagination cursor; the queue is incomplete.");
                }
            }
            throw new InvalidDataException($"Reached the {MaxPages}-page safety limit; the queue is incomplete.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && GitHubDashboardTransport.IsProviderFailure(ex))
        {
            var message = GitHubDashboardTransport.Redact(
                $"{repo} ({host}): {(ex is TaskCanceledException ? "GitHub request timed out." : ex.Message)}", account.Token);
            logger.LogWarning("GitHub repository load failed: {Reason}", message);
            return new(host, repo, items, message);
        }
        finally
        {
            concurrency.Release();
        }
    }

    internal static JsonObject NormalizePr(string repo, JsonObject node, HashSet<string> viewers, bool repoPrivate)
    {
        var requested = Nodes(node, "reviewRequests").Select(request => request["requestedReviewer"].Text("login",
            request["requestedReviewer"].Text("name"))).Where(login => login.Length > 0).ToArray();
        var labels = Nodes(node, "labels").Select(label => label.Text("name")).ToArray();
        var assignees = Nodes(node, "assignees").Select(assignee => assignee.Text("login")).ToArray();
        var unresolved = Nodes(node, "reviewThreads").Count(thread => !thread.Flag("isResolved"));
        var commitNodes = Nodes(node, "commits").ToArray();
        var state = commitNodes.FirstOrDefault()?["commit"]?["statusCheckRollup"].Text("state") switch
        {
            "SUCCESS" => "success",
            "FAILURE" or "ERROR" => "failure",
            "PENDING" or "EXPECTED" => "pending",
            _ => "none"
        };
        var checks = new JsonObject
        {
            ["state"] = state,
            ["totalCount"] = 0,
            ["successCount"] = 0,
            ["failureCount"] = 0,
            ["pendingCount"] = 0,
            ["neutralCount"] = 0,
            ["skippedCount"] = 0,
            ["completedAt"] = null,
            ["failingChecks"] = new JsonArray()
        };
        var review = DeriveReview(Nodes(node, "reviews").ToArray(), requested, viewers,
            node["baseRef"]?["branchProtectionRule"].Flag("requiresConversationResolution") == true, unresolved);
        var author = node["author"].Text("login", "ghost");
        if (IsCopilotLogin(author))
        {
            var humans = assignees.Where(login => login.Length > 0 && !login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) &&
                !login.Equals("copilot", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (humans.Length == 1)
            {
                author = $"{humans[0]}/copilot";
            }
        }
        var pr = new JsonObject
        {
            ["repository"] = repo,
            ["number"] = node.Number("number"),
            ["title"] = node.Text("title"),
            ["url"] = node.Text("url"),
            ["state"] = node.Text("state", "OPEN").ToLowerInvariant(),
            ["draft"] = node.Flag("isDraft"),
            ["author"] = author,
            ["authorType"] = node["author"]?["__typename"]?.DeepClone(),
            ["authorAvatarUrl"] = node["author"]?["avatarUrl"]?.DeepClone(),
            ["createdAt"] = node["createdAt"]?.DeepClone(),
            ["readyForReviewAt"] = Nodes(node, "readyForReviewEvents").FirstOrDefault()?["createdAt"]?.DeepClone(),
            ["updatedAt"] = node["updatedAt"]?.DeepClone(),
            ["baseRef"] = node["baseRefName"]?.DeepClone(),
            ["milestone"] = node["milestone"]?["title"]?.DeepClone(),
            ["labels"] = JsonData.Array(labels),
            ["assignees"] = JsonData.Array(assignees),
            ["requestedReviewers"] = JsonData.Array(requested),
            ["additions"] = node.Number("additions"),
            ["deletions"] = node.Number("deletions"),
            ["changedFiles"] = node.Number("changedFiles"),
            ["commitCount"] = node["commits"].Number("totalCount", commitNodes.Length),
            ["lastCommitAt"] = review["lastReviewedAt"] is not null && review.Text("state") is "reviewed" or "changes_requested"
                ? commitNodes.LastOrDefault()?["commit"]?["committedDate"]?.DeepClone() : null,
            ["linkedIssues"] = JsonData.Array(ScopedLinkedNodes(node, "closingIssuesReferences", repo).Select(issue => new JsonObject
            {
                ["repository"] = issue["repository"].Text("nameWithOwner", repo),
                ["number"] = issue.Number("number"),
                ["title"] = issue.Text("title"),
                ["url"] = issue.Text("url"),
                ["milestone"] = issue["milestone"]?["title"]?.DeepClone(),
                ["labels"] = JsonData.Array(Nodes(issue, "labels").Select(label => label.Text("name")))
            })),
            ["checks"] = checks,
            ["mergeableState"] = node.Text("mergeable") switch { "MERGEABLE" => "clean", "CONFLICTING" => "dirty", _ => "unknown" },
            ["unresolvedThreadCount"] = unresolved,
            ["mergeable"] = node["mergeable"]?.DeepClone(),
            ["reviewDecision"] = node["reviewDecision"]?.DeepClone(),
            ["review"] = review,
            ["isMine"] = viewers.Any(viewer => ReviewModel.ActorIdentityKey(viewer) == ReviewModel.ActorIdentityKey(author)),
            ["repoPrivate"] = repoPrivate
        };
        pr["checksState"] = ReviewModel.VisibleCheckState(pr);
        return pr;
    }

    private static JsonObject NormalizeIssue(string repo, JsonObject node, HashSet<string> viewers)
    {
        var author = node["author"].Text("login", "ghost");
        var assignees = Nodes(node, "assignees").Select(assignee => assignee.Text("login")).ToArray();
        return new JsonObject
        {
            ["repository"] = repo,
            ["number"] = node.Number("number"),
            ["title"] = node.Text("title"),
            ["url"] = node.Text("url"),
            ["author"] = author,
            ["authorAvatarUrl"] = node["author"]?["avatarUrl"]?.DeepClone(),
            ["authorType"] = node["author"]?["__typename"]?.DeepClone(),
            ["createdAt"] = node["createdAt"]?.DeepClone(),
            ["updatedAt"] = node["updatedAt"]?.DeepClone(),
            ["milestone"] = node["milestone"]?["title"]?.DeepClone(),
            ["labels"] = JsonData.Array(Nodes(node, "labels").Select(label => label.Text("name"))),
            ["assignees"] = JsonData.Array(assignees),
            ["linkedPullRequests"] = JsonData.Array(ScopedLinkedNodes(node, "closedByPullRequestsReferences", repo).Where(pr => pr.Text("state") != "CLOSED").Select(pr => new JsonObject
            {
                ["repository"] = pr["repository"].Text("nameWithOwner", repo),
                ["number"] = pr.Number("number"),
                ["title"] = pr.Text("title"),
                ["url"] = pr.Text("url"),
                ["state"] = pr.Text("state")
            })),
            ["isMine"] = viewers.Contains(author.ToLowerInvariant()),
            ["assignedToMe"] = assignees.Any(assignee => viewers.Contains(assignee.ToLowerInvariant()))
        };
    }

    private static JsonObject DeriveReview(IReadOnlyList<JsonObject> reviews, string[] requested, HashSet<string> viewers,
        bool requiresConversationResolution, int rawUnresolved)
    {
        var human = reviews.Where(review => review["author"].Text("login").Length > 0 &&
            !IsBotReviewer(review["author"].Text("login")) && review.Date("submittedAt") is not null)
            .OrderBy(review => review.Date("submittedAt")).ToArray();
        var latest = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in human)
        {
            latest[review["author"].Text("login")] = review;
        }
        var state = latest.Values.Any(review => review.Text("state") == "CHANGES_REQUESTED") ? "changes_requested" :
            latest.Values.Any(review => review.Text("state") == "APPROVED") ? "approved" :
            latest.Values.Any(review => review.Text("state") == "COMMENTED") ? "reviewed" : "waiting";
        var copilot = reviews.Any(review => review["author"].Text("login").ToLowerInvariant() is "copilot-pull-request-reviewer" or "copilot-pull-request-reviewer[bot]");
        return new JsonObject
        {
            ["state"] = state,
            ["approvalCount"] = human.Count(review => review.Text("state") == "APPROVED"),
            ["changesRequestedCount"] = human.Count(review => review.Text("state") == "CHANGES_REQUESTED"),
            ["commentedReviewCount"] = human.Count(review => review.Text("state") == "COMMENTED"),
            ["reviewerCount"] = latest.Count,
            ["lastApprovedAt"] = human.LastOrDefault(review => review.Text("state") == "APPROVED")?["submittedAt"]?.DeepClone(),
            ["lastReviewedAt"] = human.LastOrDefault()?["submittedAt"]?.DeepClone(),
            ["copilotReviewed"] = copilot,
            ["unresolvedThreadCount"] = state is "approved" or "reviewed" || (state == "waiting" && copilot) ? rawUnresolved : 0,
            ["requiresConversationResolution"] = requiresConversationResolution,
            ["reviewRequestedFromViewer"] = requested.Any(login => viewers.Contains(login.ToLowerInvariant())),
            ["viewerApproved"] = latest.Values.Any(review => review.Text("state") == "APPROVED" && viewers.Contains(review["author"].Text("login").ToLowerInvariant()))
        };
    }

    private static bool IsBotReviewer(string login)
    {
        var normalized = login.ToLowerInvariant();
        return normalized.EndsWith("[bot]", StringComparison.Ordinal) ||
            normalized is "copilot" or "dependabot" or "dependabot-preview" or "github-actions" or "renovate";
    }

    private static bool IsCopilotLogin(string login)
    {
        var normalized = login.ToLowerInvariant();
        return normalized is "copilot" or "copilot[bot]" or "github-copilot[bot]" || normalized.EndsWith("/copilot", StringComparison.Ordinal);
    }

    private static IEnumerable<JsonObject> Nodes(JsonObject node, string key) => node[key]?["nodes"].Objects() ?? [];

    internal static bool IsRepositoryUrl(string value, string host, string repository) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
        uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath.Equals($"/{repository}", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.StartsWith($"/{repository}/", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<JsonObject> ScopedLinkedNodes(JsonObject node, string key, string repository)
    {
        var host = Uri.TryCreate(node.Text("url"), UriKind.Absolute, out var uri) ? uri.Authority : "";
        return Nodes(node, key).Where(linked =>
            linked["repository"].Text("nameWithOwner").Equals(repository, StringComparison.OrdinalIgnoreCase) &&
            IsRepositoryUrl(linked.Text("url"), host, repository));
    }

    public static IReadOnlyList<JsonObject> DedupeSignals(IEnumerable<JsonObject> signals) => signals.DistinctBy(signal => Concept(signal.Text("label"))).ToArray();

    private static string Concept(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();
        if (normalized.StartsWith("ci failing", StringComparison.Ordinal))
        {
            return "ci failing";
        }

        if (normalized.Contains("unresolved", StringComparison.Ordinal))
        {
            return "unresolved feedback";
        }

        return normalized switch
        {
            "needs review" or "needs reviewer" or "no reviews" => "needs review",
            "ready to merge" or "merge" => "ready to merge",
            "re-review needed" or "re-review" or "commit after review" => "re-review needed",
            "fix ci" => "ci failing",
            "wait for ci" or "ci running" => "wait for ci",
            "author response" or "author fix" or "changes requested" => "author response",
            "quick wins" or "quick win" => "quick wins",
            "stalled" or "unstick" => "stalled",
            "docs" or "docs review" => "docs",
            "bots / automation" or "automation" or "bot" => "bots / automation",
            _ => normalized
        };
    }

    private static IReadOnlyList<JsonObject> SignalsFor(JsonObject pr, string reason, ReviewModel model, bool includeAction = true, int limit = 4) =>
        DedupeSignals(model.CreateAttentionSignals(pr).Skip(includeAction ? 0 : 1)
            .Where(signal => reason.Length == 0 || Concept(signal.Text("label")) != Concept(reason)).Take(limit));

    public static bool IsReadyToMerge(JsonObject pr) => !pr.Flag("draft") && pr["review"].Text("state") == "approved" &&
        !ReviewModel.IsMergeReviewBlocked(pr) && pr.Text("checksState") == "success" &&
        pr.Number("unresolvedThreadCount") == 0 && pr.Text("mergeable") != "CONFLICTING";

    private static bool AwaitingReview(JsonObject pr) => !pr.Flag("draft") && pr.Text("checksState") == "success" &&
        pr.Number("unresolvedThreadCount") == 0 && pr["review"].Text("state") != "changes_requested" &&
        pr.Text("mergeable") != "CONFLICTING" && !pr.Flag("isMine") && pr["review"].Text("state") != "approved" &&
        !ReviewModel.ShouldHideFromSharedPullRequestLists(pr);

    private static JsonObject Lane(string id, string label, string tone) =>
        new() { ["id"] = id, ["label"] = label, ["tone"] = tone, ["items"] = new JsonArray() };

    private static JsonObject Card(JsonObject pr, string reason, ReviewModel model, bool reviewDebt = false)
    {
        var card = new JsonObject { ["pr"] = pr.DeepClone(), ["reason"] = reason, ["signals"] = JsonData.Array(SignalsFor(pr, reason, model)) };
        if (reviewDebt)
        {
            card["reviewDebt"] = model.IsReviewDebt(pr);
        }
        return card;
    }

    private static IReadOnlyList<JsonObject> BucketReview(IReadOnlyList<JsonObject> prs, bool showDrafts, int limit, ReviewModel model)
    {
        var lanes = new[] { Lane("review-queue", "Review queue", "danger"), Lane("ready-to-merge", "Ready to merge", "success"), Lane("your-prs", "Your PRs", "accent") };
        foreach (var pr in prs.Where(pr => showDrafts || !pr.Flag("draft")))
        {
            var index = AwaitingReview(pr) ? 0 : IsReadyToMerge(pr) ? 1 : pr.Flag("isMine") ? 2 : -1;
            if (index < 0)
            {
                continue;
            }
            var reason = index switch
            {
                0 => $"Green and resolved{Separator}ready for review",
                1 => "Approved and green, ready to land",
                _ => OwnLaneReason(pr)
            };
            ((JsonArray)lanes[index]["items"]!).Add((JsonNode)Card(pr, reason, model));
        }
        var queue = lanes[0];
        var items = queue["items"].Objects().OrderByDescending(card =>
        {
            var pr = (JsonObject)card["pr"]!;
            return Math.Min(model.AgeDays(ReviewModel.ReviewAgeStartedAt(pr)), 90) +
                (pr["requestedReviewers"].Strings().Any() ? 20 : 0) +
                (!pr.Flag("draft") && pr.Number("changedFiles") <= 3 && pr.Number("additions") + pr.Number("deletions") <= 40 ? 12 : 0) +
                (pr["review"].Text("state") == "reviewed" ? 6 : 0);
        }).ToArray();
        queue["cappedTotal"] = items.Length;
        queue["items"] = JsonData.Array(items.Take(limit));
        return lanes.Where(lane => lane["items"].Objects().Any()).ToArray();
    }

    private static string OwnLaneReason(JsonObject pr)
    {
        if (pr.Text("checksState") == "failure")
        {
            return $"CI is failing{Separator}back in your court";
        }

        if (pr.Text("mergeable") == "CONFLICTING")
        {
            return $"Merge conflicts{Separator}back in your court";
        }

        if (pr["review"].Text("state") == "changes_requested")
        {
            return $"Changes requested{Separator}back in your court";
        }

        if (pr.Number("unresolvedThreadCount") > 0)
        {
            return $"{ReviewModel.FormatCount(pr.Number("unresolvedThreadCount"), "unresolved thread")} to resolve";
        }

        if (pr.Flag("draft"))
        {
            return $"Draft{Separator}still cooking";
        }

        return "You opened this";
    }

    private static IReadOnlyList<JsonObject> BucketIssues(IReadOnlyList<JsonObject> issues, string login, ReviewModel model)
    {
        var covered = new HashSet<string>();
        JsonObject Item(JsonObject issue)
        {
            covered.Add(issue.Text("url"));
            return new JsonObject { ["issue"] = issue.DeepClone(), ["signals"] = JsonData.Array(model.CreateIssueSignals(issue)) };
        }
        var lanes = ReviewModel.CreateFocusIssueBuckets(issues, login).Select(bucket =>
        {
            var lane = Lane($"focus-{Regex.Replace(bucket.Label.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-')}", bucket.Label, bucket.Tone);
            lane["items"] = JsonData.Array(bucket.Issues.Select(Item));
            return lane;
        }).ToList();
        var residual = issues.Where(issue => !covered.Contains(issue.Text("url"))).ToArray();
        static bool Untriaged(JsonObject issue) => !issue["labels"].Strings().Any() && !issue["assignees"].Strings().Any();
        var triage = Lane("triage", "Needs triage", "warning");
        triage["items"] = JsonData.Array(residual.Where(Untriaged).Select(Item));
        var active = Lane("active", "Recently active", "muted");
        active["items"] = JsonData.Array(residual.Where(issue => !Untriaged(issue)).Select(Item));
        lanes.Add(triage);
        lanes.Add(active);
        return lanes.Where(lane => lane["items"].Objects().Any()).ToArray();
    }

    private static IReadOnlyList<JsonObject> BucketShip(IReadOnlyList<JsonObject> prs, string release, bool showDrafts, ReviewModel model)
    {
        var lanes = new[] { Lane("ready", "Ready to ship", "success"), Lane("in-progress", "In progress", "accent"), Lane("blocked", "Blocked", "danger") };
        foreach (var pr in prs.Where(pr => (release.Length == 0 || pr.Text("milestone") == release) && (showDrafts || !pr.Flag("draft"))))
        {
            var index = pr.Text("checksState") == "failure" || pr.Text("mergeable") == "CONFLICTING" || pr["review"].Text("state") == "changes_requested"
                ? 2 : IsReadyToMerge(pr) ? 0 : 1;
            var reason = pr.Text("milestone") is { Length: > 0 } milestone ? $"Milestone {milestone}" : "No milestone";
            ((JsonArray)lanes[index]["items"]!).Add((JsonNode)Card(pr, reason, model));
        }
        return lanes.Where(lane => lane["items"].Objects().Any()).ToArray();
    }

    private static JsonObject BuildAttention(IReadOnlyList<JsonObject> prs, string[] logins, int limit, ReviewModel model)
    {
        var buckets = model.CreateAttentionBuckets(prs, logins[0]);
        var focus = model.ComputeFocusItems(buckets);
        var focusCards = focus.Select(item =>
        {
            var card = Card(item.PullRequest, item.Reason, model, reviewDebt: true);
            card["bucketLabel"] = item.BucketLabel;
            card["bucketTone"] = item.BucketTone;
            return card;
        }).ToArray();
        var capped = CapFocusKeepingDebt(focusCards, limit);
        return new JsonObject
        {
            ["forMe"] = JsonData.Array(model.CreateForMeItems(prs, logins).Select(item =>
            {
                var card = Card(item.PullRequest, item.Reason, model, reviewDebt: true);
                card["action"] = item.Action;
                card["signals"] = JsonData.Array(DedupeSignals(new[] { ReviewModel.Signal(item.Action, item.Tone) }
                    .Concat(SignalsFor(item.PullRequest, item.Reason, model, includeAction: false, limit: 3))));
                return card;
            })),
            ["focus"] = JsonData.Array(capped),
            ["focusTotal"] = focus.Count,
            ["focusLimit"] = limit,
            ["focusMixed"] = capped.Count > limit,
            ["focusExclusions"] = JsonData.Array(model.ComputeFocusExclusionItems(prs, buckets, focus, logins[0]).Select(item => new JsonObject
            {
                ["pr"] = item.PullRequest.DeepClone(),
                ["reason"] = item.Reason.Text("detail"),
                ["reviewDebt"] = model.IsReviewDebt(item.PullRequest),
                ["signals"] = JsonData.Array(DedupeSignals(new[] { ReviewModel.Signal(item.Reason.Text("label"), item.Reason.Text("tone")) }
                    .Concat(SignalsFor(item.PullRequest, item.Reason.Text("label"), model, includeAction: false))))
            })),
            ["buckets"] = JsonData.Array(buckets.Select(bucket => new JsonObject
            {
                ["label"] = bucket.Label,
                ["tone"] = bucket.Tone,
                ["items"] = JsonData.Array(bucket.Items.Select(item => Card(item.PullRequest, item.Reason, model, reviewDebt: true)))
            })),
            ["community"] = JsonData.Array(model.ComputeCommunityItems(prs).Select(item => Card(item.PullRequest, "Community", model, reviewDebt: true))),
            ["developerCounts"] = JsonData.Array(model.CreateDeveloperPullRequestCounts(prs))
        };
    }

    public static IReadOnlyList<JsonObject> CapFocusKeepingDebt(IReadOnlyList<JsonObject> cards, int limit) =>
        cards.Take(limit).Concat(cards.Skip(limit).Where(card => card.Flag("reviewDebt"))).ToArray();

    public static IReadOnlyList<JsonObject> BuildNotifications(IReadOnlyList<JsonObject> prs, JsonObject prefs, IReadOnlyList<string> dismissed)
    {
        var skip = dismissed.ToHashSet(StringComparer.Ordinal);
        var notifications = new List<JsonObject>();
        foreach (var pr in prs)
        {
            if (prefs.Flag("reviewRequested") && !pr.Flag("draft") && !pr.Flag("isMine") && pr["review"].Flag("reviewRequestedFromViewer"))
            {
                Add(pr, "review-requested", "danger", "Review requested from you");
            }
            if (prefs.Flag("readyToMerge") && IsReadyToMerge(pr) && (pr.Flag("isMine") || pr["review"].Flag("viewerApproved")))
            {
                Add(pr, "ready-to-merge", "success", pr.Flag("isMine") ? "Your PR is ready to merge" : "A PR you approved is ready to merge");
            }
            if (pr.Flag("isMine"))
            {
                if (prefs.Flag("changesRequested") && pr["review"].Text("state") == "changes_requested")
                {
                    Add(pr, "changes-requested", "warning", "Changes requested on your PR");
                }
                if (prefs.Flag("ciFailing") && pr.Text("checksState") == "failure")
                {
                    Add(pr, "ci-failing", "danger", "CI is failing on your PR");
                }
            }
        }
        return notifications;

        void Add(JsonObject pr, string kind, string tone, string detail)
        {
            var id = $"{kind}:{pr.Text("repository")}#{pr.Number("number")}";
            if (!skip.Contains(id))
            {
                notifications.Add(new JsonObject
                {
                    ["id"] = id,
                    ["kind"] = kind,
                    ["tone"] = tone,
                    ["title"] = pr.Text("title"),
                    ["repository"] = pr.Text("repository"),
                    ["number"] = pr.Number("number"),
                    ["url"] = pr.Text("url"),
                    ["detail"] = detail
                });
            }
        }
    }

    private sealed record RepositoryResult(string Host, string Repo, List<JsonObject> Items, string? Error);

    private const string PullRequestQuery = """
        query($owner:String!, $name:String!, $after:String) {
          repository(owner:$owner, name:$name) {
            nameWithOwner isPrivate
            pullRequests(states:OPEN, first:40, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
              pageInfo { hasNextPage endCursor }
              nodes {
                number title url isDraft state createdAt updatedAt
                author { __typename login avatarUrl }
                baseRefName mergeable reviewDecision
                baseRef { branchProtectionRule { requiresConversationResolution } }
                readyForReviewEvents: timelineItems(last:1, itemTypes:[READY_FOR_REVIEW_EVENT]) {
                  nodes { ... on ReadyForReviewEvent { createdAt } }
                }
                additions deletions changedFiles
                milestone { title }
                labels(first:15) { nodes { name } }
                assignees(first:10) { nodes { login } }
                reviewRequests(first:20) { nodes { requestedReviewer { __typename ... on User { login } } } }
                reviews(first:60) { nodes { state author { login } submittedAt } }
                reviewThreads(first:60) { nodes { isResolved } }
                commits(last:1) { totalCount nodes { commit { committedDate statusCheckRollup { state } } } }
                closingIssuesReferences(first:10) {
                  nodes {
                    number title url repository { nameWithOwner } milestone { title }
                    labels(first:15) { nodes { name } }
                  }
                }
              }
            }
          }
        }
        """;

    private const string IssueQuery = """
        query($owner:String!, $name:String!, $after:String) {
          repository(owner:$owner, name:$name) {
            nameWithOwner
            issues(states:OPEN, first:40, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
              pageInfo { hasNextPage endCursor }
              nodes {
                number title url createdAt updatedAt
                author { __typename login avatarUrl }
                milestone { title }
                labels(first:15) { nodes { name } }
                assignees(first:10) { nodes { login } }
                closedByPullRequestsReferences(first:5, includeClosedPrs:true) {
                  nodes { number title url state repository { nameWithOwner } }
                }
              }
            }
          }
        }
        """;
}
