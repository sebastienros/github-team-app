// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace GitHub.TeamApp;

internal static class RepositoryCatalog
{
    public static JsonObject? Selected(JsonObject prefs) =>
        prefs["repositories"].Objects().FirstOrDefault(item => item.Text("id") == prefs.Text("selectedRepository"));

    public static string NormalizeName(string repository)
    {
        if (repository.Contains("://", StringComparison.Ordinal) || repository.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Enter a repository as OWNER/REPO. The selected account determines its GitHub host.");
        }
        return SessionLauncher.NormalizeRepository(repository);
    }

    public static JsonObject Create(string accountId, string repository)
    {
        var (host, normalizedAccount) = ParseAccount(accountId);
        var name = NormalizeName(repository);
        return new JsonObject
        {
            ["id"] = $"{host}/{name}",
            ["host"] = host,
            ["repository"] = name,
            ["accountId"] = normalizedAccount
        };
    }

    public static (string Host, string AccountId) ParseAccount(string accountId)
    {
        if (!accountId.StartsWith("acct:", StringComparison.Ordinal) || accountId.Length > 256)
        {
            throw new ArgumentException("Choose a GitHub account.");
        }
        var value = accountId[5..];
        var parts = value.Split('/');
        var host = parts.Length == 1 ? "github.com" : parts[0];
        var login = parts[^1];
        if (parts.Length > 2 || host.Length == 0 || login.Length == 0 ||
            !login.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Invalid GitHub account identifier.");
        }
        try
        {
            if (GitHubDashboardTransport.NormalizeHost(host) != host.ToLowerInvariant())
            {
                throw new ArgumentException("Invalid GitHub account identifier.");
            }
        }
        catch (InvalidDataException error)
        {
            throw new ArgumentException("Invalid GitHub account identifier.", error);
        }
        host = host.ToLowerInvariant();
        return (host, $"acct:{host}/{login.ToLowerInvariant()}");
    }

    public static void Migrate(JsonObject prefs)
    {
        var repositories = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var configurations = prefs["accounts"] as JsonObject ?? new JsonObject();
        if (configurations.Count == 0 && prefs.Text("account") is { Length: > 0 } legacy)
        {
            configurations[legacy] = new JsonObject { ["active"] = true, ["repos"] = prefs["repos"]?.DeepClone() };
            prefs["accounts"] = configurations;
        }
        foreach (var (accountId, configuration) in configurations.OrderByDescending(p => p.Value.Flag("active")))
        {
            foreach (var repository in configuration?["repos"].Strings() ?? [])
            {
                var entry = Create(accountId, repository);
                if (ids.Add(entry.Text("id")))
                {
                    repositories.Add((JsonNode)entry);
                }
            }
        }
        prefs["repositories"] = repositories;
        prefs["selectedRepository"] = repositories.FirstOrDefault().Text("id");
        foreach (var pipeline in prefs["azurePipelines"].Objects())
        {
            pipeline["repositoryId"] ??= prefs.Text("selectedRepository");
        }
    }

    public static void Add(JsonObject prefs, string accountId, string repository)
    {
        var entry = Create(accountId, repository);
        var repositories = prefs["repositories"] as JsonArray ?? throw new InvalidDataException("Invalid repository preferences.");
        if (repositories.Objects().Any(item => item.Text("id") == entry.Text("id")))
        {
            throw new ArgumentException("This repository is already saved.");
        }
        if (repositories.Count >= 100)
        {
            throw new ArgumentException("At most 100 repositories can be saved.");
        }
        repositories.Add((JsonNode)entry);
        if (Selected(prefs) is null)
        {
            prefs["selectedRepository"] = entry.Text("id");
        }
        var accounts = prefs["accounts"] as JsonObject ?? throw new InvalidDataException("Invalid account preferences.");
        var configuration = accounts[entry.Text("accountId")] as JsonObject ?? new JsonObject();
        if (configuration.Parent is null)
        {
            accounts[entry.Text("accountId")] = configuration;
        }
        configuration["active"] = true;
    }

    public static void Select(JsonObject prefs, string id)
    {
        if (!prefs["repositories"].Objects().Any(item => item.Text("id") == id))
        {
            throw new ArgumentException("The repository is no longer saved.");
        }
        prefs["selectedRepository"] = id;
    }

    public static void Remove(JsonObject prefs, string id)
    {
        var repositories = prefs["repositories"] as JsonArray ?? throw new InvalidDataException("Invalid repository preferences.");
        var entry = repositories.Objects().FirstOrDefault(item => item.Text("id") == id)
            ?? throw new ArgumentException("The repository is no longer saved.");
        repositories.Remove(entry);
        if (prefs.Text("selectedRepository") == id)
        {
            prefs["selectedRepository"] = repositories.FirstOrDefault().Text("id");
        }
        if (prefs["azurePipelines"] is JsonArray pipelines)
        {
            foreach (var pipeline in pipelines.Objects().Where(item => item.Text("repositoryId") == id).ToArray())
            {
                pipelines.Remove(pipeline);
            }
        }
    }

    public static Account[] ScopeAccounts(IReadOnlyList<Account> accounts, JsonObject prefs)
    {
        if (Selected(prefs) is not { } selected)
        {
            return [];
        }
        return accounts.Where(account => account.Active &&
            account.Id == selected.Text("accountId") && account.Host == selected.Text("host") &&
            account.Metadata.Text("status") != "failed")
            .Select(account => account with { Repos = [selected.Text("repository")] }).ToArray();
    }

    public static JsonObject ScopePreferences(JsonObject prefs)
    {
        var scoped = (JsonObject)prefs.DeepClone();
        var selected = Selected(prefs);
        scoped["azurePipelines"] = JsonData.Array(prefs["azurePipelines"].Objects()
            .Where(item => selected is not null && item.Text("repositoryId") == selected.Text("id")));
        return scoped;
    }
}
