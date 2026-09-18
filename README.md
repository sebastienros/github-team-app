# GitHub Team App

A standalone, local-first GitHub team dashboard served by .NET in your browser.
The application includes Review, Issues, Ship, and repository Health views,
GitHub Copilot App session handoff, and a read-only prerequisite doctor.

## Run

Install the .NET 10 SDK or newer and authenticate GitHub CLI with `gh auth login`.

```sh
dotnet run -- doctor
dotnet run
```

The application binds to an OS-assigned loopback port and opens your browser.
Use `--no-browser`, `--port 5143`, or `--data-dir /path/to/data` to customize it.

```sh
dotnet run -- --no-browser --port 5143
dotnet run -- doctor --json
```

GitHub data requires `gh` and an authenticated account. Azure CLI and its
`azure-devops` extension are optional for Azure pipeline health. GitHub Copilot App
is optional for session actions; its documented deep-link interface asks for
confirmation before creating a session. Copilot CLI session launching is deferred.

## Repositories and accounts

Open **Repositories** to search GitHub using a detected account, or enter an
`OWNER/REPO` name and save it. Search uses the selected account's GitHub host,
including GitHub Enterprise Server (GHES); arbitrary API URLs are not accepted.
The account must already be authenticated through GitHub CLI or a supported
environment credential. **Accounts** manages credentials separately from the
saved repository list.

The header project picker selects **one repository at a time** for Review,
Issues, Ship, and Health. Counts, notifications, actions, and refreshes belong to
that repository and its assigned account; repositories are never aggregated.
Removing a saved repository removes only local configuration, not anything on
GitHub. There are no preselected repositories or assumed team members.

Settings accepts an optional release/milestone filter and a list of GitHub team
member logins. An empty release means no release filter. These view/filter
preferences are shared across projects; cached data is keyed by their values.
Azure pipelines are explicitly added to the selected repository and are shown
only there. Legacy account-based repository lists are migrated without dropping
saved preferences, choosing the first repository of an active account (otherwise
the first saved repository). The migration is persisted on the next settings
write. Pass an existing data directory with `--data-dir` to migrate its settings.

## Local cache and privacy

Successful dashboard data is cached on disk. Startup and project/view switching
read the matching cache immediately, without waiting for authentication or
network discovery, then refresh in the background. The UI indicates loading,
refreshing, freshness, and errors. A failed refresh retains the last complete
matching snapshot. Late responses cannot replace a different selected project,
and actions are resolved against the browser's current canonical snapshot.

The cache is scoped by canonical host/repository, assigned account, view, and
data-affecting filters. It is versioned and atomically replaced, with user-only
file permissions on Unix. Corrupt or incompatible cache entries are reported and
refetched; corrupt preferences are reported without overwriting them.
Credentials are never serialized, but cached repository data may contain private
titles, descriptions, usernames, and CI metadata. Protect the data directory
accordingly. Removing a repository does not erase previously cached files.

`GITHUB_TEAM_APP_HOME` overrides the default local application-data directory
(`GitHub/TeamApp` beneath the platform's local application-data folder).
`--data-dir` takes precedence for the server. The legacy `ASPIRE_TEAM_APP_HOME`
environment variable is accepted for migration compatibility. CLI doctor uses
the same environment-based directory; the web doctor uses the running server's
directory.

The server listens only on loopback, validates the request host/origin and
fetch-site, and requires a per-browser client identifier for dashboard APIs.
Treat access to the local browser profile and data directory as access to the
dashboard. Session project names in Settings are user-supplied routing
suggestions, not discovery of installed GitHub App projects. `ghapp://session/new`
routes by repository coordinates. GHES session actions are explicitly unsupported.

## Native AOT

The C# backend has no third-party runtime packages. Browser assets are embedded;
neither Node.js nor a .NET runtime is needed by the published native executable.

```sh
dotnet publish GitHub.TeamApp.csproj -c Release -r osx-arm64 -p:PublishAot=true
```

Use the RID and native compiler prerequisites appropriate to the build host.
GitHub CLI and optional provider tools remain external prerequisites.

## Development

The xUnit tests run through Microsoft.Testing.Platform:

```sh
dotnet run --project tests/GitHub.TeamApp.Tests -- \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
node --test tests/browser/render.test.mjs
```

## Attribution

Extracted from the Aspire team dashboard canvas and its standalone .NET port in
[microsoft/aspire](https://github.com/microsoft/aspire). Original copyright notices
and the MIT license are retained. This is an independent community application,
not an official GitHub product.

The GitHub mark is from [Primer Octicons](https://github.com/primer/octicons),
copyright GitHub Inc., used under the MIT license in
[`assets/Octicons.LICENSE.txt`](assets/Octicons.LICENSE.txt).
