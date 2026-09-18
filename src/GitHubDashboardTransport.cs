// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.TeamApp;

internal static class GitHubDashboardTransport
{
    public static string NormalizeHost(string? host)
    {
        var value = (host ?? "").Trim().ToLowerInvariant();
        if (value.StartsWith("https://", StringComparison.Ordinal))
        {
            value = value[8..];
        }
        else if (value.StartsWith("http://", StringComparison.Ordinal))
        {
            value = value[7..];
        }
        value = value.Split('/')[0];
        if (value is "" or "api.github.com" or "github.com")
        {
            return "github.com";
        }
        // Credentials must never be sent to a host containing userinfo, a query, or a fragment.
        if (value.IndexOfAny(['@', '?', '#', '\\']) >= 0 ||
            !Uri.TryCreate($"https://{value}", UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" || uri.HostNameType == UriHostNameType.Unknown)
        {
            throw new InvalidDataException("Invalid GitHub hostname.");
        }
        return uri.Authority;
    }

    public static string GraphqlUrl(string host) =>
        NormalizeHost(host) is "github.com" ? "https://api.github.com/graphql" : $"https://{NormalizeHost(host)}/api/graphql";

    public static string RestUrl(string host) =>
        NormalizeHost(host) is "github.com" ? "https://api.github.com" : $"https://{NormalizeHost(host)}/api/v3";

    public static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("aspire-team-app");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    public static async Task<JsonObject> QueryAsync(
        HttpClient http, string host, string token, string query, JsonObject? variables,
        CancellationToken ct, bool allowPartialData = false)
    {
        using var request = Request(HttpMethod.Post, GraphqlUrl(host), token);
        request.Content = new StringContent(new JsonObject
        {
            ["query"] = query,
            ["variables"] = variables?.DeepClone()
        }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? body;
        try
        {
            body = JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"GitHub API {(int)response.StatusCode} returned invalid JSON.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(Redact(
                $"GitHub API {(int)response.StatusCode} {response.ReasonPhrase}: {body.Text("message", "Request failed")}", token),
                null, response.StatusCode);
        }
        if (body is null)
        {
            throw new InvalidDataException("GitHub API returned an empty response.");
        }
        var errors = body["errors"].Objects().Select(error => error.Text("message", "GraphQL request failed")).ToArray();
        if (errors.Length > 0 && !(allowPartialData && body["data"] is JsonObject))
        {
            throw new InvalidDataException(Redact(string.Join("; ", errors), token));
        }
        if (body["data"] is not JsonObject)
        {
            throw new InvalidDataException("GitHub API returned no data.");
        }
        return body;
    }

    public static string Redact(string message, string token) =>
        token.Length == 0 ? message : message.Replace(token, "[redacted]", StringComparison.Ordinal);

    public static bool IsProviderFailure(Exception exception) =>
        exception is HttpRequestException or IOException or InvalidDataException or JsonException or TimeoutException or TaskCanceledException;
}
