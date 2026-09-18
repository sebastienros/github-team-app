// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace GitHub.TeamApp;

internal static class DashboardConstants
{
    public const string CurrentRelease = "";
    public const int ReviewLimit = 10;
    public const string ReviewDebtSignalLabel = "review debt";
    public const string ResolveConflicts = "Resolve conflicts";
    public const string NeedsAttention = "Needs your attention";
    public const string FixCi = "Fix CI";
    public const string ReviewThis = "Review this";
    public const string RespondHere = "Respond here";
    public const string FinishThis = "Finish this";

    public static readonly string[] DefaultRepos = [];
    public static readonly string[] DefaultEmuRepos = [];
    public static readonly string[] CoreTeamMemberAliasSuffixes = [];
}
