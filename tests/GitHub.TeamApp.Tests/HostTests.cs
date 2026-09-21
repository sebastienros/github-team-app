// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Text;
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

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task AppearancePersistsWithoutChangingOtherSettingsOrCacheKeys(string appearance)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new PreferenceStore(directory.FullName);
            var original = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("system", PreferenceStore.Appearance(original));
            Assert.Equal("system", PreferenceStore.Appearance(new JsonObject()));
            var key = DashboardCache.Key(original);
            await store.UpdateAsync(p => p["appearance"] = appearance, TestContext.Current.CancellationToken);
            var reopened = await new PreferenceStore(directory.FullName).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(appearance, PreferenceStore.Appearance(reopened));
            Assert.Equal(key, DashboardCache.Key(reopened));
            reopened.Remove("appearance");
            original.Remove("appearance");
            Assert.True(JsonNode.DeepEquals(original, reopened));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("\"auto\"")]
    [InlineData("\"DARK\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("12")]
    [InlineData("{}")]
    public async Task InvalidPersistedAppearanceIsReportedWithoutOverwriting(string invalid)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "preferences.json");
            var text = $"{{\"appearance\":{invalid}}}";
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
            var store = new PreferenceStore(directory.FullName);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(p => p["appearance"] = "light", TestContext.Current.CancellationToken));
            Assert.Equal(text, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { directory.Delete(recursive: true); }
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

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(10000)]
    public async Task OpenItemLimitPreferenceAcceptsRangeAndPersists(int limit)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new PreferenceStore(directory.FullName);
            Assert.Equal(200, (await store.ReadAsync(TestContext.Current.CancellationToken)).Number("maxOpenItems"));
            await store.UpdateAsync(prefs => Program.UpdatePreferences(prefs, "/api/prefs",
                new JsonObject { ["maxOpenItems"] = limit }), TestContext.Current.CancellationToken);
            var reopened = await new PreferenceStore(directory.FullName).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(limit, PreferenceStore.MaxOpenItems(reopened));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10001")]
    [InlineData("200.5")]
    [InlineData("200.0")]
    [InlineData("\"200\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("2147483648")]
    public async Task InvalidOpenItemLimitsAreRejectedForRequestsAndPersistedSettings(string invalid)
    {
        var prefs = PreferenceStore.Defaults();
        var body = JsonNode.Parse($"{{\"maxOpenItems\":{invalid},\"release\":\"must not change\"}}")!.AsObject();
        var error = Assert.Throws<ArgumentException>(() => Program.UpdatePreferences(prefs, "/api/prefs", body));
        Assert.Contains("maxOpenItems must be an integer between 1 and 10000", error.Message);
        Assert.Equal(200, prefs.Number("maxOpenItems"));
        Assert.Equal("", prefs.Text("release"));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "preferences.json");
            var text = body.ToJsonString();
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
            var store = new PreferenceStore(directory.FullName);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(p => p["release"] = "changed", TestContext.Current.CancellationToken));
            Assert.Equal(text, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Throws<InvalidDataException>(() => DashboardCache.Key(body));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task LegacyPreferencesDefaultToTwoHundredAndPresentationKeysIncludeEffectiveCap()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "preferences.json"), """{"mode":"issues"}""",
                TestContext.Current.CancellationToken);
            var prefs = await new PreferenceStore(directory.FullName).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(200, prefs.Number("maxOpenItems"));
            var missing = new JsonObject { ["mode"] = "issues" };
            var explicitDefault = new JsonObject { ["mode"] = "issues", ["maxOpenItems"] = 200 };
            Assert.Equal(DashboardCache.Key(missing), DashboardCache.Key(explicitDefault));
            var oldKey = JsonNode.Parse(DashboardCache.Key(missing))!.AsObject();
            oldKey.Remove("maxOpenItems");
            Assert.NotEqual(oldKey.ToJsonString(), DashboardCache.Key(missing));
            explicitDefault["maxOpenItems"] = 201;
            Assert.NotEqual(DashboardCache.Key(missing), DashboardCache.Key(explicitDefault));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task PreferencesHttpEndpointRejectsInvalidLimitsWithoutChangingSavedValue()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in new[] { typeof(DashboardService).Assembly.Location, "--no-browser", "--port", "0", "--data-dir", directory.FullName })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test host.");
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
            using var validBody = new StringContent("""{"maxOpenItems":321}""", Encoding.UTF8, "application/json");
            using var valid = await http.PostAsync("/api/prefs", validBody, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.Equal(321, JsonNode.Parse(await valid.Content.ReadAsStringAsync(timeout.Token))!["prefs"].Number("maxOpenItems"));
            foreach (var invalid in new[] { "0", "10001", "2.5", "\"200\"", "null", "true" })
            {
                using var body = new StringContent($"{{\"maxOpenItems\":{invalid}}}", Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("/api/prefs", body, timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains("maxOpenItems", await response.Content.ReadAsStringAsync(timeout.Token));
                Assert.Equal(321, (await new PreferenceStore(directory.FullName).ReadAsync(timeout.Token)).Number("maxOpenItems"));
            }
            foreach (var appearance in new[] { "system", "dark", "light" })
            {
                using var body = new StringContent($"{{\"appearance\":\"{appearance}\"}}", Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("/api/appearance", body, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(appearance, JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)).Text("appearance"));
                using var bootstrap = await http.GetAsync("/theme.js", timeout.Token);
                Assert.Equal("text/javascript", bootstrap.Content.Headers.ContentType?.MediaType);
                Assert.Contains("no-store", bootstrap.Headers.CacheControl!.ToString());
                Assert.StartsWith($"window.githubTeamAppearance = \"{appearance}\";\n", await bootstrap.Content.ReadAsStringAsync(timeout.Token));
                Assert.Equal(321, (await new PreferenceStore(directory.FullName).ReadAsync(timeout.Token)).Number("maxOpenItems"));
            }
            foreach (var invalid in new[] { "{}", """{"appearance":null}""", """{"appearance":"auto"}""", """{"appearance":true}""" })
            {
                using var body = new StringContent(invalid, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("/api/appearance", body, timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("light", PreferenceStore.Appearance(await new PreferenceStore(directory.FullName).ReadAsync(timeout.Token)));
            }
            foreach (var asset in new[] { "/octo.svg", "/octo-dock.svg" })
            {
                using var response = await http.GetAsync(asset, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains("GitHub Team App - Octo", await response.Content.ReadAsStringAsync(timeout.Token));
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await errors;
            directory.Delete(recursive: true);
        }
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
