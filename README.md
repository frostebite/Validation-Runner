# Validation Runner

Lightweight GitHub-hosted validation loop for packaged GameClient sandbox builds.

The workflow checks out GameClient configuration, finds submodule profiles marked `active_development: true`, asks the platform for the latest successful sandbox build for each profile and target, downloads the build folder, runs the packaged runtime xUnit harness, and sends aggregate pass/fail counts back to the platform.

Windows is strict and fails when a matching build or test pass fails. Linux is wired into the same runner, but the workflow currently treats a missing Linux sandbox artifact as blocked/non-failing until Linux sandbox builds are available.

Actions output is intentionally terse. `dotnet test` output is redirected to files and the log prints only profile-level totals.

## Platform Connectivity

The platform is reachable from GitHub-hosted runners only via a Cloudflare Quick Tunnel, whose hostname rotates on every restart (see `WebPlatform/github-pages-deploy/config.json`). A per-profile network-level failure (DNS resolution, connection refused, timeout, or a 502/503/504 response) is retried up to 3 times with exponential backoff before being counted. If it still fails, the profile is reported as `platform_unreachable` rather than `failures` in the per-line and summary output, so a run that failed because the platform itself was unreachable is distinguishable at a glance from a run where packaged runtime tests actually failed. Both still fail the job overall (exit code 1) — this only changes how the failure is labeled, not whether it's reported as green.

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
