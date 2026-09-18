// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aspire.TeamApp;

// Keep this engine independent of HTTP and serialization so one clock governs a complete snapshot.
internal sealed class ReviewModel(TimeProvider? timeProvider = null)
{
    private readonly DateTimeOffset _now = (timeProvider ?? TimeProvider.System).GetUtcNow();
    private const string Regression = "Regression";
    private const string ApprovedAging = "Approved but aging";
    private const string AgedCommunity = "Aged out community";
    private const string MyDrafts = "My draft PRs";
    private const string Separator = " \u00b7 ";

    private static readonly (string Label, string Tone)[] s_bucketDefinitions =
    [
        (Regression, "danger"), (ApprovedAging, "danger"), ("CI failing", "danger"),
        ("Merge conflicts", "danger"), ("Unresolved feedback", "danger"), ("Ready to merge", "success"),
        ("Re-review needed", "warning"), ("Docs", "accent"), ("Community Toolkit", "accent"),
        ("Bots / automation", "accent"), (AgedCommunity, "warning"), ("Quick wins", "success"),
        ("Needs review", "warning"), ("Review started", "accent"), ("Stalled", "warning"),
        ("Author response", "danger"), ("Draft", "accent")
    ];

    private static readonly HashSet<string> s_excludedFocus =
    [
        "Stalled", "Draft", MyDrafts, "Docs", "Community Toolkit", "Bots / automation",
        "Community", AgedCommunity, "Unresolved feedback", "Merge conflicts", "CI failing", "Author response"
    ];

    private static readonly HashSet<string> s_disqualifyingFocus =
    [
        "Draft", MyDrafts, "Docs", "Community Toolkit", "Bots / automation",
        "Community", AgedCommunity, "Unresolved feedback", "Merge conflicts"
    ];

    private static readonly HashSet<string> s_specializedFocus =
        ["Docs", "Community Toolkit", "Bots / automation", "Community", AgedCommunity];

    private static readonly string[] s_exclusionRanks =
    [
        "ci-failing", "merge-conflicts", "unresolved-feedback", "held-by-label", "author-response",
        "stale-activity", "community-list", "specialized-lane", "stalled-only", "outside-queue"
    ];

    private static readonly (string Label, string Tone, string[] Terms)[] s_issueDomains =
    [
        ("Installer/acquisition", "accent", ["installer", "install", "workload", "acquisition", "setup", "visual studio", " vs ", "sdk"]),
        ("TypeScript/polyglot", "accent", ["typescript", " ts ", "javascript", " js ", "node", "polyglot", "apphost"]),
        ("CLI channel/versioning", "accent", ["cli", "channel", "version", "versioning", "feed", "template"]),
        ("Docs/release readiness", "success", ["docs", "documentation", "release notes", "readme", "release readiness", "announcement"])
    ];

    public static string ActorIdentityKey(string actor)
    {
        var normalized = actor.ToLowerInvariant();
        if (normalized.EndsWith("/copilot", StringComparison.Ordinal))
        {
            normalized = normalized[..^"/copilot".Length];
        }
        return new string(normalized.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
    }

    private static bool SameLogin(string first, string second) => ActorIdentityKey(first) == ActorIdentityKey(second);
    private static bool CopilotAttributed(string author) => author.EndsWith("/copilot", StringComparison.OrdinalIgnoreCase);

    private static bool IsBotAuthor(JsonObject item)
    {
        var author = item.Text("author").ToLowerInvariant();
        if (CopilotAttributed(author))
        {
            return false;
        }
        return item.Text("authorType") == "Bot" || author is "copilot-swe-agent" or "dotnet-maestro" or "copilot" or "github-actions" ||
            author.Contains("bot", StringComparison.Ordinal) || author.EndsWith("[bot]", StringComparison.Ordinal);
    }

    private static string? CoreTeamOwnershipActor(string author)
    {
        var direct = DashboardConstants.CoreTeamMembers.FirstOrDefault(member => SameLogin(member, author));
        if (direct is not null)
        {
            return direct;
        }
        var suffix = DashboardConstants.CoreTeamMemberAliasSuffixes.FirstOrDefault(s =>
            s.Length > 0 && author.Length > s.Length && author.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (suffix is null)
        {
            return null;
        }
        return DashboardConstants.CoreTeamMembers.FirstOrDefault(member => SameLogin(member, author[..^suffix.Length])) ?? author;
    }

    private static bool CoreTeam(JsonObject pr) => CoreTeamOwnershipActor(pr.Text("author")) is not null;
    private static bool Toolkit(JsonObject pr) => pr.Text("repository").Equals("communitytoolkit/aspire", StringComparison.OrdinalIgnoreCase);
    private static bool CommunityAuthor(JsonObject pr) => !IsBotAuthor(pr) && !CoreTeam(pr);

    public static bool IsCommunityPullRequest(JsonObject pr) =>
        !pr.Flag("isMine") && !pr.Flag("repoPrivate") && CommunityAuthor(pr) && !Toolkit(pr);

    public static string ReviewAgeStartedAt(JsonObject pr) => pr.Text("readyForReviewAt", pr.Text("createdAt"));
    private bool AgedOutCommunity(JsonObject pr) => IsCommunityPullRequest(pr) && Age(pr.Text("updatedAt")).TotalDays > 14;
    private bool CommunityWaiting(JsonObject pr) =>
        !pr.Flag("isMine") && !pr.Flag("repoPrivate") && CommunityAuthor(pr) &&
        ((Review(pr).Text("state") == "waiting" && Age(ReviewAgeStartedAt(pr)).TotalHours >= 12) ||
         (Review(pr).Text("state") == "reviewed" && Idle(pr)));

    public static IEnumerable<DashboardConstants.CheckFailureRule> FilterCheckFailureRules(IEnumerable<DashboardConstants.CheckFailureRule> rules) =>
        rules.Where(rule => rule.Repository.Length > 0 && rule.Label.Length > 0 &&
            (rule.CheckNames.Length > 0 || rule.CheckNameContains.Length > 0));

    private static IEnumerable<DashboardConstants.CheckFailureRule> Rules(JsonObject pr) =>
        FilterCheckFailureRules(DashboardConstants.NonBlockingCheckFailureRules)
            .Where(rule => rule.Repository.Equals(pr.Text("repository"), StringComparison.OrdinalIgnoreCase));

    private static bool NonBlockingAggregateFailure(JsonObject pr)
    {
        var checks = pr["checks"];
        return checks.Text("state") == "failure" && Rules(pr).Any() &&
            checks.Number("totalCount") == 0 && checks.Number("failureCount") == 0 &&
            (checks?["failingChecks"].Objects().Count() ?? 0) == 0;
    }

    private static DashboardConstants.CheckFailureRule? NonBlockingOnlyFailureRule(JsonObject pr)
    {
        var checks = pr["checks"];
        var failures = checks?["failingChecks"].Objects().ToArray() ?? [];
        if (checks.Text("state") != "failure" || failures.Length == 0 || checks.Number("failureCount") != failures.Length)
        {
            return null;
        }
        var matched = failures.Select(check => Rules(pr).FirstOrDefault(rule =>
            rule.CheckNames.Any(name => name.Equals(check.Text("name").Trim(), StringComparison.OrdinalIgnoreCase)) ||
            rule.CheckNameContains.Any(fragment => check.Text("name").Trim().Contains(fragment, StringComparison.OrdinalIgnoreCase)))).ToArray();
        return matched.All(rule => rule is not null) ? matched[0] : null;
    }

    public static bool IsChecksFailing(JsonObject pr) =>
        pr["checks"].Text("state") == "failure" && !NonBlockingAggregateFailure(pr) && NonBlockingOnlyFailureRule(pr) is null;

    public static string VisibleCheckState(JsonObject pr) =>
        NonBlockingAggregateFailure(pr) ? "unknown" :
        NonBlockingOnlyFailureRule(pr) is null ? pr["checks"].Text("state", "none") :
        pr["checks"].Number("pendingCount") > 0 ? "pending" : "success";

    public static bool HasMergeConflicts(JsonObject pr) => pr.Text("mergeableState") == "dirty";
    public static bool IsMergeReviewBlocked(JsonObject pr) => pr.Text("reviewDecision") is "REVIEW_REQUIRED" or "CHANGES_REQUESTED";
    private static string? DoNotMergeLabel(JsonObject pr) =>
        pr["labels"].Strings().FirstOrDefault(label => label.ToLowerInvariant() is "needs-author-action" or "no-merge");
    public static bool HasNeedsAuthorActionLabel(JsonObject pr) => DoNotMergeLabel(pr) is not null;
    public static bool ShouldHideFromSharedPullRequestLists(JsonObject pr) => pr.Flag("draft") || HasMergeConflicts(pr) || HasNeedsAuthorActionLabel(pr);
    private static JsonObject Review(JsonObject pr) => (JsonObject)pr["review"]!;
    private static bool Unresolved(JsonObject pr) => Review(pr).Number("unresolvedThreadCount") > 0;
    private static bool ChecksPending(JsonObject pr) => VisibleCheckState(pr) is "pending" or "unknown";
    private bool Idle(JsonObject pr) => Age(pr.Text("updatedAt")).TotalDays >= 7;
    private static bool RegressionLabels(JsonObject item) => item["labels"].Strings().Any(label => label.Contains("regression", StringComparison.OrdinalIgnoreCase));
    private static bool RegressionSignal(JsonObject pr) => RegressionLabels(pr) || pr["linkedIssues"].Objects().Any(RegressionLabels);
    private static bool GeneratedDocs(JsonObject pr) => pr.Text("repository").Equals("microsoft/aspire.dev", StringComparison.OrdinalIgnoreCase) &&
        pr["labels"].Strings().Contains("docs-from-code", StringComparer.OrdinalIgnoreCase);
    private static string ApprovalAgeAt(JsonObject pr) => Review(pr).Text("lastApprovedAt", Review(pr).Text("lastReviewedAt"));
    private bool ApprovedButAging(JsonObject pr) => Review(pr).Text("state") == "approved" &&
        ApprovalAgeAt(pr).Length > 0 && Age(ApprovalAgeAt(pr)).TotalDays >= 2;

    private static bool NeedsReReview(JsonObject pr) =>
        Review(pr).Text("state") is "reviewed" or "changes_requested" &&
        pr.Date("lastCommitAt") is { } committed && Review(pr).Date("lastReviewedAt") is { } reviewed && committed > reviewed;

    private static bool ReleaseMatches(string value) => Regex.IsMatch(value,
        $@"(^|[^0-9]){Regex.Escape(DashboardConstants.CurrentRelease)}([^0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool TargetsReleaseIssue(JsonObject item) =>
        new[] { item.Text("title"), item.Text("milestone") }.Concat(item["labels"].Strings()).Any(ReleaseMatches);
    private static bool TargetsRelease(JsonObject pr) => TargetsReleaseIssue(pr) || pr["linkedIssues"].Objects().Any(TargetsReleaseIssue);

    private bool QuickWin(JsonObject pr)
    {
        var lines = pr.Number("additions") + pr.Number("deletions");
        return Review(pr).Text("state") == "waiting" && !Unresolved(pr) && !HasMergeConflicts(pr) &&
            CoreTeam(pr) && !TargetsRelease(pr) && pr["linkedIssues"].Objects().Count() <= 1 &&
            pr.Number("commitCount") <= 2 && pr.Number("changedFiles") is > 0 and <= 3 &&
            lines is > 0 and <= 80 && !Idle(pr);
    }

    private static bool NeedsReview(JsonObject pr) =>
        Review(pr).Text("state") == "waiting" && !Unresolved(pr) && !HasMergeConflicts(pr) && CoreTeam(pr);

    private List<string> ReviewBucketLabels(JsonObject pr)
    {
        var labels = new List<string>();
        if (RegressionSignal(pr))
        {
            labels.Add(Regression);
        }

        if (pr.Flag("draft"))
        {
            labels.Add("Draft");
            return labels;
        }
        if (IsCommunityPullRequest(pr) && !AgedOutCommunity(pr))
        {
            return labels;
        }
        if (IsBotAuthor(pr))
        {
            labels.Add("Bots / automation");
        }

        if (GeneratedDocs(pr))
        {
            labels.Add("Docs");
        }

        if (Toolkit(pr))
        {
            labels.Add("Community Toolkit");
        }

        if (IsChecksFailing(pr))
        {
            labels.Add("CI failing");
        }

        if (HasMergeConflicts(pr))
        {
            labels.Add("Merge conflicts");
        }

        if (ApprovedButAging(pr))
        {
            labels.Add(ApprovedAging);
        }

        if (Unresolved(pr))
        {
            labels.Add("Unresolved feedback");
        }

        if (Review(pr).Text("state") == "approved" && !ApprovedButAging(pr) && !IsChecksFailing(pr) &&
            !ChecksPending(pr) && !(Unresolved(pr) && Review(pr).Flag("requiresConversationResolution")) &&
            !HasMergeConflicts(pr) && !HasNeedsAuthorActionLabel(pr) && !IsMergeReviewBlocked(pr))
        {
            labels.Add("Ready to merge");
        }
        if (NeedsReReview(pr))
        {
            labels.Add("Re-review needed");
        }

        if (Review(pr).Text("state") == "changes_requested")
        {
            labels.Add("Author response");
        }

        if (Idle(pr))
        {
            labels.Add("Stalled");
        }

        if (IsCommunityPullRequest(pr))
        {
            labels.Add(AgedOutCommunity(pr) ? AgedCommunity : "Community");
        }

        if (QuickWin(pr) && !IsChecksFailing(pr))
        {
            labels.Add("Quick wins");
        }

        if (NeedsReview(pr))
        {
            labels.Add("Needs review");
        }

        if (labels.Count == 0)
        {
            labels.Add("Review started");
        }

        return labels;
    }

    private string ReviewSignal(JsonObject pr, string label)
    {
        if (pr.Flag("draft"))
        {
            return "Draft";
        }
        return label switch
        {
            Regression => RegressionLabels(pr) ? "Regression label" : Regression,
            ApprovedAging => ApprovalAgeAt(pr).Length > 0 ? $"Approved {FormatAge(ApprovalAgeAt(pr))}" : "Approved",
            "CI failing" => pr["checks"].Number("failureCount") > 0 ? FormatCount(pr["checks"].Number("failureCount"), "failing check") : "CI failing",
            "Unresolved feedback" => FormatCount(Review(pr).Number("unresolvedThreadCount"), "unresolved thread"),
            "Ready to merge" => FormatCount(Review(pr).Number("approvalCount"), "approval"),
            "Re-review needed" => pr.Text("lastCommitAt").Length > 0 ? $"Pushed {FormatAge(pr.Text("lastCommitAt"))}" : "Pushed after review",
            "Merge conflicts" => "Merge conflicts",
            "Docs" => "generated docs",
            "Community Toolkit" => "CommunityToolkit/Aspire",
            "Bots / automation" => "bot",
            "Community" => CommunityWaiting(pr) ? $"Community{Separator}waiting {FormatAge(ReviewAgeStartedAt(pr))}" : "community",
            AgedCommunity => AgedCommunity,
            "Quick wins" => ReviewFootprint(pr),
            "Needs review" => "No reviews",
            "Stalled" => $"Idle {FormatAge(pr.Text("updatedAt"))} ago",
            "Author response" => "Changes requested",
            _ => FormatCount(Review(pr).Number("reviewerCount"), "reviewer")
        };
    }

    private static string ReviewFootprint(JsonObject pr)
    {
        var parts = new[] { (pr.Number("changedFiles"), "file"), (pr.Number("additions") + pr.Number("deletions"), "line"), (pr.Number("commitCount"), "commit") }
            .Where(part => part.Item1 > 0).Take(2).Select(part => FormatCount(part.Item1, part.Item2)).ToArray();
        return parts.Length == 0 ? "size unknown" : string.Join(Separator, parts);
    }

    public IReadOnlyList<JsonObject> CreateAttentionSignals(JsonObject pr)
    {
        var action = ActionSignal(pr);
        var signals = new List<JsonObject> { action };
        if (HasMergeConflicts(pr) && action.Text("label") != "merge conflicts")
        {
            signals.Add(Signal("merge conflicts", "danger"));
        }

        var progress = ReviewProgressSignal(pr);
        var prioritizeProgress = progress.Text("tone") == "success" && Review(pr).Number("approvalCount") > 0;
        if (progress is not null && prioritizeProgress)
        {
            signals.Add(progress);
        }

        if (TargetsRelease(pr))
        {
            signals.Add(Signal($"release {DashboardConstants.CurrentRelease}", "danger"));
        }

        if (RegressionSignal(pr))
        {
            signals.Add(Signal("regression", "danger"));
        }

        if (pr.Text("baseRef").StartsWith("release/", StringComparison.Ordinal))
        {
            signals.Add(Signal($"base {pr.Text("baseRef")}", "danger"));
        }

        if (ChecksAttentionSignal(pr) is { } checksSignal)
        {
            signals.Add(checksSignal);
        }

        if (Unresolved(pr))
        {
            signals.Add(Signal(FormatCount(Review(pr).Number("unresolvedThreadCount"), "unresolved", "unresolved"), "danger"));
        }

        if (ApprovedButAging(pr))
        {
            signals.Add(Signal($"approved {FormatAge(ApprovalAgeAt(pr))}", "danger"));
        }

        if (NeedsReReview(pr))
        {
            signals.Add(Signal("commit after review", "warning"));
        }

        if (GeneratedDocs(pr))
        {
            signals.Add(Signal("docs", "accent"));
        }

        if (Toolkit(pr))
        {
            signals.Add(Signal("community toolkit", "accent"));
        }

        if (AgedOutCommunity(pr))
        {
            signals.Add(Signal(AgedCommunity.ToLowerInvariant(), "warning"));
        }
        else if (CommunityWaiting(pr))
        {
            signals.Add(Signal("community wait", "warning"));
        }

        if (QuickWin(pr))
        {
            signals.Add(Signal("quick win", "success"));
        }

        if (Idle(pr))
        {
            signals.Add(Signal($"idle {FormatAge(pr.Text("updatedAt"))}", "warning"));
        }

        if (OldFirstSignal(pr) is { } oldFirst)
        {
            signals.Add(oldFirst);
        }

        signals.Add(Signal($"open {FormatAge(ReviewAgeStartedAt(pr))}", Age(ReviewAgeStartedAt(pr)).TotalDays >= 7 ? "warning" : "muted"));
        if (progress is not null && !prioritizeProgress)
        {
            signals.Add(progress);
        }

        if (Review(pr).Text("lastReviewedAt").Length > 0 && Review(pr).Text("state") != "waiting")
        {
            signals.Add(Signal($"reviewed {FormatAge(Review(pr).Text("lastReviewedAt"))}", "muted"));
        }
        if (Review(pr).Number("commentedReviewCount") > 0)
        {
            signals.Add(Signal(FormatCount(Review(pr).Number("commentedReviewCount"), "review comment"), "muted"));
        }

        foreach (var label in pr["labels"].Strings().Where(label => !GeneratedDocs(pr) || !label.Equals("docs-from-code", StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            var signal = Signal(label, "accent");
            // Only computed signals may authorize the browser's card actions.
            signal["kind"] = "repo-label";
            signals.Add(signal);
        }
        if (IsBotAuthor(pr))
        {
            signals.Add(Signal("bot", "accent"));
        }
        else if (CopilotAttributed(pr.Text("author")))
        {
            signals.Add(Signal("copilot", "accent"));
        }

        return signals.Take(7).ToArray();
    }

    private static JsonObject? ChecksAttentionSignal(JsonObject pr)
    {
        var checks = pr["checks"];
        if (checks is null || checks.Text("state") is "none" or "unknown" || NonBlockingAggregateFailure(pr))
        {
            return null;
        }
        if (NonBlockingOnlyFailureRule(pr) is { } rule)
        {
            return Signal(rule.Label, "warning");
        }
        return checks.Text("state") switch
        {
            "failure" => Signal(checks.Number("failureCount") > 0
                ? $"CI failing{Separator}{FormatCount(checks.Number("failureCount"), "check")}" : "CI failing", "danger"),
            "pending" => Signal("CI running", "warning"),
            _ => null
        };
    }

    private JsonObject? OldFirstSignal(JsonObject pr)
    {
        var age = Age(AgingReferenceAt(pr));
        if (IsReviewDebt(pr))
        {
            return Signal(DashboardConstants.ReviewDebtSignalLabel, "danger");
        }

        if (age.TotalDays >= 7)
        {
            return Signal("old first", "warning");
        }

        if (age.TotalHours < 12)
        {
            return Signal("newer", "muted");
        }

        return null;
    }

    private static string AgingReferenceAt(JsonObject pr)
    {
        if (IsChecksFailing(pr))
        {
            return LatestDate(pr["checks"].Text("completedAt"), pr.Text("lastCommitAt")) ?? pr.Text("updatedAt");
        }

        if (Review(pr).Text("state") == "approved")
        {
            return LatestDate(Review(pr).Text("lastApprovedAt"), Review(pr).Text("lastReviewedAt")) ?? pr.Text("updatedAt");
        }

        if (NeedsReReview(pr))
        {
            return pr.Text("lastCommitAt", Review(pr).Text("lastReviewedAt", pr.Text("updatedAt")));
        }

        if (Review(pr).Text("state") is "reviewed" or "changes_requested")
        {
            return Review(pr).Text("lastReviewedAt", pr.Text("updatedAt"));
        }

        return pr.Text("updatedAt");
    }

    private JsonObject ActionSignal(JsonObject pr)
    {
        if (RegressionSignal(pr))
        {
            return Signal("regression", "danger");
        }

        if (pr.Flag("draft"))
        {
            return Signal("draft", "muted");
        }

        if (IsBotAuthor(pr))
        {
            return Signal("automation", "accent");
        }

        if (GeneratedDocs(pr))
        {
            return Signal("docs review", "accent");
        }

        if (Toolkit(pr))
        {
            return Signal("toolkit review", "accent");
        }

        if (IsChecksFailing(pr))
        {
            return Signal("fix CI", "danger");
        }

        if (HasMergeConflicts(pr))
        {
            return Signal("merge conflicts", "danger");
        }

        if (Unresolved(pr))
        {
            return Signal("resolve feedback", "danger");
        }

        if (ApprovedButAging(pr))
        {
            return Signal("land approval", "danger");
        }

        if (Review(pr).Text("state") == "approved")
        {
            return ChecksPending(pr) ? Signal("wait for CI", "warning") : Signal("merge", "success");
        }

        if (NeedsReReview(pr))
        {
            return Signal("re-review", "warning");
        }

        if (Review(pr).Text("state") == "changes_requested")
        {
            return Signal("author fix", "danger");
        }

        if (AgedOutCommunity(pr))
        {
            return Signal(AgedCommunity.ToLowerInvariant(), "warning");
        }

        if (Idle(pr))
        {
            return Signal("unstick", "warning");
        }

        if (!pr.Flag("isMine") && !pr.Flag("repoPrivate") && CommunityAuthor(pr))
        {
            return CommunityWaiting(pr) ? Signal("community wait", "warning") : Signal("community", "accent");
        }
        if (QuickWin(pr))
        {
            return Signal("quick win", "success");
        }

        return Review(pr).Text("state") == "waiting" ? Signal("needs reviewer", "warning") : Signal("finish review", "accent");
    }

    private static JsonObject? ReviewProgressSignal(JsonObject pr)
    {
        var review = Review(pr);
        if (review.Text("state") == "waiting")
        {
            return Signal("no reviews", "warning");
        }

        if (review.Text("state") == "changes_requested")
        {
            return Signal(FormatCount(Math.Max(1, review.Number("changesRequestedCount")), "change request"), "danger");
        }

        if (review.Number("approvalCount") > 0)
        {
            return Signal(FormatCount(review.Number("approvalCount"), "approval"), "success");
        }

        if (review.Number("reviewerCount") > 0)
        {
            return Signal($"{FormatCount(review.Number("reviewerCount"), "reviewer")}{Separator}0 approvals", "accent");
        }

        return null;
    }

    public IReadOnlyList<AttentionBucket> CreateAttentionBuckets(IReadOnlyList<JsonObject> prs, string? login)
    {
        var buckets = s_bucketDefinitions.Select(definition => new AttentionBucket(definition.Label, definition.Tone, [])).ToList();
        if (!string.IsNullOrEmpty(login))
        {
            buckets.Add(new(MyDrafts, "accent", []));
        }

        var byLabel = buckets.ToDictionary(bucket => bucket.Label);
        foreach (var pr in prs.Where(pr => pr.Text("state") == "open" && !HasNeedsAuthorActionLabel(pr)))
        {
            foreach (var label in ReviewBucketLabels(pr))
            {
                if (byLabel.TryGetValue(label, out var bucket))
                {
                    bucket.Items.Add(new(pr, ReviewSignal(pr, label), label, bucket.Tone));
                }
            }
        }
        if (!string.IsNullOrEmpty(login))
        {
            foreach (var pr in prs.Where(pr => pr.Text("state") == "open" && pr.Flag("draft") && pr.Flag("isMine")))
            {
                byLabel[MyDrafts].Items.Add(new(pr, "Draft", MyDrafts, "accent"));
            }
        }
        return buckets.Where(bucket => bucket.Items.Count > 0).ToArray();
    }

    private static string FocusActivityAt(JsonObject pr, string label) => label switch
    {
        ApprovedAging or "Ready to merge" => LatestDate(Review(pr).Text("lastApprovedAt"), Review(pr).Text("lastReviewedAt")) ?? pr.Text("updatedAt"),
        "Re-review needed" => pr.Text("lastCommitAt", Review(pr).Text("lastReviewedAt", pr.Text("updatedAt"))),
        "Author response" or "Review started" => Review(pr).Text("lastReviewedAt", pr.Text("updatedAt")),
        "CI failing" => LatestDate(pr["checks"].Text("completedAt"), pr.Text("lastCommitAt")) ?? pr.Text("updatedAt"),
        _ => pr.Text("updatedAt")
    };

    private bool WithinFocusAge(JsonObject pr, string label) => Age(FocusActivityAt(pr, label)).TotalDays <= 14;
    public bool IsReviewDebt(JsonObject pr) => Review(pr).Text("state") != "approved" && Age(AgingReferenceAt(pr)).TotalDays >= 14;
    private static int FocusRank(string label) => label switch
    {
        Regression => -2,
        ApprovedAging => 0,
        "Re-review needed" => 1,
        "Ready to merge" => 2,
        "Author response" => 3,
        "Needs review" => 4,
        "Quick wins" => 5,
        "Review started" => 6,
        _ => int.MaxValue
    };

    private static string PrKey(JsonObject pr)
    {
        var host = Uri.TryCreate(pr.Text("url"), UriKind.Absolute, out var uri) ? uri.Authority : "";
        return $"{host}\n{pr.Text("repository").ToLowerInvariant()}#{pr.Number("number")}";
    }

    public IReadOnlyList<AttentionItem> ComputeFocusItems(IReadOnlyList<AttentionBucket> buckets)
    {
        var blocked = buckets.Where(bucket => s_disqualifyingFocus.Contains(bucket.Label))
            .SelectMany(bucket => bucket.Items.Select(item => PrKey(item.PullRequest))).ToHashSet();
        foreach (var (key, labels) in BucketLabelsByPr(buckets))
        {
            if (WaitingOnAuthor(labels))
            {
                blocked.Add(key);
            }
        }
        var byPr = new Dictionary<string, AttentionItem>();
        foreach (var bucket in buckets.Where(bucket => !s_excludedFocus.Contains(bucket.Label) || bucket.Label == "Stalled"))
        {
            foreach (var item in bucket.Items)
            {
                if (bucket.Label == "Stalled" && !IsReviewDebt(item.PullRequest))
                {
                    continue;
                }
                var key = PrKey(item.PullRequest);
                if (!blocked.Contains(key) &&
                    (!byPr.TryGetValue(key, out var previous) || FocusRank(bucket.Label) < FocusRank(previous.BucketLabel)))
                {
                    byPr[key] = item with { BucketLabel = bucket.Label, BucketTone = bucket.Tone };
                }
            }
        }
        return byPr.Values.Where(item => WithinFocusAge(item.PullRequest, item.BucketLabel) || IsReviewDebt(item.PullRequest))
            .Where(item => !IsCommunityPullRequest(item.PullRequest) && !IsChecksFailing(item.PullRequest))
            .OrderByDescending(item => item.PullRequest.Date("updatedAt")).ToArray();
    }

    public IReadOnlyList<AttentionItem> ComputeCommunityItems(IReadOnlyList<JsonObject> prs) =>
        prs.Where(pr => pr.Text("state") == "open" && !pr.Flag("draft") && !HasNeedsAuthorActionLabel(pr) &&
            IsCommunityPullRequest(pr) && !AgedOutCommunity(pr))
            .OrderByDescending(pr => pr.Date("updatedAt")).Select(pr => new AttentionItem(pr, "Community", "Community", "accent")).ToArray();

    public IReadOnlyList<FocusExclusion> ComputeFocusExclusionItems(
        IReadOnlyList<JsonObject> prs, IReadOnlyList<AttentionBucket> buckets, IReadOnlyList<AttentionItem> focus, string? login)
    {
        if (string.IsNullOrEmpty(login))
        {
            return [];
        }
        var keys = focus.Select(item => PrKey(item.PullRequest)).ToHashSet();
        var labels = BucketLabelsByPr(buckets);
        return prs.Where(pr => pr.Text("state") == "open" && !pr.Flag("draft") && pr.Flag("isMine") && !keys.Contains(PrKey(pr)))
            .Select(pr => new FocusExclusion(pr, ExclusionReason(pr, labels.GetValueOrDefault(PrKey(pr)) ?? [])))
            .OrderBy(item => Array.IndexOf(s_exclusionRanks, item.Reason.Text("kind")))
            .ThenByDescending(item => item.PullRequest.Date("updatedAt")).ToArray();
    }

    private JsonObject ExclusionReason(JsonObject pr, IReadOnlyList<string> labels)
    {
        if (IsChecksFailing(pr))
        {
            return Reason("ci-failing", "CI failing", "Failing checks keep it out until CI is green again.", "danger");
        }

        if (HasMergeConflicts(pr))
        {
            return Reason("merge-conflicts", "Merge conflicts", "The author needs to rebase before reviewers can finish it.", "danger");
        }

        if (Unresolved(pr))
        {
            return Reason("unresolved-feedback", "Unresolved feedback", "Open review threads make it author-blocked.", "danger");
        }

        if (HasNeedsAuthorActionLabel(pr))
        {
            return Reason("held-by-label", "Held by label", "A do-not-merge label keeps it out of the focused queue.", "danger");
        }

        if (WaitingOnAuthor(labels))
        {
            return Reason("author-response", "Author response", "Changes were requested, so this is waiting on the author rather than the focused queue.", "danger");
        }

        if (IsCommunityPullRequest(pr) && !AgedOutCommunity(pr))
        {
            return Reason("community-list", "Community list", "Active external-contributor PRs show in the Community list.", "accent");
        }

        if (labels.FirstOrDefault(s_specializedFocus.Contains) is { } specialized)
        {
            return Reason("specialized-lane", $"{specialized} lane", $"Routed to the {specialized} lane instead of Needs attention.", "accent");
        }
        var candidate = labels.Where(label => !s_excludedFocus.Contains(label)).OrderBy(FocusRank).FirstOrDefault();
        if (candidate is not null && !WithinFocusAge(pr, candidate))
        {
            return Reason("stale-activity", "Stale activity", "Its actionable lane has not had fresh activity in 14 days.", "warning");
        }

        if (labels.Contains("Stalled"))
        {
            return Reason("stalled-only", "Stalled only", "It has gone quiet with no fresher actionable lane.", "warning");
        }

        return Reason("outside-queue", "Outside queue", "It does not currently match a focused, actionable lane.", "muted");
    }

    private static JsonObject Reason(string kind, string label, string detail, string tone) =>
        new() { ["kind"] = kind, ["label"] = label, ["detail"] = detail, ["tone"] = tone };

    private static bool WaitingOnAuthor(IReadOnlyList<string> labels) => labels.Contains("Author response") && !labels.Contains("Re-review needed");
    private static Dictionary<string, List<string>> BucketLabelsByPr(IReadOnlyList<AttentionBucket> buckets)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var bucket in buckets)
        {
            foreach (var item in bucket.Items)
            {
                var key = PrKey(item.PullRequest);
                if (!result.TryGetValue(key, out var labels))
                {
                    result[key] = labels = [];
                }
                labels.Add(bucket.Label);
            }
        }
        return result;
    }

    public IReadOnlyList<PersonalPick> CreateForMeItems(IReadOnlyList<JsonObject> prs, IReadOnlyList<string> logins)
    {
        if (logins.Count == 0)
        {
            return [];
        }
        return prs.Where(pr => pr.Text("state") == "open" && !pr.Flag("draft") &&
            !(CopilotAttributed(pr.Text("author")) && logins.Any(login => SameLogin(pr.Text("author"), login))))
            .Select(CreatePersonalPick).OfType<PersonalPick>().OrderByDescending(PickScore).Take(10).ToArray();
    }

    private PersonalPick? CreatePersonalPick(JsonObject pr)
    {
        var mine = pr.Flag("isMine");
        if (HasMergeConflicts(pr))
        {
            return mine ? Pick(DashboardConstants.ResolveConflicts, "Your PR has merge conflicts", "danger") : null;
        }

        if (HasNeedsAuthorActionLabel(pr))
        {
            return mine ? Pick(DashboardConstants.NeedsAttention, $"Your PR is labeled {DoNotMergeLabel(pr)}", "danger") : null;
        }

        if (mine && IsChecksFailing(pr))
        {
            var failures = pr["checks"].Number("failureCount");
            return Pick(DashboardConstants.FixCi, failures > 0 ? $"Your PR has {FormatCount(failures, "failing check")}" : "Your PR is failing CI", "danger");
        }
        if (!mine && Review(pr).Flag("reviewRequestedFromViewer"))
        {
            var ci = IsChecksFailing(pr) ? $"{Separator}CI failing" : VisibleCheckState(pr) == "pending" ? $"{Separator}CI running" : "";
            return Pick(DashboardConstants.ReviewThis, "Review requested from you", "warning") with { Reason = $"Review requested from you{Separator}{PickReason(pr)}{ci}" };
        }
        if (mine && Review(pr).Text("state") == "changes_requested")
        {
            return Pick(DashboardConstants.RespondHere, "Your PR has changes requested", "danger");
        }

        if (mine && Review(pr).Text("state") == "approved")
        {
            return Pick(DashboardConstants.FinishThis, "Your PR is approved and still open", "success");
        }

        return null;

        PersonalPick Pick(string action, string reason, string tone) => new(pr, action, $"{reason}{Separator}{PickReason(pr)}", tone);
    }

    private double PickScore(PersonalPick item)
    {
        var score = 1000 + (item.Action switch
        {
            DashboardConstants.ResolveConflicts => 200,
            DashboardConstants.NeedsAttention => 190,
            DashboardConstants.FixCi => 110,
            DashboardConstants.ReviewThis => 90,
            DashboardConstants.FinishThis => 80,
            DashboardConstants.RespondHere => 75,
            _ => 0
        });
        score += Review(item.PullRequest).Text("state") switch
        {
            "changes_requested" => 30,
            "waiting" => 45,
            "reviewed" => 25,
            "approved" => 5,
            _ => 0
        };
        if (IsBotAuthor(item.PullRequest))
        {
            score -= 120;
        }

        return score + Math.Min(3, Math.Floor(Age(ReviewAgeStartedAt(item.PullRequest)).TotalDays)) +
            Math.Min(1, Math.Floor(Age(item.PullRequest.Text("updatedAt")).TotalDays));
    }

    private string PickReason(JsonObject pr)
    {
        var signals = new List<string> { $"open {FormatAge(ReviewAgeStartedAt(pr))}" };
        var review = Review(pr);
        signals.Add(review.Number("approvalCount") > 0 ? FormatCount(review.Number("approvalCount"), "approval") :
            review.Number("reviewerCount") > 0 ? $"{FormatCount(review.Number("reviewerCount"), "reviewer")}{Separator}0 approvals" : "no reviews");
        if (Idle(pr))
        {
            signals.Add($"idle {FormatAge(pr.Text("updatedAt"))}");
        }

        return string.Join(Separator, signals);
    }

    public static IReadOnlyList<JsonObject> CreateDeveloperPullRequestCounts(IReadOnlyList<JsonObject> prs)
    {
        var byDeveloper = new Dictionary<string, (string Actor, List<JsonObject> Prs)>();
        foreach (var pr in prs.Where(pr => pr.Text("state") == "open" && !Toolkit(pr) && !ShouldHideFromSharedPullRequestLists(pr)))
        {
            var owner = CoreTeamOwnershipActor(pr.Text("author"));
            if (owner is null)
            {
                continue;
            }
            var key = ActorIdentityKey(owner);
            if (!byDeveloper.TryGetValue(key, out var developer))
            {
                byDeveloper[key] = developer = (owner, []);
            }
            developer.Prs.Add(pr);
        }
        return byDeveloper.Values.Select(developer => new JsonObject
        {
            ["actor"] = developer.Actor,
            ["openPullRequestCount"] = developer.Prs.Count,
            ["latestUpdatedAt"] = developer.Prs.OrderByDescending(pr => pr.Date("updatedAt")).First().Text("updatedAt")
        }).OrderByDescending(item => item.Number("openPullRequestCount"))
            .ThenBy(item => item.Text("actor"), StringComparer.InvariantCulture).ToArray();
    }

    private static bool CtiIssue(JsonObject issue) => issue.Text("title").Contains("[aspiree2e]", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<(string Label, string Tone)> IssueDefinitions(JsonObject issue)
    {
        if (RegressionLabels(issue))
        {
            yield return (Regression, "danger");
        }

        if (CtiIssue(issue))
        {
            yield return ("CTI team", "warning");
        }

        if (SameLogin(issue.Text("author"), "afscrome"))
        {
            yield return ("afscrome finds", "success");
        }
    }

    public static IReadOnlyList<IssueBucket> CreateFocusIssueBuckets(IReadOnlyList<JsonObject> issues, string? login)
    {
        var definitions = new List<(string Label, string Tone, Func<JsonObject, bool> Match)>
        {
            (Regression, "danger", RegressionLabels),
            ("CTI team", "warning", CtiIssue),
            ("afscrome finds", "success", issue => SameLogin(issue.Text("author"), "afscrome"))
        };
        if (!string.IsNullOrEmpty(login))
        {
            definitions.Add(("My issues", "accent", issue => issue["assignees"].Strings().Any(assignee => SameLogin(assignee, login))));
        }
        return definitions.Select(definition => new IssueBucket(definition.Label, definition.Tone,
            issues.Where(definition.Match).OrderByDescending(issue => issue.Date("updatedAt")).ToArray()))
            .Where(bucket => bucket.Issues.Count > 0).ToArray();
    }

    public IReadOnlyList<JsonObject> CreateIssueSignals(JsonObject issue)
    {
        var definitions = IssueDefinitions(issue).ToArray();
        var action = issue["labels"].Strings().Any(label => label.Contains("blocking-release", StringComparison.OrdinalIgnoreCase))
            ? Signal("Blocking release", "danger") : definitions.Length > 0 ? Signal(definitions[0].Label, definitions[0].Tone) :
            !issue["assignees"].Strings().Any() ? Signal("Unowned", "warning") : Signal("Needs validation", "warning");
        var signals = new List<JsonObject> { action };
        if (TargetsReleaseIssue(issue))
        {
            signals.Add(Signal($"release {DashboardConstants.CurrentRelease}", "danger"));
        }
        // linkedPullRequests includes merged fixes, not an authoritative complete open-PR count.
        // Only use linkedOpenPullRequests when explicitly supplied, matching the original model.
        var linkedCount = issue["linkedOpenPullRequests"] is JsonArray linked ? (int?)linked.Count : null;
        if (linkedCount > 0)
        {
            signals.Add(Signal(FormatCount(linkedCount.Value, "open PR"), "warning"));
        }

        foreach (var definition in definitions)
        {
            signals.Add(Signal(definition.Label, definition.Tone));
        }

        if (linkedCount == 0 && !CtiIssue(issue))
        {
            signals.Add(Signal("Needs PR", "danger"));
        }

        var searchText = $" {string.Join(" ", new[] { issue.Text("title"), issue.Text("author") }.Concat(issue["labels"].Strings())).ToLowerInvariant()} ";
        if (CtiIssue(issue) || linkedCount > 0 ||
            new[] { "validation", "validate", "verify", "verification", "test", "e2e", "servicing validation" }.Any(term => searchText.Contains(term, StringComparison.Ordinal)))
        {
            signals.Add(Signal("Needs validation", "warning"));
        }
        var domainMatch = CtiIssue(issue);
        foreach (var (label, tone, terms) in s_issueDomains)
        {
            if (terms.Any(term => searchText.Contains(term, StringComparison.Ordinal)))
            {
                domainMatch = true;
                signals.Add(Signal(label, tone));
            }
        }
        if (!issue["assignees"].Strings().Any() && !domainMatch)
        {
            signals.Add(Signal("Unowned", "warning"));
        }

        var age = Age(issue.Text("updatedAt"));
        if (age.TotalDays >= 7)
        {
            signals.Add(Signal($"idle {FormatAge(issue.Text("updatedAt"))}", age.TotalDays >= 14 ? "danger" : "warning"));
        }

        if (issue.Text("milestone").Length > 0)
        {
            signals.Add(Signal("milestone", "accent"));
        }

        foreach (var label in issue["labels"].Strings().Take(2))
        {
            signals.Add(Signal(label, "accent"));
        }

        if (IsBotAuthor(issue))
        {
            signals.Add(Signal("bot", "accent"));
        }

        return signals.DistinctBy(signal => signal.Text("label").Trim().ToLowerInvariant()).Take(7).ToArray();
    }

    public static JsonObject Signal(string label, string tone) => new() { ["label"] = label, ["tone"] = tone };
    public static string FormatCount(int count, string singular, string? plural = null) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

    public string FormatAge(string value)
    {
        var seconds = Math.Max(0, Math.Floor(Age(value).TotalSeconds));
        if (seconds < 60)
        {
            return $"{seconds.ToString(CultureInfo.InvariantCulture)}s";
        }

        var minutes = Math.Floor(seconds / 60);
        if (minutes < 60)
        {
            return $"{minutes.ToString(CultureInfo.InvariantCulture)}m";
        }

        var hours = Math.Floor(minutes / 60);
        if (hours < 24)
        {
            return $"{hours.ToString(CultureInfo.InvariantCulture)}h";
        }

        var days = Math.Floor(hours / 24);
        if (days < 30)
        {
            return $"{days.ToString(CultureInfo.InvariantCulture)}d";
        }

        var months = Math.Floor(days / 30);
        return months < 12 ? $"{months.ToString(CultureInfo.InvariantCulture)}mo" : $"{Math.Floor(months / 12).ToString(CultureInfo.InvariantCulture)}y";
    }

    public double AgeDays(string value) => Age(value).TotalDays;

    private TimeSpan Age(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
        ? _now - date : throw new InvalidDataException("GitHub returned an invalid activity timestamp.");

    private static string? LatestDate(params string[] values) => values
        .Where(value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        .OrderByDescending(value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)).FirstOrDefault();

    internal sealed record AttentionItem(JsonObject PullRequest, string Reason, string BucketLabel, string BucketTone);
    internal sealed record AttentionBucket(string Label, string Tone, List<AttentionItem> Items);
    internal sealed record FocusExclusion(JsonObject PullRequest, JsonObject Reason);
    internal sealed record PersonalPick(JsonObject PullRequest, string Action, string Reason, string Tone);
    internal sealed record IssueBucket(string Label, string Tone, IReadOnlyList<JsonObject> Issues);
}
