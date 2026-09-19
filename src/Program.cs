// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace GitHub.TeamApp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        try
        {
            if (args is ["doctor"] or ["doctor", "--json"])
            {
                return await Doctor.RunAsync(args.Length == 2, shutdown.Token);
            }
            if (args is ["--help"] or ["-h"] or ["help"])
            {
                Console.WriteLine("""
                    GitHub Team App

                    github-team [--port PORT] [--browser | --no-browser] [--data-dir DIRECTORY]
                    github-team doctor [--json]

                    Opens a native desktop window on Windows and macOS (the default browser elsewhere).
                    --browser explicitly opens the browser; --no-browser runs only the server.
                    The default port is assigned by the operating system.
                    GITHUB_TEAM_APP_HOME overrides the default preferences directory.
                    """);
                return 0;
            }
            var port = 0;
            var browser = true;
            var desktop = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
            var desktopManaged = false;
            var directory = Environment.GetEnvironmentVariable("GITHUB_TEAM_APP_HOME")
                ?? Environment.GetEnvironmentVariable("ASPIRE_TEAM_APP_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub", "TeamApp");
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var value) && value is >= 0 and <= 65535:
                        port = value;
                        break;
                    case "--data-dir" when i + 1 < args.Length:
                        directory = args[++i];
                        break;
                    case "--no-browser":
                        browser = false;
                        desktop = false;
                        break;
                    case "--browser":
                        browser = true;
                        desktop = false;
                        break;
                    case "--desktop-managed":
                        desktopManaged = true;
                        break;
                    default:
                        Console.Error.WriteLine($"Invalid argument: {args[i]}. Use --help for usage.");
                        return 2;
                }
            }
            if (desktopManaged)
            {
                browser = false;
                desktop = false;
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Listen(IPAddress.Loopback, port);
                server.Limits.MaxRequestBodySize = 64 * 1024;
            });
            var preferences = new PreferenceStore(directory);
            builder.Services.AddSingleton(preferences);
            builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
            builder.Services.AddSingleton(sp => new AccountService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ILogger<AccountService>>()));
            builder.Services.AddSingleton(sp => new GitHubDashboard(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ILogger<GitHubDashboard>>()));
            builder.Services.AddSingleton(sp => new HealthDashboard(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ILogger<HealthDashboard>>()));
            builder.Services.AddSingleton(sp => new DashboardService(preferences, sp.GetRequiredService<AccountService>(),
                sp.GetRequiredService<GitHubDashboard>(), sp.GetRequiredService<HealthDashboard>(), sp.GetRequiredService<ILogger<DashboardService>>()));
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardService>());
            await using var app = builder.Build();
            app.Run(context => HandleAsync(context, app.Services));
            await app.StartAsync(shutdown.Token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Console.WriteLine($"GitHub Team App: {address}");
            if (desktopManaged)
            {
                var lifetime = app.Lifetime;
                var stopping = lifetime.ApplicationStopping;
                var logger = app.Logger;
                // Console.In can read synchronously; a pool thread must not block startup or keep the process alive.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var line = await Console.In.ReadLineAsync();
                        if (!stopping.IsCancellationRequested && line is not (null or "shutdown"))
                        {
                            logger.LogError("Unexpected desktop control input; shutting down.");
                        }
                    }
                    catch (IOException error)
                    {
                        if (!stopping.IsCancellationRequested)
                        {
                            logger.LogError(error, "Could not read the desktop control pipe; shutting down.");
                        }
                    }
                    finally
                    {
                        if (!stopping.IsCancellationRequested)
                        {
                            lifetime.StopApplication();
                        }
                    }
                });
            }
            if (desktop)
            {
                using var desktopLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                    shutdown.Token, app.Lifetime.ApplicationStopping);
                try
                {
                    await DesktopLauncher.RunAsync(address, desktopLifetime.Token);
                }
                catch (OperationCanceledException) when (desktopLifetime.IsCancellationRequested)
                {
                    // Closing the host also closes its owned desktop window.
                }
                finally
                {
                    await app.StopAsync(CancellationToken.None);
                }
                return 0;
            }
            if (browser)
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception error)
                {
                    app.Logger.LogWarning("Could not open a browser: {Message}. Open {Address} manually.", error.Message, address);
                }
            }
            await app.WaitForShutdownAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"GitHub Team App: {error.Message}");
            return 1;
        }
    }

    internal static bool IsAllowedRequest(HttpRequest request, int localPort)
    {
        if (request.Host.Port != localPort || request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
        {
            return false;
        }
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.GetLeftPart(UriPartial.Authority) != $"http://{request.Host}"))
        {
            return false;
        }
        return request.Headers["Sec-Fetch-Site"].ToString() is "" or "same-origin" or "none";
    }

    private static async Task HandleAsync(HttpContext context, IServiceProvider services)
    {
        var cancellationToken = context.RequestAborted;
        try
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            if (!IsAllowedRequest(context.Request, context.Connection.LocalPort))
            {
                await ErrorAsync(context, 403, "Request must originate from this loopback dashboard.");
                return;
            }
            var path = context.Request.Path.Value ?? "/";
            if (context.Request.Method == "GET" && path == "/favicon.ico")
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (context.Request.Method == "GET" && path is "/" or "/index.html" or "/app.js" or "/styles.css" or "/theme.js" or "/standalone.js")
            {
                if (path == "/standalone.js")
                {
                    context.Response.ContentType = "text/javascript";
                    await context.Response.WriteAsync("window.githubTeamStandalone = true;", cancellationToken);
                    return;
                }
                var asset = path is "/" or "/index.html" ? "index.html" : path[1..];
                await using var stream = typeof(Program).Assembly.GetManifestResourceStream($"Assets/{asset}")
                    ?? throw new InvalidOperationException($"Missing embedded browser asset: {asset}");
                context.Response.ContentType = asset.EndsWith(".css", StringComparison.Ordinal) ? "text/css"
                    : asset.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/html";
                await stream.CopyToAsync(context.Response.Body, cancellationToken);
                return;
            }

            var preferences = services.GetRequiredService<PreferenceStore>();
            var dashboard = services.GetRequiredService<DashboardService>();
            var rawClient = context.Request.Headers["X-Team-App-Client"].FirstOrDefault()
                ?? context.Request.Query["client"].FirstOrDefault();
            if (path == "/api/doctor" && context.Request.Method == "GET")
            {
                await JsonAsync(context, await Doctor.CheckAsync(await preferences.ReadAsync(cancellationToken), cancellationToken));
                return;
            }
            if (!Guid.TryParse(rawClient, out var client))
            {
                await ErrorAsync(context, 400, "A dashboard client identifier is required.");
                return;
            }
            if (context.Request.Method == "GET")
            {
                switch (path)
                {
                    case "/api/state":
                        await JsonAsync(context, await dashboard.GetAsync(client, false, cancellationToken));
                        return;
                    case "/api/accounts":
                        dashboard.InvalidateAccounts();
                        await JsonAsync(context, new JsonObject
                        {
                            ["accounts"] = await dashboard.DiscoverAccountsAsync(cancellationToken)
                        });
                        return;
                    case "/api/repositories/search":
                        await JsonAsync(context, await services.GetRequiredService<AccountService>().SearchRepositoriesAsync(
                            await preferences.ReadAsync(cancellationToken),
                            context.Request.Query["accountId"].ToString(), context.Request.Query["q"].ToString(), cancellationToken));
                        return;
                    case "/api/session/configuration":
                        await JsonAsync(context, SessionLauncher.GetConfiguration(await preferences.ReadAsync(cancellationToken)));
                        return;
                    case "/events":
                        await dashboard.StreamAsync(context, client);
                        return;
                }
            }
            if (context.Request.Method != "POST")
            {
                await ErrorAsync(context, 404, "Not found.");
                return;
            }
            if (!context.Request.HasJsonContentType())
            {
                await ErrorAsync(context, 415, "Use application/json.");
                return;
            }
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken) as JsonObject
                ?? throw new ArgumentException("Request body must be a JSON object.");
            if (path == "/api/agent/action")
            {
                RequireNewSession(body);
                var pr = dashboard.FindPullRequest(client, body["pr"] as JsonObject ?? new JsonObject())
                    ?? throw new ArgumentException("This pull request is no longer in view. Refresh and try again.");
                await JsonAsync(context, SessionLauncher.BuildPullRequestAction(pr, body.Text("kind"),
                    await preferences.ReadAsync(cancellationToken)));
                return;
            }
            if (path == "/api/health/action")
            {
                RequireNewSession(body);
                var source = dashboard.FindHealthSource(client, body["source"].Text("id"))
                    ?? throw new ArgumentException("This health source is no longer in view. Refresh and try again.");
                await JsonAsync(context, SessionLauncher.BuildHealthAction(source, body.Text("kind"),
                    await preferences.ReadAsync(cancellationToken)));
                return;
            }
            if (path == "/api/open-pr")
            {
                var url = body.Text("url");
                if (!dashboard.IsLinkedPullRequest(client, url))
                {
                    throw new ArgumentException("This link is no longer in view.");
                }
                await JsonAsync(context, new JsonObject { ["url"] = url });
                return;
            }
            if (path == "/api/session/configuration")
            {
                var configuration = SessionLauncher.ValidateConfiguration(body);
                var prefs = await preferences.UpdateAsync(p => p["sessionLauncher"] = configuration, cancellationToken);
                await dashboard.PreferencesChangedAsync(cancellationToken);
                await JsonAsync(context, SessionLauncher.GetConfiguration(prefs));
                return;
            }
            if (path == "/api/auto-apply")
            {
                if (body["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var autoApply))
                {
                    throw new ArgumentException("enabled must be a boolean.");
                }
                var prefs = await preferences.UpdateAsync(p => p["autoApplyUpdates"] = autoApply, cancellationToken);
                await dashboard.PreferencesChangedAsync(cancellationToken);
                await JsonAsync(context, new JsonObject { ["prefs"] = prefs });
                return;
            }
            if (path == "/api/health/pipeline/add")
            {
                var selected = RepositoryCatalog.Selected(await preferences.ReadAsync(cancellationToken))
                    ?? throw new ArgumentException("Select a repository before adding a pipeline.");
                var selectedId = selected.Text("id");
                var pipeline = await services.GetRequiredService<HealthDashboard>()
                    .ResolvePipelineAsync(body.Text("url"), body.Text("branch"), cancellationToken);
                await preferences.UpdateAsync(p =>
                {
                    if (p.Text("selectedRepository") != selectedId)
                    {
                        throw new ArgumentException("The selected repository changed. Add the pipeline again.");
                    }
                    pipeline["repositoryId"] = selectedId;
                    var pipelines = p["azurePipelines"] as JsonArray ?? throw new InvalidDataException("Invalid pipeline preferences.");
                    var old = pipelines.OfType<JsonObject>().FirstOrDefault(item => item.Text("id") == pipeline.Text("id") &&
                        item.Text("repositoryId") == selectedId);
                    if (old is not null)
                    {
                        pipelines.Remove(old);
                    }
                    pipelines.Add((JsonNode)pipeline);
                }, cancellationToken);
            }
            else if (path == "/api/notifications/dismiss-all")
            {
                var current = await dashboard.GetAsync(client, false, cancellationToken);
                var ids = (current["dashboard"]?["notifications"]).Objects().Select(item => item.Text("id")).ToArray();
                await preferences.UpdateAsync(p =>
                    p["dismissedNotifications"] = JsonData.Array(p["dismissedNotifications"].Strings().Concat(ids).Distinct()), cancellationToken);
            }
            else if (path != "/api/refresh")
            {
                if (path == "/api/repositories/add")
                {
                    var accountId = RepositoryCatalog.ParseAccount(body.Text("accountId")).AccountId;
                    var available = await services.GetRequiredService<AccountService>()
                        .ResolveAsync(await preferences.ReadAsync(cancellationToken), cancellationToken);
                    if (!available.Any(account => account.Id == accountId && account.Metadata.Text("status") != "failed"))
                    {
                        throw new ArgumentException("Choose an available GitHub account. Refresh accounts to sign in.");
                    }
                }
                await preferences.UpdateAsync(p => UpdatePreferences(p, path, body), cancellationToken);
                dashboard.InvalidateAccounts();
            }
            dashboard.ConfigurationChanged();
            await JsonAsync(context, await dashboard.GetAsync(client, true, cancellationToken));
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            if (!context.Response.HasStarted)
            {
                await ErrorAsync(context, 400, error.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser disconnected.
        }
        catch (Exception error)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Requests").LogError(error, "Dashboard request failed");
            if (!context.Response.HasStarted)
            {
                await ErrorAsync(context, 500, "The request failed. See the application log for details.");
            }
        }
    }

    private static void RequireNewSession(JsonObject body)
    {
        if (body.Text("target", "new-session") != "new-session")
        {
            throw new ArgumentException("The standalone dashboard can open new sessions, but has no current Copilot conversation.");
        }
    }

    internal static void UpdatePreferences(JsonObject prefs, string path, JsonObject body)
    {
        switch (path)
        {
            case "/api/mode":
                var mode = body.Text("mode");
                if (mode is not ("review" or "issues" or "ship" or "health"))
                {
                    throw new ArgumentException("Unknown dashboard mode.");
                }
                prefs["mode"] = mode;
                break;
            case "/api/prefs":
                if (body.ContainsKey("release"))
                {
                    prefs["release"] = body.Text("release").Trim();
                }
                if (body.ContainsKey("teamMembers"))
                {
                    var members = body.Text("teamMembers").Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => value.TrimStart('@').ToLowerInvariant()).Distinct().ToArray();
                    if (members.Length > 100 || members.Any(value => value.Length is 0 or > 100 ||
                        !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    {
                        throw new ArgumentException("Team members must be GitHub logins separated by commas or spaces (at most 100).");
                    }
                    prefs["teamMembers"] = JsonData.Array(members);
                }
                prefs["showDrafts"] = body.Flag("showDrafts");
                if (body["notifications"] is JsonObject notifications && prefs["notifications"] is JsonObject saved)
                {
                    foreach (var key in saved.Select(p => p.Key).ToArray())
                    {
                        if (notifications[key] is JsonValue value && value.TryGetValue<bool>(out var enabled))
                        {
                            saved[key] = enabled;
                        }
                    }
                }
                break;
            case "/api/repositories/add":
                RepositoryCatalog.Add(prefs, body.Text("accountId"), body.Text("repository"));
                break;
            case "/api/repositories/select":
                RepositoryCatalog.Select(prefs, body.Text("id"));
                break;
            case "/api/repositories/remove":
                RepositoryCatalog.Remove(prefs, body.Text("id"));
                break;
            case "/api/account/repos":
            case "/api/account/toggle":
                var id = body.Text("id");
                if (!id.StartsWith("acct:", StringComparison.Ordinal) || id.Length > 256)
                {
                    throw new ArgumentException("Invalid account identifier.");
                }
                var accounts = prefs["accounts"] as JsonObject ?? throw new InvalidDataException("Invalid account preferences.");
                var configuration = accounts[id] as JsonObject;
                if (configuration is null)
                {
                    configuration = new JsonObject { ["active"] = false };
                    accounts[id] = configuration;
                }
                if (path.EndsWith("/toggle", StringComparison.Ordinal))
                {
                    configuration["active"] = body.Flag("active");
                }
                else
                {
                    var repos = body.Text("repos").Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(SessionLauncher.NormalizeRepository).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    configuration["repos"] = JsonData.Array(repos);
                    var accountId = RepositoryCatalog.ParseAccount(id).AccountId;
                    foreach (var removed in prefs["repositories"].Objects()
                        .Where(item => item.Text("accountId") == accountId && !repos.Contains(item.Text("repository")))
                        .Select(item => item.Text("id")).ToArray())
                    {
                        RepositoryCatalog.Remove(prefs, removed);
                    }
                    var wasActive = configuration.Flag("active");
                    foreach (var repository in repos)
                    {
                        var entry = RepositoryCatalog.Create(accountId, repository);
                        if (!prefs["repositories"].Objects().Any(item => item.Text("id") == entry.Text("id")))
                        {
                            RepositoryCatalog.Add(prefs, accountId, repository);
                        }
                    }
                    if (accounts[accountId] is JsonObject canonicalConfiguration)
                    {
                        canonicalConfiguration["active"] = wasActive;
                    }
                }
                break;
            case "/api/health/pipeline/remove":
                var pipelines = prefs["azurePipelines"] as JsonArray ?? throw new InvalidDataException("Invalid pipeline preferences.");
                var pipeline = pipelines.OfType<JsonObject>().FirstOrDefault(item => item.Text("id") == body.Text("id") &&
                    item.Text("repositoryId") == prefs.Text("selectedRepository"))
                    ?? throw new ArgumentException("The pipeline is no longer configured.");
                pipelines.Remove(pipeline);
                break;
            case "/api/health/order":
                if (body["order"] is not JsonArray order || order.Any(item => item is not JsonValue v || !v.TryGetValue<string>(out _)))
                {
                    throw new ArgumentException("Health order must be an array of source IDs.");
                }
                prefs["healthOrder"] = JsonData.Array(order.Strings().Distinct());
                break;
            case "/api/notifications/dismiss":
                var notification = body.Text("id");
                if (string.IsNullOrWhiteSpace(notification))
                {
                    throw new ArgumentException("Notification ID is required.");
                }
                prefs["dismissedNotifications"] = JsonData.Array(prefs["dismissedNotifications"].Strings().Append(notification).Distinct());
                break;
            case "/api/notifications/restore":
                prefs["dismissedNotifications"] = new JsonArray();
                break;
            default:
                throw new ArgumentException("Unknown API route.");
        }
    }

    private static Task JsonAsync(HttpContext context, JsonNode node)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(node.ToJsonString(), context.RequestAborted);
    }

    private static Task ErrorAsync(HttpContext context, int code, string message)
    {
        context.Response.StatusCode = code;
        return JsonAsync(context, new JsonObject { ["error"] = message });
    }
}
