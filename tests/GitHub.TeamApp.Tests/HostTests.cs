// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GitHub.TeamApp.Tests;

public class HostTests
{
    [Theory]
    [InlineData("127.0.0.1:5143", "", "", true)]
    [InlineData("127.0.0.1:5143", "http://127.0.0.1:5143", "same-origin", true)]
    [InlineData("localhost:5143", "http://localhost:5143", "same-origin", true)]
    [InlineData("attacker.test:5143", "http://attacker.test:5143", "same-origin", false)]
    [InlineData("127.0.0.1:5143", "https://attacker.test", "cross-site", false)]
    [InlineData("127.0.0.1:5144", "", "", false)]
    [InlineData("localhost", "", "", false)]
    [InlineData("127.0.0.1:5143", "null", "", false)]
    public void PinsRequestsToLoopbackOrigin(string host, string origin, string fetchSite, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Host = new HostString(host);
        request.Headers.Origin = origin;
        request.Headers["Sec-Fetch-Site"] = fetchSite;
        Assert.Equal(expected, Program.IsAllowedRequest(request, 5143));
    }

    [Fact]
    public async Task PreferenceChangesSurviveReopeningAndConcurrentUpdates()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new PreferenceStore(directory.FullName);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
                store.UpdateAsync(p => p[$"setting{i}"] = i, TestContext.Current.CancellationToken)));
            var reopened = await new PreferenceStore(directory.FullName).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("review", reopened.Text("mode"));
            Assert.Equal(Enumerable.Range(0, 12), Enumerable.Range(0, 12).Select(i => reopened.Number($"setting{i}")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MalformedPreferencesAreReportedWithoutOverwritingThem()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "preferences.json");
            await File.WriteAllTextAsync(path, "{invalid", TestContext.Current.CancellationToken);
            var store = new PreferenceStore(directory.FullName);
            await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
                store.UpdateAsync(p => p["mode"] = "health", TestContext.Current.CancellationToken));
            Assert.Equal("{invalid", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void UpdatesOnlyKnownNotificationSettings()
    {
        var prefs = PreferenceStore.Defaults();
        Program.UpdatePreferences(prefs, "/api/prefs", JsonNode.Parse("""
            {"release":"14.0","showDrafts":true,"notifications":{"ciFailing":false,"unknown":true}}
            """)!.AsObject());
        Assert.Equal("14.0", prefs.Text("release"));
        Assert.True(prefs.Flag("showDrafts"));
        Assert.Equal(new[] { "reviewRequested", "readyToMerge", "changesRequested", "ciFailing" },
            prefs["notifications"]!.AsObject().Select(p => p.Key));
        Assert.False(prefs["notifications"].Flag("ciFailing"));
    }

    [Fact]
    public void HealthOrderingKeepsRepositoryGroupsTogether()
    {
        var dashboard = JsonNode.Parse("""
            {"health":{"items":[{"id":"a","groupId":"repo1"},{"id":"b","groupId":"repo2"},{"id":"c","groupId":"repo1"}]}}
            """)!.AsObject();
        DashboardService.ApplyHealthOrder(dashboard, ["c", "b", "a"]);
        Assert.Equal(new[] { "c", "a", "b" }, dashboard["health"]!["items"].Objects().Select(i => i.Text("id")));
    }
}
