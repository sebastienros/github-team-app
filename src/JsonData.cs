// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;

namespace GitHub.TeamApp;

internal static class JsonData
{
    public static string Text(this JsonNode? node, string key, string fallback = "") =>
        node?[key]?.GetValue<string>() ?? fallback;

    public static bool Flag(this JsonNode? node, string key, bool fallback = false) =>
        node?[key]?.GetValue<bool>() ?? fallback;

    public static int Number(this JsonNode? node, string key, int fallback = 0) =>
        node?[key]?.GetValue<int>() ?? fallback;

    public static IEnumerable<JsonObject> Objects(this JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    public static IEnumerable<string> Strings(this JsonNode? node) =>
        node is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? "") : [];

    public static JsonArray Array(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    public static JsonArray Array(IEnumerable<JsonObject> values) =>
        new(values.Select(value => (JsonNode?)value.DeepClone()).ToArray());

    public static DateTimeOffset? Date(this JsonNode? node, string key) =>
        DateTimeOffset.TryParse(node.Text(key), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
