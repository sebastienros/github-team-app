// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GitHub.TeamApp.Tests;

public class RepositoryCatalogTests
{
    [Fact]
    public void FreshPreferencesDoNotSelectAnyRepository()
    {
        var prefs = PreferenceStore.Defaults();
        Assert.Empty(prefs["repositories"].Objects());
        Assert.Null(RepositoryCatalog.Selected(prefs));
        Assert.Empty(prefs.Text("release"));
        Assert.Empty(prefs["teamMembers"].Strings());
    }

    [Fact]
    public async Task MigratesLegacyRepositoriesWithoutLosingSettingsAndPersistsSelection()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(directory.FullName, "preferences.json");
            await File.WriteAllTextAsync(file, """
                {"release":"v2","showDrafts":true,"accounts":{
                  "acct:github.com/inactive":{"active":false,"repos":["owner/other"]},
                  "acct:alice":{"active":true,"repos":["Owner/First","owner/second"]}},
                  "azurePipelines":[{"id":"pipeline"}],"sessionLauncher":{"projects":[]}}
                """, TestContext.Current.CancellationToken);
            var store = new PreferenceStore(directory.FullName);
            var migrated = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("github.com/owner/first", migrated.Text("selectedRepository"));
            Assert.Equal(3, migrated["repositories"].Objects().Count());
            Assert.Equal("acct:github.com/alice", RepositoryCatalog.Selected(migrated).Text("accountId"));
            Assert.Equal("github.com/owner/first", migrated["azurePipelines"]![0].Text("repositoryId"));
            Assert.Equal("v2", migrated.Text("release"));
            Assert.True(migrated.Flag("showDrafts"));
            await store.UpdateAsync(p => RepositoryCatalog.Select(p, "github.com/owner/second"), TestContext.Current.CancellationToken);
            var reopened = await new PreferenceStore(directory.FullName).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("github.com/owner/second", reopened.Text("selectedRepository"));
            Assert.NotNull(reopened["sessionLauncher"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AddSelectRemoveAndDuplicateValidationKeepManySavedButOnlyOneSelected()
    {
        var prefs = PreferenceStore.Defaults();
        RepositoryCatalog.Add(prefs, "acct:github.com/alice", "Owner/First");
        RepositoryCatalog.Add(prefs, "acct:enterprise.test/bob", "owner/second");
        Assert.Equal("github.com/owner/first", prefs.Text("selectedRepository"));
        Assert.Throws<ArgumentException>(() => RepositoryCatalog.Add(prefs, "acct:github.com/other", "OWNER/FIRST"));
        RepositoryCatalog.Select(prefs, "enterprise.test/owner/second");
        Assert.Equal("enterprise.test", RepositoryCatalog.Selected(prefs).Text("host"));
        RepositoryCatalog.Remove(prefs, "enterprise.test/owner/second");
        Assert.Equal("github.com/owner/first", prefs.Text("selectedRepository"));
        RepositoryCatalog.Remove(prefs, "github.com/owner/first");
        Assert.Null(RepositoryCatalog.Selected(prefs));
        Assert.Throws<ArgumentException>(() => RepositoryCatalog.Select(prefs, "github.com/owner/first"));
    }

    [Theory]
    [InlineData("acct:evil.test/path/user", "owner/repo")]
    [InlineData("acct:github.com/alice", "https://evil.test/owner/repo")]
    [InlineData("acct:github.com/alice", "owner/repo/../other")]
    [InlineData("acct:github.com/alice", "owner/repo?token=secret")]
    [InlineData("acct:github.com/alice", "git@github.com:owner/repo")]
    [InlineData("acct:******github.com/alice", "owner/repo")]
    public void RejectsAmbiguousRepositoryCoordinates(string account, string repository)
    {
        Assert.ThrowsAny<ArgumentException>(() => RepositoryCatalog.Create(account, repository));
    }

    [Fact]
    public void ScopeUsesExactAccountHostRepositoryAndOnlyAssignedPipelines()
    {
        var prefs = PreferenceStore.Defaults();
        RepositoryCatalog.Add(prefs, "acct:github.com/alice", "owner/first");
        RepositoryCatalog.Add(prefs, "acct:enterprise.test/alice", "owner/second");
        prefs["azurePipelines"] = JsonNode.Parse("""
            [{"id":"one","repositoryId":"github.com/owner/first"},
             {"id":"two","repositoryId":"enterprise.test/owner/second"},
             {"id":"unassigned"}]
            """);
        Account[] accounts =
        [
            Account("acct:github.com/alice", "github.com"),
            Account("acct:github.com/bob", "github.com"),
            Account("acct:enterprise.test/alice", "enterprise.test")
        ];
        var scoped = Assert.Single(RepositoryCatalog.ScopeAccounts(accounts, prefs));
        Assert.Equal("acct:github.com/alice", scoped.Id);
        Assert.Equal(["owner/first"], scoped.Repos);
        Assert.Equal("one", Assert.Single(RepositoryCatalog.ScopePreferences(prefs)["azurePipelines"].Objects()).Text("id"));
        Assert.Equal(3, prefs["azurePipelines"].Objects().Count());
        RepositoryCatalog.Select(prefs, "enterprise.test/owner/second");
        scoped = Assert.Single(RepositoryCatalog.ScopeAccounts(accounts, prefs));
        Assert.Equal("enterprise.test", scoped.Host);
        Assert.Equal(["owner/second"], scoped.Repos);
        Assert.Equal("two", Assert.Single(RepositoryCatalog.ScopePreferences(prefs)["azurePipelines"].Objects()).Text("id"));
    }

    [Fact]
    public async Task SearchUsesAuthenticatedAccountHostAndEncodesQuery()
    {
        var handler = new SearchHandler();
        using var http = new HttpClient(handler);
        var service = new AccountService(http, NullLogger<AccountService>.Instance,
            (_, _) => Task.FromResult(new ProcessResult(0, """{"hosts":{}}""", "")),
            () => new Dictionary<string, string?> { ["GH_TOKEN"] = "test-token", ["GH_HOST"] = "enterprise.test" });
        var result = await service.SearchRepositoriesAsync(PreferenceStore.Defaults(),
            "acct:enterprise.test/alice", "org:owner language:C# & x", TestContext.Current.CancellationToken);
        var item = Assert.Single(result["items"].Objects());
        Assert.Equal("owner/repo", item.Text("repository"));
        Assert.Equal("https://enterprise.test/owner/repo", item.Text("url"));
        Assert.Equal("enterprise.test", handler.SearchUri!.Host);
        Assert.Equal("/api/v3/search/repositories", handler.SearchUri.AbsolutePath);
        Assert.Contains("q=org%3Aowner%20language%3AC%23%20%26%20x&per_page=20", handler.SearchUri.Query);
        Assert.DoesNotContain("test-token", result.ToJsonString());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchRepositoriesAsync(PreferenceStore.Defaults(),
            "acct:unconfigured.test/alice", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReleaseCanBeClearedAndTeamMembersConfiguredWithoutDefaults()
    {
        var prefs = PreferenceStore.Defaults();
        prefs["release"] = "v1";
        Program.UpdatePreferences(prefs, "/api/prefs", new JsonObject { ["release"] = "", ["teamMembers"] = "@Alice, BOB alice" });
        Assert.Empty(prefs.Text("release"));
        Assert.Equal(["alice", "bob"], prefs["teamMembers"].Strings());
    }

    [Fact]
    public void SessionSuggestionsUseSavedPublicHostRepositoriesNotInstalledProjectDiscovery()
    {
        var prefs = PreferenceStore.Defaults();
        RepositoryCatalog.Add(prefs, "acct:github.com/alice", "owner/first");
        RepositoryCatalog.Add(prefs, "acct:enterprise.test/alice", "owner/second");
        var configuration = SessionLauncher.GetConfiguration(prefs);
        var suggestion = Assert.Single(configuration["suggestedProjects"].Objects());
        Assert.Equal("https://github.com/owner/first", suggestion.Text("repositoryUrl"));
        Assert.False(configuration["capabilities"].Flag("projectDiscovery"));
        Assert.False(configuration["capabilities"].Flag("githubEnterpriseServer"));
    }

    [Fact]
    public void LegacyRepositoryEditingKeepsCatalogInSyncWithoutEnablingInactiveAccount()
    {
        var prefs = PreferenceStore.Defaults();
        RepositoryCatalog.Add(prefs, "acct:github.com/alice", "owner/first");
        Program.UpdatePreferences(prefs, "/api/account/toggle",
            new JsonObject { ["id"] = "acct:github.com/alice", ["active"] = false });
        Program.UpdatePreferences(prefs, "/api/account/repos",
            new JsonObject { ["id"] = "acct:github.com/alice", ["repos"] = "owner/second owner/third" });
        Assert.Equal(["owner/second", "owner/third"], prefs["repositories"].Objects().Select(item => item.Text("repository")));
        Assert.Equal("github.com/owner/second", prefs.Text("selectedRepository"));
        Assert.False(prefs["accounts"]!["acct:github.com/alice"].Flag("active"));
    }

    private static Account Account(string id, string host) =>
        new(id, "alice", host, "test-token", ["owner/first", "owner/second"], true,
            new JsonObject { ["status"] = "ok" });

    private sealed class SearchHandler : HttpMessageHandler
    {
        public Uri? SearchUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            var json = "{}";
            if (request.Method == HttpMethod.Post)
            {
                json = """{"data":{"viewer":{"login":"alice"}}}""";
            }
            else if (request.RequestUri!.AbsolutePath.EndsWith("/search/repositories", StringComparison.Ordinal))
            {
                SearchUri = request.RequestUri;
                json = """{"items":[{"full_name":"Owner/Repo","description":"Example","private":true,"html_url":"https://evil.test/"}]}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
