// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHub.TeamApp;

internal sealed class AccountService
{
    private readonly HttpClient _http;
    private readonly ILogger<AccountService> _logger;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> _runGh;
    private readonly Func<IReadOnlyDictionary<string, string?>> _environment;

    public AccountService(HttpClient http, ILogger<AccountService> logger)
        : this(http, logger, RunGhAsync, ReadEnvironment)
    {
    }

    internal AccountService(HttpClient http, ILogger<AccountService> logger,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> runGh,
        Func<IReadOnlyDictionary<string, string?>> environment)
    {
        _http = http;
        _logger = logger;
        _runGh = runGh;
        _environment = environment;
    }

    public async Task<IReadOnlyList<Account>> ResolveAsync(JsonObject prefs, CancellationToken ct)
    {
        var configurations = prefs["accounts"] as JsonObject ?? new JsonObject();
        if (configurations.Count == 0 && prefs.Text("account") is { Length: > 0 } legacyId)
        {
            configurations = new JsonObject
            {
                [legacyId] = new JsonObject { ["repos"] = prefs["repos"]?.DeepClone(), ["active"] = true }
            };
        }
        var candidates = await DetectCandidatesAsync(ct).ConfigureAwait(false);
        var probes = new List<Probe>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var probe = new Probe(candidate);
            try
            {
                if (candidate.Error is not null)
                {
                    throw new InvalidDataException(candidate.Error);
                }
                var identity = await GitHubDashboardTransport.QueryAsync(_http, candidate.Host, candidate.Token,
                    "query { viewer { login avatarUrl } }", null, ct, allowPartialData: true).ConfigureAwait(false);
                var viewer = identity["data"]?["viewer"];
                probe.Login = viewer.Text("login", candidate.Login ?? "unknown");
                if (string.IsNullOrWhiteSpace(viewer.Text("login")))
                {
                    var reason = identity["errors"].Objects().FirstOrDefault().Text("message",
                        "Authentication failed: GitHub returned no viewer login.");
                    throw new InvalidDataException(GitHubDashboardTransport.Redact(reason, candidate.Token));
                }
                probe.AvatarUrl = viewer?["avatarUrl"]?.GetValue<string>();
                probe.Status = "ok";
                try
                {
                    using var request = GitHubDashboardTransport.Request(HttpMethod.Get,
                        $"{GitHubDashboardTransport.RestUrl(candidate.Host)}/", candidate.Token);
                    using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"GitHub scopes request failed (HTTP {(int)response.StatusCode}).");
                    }
                    if (response.Headers.TryGetValues("x-oauth-scopes", out var scopes))
                    {
                        probe.Scopes = string.Join(",", scopes).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && GitHubDashboardTransport.IsProviderFailure(ex))
                {
                    // Scope discovery is advisory: a fine-grained token can read repos without this header.
                    probe.Reason = $"Scope discovery: {SafeError(ex, candidate.Token)}";
                    _logger.LogWarning("GitHub account {Login} on {Host}: {Reason}", probe.Login, candidate.Host, probe.Reason);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && GitHubDashboardTransport.IsProviderFailure(ex))
            {
                probe.Status = "failed";
                probe.Reason = SafeError(ex, candidate.Token);
                _logger.LogWarning("GitHub authentication failed for {Login} on {Host}: {Reason}", probe.Login, candidate.Host, probe.Reason);
            }
            probes.Add(probe);
        }

        var accounts = new List<Account>();
        foreach (var group in probes.GroupBy(p => $"acct:{p.Candidate.Host}/{p.Login.Trim().ToLowerInvariant()}"))
        {
            var first = group.First();
            var config = configurations[group.Key] ??
                (first.Candidate.Host == "github.com" ? configurations[$"acct:{first.Login.Trim().ToLowerInvariant()}"] : null);
            var repos = prefs.ContainsKey("repositories")
                ? prefs["repositories"].Objects().Where(item => item.Text("accountId") == group.Key)
                    .Select(item => item.Text("repository")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : config?["repos"].Strings().Distinct(StringComparer.Ordinal).ToArray() ?? [];
            foreach (var probe in group.Where(p => p.Status != "failed" && repos.Length > 0))
            {
                try
                {
                    var selections = repos.Select((repo, i) =>
                    {
                        var parts = repo.Split('/');
                        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
                        {
                            throw new InvalidDataException($"Invalid repo \"{repo}\".");
                        }
                        return $"r{i}: repository(owner:{JsonValue.Create(parts[0])!.ToJsonString()}, name:{JsonValue.Create(parts[1])!.ToJsonString()}) {{ nameWithOwner }}";
                    });
                    var response = await GitHubDashboardTransport.QueryAsync(_http, probe.Candidate.Host,
                        probe.Candidate.Token, $"query {{ {string.Join("\n", selections)} }}", null, ct, allowPartialData: true).ConfigureAwait(false);
                    probe.Accessible = Enumerable.Range(0, repos.Length).Count(i => response["data"]?[$"r{i}"] is JsonObject);
                    probe.Status = probe.Accessible == repos.Length ? "ok" : probe.Accessible == 0 ? "limited" : "partial";
                    var errors = response["errors"].Objects().Select(error => error.Text("message")).ToArray();
                    if (errors.Length > 0)
                    {
                        probe.Reason = GitHubDashboardTransport.Redact(string.Join("; ", errors), probe.Candidate.Token);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && GitHubDashboardTransport.IsProviderFailure(ex))
                {
                    probe.Status = "failed";
                    probe.Reason = SafeError(ex, probe.Candidate.Token);
                    _logger.LogWarning("GitHub repository probing failed for {Login} on {Host}: {Reason}",
                        probe.Login, probe.Candidate.Host, probe.Reason);
                }
            }

            var ranked = group.OrderByDescending(Score).ToArray();
            var best = ranked[0];
            var sources = ranked.Select((probe, i) => new JsonObject
            {
                ["source"] = probe.Candidate.Source,
                ["label"] = probe.Candidate.Source switch { "gh" => "GitHub CLI", "copilot" => "Copilot", _ => "Environment" },
                // The renderer never uses the old token hash. Keep an opaque, scan-local source id instead.
                ["hash"] = probe.Candidate.Id,
                ["host"] = probe.Candidate.Host,
                ["enterprise"] = probe.Candidate.Host != "github.com",
                ["status"] = probe.Status,
                ["scopes"] = JsonData.Array(probe.Scopes),
                ["accessible"] = probe.Accessible,
                ["total"] = repos.Length,
                ["hasReadOrg"] = probe.Scopes.Contains("read:org"),
                ["reason"] = probe.Reason,
                ["chosen"] = i == 0
            });
            var active = config.Flag("active");
            var metadata = new JsonObject
            {
                ["id"] = group.Key,
                ["login"] = best.Login,
                ["avatarUrl"] = best.AvatarUrl ?? ranked.Select(p => p.AvatarUrl).FirstOrDefault(url => url is not null),
                ["host"] = best.Candidate.Host,
                ["enterprise"] = best.Candidate.Host != "github.com",
                ["graphql"] = best.Candidate.ValidHost ? GitHubDashboardTransport.GraphqlUrl(best.Candidate.Host) : null,
                ["status"] = best.Status,
                ["accessible"] = best.Accessible,
                ["total"] = repos.Length,
                ["hasReadOrg"] = ranked.Any(p => p.Scopes.Contains("read:org")),
                ["reason"] = best.Reason,
                ["sources"] = JsonData.Array(sources),
                ["sourceKinds"] = JsonData.Array(ranked.Select(p => p.Candidate.Source).Distinct(StringComparer.Ordinal)),
                ["repos"] = JsonData.Array(repos),
                ["active"] = active
            };
            accounts.Add(new Account(group.Key, best.Login, best.Candidate.Host, best.Candidate.Token, repos, active, metadata));
        }

        accounts = accounts.OrderByDescending(a => a.Metadata.Text("status") == "failed" ? -1 :
            100000 + a.Metadata.Number("accessible") * 1000 + (a.Metadata.Flag("hasReadOrg") ? 100 : 0) +
            Math.Min(a.Metadata["sources"].Objects().Count(), 5)).ToList();
        if (configurations.Count == 0 && accounts.FirstOrDefault() is { } strongest &&
            strongest.Metadata.Text("status") != "failed")
        {
            strongest.Metadata["active"] = true;
            accounts[0] = strongest with { Active = true };
        }
        return accounts;
    }

    public async Task<JsonObject> SearchRepositoriesAsync(JsonObject prefs, string accountId, string query, CancellationToken ct)
    {
        var normalizedId = RepositoryCatalog.ParseAccount(accountId).AccountId;
        query = query.Trim();
        if (query.Length is < 1 or > 200 || query.Any(char.IsControl))
        {
            throw new ArgumentException("Enter between 1 and 200 characters to search repositories.");
        }
        var account = (await ResolveAsync(prefs, ct)).FirstOrDefault(item => item.Id == normalizedId &&
            item.Metadata.Text("status") != "failed")
            ?? throw new ArgumentException("This account is unavailable. Refresh accounts and sign in with GitHub CLI.");
        using var request = GitHubDashboardTransport.Request(HttpMethod.Get,
            $"{GitHubDashboardTransport.RestUrl(account.Host)}/search/repositories?q={Uri.EscapeDataString(query)}&per_page=20",
            account.Token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Repository search failed (HTTP {(int)response.StatusCode}).");
        }
        var raw = await response.Content.ReadAsStringAsync(ct);
        var result = JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidDataException("Repository search returned invalid JSON.");
        if (result["items"] is not JsonArray)
        {
            throw new InvalidDataException("Repository search returned no repository list.");
        }
        var items = new JsonArray();
        foreach (var item in result["items"].Objects())
        {
            var repository = RepositoryCatalog.NormalizeName(item.Text("full_name"));
            items.Add((JsonNode)new JsonObject
            {
                ["repository"] = repository,
                ["description"] = item.Text("description"),
                ["url"] = $"https://{account.Host}/{repository}",
                ["private"] = item.Flag("private")
            });
        }
        return new JsonObject { ["items"] = items };
    }

    public static string AccountId(string login, string host) =>
        $"acct:{GitHubDashboardTransport.NormalizeHost(host)}/{login.Trim().ToLowerInvariant()}";

    public static bool IsEmuLogin(string login) =>
        DashboardConstants.CoreTeamMemberAliasSuffixes.Any(suffix =>
            login.Trim().Length > suffix.Length && login.Trim().EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private async Task<List<Candidate>> DetectCandidatesAsync(CancellationToken ct)
    {
        var candidates = new List<Candidate>();
        var seen = new HashSet<(string Host, string Token)>();
        void Add(string source, string? token, string? login, string? host, string? error = null)
        {
            if (string.IsNullOrWhiteSpace(token) && error is null)
            {
                return;
            }
            string normalizedHost;
            try
            {
                normalizedHost = GitHubDashboardTransport.NormalizeHost(host);
            }
            catch (InvalidDataException ex)
            {
                // An invalid environment account must not prevent valid CLI/other-host accounts
                // from being offered. Retain failed metadata without constructing an API endpoint.
                candidates.Add(new(source, "", login, (host ?? "").Trim().ToLowerInvariant(),
                    $"credential-{candidates.Count + 1}", ex.Message, ValidHost: false));
                return;
            }
            var value = token?.Trim() ?? "";
            if (value.Length == 0 || seen.Add((normalizedHost, value)))
            {
                candidates.Add(new(source, value, login, normalizedHost, $"credential-{candidates.Count + 1}", error, ValidHost: true));
            }
        }

        try
        {
            var result = await _runGh(["auth", "status", "--json", "hosts"], ct).ConfigureAwait(false);
            // gh emits {"hosts":{"github.com":[{"login":"octo","state":"success",...}]}}.
            // Failed accounts remain in that array even when gh exits nonzero.
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var status = JsonNode.Parse(result.StandardOutput) as JsonObject
                    ?? throw new InvalidDataException("GitHub CLI returned invalid account metadata.");
                if (status["hosts"] is not JsonObject hosts)
                {
                    throw new InvalidDataException("GitHub CLI account metadata is missing hosts.");
                }
                foreach (var (host, entries) in hosts)
                {
                    foreach (var entry in entries.Objects())
                    {
                        var login = entry.Text("login");
                        if (login.Length == 0)
                        {
                            continue;
                        }
                        try
                        {
                            var token = await _runGh(["auth", "token", "--hostname", host, "--user", login], ct).ConfigureAwait(false);
                            Add("gh", token.ExitCode == 0 ? token.StandardOutput : null, login, host,
                                token.ExitCode != 0 || string.IsNullOrWhiteSpace(token.StandardOutput)
                                    ? $"GitHub CLI could not read the credential for {login} on {host}. Run gh auth login --hostname {host} to sign in again."
                                    : null);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested &&
                            (GitHubDashboardTransport.IsProviderFailure(ex) || ex is Win32Exception))
                        {
                            Add("gh", null, login, host, $"GitHub CLI credential lookup failed ({ex.GetType().Name}). Sign in again with gh auth login.");
                        }
                    }
                }
            }
            else if (result.ExitCode != 0)
            {
                _logger.LogWarning("GitHub CLI did not return any accounts (exit {ExitCode}). Run gh auth login to configure one.", result.ExitCode);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (GitHubDashboardTransport.IsProviderFailure(ex) || ex is Win32Exception))
        {
            // Do not log process output: gh auth status can contain credentials on older versions.
            _logger.LogWarning("GitHub CLI account discovery failed ({ErrorType}); environment credentials will still be checked.", ex.GetType().Name);
        }

        var environment = _environment();
        environment.TryGetValue("GH_HOST", out var envHost);
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" })
        {
            if (environment.TryGetValue(key, out var token))
            {
                Add("env", token, null, envHost);
            }
        }
        foreach (var (key, token) in environment)
        {
            if (!key.StartsWith("COPILOT_GH_ACCOUNT_", StringComparison.Ordinal))
            {
                continue;
            }
            // COPILOT_GH_ACCOUNT_github_2E_com_alice_5F_microsoft: escaped underscores
            // must be protected before finding the bare host/login separator.
            var value = key["COPILOT_GH_ACCOUNT_".Length..].Replace("_2E_", "\u0001", StringComparison.Ordinal)
                .Replace("_5F_", "\u0002", StringComparison.Ordinal);
            var separator = value.IndexOf('_');
            static string Decode(string s) => s.Replace('\u0001', '.').Replace('\u0002', '_');
            Add("copilot", token, Decode(separator < 0 ? value : value[(separator + 1)..]),
                separator < 0 ? "github.com" : Decode(value[..separator]));
        }
        return candidates;
    }

    private static Task<ProcessResult> RunGhAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        ProcessRunner.RunAsync("gh", args, ct, TimeSpan.FromSeconds(8),
            new Dictionary<string, string?>
            {
                ["GH_TOKEN"] = null,
                ["GITHUB_TOKEN"] = null,
                ["GH_ENTERPRISE_TOKEN"] = null,
                ["GITHUB_ENTERPRISE_TOKEN"] = null,
                ["GH_PROMPT_DISABLED"] = "1"
            });

    private static IReadOnlyDictionary<string, string?> ReadEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => Convert.ToString(entry.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);

    private static int Score(Probe probe) => probe.Status == "failed" ? -1 :
        100000 + probe.Accessible * 1000 + (probe.Scopes.Contains("read:org") ? 100 : 0) +
        (probe.Candidate.Source switch { "gh" => 3, "copilot" => 2, _ => 1 });

    private static string SafeError(Exception error, string token) =>
        GitHubDashboardTransport.Redact(error is TaskCanceledException ? "GitHub request timed out." : error.Message, token);

    private sealed record Candidate(string Source, string Token, string? Login, string Host, string Id, string? Error, bool ValidHost);

    private sealed class Probe(Candidate candidate)
    {
        public Candidate Candidate { get; } = candidate;
        public string Login { get; set; } = candidate.Login ?? "unknown";
        public string? AvatarUrl { get; set; }
        public string[] Scopes { get; set; } = [];
        public string Status { get; set; } = "failed";
        public string? Reason { get; set; }
        public int Accessible { get; set; }
    }
}
