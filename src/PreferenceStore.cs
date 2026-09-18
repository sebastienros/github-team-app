// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHub.TeamApp;

internal sealed class PreferenceStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "preferences.json");

    public string DirectoryPath => Path.GetDirectoryName(_path)!;

    public static JsonObject Defaults() => new()
    {
        ["mode"] = "review",
        ["release"] = DashboardConstants.CurrentRelease,
        ["showDrafts"] = false,
        ["autoApplyUpdates"] = true,
        ["notifications"] = new JsonObject
        {
            ["reviewRequested"] = true,
            ["readyToMerge"] = true,
            ["changesRequested"] = true,
            ["ciFailing"] = true
        },
        ["accounts"] = new JsonObject(),
        ["repositories"] = new JsonArray(),
        ["selectedRepository"] = "",
        ["teamMembers"] = new JsonArray(),
        ["azurePipelines"] = new JsonArray(),
        ["healthOrder"] = new JsonArray(),
        ["dismissedNotifications"] = new JsonArray()
    };

    public async Task<JsonObject> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonObject> UpdateAsync(Action<JsonObject> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var prefs = await ReadCoreAsync(cancellationToken);
            update(prefs);
            Directory.CreateDirectory(DirectoryPath);
            var temporary = Path.Combine(DirectoryPath, $".preferences-{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                await using (var stream = new FileStream(temporary, options))
                {
                    using var writer = new Utf8JsonWriter(stream);
                    prefs.WriteTo(writer);
                    await writer.FlushAsync(cancellationToken);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            return prefs;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var prefs = Defaults();
        try
        {
            await using var stream = File.OpenRead(_path);
            var saved = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
                ?? throw new InvalidDataException("Preferences must contain a JSON object.");
            foreach (var (key, value) in saved)
            {
                prefs[key] = value?.DeepClone();
            }
            if (!saved.ContainsKey("repositories"))
            {
                RepositoryCatalog.Migrate(prefs);
            }
        }
        catch (FileNotFoundException)
        {
            // A fresh installation has no preferences until the first change.
        }
        catch (DirectoryNotFoundException)
        {
            // The data directory is created only when a preference is saved.
        }
        return prefs;
    }
}
