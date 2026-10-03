# Builds and app updates

## Release checklist for agents

The public source and release repository is `Svovoniks/WatchRoom`, and the release
branch is `master`. The updater uses its public `/releases/latest` endpoint.
Shipping is authorized when the user asks to ship an update; complete the steps
below rather than stopping at a source commit or workflow artifact.

Shipping means publishing and verifying the GitHub release. Do not open, control,
or update the user's desktop app as part of shipping. Install a release locally
only when the user explicitly requests a local installation. Computer use is not
part of this release checklist.

1. Inspect `git status`, fetch `origin`, and inspect the latest GitHub release.
   Preserve unrelated changes. Stage the intended source, tests, build files, and
   general documentation explicitly. Keep personal HTML investigations, library
   snapshots, credentials, and unrelated `sites` submodule changes out of commits.
2. Choose a version higher than the latest published stable release. Update
   `<Version>` in `Directory.Build.props` (the single source of the app version).
   Use three numeric components, such as `0.2.1`; the release tag must be exactly
   `v0.2.1`. The workflow rejects tags that disagree with the source version.
3. Run the checks below from the repository root and resolve failures. For
   playback or room-networking changes, also run the full smoke suite against a
   fresh test directory. Do not reuse previous smoke data.
4. Commit the intended changes. Push the release commit to `master`, or merge an
   approved PR there if repository protections require it. Ensure the tag points
   to that exact commit, then push the annotated version tag. Never force-push or
   move an existing release tag.
5. Wait for the **tag's** `Build and release` Actions run to succeed. A branch
   build does not publish a release. The tag workflow builds, tests, packages,
   uploads assets to a draft release, then publishes it as the latest release.
6. Verify without authentication that `/releases/latest` returns the expected
   tag, `draft=false`, and `prerelease=false`. Check that the Windows installer has
   the exact expected filename, a nonzero size, and a `sha256:` digest. Download
   the installer and verify its SHA-256 against the API digest and SHA256SUMS.txt.
   This step matters: a private or incomplete release cannot update users' apps.
7. Check that the public GitHub release page is available and links to the
   installer and checksums. Report the version, release URL, Actions result, and
   any validation limitations. Leave the user's installed app unchanged.

PowerShell checks (use `dotnet` instead of the workspace SDK path on other machines):

```powershell
.tools/dotnet/dotnet.exe restore Watchroom.slnx --locked-mode
.tools/dotnet/dotnet.exe run --project tests/Watchroom.UpdateChecks -c Release
.tools/dotnet/dotnet.exe run --project tests/Watchroom.Smoke -c Release -- --metadata-cache
.tools/dotnet/dotnet.exe run --project tests/Watchroom.DesktopChecks -c Release
```

Example release after checks, using an example version (replace it with the
version actually chosen; do not rerun an existing tag):

```powershell
git add <explicit intended paths>
git commit -m 'Release Watchroom 0.2.1 with back navigation'
git push origin HEAD:master
git tag -a v0.2.1 -m 'Watchroom 0.2.1: back and swipe navigation'
git push origin v0.2.1
```

Inspect Actions at https://github.com/Svovoniks/WatchRoom/actions and releases at
https://github.com/Svovoniks/WatchRoom/releases. With GitHub CLI authentication,
`gh run list --repo Svovoniks/WatchRoom` and `gh run watch RUN_ID --repo
Svovoniks/WatchRoom --exit-status` can monitor the run. Public REST endpoints also
work without authentication; never print credentials when using authenticated APIs.

Public updater verification:

```powershell
$release = Invoke-RestMethod 'https://api.github.com/repos/Svovoniks/WatchRoom/releases/latest'
$release | Select-Object tag_name,draft,prerelease
$release.assets | Select-Object name,size,digest,browser_download_url
```

For a local installer build, publish first using the source version, then call
`./scripts/package.ps1 -SkipPublish`. Both the workflow and package script derive
the version from `Directory.Build.props`; the package script refuses to package an
app whose embedded version does not match. Do not call Inno Setup directly.

If a run fails before publication, inspect its logs and fix the failure. A draft
release is not shipped. If a failure leaves a draft behind, finish that draft only
when it still corresponds to the same validated commit and installer; otherwise
ship a higher version and leave the failed draft unpublished. Never overwrite
bytes for an already published version. For a defective published version,
release a higher patch version with the fix; the updater refuses downgrades.

## Diagnosing room and playback failures

Windows builds save connection and playback diagnostics automatically to
`%LOCALAPPDATA%\Watchroom\logs`. Settings offers **Open logs folder**. After
reproducing a failed watch session, close both apps and share the logs from both
devices, together with the approximate time of the failure.

`diagnostics.jsonl` contains timestamped structured events for app version,
room-service capabilities, peer connection states, control-channel negotiation,
guest departures, media loading, buffering, sync corrections, and transfer errors.
It excludes invitation codes, credentials, raw signaling, and movie contents.
The logger keeps four files of up to 5 MiB each and writes asynchronously.
`WATCHROOM_DIAGNOSTICS` still overrides the output directory for test runs.

## Build and updater behavior

GitHub Actions builds the Windows x64 app and installer on pushes, pull requests,
and manual runs. The workflow checks metadata caching, desktop interactions, and
update download verification before packaging. Successful builds retain installers
as workflow artifacts for 14 days.

Push a version tag such as `v0.2.1` to publish a GitHub Release. Tags must use
`vMAJOR.MINOR.PATCH`; the same version is embedded in the application and installer.
The workflow uploads `Watchroom-Setup-VERSION-win-x64.exe` and `SHA256SUMS.txt`.
Only tagged builds publish releases. Do not replace an existing release with
different bytes; publish a new version instead.

The Windows app checks GitHub's latest stable release 15 seconds after startup
and every six hours. Settings offers **Check for updates** and a saved preference
to disable automatic checks. Checks do not install anything. A newer release
shows an update card; **Later** hides that version until a manual check or a
different release.

**Update** streams the installer into the profile's `updates` folder, reports
progress, and verifies its length and GitHub-provided SHA-256 digest. Failed or
interrupted downloads never launch an installer. After successful verification,
the per-user Inno Setup installer updates the running app's directory, and
reopens Watchroom. Library data, rooms, queues, and credentials remain outside the
installation directory. Updating closes playback and disconnects the current room.
Installation failures are handled by Inno Setup; its setup log is available for
diagnosis. The binaries currently have no publisher code-signing certificate.
Checksums protect against transfer corruption, not a compromised GitHub account.

Update downloads require a public releases repository. No GitHub token is stored
in the distributed app. `UpdateRepository` is an MSBuild property baked into the
Windows assembly; it defaults to `Svovoniks/WatchRoom`.

To test: `dotnet run --project tests/Watchroom.UpdateChecks -c Release`.
For isolated desktop tests set `WATCHROOM_DISABLE_UPDATES=1`; normal users do not
need this environment variable.
