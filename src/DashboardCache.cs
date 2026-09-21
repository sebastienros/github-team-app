// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHub.TeamApp;

// Only presentation data is persisted. Preferences and Account instances can contain secrets.
internal sealed class DashboardCache(string directory)
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(directory), "dashboard-cache-v1");

    internal static string Key(JsonObject prefs)
    {
        var selected = RepositoryCatalog.Selected(prefs);
        var scoped = RepositoryCatalog.ScopePreferences(prefs);
        var inputs = new JsonObject();
        foreach (var field in new[] { "mode", "release", "showDrafts", "reviewLimit", "notifications",
            "dismissedNotifications", "teamMembers", "azurePipelines" })
        {
            inputs[field] = scoped[field]?.DeepClone();
        }
        inputs["maxOpenItems"] = PreferenceStore.MaxOpenItems(prefs);
        inputs["identity"] = selected is null ? null : new JsonObject
        {
            ["host"] = GitHubDashboardTransport.NormalizeHost(selected.Text("host")),
            ["repository"] = selected.Text("repository").Trim().ToLowerInvariant(),
            ["accountId"] = selected.Text("accountId").Trim().ToLowerInvariant(),
            ["active"] = prefs["accounts"]?[selected.Text("accountId")].Flag("active")
        };
        return Canonical(Presentation(inputs, []))!.ToJsonString();
    }

    internal string SnapshotPath(string key) => Path.Combine(DirectoryPath, IndexFileName(key));

    internal static string IndexFileName(string key)
    {
        var first = 14695981039346656037UL;
        var second = 7809847782465536322UL;
        foreach (var value in Encoding.UTF8.GetBytes(key))
        {
            first = unchecked((first ^ value) * 1099511628211UL);
            second = unchecked((second ^ value) * 1099511628211UL);
        }
        // FNV-1a is only a bounded filename index. The envelope's full key is authoritative.
        return $"fnv1a-{first:x16}{second:x16}.json";
    }

    internal async Task<JsonObject?> ReadAsync(string key, CancellationToken ct)
    {
        var envelope = await ReadFileAsync(SnapshotPath(key), ct);
        if (envelope is null)
        {
            return null;
        }
        if (envelope.Text("key") != key || envelope["dashboard"] is not JsonObject dashboard ||
            dashboard.Date("fetchedAt") is null || dashboard.Text("repositoryId").Length == 0 ||
            dashboard["lanes"] is not JsonArray || dashboard["counts"] is not JsonObject ||
            !dashboard.Flag("authenticated") ||
            DashboardService.HasProviderFailure(dashboard))
        {
            throw new InvalidDataException("The dashboard cache does not contain a complete matching snapshot.");
        }
        return (JsonObject)dashboard.DeepClone();
    }

    internal Task WriteAsync(string key, JsonObject dashboard, CancellationToken ct) =>
        WriteFileAsync(SnapshotPath(key), new JsonObject
        {
            ["version"] = 1,
            ["key"] = key,
            ["dashboard"] = dashboard.DeepClone()
        }, ct);

    internal async Task<JsonArray> ReadAccountsAsync(CancellationToken ct)
    {
        var envelope = await ReadFileAsync(Path.Combine(DirectoryPath, "accounts.json"), ct);
        if (envelope is null)
        {
            return new JsonArray();
        }
        if (envelope["accounts"] is not JsonArray accounts || accounts.Any(a => a is not JsonObject ||
            a.Text("id").Length == 0 || a.Text("host").Length == 0))
        {
            throw new InvalidDataException("The account cache does not contain account metadata.");
        }
        return accounts;
    }

    internal Task WriteAccountsAsync(JsonArray accounts, CancellationToken ct) =>
        WriteFileAsync(Path.Combine(DirectoryPath, "accounts.json"), new JsonObject
        {
            ["version"] = 1,
            ["accounts"] = accounts.DeepClone()
        }, ct);

    internal static JsonObject Metadata(Account account)
    {
        var metadata = new JsonObject();
        foreach (var field in new[] { "id", "login", "avatarUrl", "host", "enterprise", "status", "accessible",
            "total", "hasReadOrg", "sourceKinds", "repos", "active", "graphql", "reason" })
        {
            metadata[field] = account.Metadata[field]?.DeepClone();
        }
        metadata["id"] = account.Id;
        metadata["login"] = account.Login;
        metadata["host"] = account.Host;
        metadata["repos"] = JsonData.Array(account.Repos);
        metadata["active"] = account.Active;
        var sources = new JsonArray();
        foreach (var source in account.Metadata["sources"].Objects())
        {
            var copy = new JsonObject();
            foreach (var field in new[] { "source", "label", "hash", "host", "enterprise", "status",
                "scopes", "accessible", "total", "hasReadOrg", "reason", "chosen" })
            {
                copy[field] = source[field]?.DeepClone();
            }
            sources.Add((JsonNode)copy);
        }
        metadata["sources"] = sources;
        return metadata;
    }

    internal static JsonObject Presentation(JsonObject dashboard, IEnumerable<Account> accounts)
    {
        var tokens = accounts.Select(a => a.Token).Where(t => t.Length > 0).Distinct().ToArray();
        return (JsonObject)Clean(dashboard)!;

        JsonNode? Clean(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("credential", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    result[key] = Clean(value);
                }
                return result;
            }
            if (node is JsonArray array)
            {
                var result = new JsonArray();
                foreach (var value in array)
                {
                    result.Add(Clean(value));
                }
                return result;
            }
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                foreach (var token in tokens)
                {
                    text = GitHubDashboardTransport.Redact(text, token);
                }
                return JsonValue.Create(text);
            }
            return node?.DeepClone();
        }
    }

    private static JsonNode? Canonical(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                result[key] = Canonical(value);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(Canonical).ToArray());
        }
        return node?.DeepClone();
    }

    private static async Task<JsonObject?> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var envelope = await JsonNode.ParseAsync(stream, cancellationToken: ct) as JsonObject;
            if (envelope is null || envelope.Number("version") != 1)
            {
                throw new InvalidDataException("The dashboard cache format is invalid or unsupported.");
            }
            return envelope;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static async Task WriteFileAsync(string path, JsonObject envelope, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporary, options))
            {
                using var writer = new Utf8JsonWriter(stream);
                envelope.WriteTo(writer);
                await writer.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
