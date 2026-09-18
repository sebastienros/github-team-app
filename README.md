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
