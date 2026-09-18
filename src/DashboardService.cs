// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Aspire.TeamApp;

internal sealed class DashboardService(
    PreferenceStore preferences,
    AccountService accountService,
    GitHubDashboard github,
    HealthDashboard health,
    ILogger<DashboardService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _compute = new(1);
    private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, DisplayedSnapshot> _displayed = new();
    private volatile JsonObject? _cache;
    private long _sequence;
    private DateTimeOffset _nextPoll = DateTimeOffset.UtcNow.AddSeconds(90);
    private IReadOnlyList<Account>? _accounts;
    private string? _accountsKey;
    private DateTimeOffset _accountsAt;

    public async Task<JsonObject> GetAsync(Guid client, bool force, CancellationToken cancellationToken)
    {
        var snapshot = !force && _cache is { } cached
            ? cached
            : await RefreshAsync(background: false, cancellationToken);
        Remember(client, snapshot);
        return (JsonObject)snapshot.DeepClone();
    }

    public void InvalidateAccounts() => _accountsKey = null;

    public JsonObject? FindPullRequest(Guid client, JsonObject requested)
    {
        var url = requested.Text("url");
        return EnumerateObjects(Displayed(client)?["dashboard"])
            .FirstOrDefault(item => item.Text("url") == url &&
                item.Text("repository") == requested.Text("repository") &&
                item.Number("number") == requested.Number("number")) is { } pr
            ? (JsonObject)pr.DeepClone() : null;
    }

    public JsonObject? FindHealthSource(Guid client, string id) =>
        Displayed(client)?["dashboard"]?["health"]?["items"].Objects()
            .FirstOrDefault(source => source.Text("id") == id)?.DeepClone() as JsonObject;

    public bool IsLinkedPullRequest(Guid client, string url) =>
        EnumerateObjects(Displayed(client)?["dashboard"])
            .Any(item => item.Text("url") == url && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Scheme == "https" && uri.AbsolutePath.Contains("/pull/", StringComparison.Ordinal));

    private JsonObject? Displayed(Guid client) => _displayed.GetValueOrDefault(client)?.Snapshot;

    private void Remember(Guid client, JsonObject snapshot)
    {
        foreach (var (id, previous) in _displayed)
        {
            if (DateTimeOffset.UtcNow - previous.At > TimeSpan.FromHours(1) &&
                !_subscribers.Values.Any(subscriber => subscriber.Client == id))
            {
                _displayed.TryRemove(id, out _);
            }
        }
        if (_displayed.Count >= 100 && !_displayed.ContainsKey(client))
        {
            throw new InvalidOperationException("Too many dashboard tabs. Close unused tabs and restart the app.");
        }
        _displayed[client] = new(snapshot, DateTimeOffset.UtcNow);
    }

    public async Task PreferencesChangedAsync(CancellationToken cancellationToken)
    {
        await _compute.WaitAsync(cancellationToken);
        try
        {
            var prefs = await preferences.ReadAsync(cancellationToken);
            Publish("preferences", prefs);
            if (_cache is { } cached)
            {
                var next = (JsonObject)cached.DeepClone();
                next["prefs"] = prefs;
                _cache = next;
            }
        }
        finally
        {
            _compute.Release();
        }
    }

    public async Task StreamAsync(HttpContext context, Guid client)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        var id = Guid.NewGuid();
        _subscribers[id] = new(client, channel);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
            await context.Response.WriteAsync(Frame("poll-schedule", PollSchedule()), context.RequestAborted);
            if (_cache is { } current)
            {
                await context.Response.WriteAsync(Frame("snapshot", new JsonObject
                {
                    ["seq"] = current["dashboard"]?["seq"]?.DeepClone(),
                    ["fetchedAt"] = current["dashboard"]?["fetchedAt"]?.DeepClone(),
                    ["prefs"] = current["prefs"]?.DeepClone(),
                    ["nextPollAt"] = _nextPoll.ToUnixTimeMilliseconds()
                }), context.RequestAborted);
            }
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await foreach (var message in channel.Reader.ReadAllAsync(context.RequestAborted))
            {
                await context.Response.WriteAsync(message, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // EventSource disconnected.
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(90));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            _nextPoll = DateTimeOffset.UtcNow.AddSeconds(90);
            Publish("poll-schedule", PollSchedule());
            if (_subscribers.IsEmpty)
            {
                continue;
            }
            try
            {
                await RefreshAsync(background: true, stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Background dashboard refresh failed; retaining the last complete snapshot");
                Publish("refresh-error", new JsonObject { ["error"] = "Background refresh failed. Refresh to retry." });
            }
        }
    }

    private async Task<JsonObject> RefreshAsync(bool background, CancellationToken cancellationToken)
    {
        await _compute.WaitAsync(cancellationToken);
        try
        {
            var prefs = await preferences.ReadAsync(cancellationToken);
            var accountKey = prefs["accounts"]?.ToJsonString();
            if (_accounts is null || _accountsKey != accountKey || DateTimeOffset.UtcNow - _accountsAt > TimeSpan.FromMinutes(10))
            {
                _accounts = await accountService.ResolveAsync(prefs, cancellationToken);
                _accountsKey = accountKey;
                _accountsAt = DateTimeOffset.UtcNow;
            }
            var accounts = _accounts;
            if (prefs["accounts"] is JsonObject { Count: 0 } && accounts.Any(a => a.Active))
            {
                prefs = await preferences.UpdateAsync(saved =>
                {
                    if (saved["accounts"] is not JsonObject { Count: 0 })
                    {
                        return;
                    }
                    var configurations = new JsonObject();
                    foreach (var account in accounts.Where(a => a.Active))
                    {
                        configurations[account.Id] = new JsonObject
                        {
                            ["active"] = true,
                            ["repos"] = JsonData.Array(account.Repos)
                        };
                    }
                    saved["accounts"] = configurations;
                }, cancellationToken);
                _accountsKey = prefs["accounts"]?.ToJsonString();
            }
            var active = accounts.Where(a => a.Active && a.Metadata.Text("status") != "failed").ToArray();
            var dashboard = prefs.Text("mode") == "health"
                ? await health.LoadAsync(active, prefs, cancellationToken)
                : await github.LoadAsync(active, prefs, cancellationToken);
            dashboard["accounts"] = JsonData.Array(accounts.Select(a => a.Metadata));
            dashboard["activeAccounts"] = JsonData.Array(active.Select(a => a.Metadata));
            dashboard["dismissedCount"] = (prefs["dismissedNotifications"] as JsonArray)?.Count ?? 0;
            var latestPrefs = await preferences.ReadAsync(cancellationToken);
            // Configuration writes can finish during provider I/O. Never publish an old-mode result
            // over a newer one; the mutation's forced refresh queues behind this computation.
            if (_cache is { } prior && InputKey(prefs) != InputKey(latestPrefs))
            {
                return prior;
            }
            ApplyHealthOrder(dashboard, latestPrefs["healthOrder"].Strings().ToArray());
            var changed = !JsonNode.DeepEquals(Content(_cache?["dashboard"]), Content(dashboard));
            dashboard["seq"] = changed ? ++_sequence : _sequence;
            var next = new JsonObject { ["dashboard"] = dashboard, ["prefs"] = latestPrefs };
            _cache = next;
            if (!background || changed)
            {
                if (!background || latestPrefs.Flag("autoApplyUpdates", true))
                {
                    foreach (var subscriber in _subscribers.Values)
                    {
                        Remember(subscriber.Client, next);
                    }
                    Publish("state", next);
                }
                else
                {
                    Publish("update-available", new JsonObject
                    {
                        ["seq"] = _sequence,
                        ["fetchedAt"] = dashboard["fetchedAt"]?.DeepClone(),
                        ["counts"] = dashboard["counts"]?.DeepClone()
                    });
                }
            }
            return next;
        }
        finally
        {
            _compute.Release();
        }
    }

    public static void ApplyHealthOrder(JsonObject dashboard, string[] order)
    {
        if (dashboard["health"] is not JsonObject healthData || healthData["items"] is not JsonArray items)
        {
            return;
        }
        var rank = order.Select((id, index) => (id, index)).DistinctBy(item => item.id)
            .ToDictionary(item => item.id, item => item.index);
        var ordered = items.OfType<JsonObject>().OrderBy(item => rank.GetValueOrDefault(item.Text("id"), int.MaxValue))
            .GroupBy(item => item.Text("groupId", item.Text("id")))
            .SelectMany(group => group);
        healthData["items"] = JsonData.Array(ordered);
    }

    private static string InputKey(JsonObject prefs)
    {
        var key = (JsonObject)prefs.DeepClone();
        key.Remove("autoApplyUpdates");
        key.Remove("healthOrder");
        key.Remove("sessionLauncher");
        return key.ToJsonString();
    }

    private static JsonNode? Content(JsonNode? node)
    {
        if (node?.DeepClone() is not JsonObject content)
        {
            return null;
        }
        content.Remove("seq");
        content.Remove("fetchedAt");
        return content;
    }

    private static IEnumerable<JsonObject> EnumerateObjects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.SelectMany(property => EnumerateObjects(property.Value)))
            {
                yield return child;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.SelectMany(EnumerateObjects))
            {
                yield return child;
            }
        }
    }

    private JsonObject PollSchedule() => new() { ["nextPollAt"] = _nextPoll.ToUnixTimeMilliseconds() };
    private static string Frame(string name, JsonNode data) => $"event: {name}\ndata: {data.ToJsonString()}\n\n";
    private void Publish(string name, JsonNode data)
    {
        var frame = Frame(name, data);
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Messages.Writer.TryWrite(frame);
        }
    }

    private sealed record Subscription(Guid Client, Channel<string> Messages);
    private sealed record DisplayedSnapshot(JsonObject Snapshot, DateTimeOffset At);
}
