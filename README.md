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

Use the repository dropdown in the header to switch dashboards. It shows each
repository's name and its GitHub owner avatar, with a generic repository icon
when no image is available. The cog beside it opens the compact repository
management list. Search GitHub using a detected account, or enter an
`OWNER/REPO` name and save it. Search uses the selected account's GitHub host,
including GitHub Enterprise Server (GHES); arbitrary API URLs are not accepted.
The account must already be authenticated through GitHub CLI or a supported
environment credential. **Accounts** manages credentials separately from the
saved repository list.

Select a saved repository row to open its dashboard. Opening the already
selected repository returns to its dashboard without changing the selection
or starting another sync. The management page's account dropdown uses the same
avatars and names as the header and initially selects the current repository's
account (otherwise an active account or the first available credential).
Both dropdowns support arrow keys, Home/End, first-letter navigation, and
Escape to close; Tab continues to the next control.

Review, Issues, Ship, and Health show **one repository at a time**.
Counts, notifications, actions, and refreshes belong to
that repository and its assigned account; repositories are never aggregated.
Removing a saved repository removes only local configuration, not anything on
GitHub. There are no preselected repositories or assumed team members.

Settings accepts an optional release/milestone filter and a list of GitHub team
member logins. **Maximum open items** defaults to **200** and accepts integers
from **1 to 10000**. It bounds each selected repository's issue or PR queue to
the most recently updated open items before full details are fetched. Review
and Ship use the same PR window. An empty release means no release filter. These view/filter
preferences are shared across projects; cached data is keyed by their values.
Azure pipelines are explicitly added to the selected repository and are shown
only there. Legacy account-based repository lists are migrated without dropping
saved preferences, choosing the first repository of an active account (otherwise
the first saved repository). The migration is persisted on the next settings
write. Pass an existing data directory with `--data-dir` to migrate its settings.

## Appearance and application identity

**Settings > Appearance** uses standard switches. **Follow system appearance**
is on by default and follows OS appearance changes while the app is running.
Turn it off to enable the **Dark mode** switch: on selects Dark, off selects
Light. Turning off automatic switching keeps the current theme. While following
the system, the disabled Dark mode switch reflects the effective OS appearance.
Manual Light and Dark remain fixed until changed, regardless of the OS setting.
The selection applies and saves immediately, independently of the other Settings
fields; Cancel does not undo it. Failed saves restore the previous theme and show
an error beside the switches. Existing settings without an appearance use System.

Appearance is stored as `appearance` in the existing `preferences.json`, shared
by native windows and browsers using the same backend/data directory. It is not
stored in origin-bound localStorage: native launches can use different loopback
ports without losing the preference. The server prefixes the blocking `theme.js`
response with the saved setting, applying it before the document body renders.
Native shells reveal their first window after the page loads, avoiding an
unthemed blank webview during startup.
Preference events synchronize open clients without replacing their unsaved form
fields. Appearance does not change GitHub filters or dashboard cache keys.

The macOS shell synchronizes window appearance and WKWebView background with the
selected theme. It reports OS appearance separately from the window override so
switching back to System cannot get stuck in a forced Light/Dark mode. Windows
WebView2 observes OS changes through its media query; the shell updates the
content background and dark title bar from same-origin appearance messages.
Windows title-bar support depends on the OS version; unsupported older builds
retain native chrome, and native Windows menus follow Windows rather than the
web theme.

Octo is the app's own identity, not the GitHub provider mark. The approved artwork
has pointed ears, an inset feline face with pill eyes, and four curled tentacles.
`assets/octo.svg` contains the shared, unmodified mark geometry; its `#octo` group
inherits `currentColor` in both the loading header and rendered header. The
header mark sits on a circular accent background: rose `#B11F4B` with a white
mark in Light, and pink `#FD8EA1` with a charcoal `#292929` mark in Dark, using
the existing accent and surface tokens.
`assets/octo-dock.svg` is the approved pink tile used for the browser favicon.
Provider icons and account/repository avatars retain their provider meaning.

### Rebuilding native icons

`desktop/icons/octo-1024.png` is the exact approved 1024-pixel tile raster and the
authoritative native build input. Keep it and the approved Dock SVG in sync when
intentionally revising the artwork; builds never redraw it or substitute a font.
No external SVG renderer, ImageMagick, Python, or machine-specific font is needed.

- macOS `desktop/macos/build.sh` uses the shell's `--write-icons SOURCE DIRECTORY`
  mode to resample with Core Graphics into exact-pixel 16, 32, 128, 256 and 512
  point representations at 1x and 2x. Apple's `iconutil` packages `AppIcon.icns`,
  referenced by the existing `Info.plist`. Pixel dimensions do not depend on the
  build machine's display scale. MSBuild tracks the PNG and ICNS in incremental
  builds.
- Windows `desktop/windows/create-icon.ps1` resamples the same PNG with built-in
  System.Drawing and writes a multi-representation, alpha-preserving ICO at
  16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels. CMake tracks the source raster;
  the existing `app.rc` embeds the generated icon. Window/taskbar icons select
  the appropriate size on startup and DPI changes.

## Local cache and privacy

### Incremental GitHub synchronization

The first load selects up to `maxOpenItems` open issues or pull requests, ordered
by `updatedAt` descending with item number descending as the stable tie-breaker.
The default is 200, including for legacy preferences without this setting.
Invalid settings requests or persisted values are explicitly rejected, not
clamped or silently reset. Lightweight GraphQL inventory pages use
`first = min(pageSize, remaining)`; only the selected items are then hydrated
with full details. Because GitHub supports only one ordering field, timestamp
ties crossing the window boundary require additional lightweight metadata reads
before selecting the highest numbers. These extra reads never hydrate older
out-of-window items; unusually large tie groups can still consume extra requests.

Inventory and hydration progress totals describe the bounded target, not the
whole repository. Delta pagination reports the number of changes read without
an estimated total or an open-item cap.
`dashboard.itemScope` exposes `{ limit, loaded, totalOpen, limited }`, using
GitHub's real open-item count so the UI can explain an intentional window such
as 200 of 10,000. Reaching the configured limit is not an incomplete-sync error.
Each lane displays its selected items newest first; existing classification and
the Review queue's separate attention-based selection limit still apply.
A separate
raw item store in `repository-items-v1` beneath the application's data directory
survives restarts. It is keyed by canonical GitHub host/repository, account
identity, item kind, and effective item cap, not release, team, or presentation filters. Review and
Ship share the same PR records; Issues has its own store. No cache is written
inside the source repository. Raw and rendered keys both include the effective
cap, so old unbounded caches cannot bypass the new default. The version-2 raw
envelope also stores `totalOpen`.

Subsequent refreshes read GitHub's repository issues REST feed with `state=all`
and `since` the previous successful sync timestamp, overlapping by two minutes.
This timestamp filters by last update, not creation. The feed includes PRs
(identified by `pull_request`) as well as issues, including closed items. It is
read with `sort=updated&direction=asc&per_page=100` through every page, independently
of the open-item cap.
Every refresh authoritatively selects the current newest-open window: older
records are dropped, newer items admitted, and gaps from closures, deletions,
or transfers refilled. New, reopened, and changed **selected** items are hydrated
in GraphQL batches by repository and number; historical/out-of-window REST
changes cannot trigger unbounded full hydration. Overlapping changes whose
timestamps are already covered by the cached item do not trigger another
full-detail request. PR-specific details are hydrated in GraphQL batches rather
than by using the dedicated PR-list endpoint, which has no `since` filter.
Changes to linked issues/PRs also refresh the cached items referencing them.
Classification, counts, notifications, and lanes are recomputed from the raw
records using the current preferences, rather than patched in place.

This is **not a delta feed for every GitHub entity**. CI, reviews, thread
resolution, and mergeability do not reliably change a PR's `updatedAt`.
Every PR refresh therefore also reads open-PR live state for its window and
patches these fields locally. PRs selected from lightweight boundary-tie
metadata receive a targeted live-state read if their full details can be reused.
Issues use a lightweight open-issue inventory. Health remains a
separate current provider fetch. Nested connections retain the dashboard's
existing bounded selections (for example, 60 reviews/threads and 10 linked
issues); this is not an archive of every nested event.

A full-detail reconciliation of the selected window runs on the next refresh
after six hours for changes missed by the REST feed, including new links not yet
represented in cached records. Known linked-item changes refresh referring
items only within the selected window; bounded nested connections can omit
links, and this is not a full-repository relationship index. Initial inventory
pagination is followed by a catch-up delta; changes to that item kind cause the
window to be selected again before hydration.
GitHub does not offer a transactionally consistent snapshot across these API
calls, so concurrent edits can appear on the next refresh or reconciliation.
Pagination is bounded at 2,000 pages per query/feed; repeated cursors/pages,
provider errors, malformed records, and safety-limit exhaustion are explicit
failures, never a silently complete raw cache.

The committed cursor is the start of the sync (the first response's provider
`Date` when available, otherwise the local start time), not its finish. A
failed/cancelled fetch or hydration leaves the prior records and cursor intact.
Successful syncs advance the cursor even when no items changed; the next sync,
including after a restart, reads that persisted cursor rather than the rendered
dashboard's `fetchedAt` timestamp.
Per-key synchronization prevents overlapping project/view refreshes from
overwriting a newer raw snapshot. Versioned records, cursor, and reconciliation
time are committed together by atomic replacement with user-only Unix file
permissions. Cache filename hashes are noncryptographic indexes; the exact
identity is verified inside each envelope. Corrupt raw entries produce a warning
and are rebuilt. ETags are not required.

### Rendered snapshots and privacy

Successful dashboard data is cached on disk. Startup and project/view switching
read the matching cache immediately, without waiting for authentication or
network discovery, then refresh in the background. Sync progress appears in a
floating popup without moving the dashboard. Completion and data-freshness messages
automatically close after six seconds; hovering or focusing a popup pauses its
timeout. The limited-window notice and refresh errors also appear in **Notifications**
with **Change limit** and **Retry** actions, and remain there until resolved or
dismissed. Closing their popup does not dismiss the notification. These notices
follow the displayed repository and view; routine success messages do not fill
the notifications list. A failed refresh retains the last complete matching
snapshot. Late responses cannot replace a different selected project,
and actions are resolved against the browser's current canonical snapshot.

The separate rendered cache is scoped by canonical host/repository, assigned account, view, and
data-affecting filters. It is versioned and atomically replaced, with user-only
file permissions on Unix. Corrupt or incompatible cache entries are reported and
refetched; corrupt preferences are reported without overwriting them.
Credentials are never serialized, but cached repository data may contain private
titles, descriptions, usernames, and CI metadata. Protect the data directory
accordingly. Removing a repository does not erase previously cached files.

Preferences and both durable caches share the OS application-data directory,
resolved using `Environment.SpecialFolder.LocalApplicationData`:

| Platform | Default directory |
| --- | --- |
| macOS | `~/Library/Application Support/GitHub/TeamApp` |
| Windows | `%LOCALAPPDATA%\GitHub\TeamApp` |
| Linux | `$XDG_DATA_HOME/GitHub/TeamApp`, or `~/.local/share/GitHub/TeamApp` when unset |

`GITHUB_TEAM_APP_HOME` overrides this default directory.
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
For appearance regression checks, open Settings in a browser/native shell:
verify Follow system appearance tracks both OS transitions, the manual Dark mode
switch ignores them, and re-enabling System immediately uses the current OS theme. Reload and restart
on a different loopback port to check persistence and the initial header/theme.
Changing appearance must retain unsaved fields, and a failed save must expose an
error and restore the previous selection. Check both themes' header/favicons and
the native icon at normal and high DPI.

## Attribution

Extracted from the Aspire team dashboard canvas and its standalone .NET port in
[microsoft/aspire](https://github.com/microsoft/aspire). Original copyright notices
and the MIT license are retained. This is an independent community application,
not an official GitHub product.

The previous GitHub application mark came from
[Primer Octicons](https://github.com/primer/octicons), copyright GitHub Inc.
Its MIT notice is retained in [`assets/Octicons.LICENSE.txt`](assets/Octicons.LICENSE.txt).
The application now uses the approved Octo artwork described above.
