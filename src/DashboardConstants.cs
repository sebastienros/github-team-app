// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TeamApp;

internal static class DashboardConstants
{
    public const string CurrentRelease = "13.5";
    public const int ReviewLimit = 10;
    public const string ReviewDebtSignalLabel = "review debt";
    public const string ResolveConflicts = "Resolve conflicts";
    public const string NeedsAttention = "Needs your attention";
    public const string FixCi = "Fix CI";
    public const string ReviewThis = "Review this";
    public const string RespondHere = "Respond here";
    public const string FinishThis = "Finish this";

    public static readonly string[] DefaultRepos =
    [
        "microsoft/aspire", "microsoft/aspire.dev", "microsoft/aspire-skills",
        "microsoft/dcp", "CommunityToolkit/Aspire"
    ];

    public static readonly string[] DefaultEmuRepos = ["devdiv-microsoft/aspire-1p"];

    public static readonly string[] CoreTeamMembers =
    [
        "davidfowl", "mitchdenny", "sebastienros", "IEvangelist", "danegsta",
        "radical", "JamesNK", "adamint", "joperezr", "maddymontaquila",
        "DamianEdwards", "eerhardt", "ellahathaway", "karolz-ms"
    ];

    public static readonly string[] CoreTeamMemberAliasSuffixes = ["_microsoft"];

    public static readonly CheckFailureRule[] NonBlockingCheckFailureRules =
    [
        new("devdiv-microsoft/aspire-1p", "proof of presence",
            ["GitOps/GitHubPop"], ["proof of presence"])
    ];

    internal sealed record CheckFailureRule(
        string Repository, string Label, string[] CheckNames, string[] CheckNameContains);
}
