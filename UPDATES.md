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

Preferred release flow for agents: use `./scripts/ship.ps1` for the full release
and reserve manual commands for exception handling, inspection, or recovery. The
script already performs the work that would otherwise be repeated across many
steps: it selects the intended paths, runs in an isolated worktree, bumps the
version, executes the release checks and smoke suite, commits, pushes, tags, waits
for the tag workflow, verifies the public release and installer checksums, then
pulls `origin/master` into the local master checkout.

If you must fall back to the manual path, keep it brief and explicit:

1. Inspect `git status`, fetch `origin`, and inspect the latest GitHub release.
   Preserve unrelated changes. Stage only the intended source, tests, build files,
   and general documentation. Keep personal HTML investigations, library
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
   installer and checksums.
8. Synchronize local `master` with `origin/master` and confirm it contains the
   published commit. Preserve unrelated staged/unstaged edits, untracked files,
   and `sites` work. Report the version, release URL, Actions result, local sync
   result, and any validation limitations. Leave the user's installed app unchanged.

Use the scripted flow by default. The goal is to let the automation do the bulk
of the work while agents stay focused on explicit file selection, failure
triage, and release validation.

PowerShell checks (use `dotnet` instead of the workspace SDK path on other machines):

```powershell
.tools/dotnet/dotnet.exe restore Watchroom.slnx --locked-mode
.tools/dotnet/dotnet.exe run --project tests/Watchroom.UpdateChecks -c Release
.tools/dotnet/dotnet.exe run --project tests/Watchroom.Smoke -c Release -- --metadata-cache
.tools/dotnet/dotnet.exe run --project tests/Watchroom.DesktopChecks -c Release
```

When voice chat changes, also run `tests/Watchroom.DesktopChecks -- --voice-processing`; a fresh checkout builds
the voice processing libraries once, which needs Visual Studio's C++ tools and Rust (`cargo`).

When packaging or the updater changes, also run `./scripts/update-package-checks.ps1`.
The release workflow runs it on every packaged build.

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

## Shipping with the release script

Run `scripts/ship.ps1` in PowerShell 7 from a checkout at `origin/master`.
Select individual files explicitly; unrelated staged and unstaged changes are
preserved. The script applies only those files to an isolated Git worktree, bumps
the version, runs the release checks plus the full smoke suite in a fresh test
directory, commits, pushes `master`, and pushes an annotated version tag. It waits
for the tag workflow and verifies the public release, updater feed, installer size,
and SHA-256 against both GitHub and `SHA256SUMS.txt`. After public verification,
it pulls master into the checkout that owns that branch, even when shipping was
started from another worktree. If master has no checkout, it fast-forwards the
local branch without switching the current checkout.

Local work is saved in a recovery stash and under the ignored verification
directory before synchronizing a dirty master. The script merges the worktree
and index separately to preserve staged and unstaged edits, retains untracked
files, and leaves unrelated `sites` contents alone. Already-shipped changes do
not remain as duplicate local edits. Divergence or conflicting local work stops
the sync without resetting commits or overwriting files. The recovery stash is
retained, and the error explicitly distinguishes a verified public release from
an incomplete local sync. `-VerifyOnly` also runs this final synchronization step
so it can complete a previously interrupted ship.

```powershell
# Preview the selected paths and next patch version without changing files or publishing.
./scripts/ship.ps1 -Paths src/Watchroom.Desktop/MainWindow.xaml -Plan

# Ship those changes. Omit Version to choose the next patch automatically.
./scripts/ship.ps1 -Paths src/Watchroom.Desktop/MainWindow.xaml -Version 0.3.18 -Message 'Clarify playback language preferences'

# For multiple files, use a comma-separated PowerShell array.
./scripts/ship.ps1 -Paths 'src/Watchroom.Desktop/MainWindow.xaml','scripts/ship.ps1','scripts/ship-checks.ps1','scripts/ship-sync-checks.ps1','UPDATES.md' -Plan

# Recheck an already pushed tag after a timeout or network interruption.
./scripts/ship.ps1 -VerifyOnly -Version 0.3.18

# Offline safety checks for the script; no release is created.
./scripts/ship-checks.ps1
```

Use `-Sdk C:/path/to/dotnet.exe` when the SDK is outside this checkout. Otherwise
the script uses `.tools/dotnet/dotnet.exe` from this checkout or its main checkout,
then `dotnet` from PATH. Git authentication
must allow pushes to the public repository; public Actions and release verification
do not require a GitHub CLI installation or token. `-TimeoutMinutes` defaults to 60.
Verification records and downloaded installers remain under ignored `artifacts/`.
Release checkouts and fresh smoke data use short paths under the system temp
directory to keep SQLite paths within Windows limits; failed checkouts are retained at the printed path
for investigation. A failed workflow
stops the script; it never rewrites a tag, overwrites a published installer, or
installs the app locally. If master advances during checks, integrate it and rerun.

## Diagnosing room and playback failures

Playback snapshots distinguish requested Play/Pause from a temporary buffering
hold. Readiness messages from updated apps carry the movie ID and seek generation;
late readiness for an earlier seek is ignored. Explicit seeks supersede buffering
at the previous position. The guest bridge retains 16 MiB of recently read
movie data plus the read-ahead window; canceled seeks stop transfers that have no
remaining consumers. VLC's network cache is chosen per movie from the measured
round trip (1.5–5 s), so starting and seeking wait only for that short buffer.
Guest read-ahead is selected per movie from the saved **Settings →
Playback → Video to buffer ahead (seconds)** setting. It defaults to 30 seconds
and accepts whole seconds from 2 to 60. Once the first frames play, the bridge
prefetches that much video (estimated from the file's average bitrate plus 25%,
capped at 256 MiB) past the playback position with a few background reads; a
seek pauses read-ahead until playback advances from the new position. This is a
target, not a guarantee of outage coverage; bandwidth and bitrate affect the data
retained. Larger values use more memory but do not lengthen startup or seeking.
Saved changes apply to the next streamed video; local-file playback bypasses
network buffering.
Buffering recovery requires 750 ms of stable full cache.
Temporary peer losses request a replacement immediately while room discovery is
available. Six seconds without host control replies also starts recovery, checked
every two seconds. The guest retains its video input and cache, continues already
running playback from buffered data for up to the active buffer setting after
recovery starts, and retries interrupted media ranges for up to 45 seconds.
Playback pauses sooner if the native input starves, fails or ends, or if Pause
is pending; a paused input never starts merely because recovery begins. Restored connections
resynchronize playback and report readiness again. A failed native player input
is reopened when necessary; genuine host departure still clears unavailable media.
Participant departure alerts wait 30 seconds and are canceled when that peer's
control handshake returns, avoiding sounds and floating alerts for brief losses.
Diagnostics include command queueing, host receipt/acceptance, acknowledgement,
player application, and scoped readiness, making network delay distinguishable
from a playback hold. The `applied` event records issuing the native player action;
`native-time` and `settle-end` establish when the native position catches up.

Windows builds save connection and playback diagnostics automatically to
`%LOCALAPPDATA%\Watchroom\logs`. Settings offers **Open logs folder**. After
reproducing a failed watch session, close both apps and share the logs from both
devices, together with the approximate time of the failure.

`diagnostics.jsonl` contains timestamped structured events for app version,
room-service capabilities, peer connection states, control-channel negotiation,
guest departures, media loading, buffering, sync corrections, and transfer errors.
It excludes invitation codes, credentials, raw signaling, and movie contents.
Updated peers negotiate an independent connection for media so a congested movie
transfer cannot queue playback commands behind its SCTP traffic. Diagnostics
record `media-transport-isolated` and `media-transport-state` for this connection.
Peers without this optional capability retain the legacy media protocol.
The logger keeps four files of up to 5 MiB each and writes asynchronously.
`WATCHROOM_DIAGNOSTICS` still overrides the output directory for test runs.

## Build and updater behavior

GitHub Actions builds the Windows x64 app and installer on pushes, pull requests,
and manual runs. The workflow checks metadata caching, desktop interactions, and
update download verification before packaging. Successful builds retain installers
as workflow artifacts for 14 days.

Push a version tag such as `v0.2.1` to publish a GitHub Release. Tags must use
`vMAJOR.MINOR.PATCH`; the same version is embedded in the application and installer.
The workflow uploads `Watchroom-Setup-VERSION-win-x64.exe`, the file list
`Watchroom-files-VERSION-win-x64.txt` (SHA-256, size and path of every installed
file), update installers `Watchroom-Update-FROM-to-VERSION-win-x64.exe` from
each of the 40 previous stable releases, and `SHA256SUMS.txt` covering all of
them. Only tagged builds publish releases. Do not replace an existing release
with different bytes; publish a new version instead.

An update installer holds only the files that differ from release FROM, plus
deletions of files the new version dropped, so an update usually downloads a few
megabytes instead of the whole app with its .NET runtime, VLC and models.
`scripts/package-updates.ps1` builds them on CI from the earlier releases'
published file lists. The most recent release, and up to three releases from
before file lists were published, are instead installed from their published
installers (digest verified) into a scratch folder; the most recent one's
update installer is applied over that installation, and the build fails unless
the folder then matches the new build exactly and the installation matched its
file list. The script refuses to run outside CI because it installs releases
into the current user profile. An update installer has the same AppId, so the
uninstaller also removes the files it added; it refuses to run unless the
registered installation is FROM in the target folder.
`scripts/update-package-checks.ps1` exercises this offline with a fixture under
a separate test identity, without file associations or shortcuts, in a scratch
folder under `artifacts/`: file lists, a direct update, refusals, a two-step
chain run the way the app runs it, and uninstalling.

The Windows app checks GitHub's latest stable release 15 seconds after startup
and every six hours. Settings offers **Check for updates** and a saved preference
to disable automatic checks. Checks do not install anything. A newer release
shows an update card; **Later** hides that version until a manual check or a
different release.

**Update** streams the installer into the profile's `updates` folder, reports
progress, and verifies its length and GitHub-provided SHA-256 digest. Failed or
interrupted downloads never launch an installer. When the release has an update
installer from the running version, the updater downloads that one. An
installation older than the last 40 releases chains update installers through
intermediate releases (the cheapest chain among the last 100 releases) when the
chain is smaller than half the full installer; a short script in the `updates`
folder runs them one after another once the app has closed, only the last one
reopens Watchroom, and a failed step reopens the version installed by then.
Before using update installers the app checks its installed files against its
own release's published file list. If the files differ, any download fails
verification, or an installer cannot be started (antivirus software may hold
back a fresh download briefly, which is retried, or remove it), the full
installer is downloaded instead; update installers that ran without reaching a
version are not offered for it again. The update card shows the download size. After successful verification,
the per-user Inno Setup installer updates the installed app's directory, and
reopens Watchroom. Only the copy running from the installer-registered folder (or
the default folder when none is registered) updates itself; development builds and
copied folders disable update checks so they cannot take over the installation.
Library data, rooms, queues, and credentials remain outside the installation
directory. Updating closes playback and disconnects the current room.
Installation failures are handled by Inno Setup; its setup log is available for
diagnosis. The binaries currently have no publisher code-signing certificate.
Checksums protect against transfer corruption, not a compromised GitHub account.

Update downloads require a public releases repository. No GitHub token is stored
in the distributed app. `UpdateRepository` is an MSBuild property baked into the
Windows assembly; it defaults to `Svovoniks/WatchRoom`.

To test: `dotnet run --project tests/Watchroom.UpdateChecks -c Release`.
For isolated desktop tests set `WATCHROOM_DISABLE_UPDATES=1`; normal users do not
need this environment variable.
