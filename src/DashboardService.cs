// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace GitHub.TeamApp;

internal sealed class DashboardService : BackgroundService
{
    private readonly PreferenceStore _preferences;
    private readonly DashboardCache _disk;
    private readonly Func<JsonObject, CancellationToken, Task<IReadOnlyList<Account>>> _resolve;
    private readonly Func<IReadOnlyList<Account>, JsonObject, CancellationToken, Task<JsonObject>> _load;
    private readonly ILogger<DashboardService> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, Subscription> _subscribers = [];
    private readonly Dictionary<Guid, DisplayedSnapshot> _displayed = [];
    private State? _current;
    private long _generation;
    private long _sequence;
    private DateTimeOffset _nextPoll = DateTimeOffset.UtcNow.AddSeconds(90);

    public DashboardService(PreferenceStore preferences, AccountService accountService,
        GitHubDashboard github, HealthDashboard health, ILogger<DashboardService> logger)
        : this(preferences, accountService.ResolveAsync,
            (accounts, prefs, ct) => prefs.Text("mode") == "health"
                ? health.LoadAsync(accounts, prefs, ct) : github.LoadAsync(accounts, prefs, ct), logger)
    {
    }

    internal DashboardService(PreferenceStore preferences,
        Func<JsonObject, CancellationToken, Task<IReadOnlyList<Account>>> resolve,
        Func<IReadOnlyList<Account>, JsonObject, CancellationToken, Task<JsonObject>> load,
        ILogger<DashboardService> logger)
    {
        _preferences = preferences;
        _disk = new(preferences.DirectoryPath);
        _resolve = resolve;
        _load = load;
        _logger = logger;
    }

    // Force schedules revalidation; even a cold read never waits for credentials or providers.
    public async Task<JsonObject> GetAsync(Guid client, bool force, CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = await SelectAsync(force, background: false, cancellationToken);
            lock (_sync)
            {
                if (IsCurrent(state))
                {
                    Remember(client, state.Snapshot, state);
                    return (JsonObject)state.Snapshot.DeepClone();
                }
            }
        }
    }

    // This never waits for authentication, provider work, or disk I/O.
    public void ConfigurationChanged()
    {
        lock (_sync)
        {
            _generation++;
            _current = null;
            _displayed.Clear();
            foreach (var subscriber in _subscribers.Values)
            {
                while (subscriber.Messages.Reader.TryRead(out _)) { }
            }
        }
    }

    public void InvalidateAccounts()
    {
        // Compatibility with callers: credentials are resolved afresh on every refresh/discovery.
    }

    public async Task<JsonArray> DiscoverAccountsAsync(CancellationToken ct)
    {
        long generation;
        lock (_sync) { generation = _generation; }
        var prefs = await _preferences.ReadAsync(ct);
        var accounts = await _resolve(prefs, ct);
        var metadata = JsonData.Array(accounts.Select(DashboardCache.Metadata));
        metadata = (JsonArray)DashboardCache.Presentation(new JsonObject { ["accounts"] = metadata }, accounts)["accounts"]!.DeepClone();
        await _gate.WaitAsync(ct);
        try
        {
            var latest = await _preferences.ReadAsync(ct);
            lock (_sync)
            {
                if (generation != _generation || DashboardCache.Key(prefs) != DashboardCache.Key(latest))
                {
                    throw new InvalidOperationException("Repository configuration changed during account discovery. Retry discovery.");
                }
            }
            await _disk.WriteAccountsAsync(metadata, ct);
            lock (_sync)
            {
                if (generation != _generation)
                {
                    throw new InvalidOperationException("Repository configuration changed during account discovery. Retry discovery.");
                }
            }
        }
        finally { _gate.Release(); }
        return metadata;
    }

    public JsonObject? FindPullRequest(Guid client, JsonObject requested)
    {
        lock (_sync)
        {
            return EnumerateObjects(Displayed(client)?["dashboard"])
                .FirstOrDefault(item => item.Text("url") == requested.Text("url") &&
                    item.Text("url").Length > 0 && item.Text("repository") == requested.Text("repository") &&
                    item.Number("number") == requested.Number("number"))?.DeepClone() as JsonObject;
        }
    }

    public JsonObject? FindHealthSource(Guid client, string id)
    {
        lock (_sync)
        {
            return Displayed(client)?["dashboard"]?["health"]?["items"].Objects()
                .FirstOrDefault(source => source.Text("id") == id)?.DeepClone() as JsonObject;
        }
    }

    public bool IsLinkedPullRequest(Guid client, string url)
    {
        lock (_sync)
        {
            return EnumerateObjects(Displayed(client)?["dashboard"])
                .Any(item => item.Text("url") == url && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    uri.Scheme == "https" && uri.AbsolutePath.Contains("/pull/", StringComparison.Ordinal));
        }
    }

    private JsonObject? Displayed(Guid client) =>
        _displayed.TryGetValue(client, out var displayed) && _current is { } current &&
        displayed.Generation == _generation && displayed.Key == current.Key ? displayed.Snapshot : null;

    private void Remember(Guid client, JsonObject snapshot, State state)
    {
        foreach (var id in _displayed.Where(pair => DateTimeOffset.UtcNow - pair.Value.At > TimeSpan.FromHours(1) &&
            !_subscribers.Values.Any(s => s.Client == pair.Key)).Select(pair => pair.Key).ToArray())
        {
            _displayed.Remove(id);
        }
        if (_displayed.Count >= 100 && !_displayed.ContainsKey(client))
        {
            throw new InvalidOperationException("Too many dashboard tabs. Close unused tabs and restart the app.");
        }
        _displayed[client] = new(snapshot, DateTimeOffset.UtcNow, state.Key, state.Generation);
    }

    public async Task PreferencesChangedAsync(CancellationToken cancellationToken)
    {
        var state = await SelectAsync(false, background: false, cancellationToken);
        lock (_sync)
        {
            if (IsCurrent(state))
            {
                Publish("preferences", state.Snapshot["prefs"]!, state);
            }
        }
    }

    private async Task<State> SelectAsync(bool force, bool background, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long generation;
                lock (_sync) { generation = _generation; }
                var prefs = await _preferences.ReadAsync(ct);
                var key = DashboardCache.Key(prefs);
                State? state;
                lock (_sync)
                {
                    if (generation != _generation) { continue; }
                    state = _current;
                    if (state is not null && state.Key != key)
                    {
                        ConfigurationChanged();
                        generation = _generation;
                        state = null;
                    }
                }
                if (state is null)
                {
                    var selected = RepositoryCatalog.Selected(prefs);
                    JsonObject? dashboard = null;
                    var error = "";
                    JsonArray metadata = new();
                    try
                    {
                        if (selected is not null)
                        {
                            dashboard = await _disk.ReadAsync(key, ct);
                        }
                        metadata = await _disk.ReadAccountsAsync(ct);
                    }
                    catch (Exception ex) when (IsCacheError(ex))
                    {
                        _logger.LogWarning("Persistent dashboard cache could not be read ({ErrorType}).", ex.GetType().Name);
                        error = "The saved dashboard cache is invalid or unreadable. Refresh to rebuild it.";
                    }
                    var complete = dashboard is not null;
                    dashboard ??= Shell(prefs);
                    dashboard["repositoryId"] = selected?.Text("id") ?? "";
                    dashboard["refreshing"] = false;
                    dashboard["refreshError"] = error;
                    dashboard["cacheStatus"] = complete ? "cached" : selected is null ? "empty" : "loading";
                    if (!complete)
                    {
                        dashboard["accounts"] = ScopedMetadata(metadata, selected);
                    }
                    ApplyHealthOrder(dashboard, prefs["healthOrder"].Strings().ToArray());
                    state = new(key, generation, new JsonObject { ["dashboard"] = dashboard, ["prefs"] = prefs.DeepClone() }, complete);
                }
                lock (_sync)
                {
                    if (generation != _generation) { continue; }
                    var isNew = !ReferenceEquals(_current, state);
                    var snapshot = (JsonObject)state.Snapshot.DeepClone();
                    snapshot["prefs"] = prefs.DeepClone();
                    if (isNew) { snapshot["dashboard"]!["seq"] = ++_sequence; }
                    ApplyHealthOrder((JsonObject)snapshot["dashboard"]!, prefs["healthOrder"].Strings().ToArray());
                    state.Snapshot = snapshot;
                    _current = state;
                    if ((isNew || force || state.Refresh is null) && RepositoryCatalog.Selected(prefs) is not null &&
                        (state.Refresh is null || state.Refresh.IsCompleted))
                    {
                        state.Snapshot["dashboard"]!["refreshing"] = true;
                        if (state.Complete) { state.Snapshot["dashboard"]!["cacheStatus"] = "cached"; }
                        // Scheduling, not calling the async provider, also isolates synchronous provider prologues.
                        state.Refresh = Task.Run(() => RefreshAsync(state, (JsonObject)prefs.DeepClone(), background, _lifetime.Token));
                    }
                    if (isNew) { Publish("state", state.Snapshot, state); }
                    return state;
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshAsync(State state, JsonObject prefs, bool background, CancellationToken ct)
    {
        try
        {
            await RefreshCoreAsync(state, prefs, background, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Dashboard refresh could not be completed ({ErrorType}).", ex.GetType().Name);
            lock (_sync)
            {
                if (!IsCurrent(state)) { return; }
                state.Snapshot = (JsonObject)state.Snapshot.DeepClone();
                state.Snapshot["dashboard"]!["refreshing"] = false;
                state.Snapshot["dashboard"]!["refreshError"] = "Dashboard refresh could not be completed. Check preferences and cache access, then retry.";
                Publish("refresh-error", new JsonObject
                {
                    ["repositoryId"] = state.Snapshot["dashboard"]!["repositoryId"]?.DeepClone(),
                    ["error"] = state.Snapshot["dashboard"]!["refreshError"]?.DeepClone()
                }, state);
            }
        }
    }

    private async Task RefreshCoreAsync(State state, JsonObject prefs, bool background, CancellationToken ct)
    {
        JsonObject? dashboard = null;
        JsonObject? partial = null;
        var error = "";
        IReadOnlyList<Account> accounts = [];
        try
        {
            var scoped = RepositoryCatalog.ScopePreferences(prefs);
            accounts = await _resolve(scoped, ct);
            var active = RepositoryCatalog.ScopeAccounts(accounts, prefs)
                .Where(a => a.Metadata.Text("status") != "failed").ToArray();
            if (active.Length == 0)
            {
                throw new InvalidDataException("No active account is available for the selected repository.");
            }
            dashboard = await _load(active, scoped, ct);
            var failed = HasProviderFailure(dashboard);
            dashboard["repositoryId"] = RepositoryCatalog.Selected(prefs)!.Text("id");
            var metadata = ScopedMetadata(JsonData.Array(accounts.Select(DashboardCache.Metadata)), RepositoryCatalog.Selected(prefs));
            dashboard["accounts"] = metadata;
            dashboard["activeAccounts"] = JsonData.Array(active.Select(DashboardCache.Metadata));
            dashboard["dismissedCount"] = (scoped["dismissedNotifications"] as JsonArray)?.Count ?? 0;
            dashboard["fetchedAt"] ??= DateTimeOffset.UtcNow.ToString("O");
            dashboard["refreshing"] = false;
            dashboard["refreshError"] = "";
            dashboard["cacheStatus"] = "live";
            dashboard["loading"] = false;
            if (dashboard["health"] is JsonObject healthData) { healthData["loading"] = false; }
            dashboard = DashboardCache.Presentation(dashboard, accounts);
            if (failed)
            {
                error = "Refresh returned incomplete provider data. Any previously complete dashboard has been retained.";
                partial = dashboard;
                dashboard = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            // Provider exceptions and process output may contain credentials.
            _logger.LogWarning("Dashboard refresh failed ({ErrorType}); retaining the complete snapshot.", ex.GetType().Name);
            error = "Dashboard refresh failed. Check account access and connectivity, then refresh to retry.";
            dashboard = null;
        }
        await _gate.WaitAsync(ct);
        try
        {
            var latest = await _preferences.ReadAsync(ct);
            lock (_sync)
            {
                if (!IsCurrent(state) || state.Key != DashboardCache.Key(latest)) { return; }
            }
            if (dashboard is not null)
            {
                try
                {
                    await _disk.WriteAsync(state.Key, dashboard, ct);
                    // Persist discovery metadata separately; never serialize Account.Token.
                    var metadata = DashboardCache.Presentation(new JsonObject
                    {
                        ["accounts"] = JsonData.Array(accounts.Select(DashboardCache.Metadata))
                    }, accounts);
                    await _disk.WriteAccountsAsync((JsonArray)metadata["accounts"]!, ct);
                }
                catch (Exception ex) when (IsCacheError(ex))
                {
                    _logger.LogWarning("Dashboard cache could not be saved ({ErrorType}).", ex.GetType().Name);
                    error = "The dashboard refreshed, but its cache could not be saved.";
                }
            }
            lock (_sync)
            {
                if (!IsCurrent(state)) { return; }
                var complete = dashboard is not null;
                var previous = state.Snapshot["dashboard"];
                dashboard ??= !state.Complete && partial is not null ? partial : (JsonObject)previous!.DeepClone();
                dashboard["refreshing"] = false;
                dashboard["refreshError"] = error;
                dashboard["loading"] = false;
                if (dashboard["health"] is JsonObject health) { health["loading"] = false; }
                if (!complete)
                {
                    dashboard["cacheStatus"] = state.Complete ? "cached" : "error";
                }
                ApplyHealthOrder(dashboard, latest["healthOrder"].Strings().ToArray());
                var changed = !JsonNode.DeepEquals(Content(previous), Content(dashboard));
                dashboard["seq"] = changed ? ++_sequence : _sequence;
                state.Complete |= complete;
                state.Snapshot = new JsonObject { ["dashboard"] = dashboard, ["prefs"] = latest };
                if (!background || latest.Flag("autoApplyUpdates", true))
                {
                    Publish("state", state.Snapshot, state);
                }
                else if (changed)
                {
                    Publish("update-available", new JsonObject
                    {
                        ["seq"] = _sequence,
                        ["repositoryId"] = dashboard["repositoryId"]?.DeepClone(),
                        ["fetchedAt"] = dashboard["fetchedAt"]?.DeepClone(),
                        ["refreshing"] = false,
                        ["refreshError"] = error,
                        ["counts"] = dashboard["counts"]?.DeepClone()
                    }, state);
                }
                if (error.Length > 0)
                {
                    Publish("refresh-error", new JsonObject
                    {
                        ["repositoryId"] = dashboard["repositoryId"]?.DeepClone(),
                        ["error"] = error
                    }, state);
                }
            }
        }
        finally { _gate.Release(); }
    }

    internal static bool HasProviderFailure(JsonObject dashboard) =>
        !dashboard.Flag("authenticated") || dashboard["lanes"] is not JsonArray || dashboard["counts"] is not JsonObject ||
        dashboard["errors"] is JsonArray { Count: > 0 } ||
        dashboard["health"]?["items"].Objects().Any(item => item.Text("state") == "unavailable") == true ||
        dashboard["providers"].Objects().Concat(dashboard["health"]?["providers"].Objects() ?? [])
            .Any(provider => provider.Text("status") is "partial" or "unavailable" or "failed" or "error");

    private static bool IsCacheError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException;

    private static JsonArray ScopedMetadata(JsonArray metadata, JsonObject? selected)
    {
        if (selected is null) { return (JsonArray)metadata.DeepClone(); }
        return JsonData.Array(metadata.Objects().Where(a =>
            a.Text("id").Equals(selected.Text("accountId"), StringComparison.OrdinalIgnoreCase) &&
            a.Text("host").Equals(selected.Text("host"), StringComparison.OrdinalIgnoreCase)).Select(a =>
            {
                var copy = (JsonObject)a.DeepClone();
                copy["repos"] = new JsonArray(selected.Text("repository"));
                return copy;
            }));
    }

    private static JsonObject Shell(JsonObject prefs) => new()
    {
        ["authenticated"] = false,
        ["mode"] = prefs.Text("mode", "review"),
        ["loading"] = RepositoryCatalog.Selected(prefs) is not null,
        ["message"] = RepositoryCatalog.Selected(prefs) is null ? "Select a repository to load its dashboard." : "Loading the selected repository.",
        ["lanes"] = new JsonArray(),
        ["notifications"] = new JsonArray(),
        ["counts"] = new JsonObject(),
        ["health"] = new JsonObject { ["items"] = new JsonArray(), ["loading"] = RepositoryCatalog.Selected(prefs) is not null },
        ["accounts"] = new JsonArray(),
        ["activeAccounts"] = new JsonArray(),
        ["fetchedAt"] = null
    };

    internal async Task WaitForRefreshAsync(CancellationToken ct)
    {
        Task? refresh;
        lock (_sync) { refresh = _current?.Refresh; }
        if (refresh is not null) { await refresh.WaitAsync(ct); }
    }

    public async Task StreamAsync(HttpContext context, Guid client)
    {
        var channel = Channel.CreateBounded<Event>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        var id = Guid.NewGuid();
        lock (_sync) { _subscribers[id] = new(client, channel); }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
            var state = await SelectAsync(false, background: false, context.RequestAborted);
            lock (_sync)
            {
                if (IsCurrent(state))
                {
                    channel.Writer.TryWrite(new("state", (JsonObject)state.Snapshot.DeepClone(), state.Key, state.Generation));
                    channel.Writer.TryWrite(new("snapshot", new JsonObject
                    {
                        ["repositoryId"] = state.Snapshot["dashboard"]?["repositoryId"]?.DeepClone(),
                        ["seq"] = state.Snapshot["dashboard"]?["seq"]?.DeepClone(),
                        ["fetchedAt"] = state.Snapshot["dashboard"]?["fetchedAt"]?.DeepClone(),
                        ["refreshing"] = state.Snapshot["dashboard"]?["refreshing"]?.DeepClone(),
                        ["refreshError"] = state.Snapshot["dashboard"]?["refreshError"]?.DeepClone(),
                        ["cacheStatus"] = state.Snapshot["dashboard"]?["cacheStatus"]?.DeepClone(),
                        ["prefs"] = state.Snapshot["prefs"]?.DeepClone(),
                        ["nextPollAt"] = _nextPoll.ToUnixTimeMilliseconds()
                    }, state.Key, state.Generation));
                    channel.Writer.TryWrite(new("poll-schedule", PollSchedule(state), state.Key, state.Generation));
                }
            }
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await foreach (var message in channel.Reader.ReadAllAsync(context.RequestAborted))
            {
                lock (_sync)
                {
                    if (_current is not { } current || message.Generation != _generation || message.Key != current.Key)
                    {
                        continue;
                    }
                }
                await context.Response.WriteAsync(Frame(message.Name, message.Data), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                lock (_sync)
                {
                    if (message.Name == "state" && _current is { } current &&
                        message.Generation == _generation && message.Key == current.Key)
                    {
                        Remember(client, message.Data, current);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // EventSource disconnected.
        }
        finally { lock (_sync) { _subscribers.Remove(id); } }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(90));
        while (await timer.WaitForNextTickAsync(linked.Token))
        {
            try { await PollAsync(linked.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Dashboard polling failed ({ErrorType}).", ex.GetType().Name);
            }
        }
    }

    internal async Task PollAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            _nextPoll = DateTimeOffset.UtcNow.AddSeconds(90);
            if (_subscribers.Count == 0) { return; }
        }
        var state = await SelectAsync(true, background: true, ct);
        lock (_sync)
        {
            if (IsCurrent(state)) { Publish("poll-schedule", PollSchedule(state), state); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _lifetime.Cancel();
        base.Dispose();
    }

    public static void ApplyHealthOrder(JsonObject dashboard, string[] order)
    {
        if (dashboard["health"] is not JsonObject healthData || healthData["items"] is not JsonArray items) { return; }
        var rank = order.Select((id, index) => (id, index)).DistinctBy(item => item.id)
            .ToDictionary(item => item.id, item => item.index);
        healthData["items"] = JsonData.Array(items.OfType<JsonObject>()
            .OrderBy(item => rank.GetValueOrDefault(item.Text("id"), int.MaxValue))
            .GroupBy(item => item.Text("groupId", item.Text("id"))).SelectMany(group => group));
    }

    private bool IsCurrent(State state) => ReferenceEquals(state, _current) && state.Generation == _generation;

    private static JsonNode? Content(JsonNode? node)
    {
        if (node?.DeepClone() is not JsonObject content) { return null; }
        content.Remove("seq");
        content.Remove("fetchedAt");
        content.Remove("refreshing");
        content.Remove("cacheStatus");
        return content;
    }

    private static IEnumerable<JsonObject> EnumerateObjects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.SelectMany(property => EnumerateObjects(property.Value))) { yield return child; }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.SelectMany(EnumerateObjects)) { yield return child; }
        }
    }

    private JsonObject PollSchedule(State state) => new()
    {
        ["nextPollAt"] = _nextPoll.ToUnixTimeMilliseconds(),
        ["repositoryId"] = state.Snapshot["dashboard"]?["repositoryId"]?.DeepClone()
    };

    private static string Frame(string name, JsonNode data) => $"event: {name}\ndata: {data.ToJsonString()}\n\n";

    private void Publish(string name, JsonNode data, State state)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Messages.Writer.TryWrite(new(name, (JsonObject)data.DeepClone(), state.Key, state.Generation));
        }
    }

    private sealed class State(string key, long generation, JsonObject snapshot, bool complete)
    {
        public string Key { get; } = key;
        public long Generation { get; } = generation;
        public JsonObject Snapshot { get; set; } = snapshot;
        public bool Complete { get; set; } = complete;
        public Task? Refresh { get; set; }
    }
    private sealed record Event(string Name, JsonObject Data, string Key, long Generation);
    private sealed record Subscription(Guid Client, Channel<Event> Messages);
    private sealed record DisplayedSnapshot(JsonObject Snapshot, DateTimeOffset At, string Key, long Generation);
}
