// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace GitHub.TeamApp;

internal sealed record SyncProgress(string Phase, int Done, int? Total, string Unit, bool Initial = false);
