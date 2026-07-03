# Validation Runner

Lightweight GitHub-hosted validation loop for packaged GameClient sandbox builds.

The workflow checks out GameClient configuration, finds submodule profiles marked `active_development: true`, asks the platform for the latest successful sandbox build for each profile and target, downloads the build folder, runs the packaged runtime xUnit harness, and sends aggregate pass/fail counts back to the platform.

Windows is strict and fails when a matching build or test pass fails. Linux is wired into the same runner, but the workflow currently treats a missing Linux sandbox artifact as blocked/non-failing until Linux sandbox builds are available.

Actions output is intentionally terse. `dotnet test` output is redirected to files and the log prints only profile-level totals.

## Required Configuration

Set repository secrets or variables:

- `PLATFORM_BASE_URL`: platform backend URL. If unset, the runner resolves the `cloud` tunnel from `https://battle-buddy-games.github.io/Platform/config.json`.
- `PLATFORM_AUTH_TOKEN`: bearer token accepted by the platform service-to-service auth.
- `GAMECLIENT_REPO_TOKEN`: token that can read `frostebite/GameClient` if the repository is private. If GameClient is public, this can be omitted.

The workflow uses `GITHUB_TOKEN` as the platform bearer token. `PLATFORM_AUTH_TOKEN`, `GH_TOKEN`, or `GITHUB_TOKEN` also work for local runs.

## Local Run

```bash
dotnet run --project src/ValidationRunner -- --gameclient ./GameClient --target windows
dotnet run --project src/ValidationRunner -- --gameclient ./GameClient --target linux --allow-missing-build true
```
