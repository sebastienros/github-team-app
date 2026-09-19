# GitHub Team App

A standalone, local-first GitHub team dashboard with native Windows and macOS windows and
an optional browser interface, powered by .NET.
The application includes Review, Issues, Ship, and repository Health views,
GitHub Copilot App session handoff, and a read-only prerequisite doctor.

## Run

Install the .NET 10 SDK or newer and authenticate GitHub CLI with `gh auth login`.

```sh
dotnet run -- doctor
dotnet run
```

On macOS, `dotnet run` opens the app's own window and Dock icon using the system
WKWebView, not a browser tab. Closing the window or choosing Quit stops the local
server. Building the shell requires Apple Command Line Tools (`xcode-select
--install`). macOS 13 or newer is required.

On Windows, the default is a native Win32 window using WebView2, not a browser
tab. Building it requires Visual Studio 2022 or 2026 with the **Desktop development
with C++** workload, Windows SDK, and CMake tools installed. CMake fetches the
official WebView2 SDK as a hash-pinned build dependency; no NuGet runtime
dependency is added to the backend. The SDK version is `1.0.3856.49`; its pinned
SHA256 verifies the downloaded dependency, not merely a cache key. The shell
links the static WebView2 loader, so no loader DLL needs to be shipped.
The **Microsoft Edge WebView2 Evergreen
Runtime** must be installed separately before using the window; the app does not
install it automatically. Closing the window stops the local server. Linux and
other platforms continue to open the default browser.

The application binds to an OS-assigned loopback port. Use `--browser` to open
your default browser explicitly, `--no-browser` for only the server, or
`--port 5143` and `--data-dir /path/to/data` to customize it.

```sh
dotnet run -- --no-browser --port 5143
dotnet run -- --browser
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

On macOS this also creates
`bin/Release/net10.0/osx-arm64/publish/GitHub Team App.app`. Open that bundle in
Finder or copy it to Applications. It includes its native backend, starts it on
an available loopback port, and stops it on Quit. The app has its own icon,
standard Edit menu shortcuts, and a Reload command. Repository links open in
your default browser; session links open the GitHub Copilot App. Only the
dashboard's exact loopback origin is allowed inside the embedded webview.

The bundle is ad-hoc signed for local use, not Developer ID signed or notarized
for distribution. A downloaded build may require proper signing before macOS
allows it to launch. No Electron, Node runtime, or third-party webview library is
needed. The shell uses the build machine's architecture; build macOS bundles on
a matching architecture rather than cross-publishing. Use
`-p:BuildDesktopShell=false` to build/publish just the backend, then launch it
with `--browser` or `--no-browser`.

Use the RID and native compiler prerequisites appropriate to the build host.
GitHub CLI and optional provider tools remain external prerequisites.

### Windows portable directory

Build on Windows; the native Windows shell is not cross-built on macOS or Linux.
Publishing an explicit Windows RID on macOS also skips the macOS shell.
For the preferred Native AOT output, run:

```powershell
dotnet publish GitHub.TeamApp.csproj -c Release -r win-x64 -p:PublishAot=true
```

Use `win-arm64` for ARM64 with the corresponding Visual Studio C++ tools installed.
The output directory `bin/Release/net10.0/win-x64/publish` contains
`github-team.exe` (the backend) and `GitHub Team App.exe` (the native window).
It also includes `WebView2.LICENSE.txt` and `WebView2.NOTICE.txt`.
Keep the entire directory together; double-click `GitHub Team App.exe` to launch.
It starts the adjacent `github-team.exe --desktop-managed` and owns its lifetime.
This internal flag always implies server-only mode, even alongside `--browser`.
The shell supplies a private inherited stdin pipe and writes `shutdown\n` when
the window closes. The backend reads one line after starting its server:
`shutdown` or EOF requests normal host shutdown; any other line logs an explicit
error and also shuts down, without interpreting or executing commands. There is
no network shutdown API. The reader does not keep the process alive. The shell
waits up to five seconds for shutdown, with its kill-on-close Windows Job Object
providing cleanup on timeout or shell crash.
Launching `github-team.exe` instead starts the server and then launches the
adjacent shell with separate `--url http://127.0.0.1:PORT --parent-pid PID`
arguments. Only the canonical loopback address and a positive parent PID are
accepted. `--browser` and `--no-browser` bypass the native window.

A non-AOT self-contained alternative is:

```powershell
dotnet publish GitHub.TeamApp.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false
```

Keep the additional .NET files in that portable directory. Neither form installs
WebView2 Runtime or GitHub CLI. `-p:BuildDesktopShell=false` skips both Windows and
macOS shell build/publish targets, including for backend-only browser development.
Both ordinary Windows builds and publishes compile the shell in Release and
install it in the corresponding backend output directory. To build it directly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/windows/build.ps1 -OutputDirectory C:\Apps\GitHubTeamApp -Architecture x64
```

The directory argument must be absolute; the architecture is `x64` or `arm64`,
using the corresponding native MSVC toolchain.
These are local, unsigned builds, not an installer or a signed distribution.

## Development

The xUnit tests run through Microsoft.Testing.Platform:

```sh
dotnet run --project tests/GitHub.TeamApp.Tests -- \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
node --test tests/browser/render.test.mjs
```

Linux CI runs the portable Windows shell policy tests through CMake/CTest.
Windows CI runs backend/launcher tests, the native shell's non-interactive
`--self-test`, and a Native AOT `win-x64` publish with executable, WebView2 notice,
and static-loader packaging checks.
These checks do not exercise an interactive WebView2 window. Windows desktop
runtime testing (startup, navigation, external links, and shutdown) remains a
separate manual step on a Windows machine with WebView2 Runtime installed.

## Attribution

Extracted from the Aspire team dashboard canvas and its standalone .NET port in
[microsoft/aspire](https://github.com/microsoft/aspire). Original copyright notices
and the MIT license are retained. This is an independent community application,
not an official GitHub product.

The GitHub mark is from [Primer Octicons](https://github.com/primer/octicons),
copyright GitHub Inc., used under the MIT license in
[`assets/Octicons.LICENSE.txt`](assets/Octicons.LICENSE.txt).
