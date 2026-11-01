# Builds and app updates

## Release checklist for agents

The public source and release repository is `Svovoniks/BlackPearl` (`Svovoniks/WatchRoom`
until it is renamed; see [Renaming from Watchroom](#renaming-from-watchroom)), and the release
branch is `master`. The updater uses its public `/releases/latest` endpoint.
Shipping is authorized when the user asks to ship an update. Complete the whole
flow below rather than stopping at a pushed branch, an open pull request, or a
workflow artifact.

Shipping means a reviewed pull request merged into `master`, then publishing and
verifying the GitHub release. Agents never push to `master` directly, and all agent
work happens in git worktrees (see AGENTS.md). Do not open, control, or update the
user's desktop app as part of shipping. Install a release locally only when the user
explicitly requests a local installation. Computer use is not part of this release
checklist.

1. **Open the pull request.** Commit the change on your worktree's branch. Write the
   description to a file outside the repository: what changed and why, intentional
   behavior changes, removals, or performance costs, and how it was tested. Then run
   `./scripts/ship.ps1 -Message '<release note>' -BodyFile <path>`.
2. **Get an independent review.** Ask a separate agent with fresh context to review
   the PR with the `review-pr` skill (`.claude/skills/review-pr/SKILL.md`), giving it
   the PR number and head commit. The reviewer posts its verdict on the pull request
   with `scripts/review-pr.ps1`. It blocks on bugs, performance regressions, and
   broken existing features unless the description says the change is intentional.
3. **Fix until approved.** Fix the blocking findings, commit, rerun
   `./scripts/ship.ps1` (no arguments are needed to update the same PR), and ask for
   a re-review of the new head with the earlier findings and your replies. If the
   same point is disputed twice, or after five rounds without approval, ask the user.
4. **Merge and release.** Run `./scripts/ship.ps1 -Merge`. Report the PR, version,
   release URL, Actions result, local sync result, and any validation limitations.

Use the scripted flow by default. The goal is to let the automation do the bulk
of the work while agents stay focused on the change, the review, failure triage,
and release validation.

## Shipping with the release script

Run `scripts/ship.ps1` in PowerShell 7 from your worktree, on a feature branch that
contains the latest `origin/master` and has no uncommitted tracked changes.

**Open or update a pull request** (default mode). The script rejects branches whose
diff from `origin/master` touches anything other than release source paths:
`sites`, `artifacts/`, `.tools/`, databases, credentials, local HTML reports,
generated media, top-level dotfiles such as `.gitignore`, and `.github/` outside
`workflows/` are refused. Change those by hand, outside this flow. It picks a version above both the latest public release
and `master`, and commits `Set version X` on the branch if needed. A branch that
already has such a version keeps it across review rounds. The script then runs the
release checks below plus the full smoke suite against a fresh test directory. It
runs them in a detached copy of the commit under the system temp directory, so
untracked files cannot affect the result and SQLite paths stay within Windows
limits. If the checks pass, it records the checked commit and pushes the branch. It
creates the pull request, or updates the existing one's title, description, and
release note. The release note is kept in the description under `### Release note`
and becomes the release commit message and tag message.

**Review.** `scripts/review-pr.ps1 -Pr N` checks out the head in a fresh temp
worktree and prints the description, changed files, CI state, and earlier verdicts.
`-Verdict approve|changes -Head <sha> -BodyFile <path>` posts the verdict as a PR
comment with a `blackpearl-review` marker for that exact commit. Every account here
is the repository owner, and GitHub does not let authors approve their own pull
requests, so the marker comment is the approval. Only markers from the owner,
members, or collaborators count. The newest one wins.

**Merge** (`-Merge`, optionally `-Pr N`). The script refuses unless all of these
hold:

- The pull request is open, not a draft, and from this repository into `master`.
- The release checks passed locally on its exact head.
- The head contains the current `origin/master`.
- The newest reviewer verdict approves that head.
- Its version is higher than the latest release and has no tag.

It waits for the `Build and release` workflow runs on the head to succeed. It then
squash-merges with GitHub's head-SHA guard and the title `Release BlackPearl X:
<release note>`. It confirms the merged tree is exactly the reviewed tree, then
tags and pushes `vX`. It deletes the merged branch, waits for the tag workflow, and
verifies the public release, updater feed, installer size, and SHA-256 against both
GitHub and `SHA256SUMS.txt`. Finally, it pulls `master` into the checkout that owns
that branch. If `master` has no checkout, it fast-forwards the local branch without
switching any checkout.

The script re-reads the verdict right before merging, so a rejection posted while CI
runs still stops the merge. If anything fails after the merge, the error names the
merged commit and the recovery step, because rerunning `-Merge` cannot help then.

**Without a release** (`-NoRelease`, only when the user asks not to release). Both
modes keep the version from `master`. They refuse a branch that changes it, and need
no release note. `-Merge -NoRelease` applies the same review, check, and CI gates.
It squash-merges with the PR title and synchronizes local `master`, with no tag or
release.

Any new commit, including a merge of `master` into the branch, needs another
`ship.ps1` run and a re-review, because approval covers one exact commit.

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
# Preview the branch's files and version without changing anything.
./scripts/ship.ps1 -Plan

# Check, push, and open the pull request.
./scripts/ship.ps1 -Message 'Clarify playback language preferences' -BodyFile $env:TEMP/pr-body.md

# After fixing review findings: check, push, and update the same pull request.
./scripts/ship.ps1

# Reviewer: check out, then post a verdict for the exact reviewed head.
./scripts/review-pr.ps1 -Pr 12
./scripts/review-pr.ps1 -Pr 12 -Verdict approve -Head <sha> -BodyFile $env:TEMP/review.md

# Preview the merge gates, then merge, tag, and verify the release.
./scripts/ship.ps1 -Merge -Plan
./scripts/ship.ps1 -Merge

# A change the user does not want released: same flow, no version bump, tag, or release.
./scripts/ship.ps1 -NoRelease -Title 'Tidy the shipping docs' -BodyFile $env:TEMP/pr-body.md
./scripts/ship.ps1 -Merge -NoRelease

# Recheck an already pushed tag after a timeout or network interruption.
./scripts/ship.ps1 -VerifyOnly -Version 0.3.18

# Offline safety checks for the scripts; no pull request or release is created.
./scripts/ship-checks.ps1
```

Use `-Sdk C:/path/to/dotnet.exe` when the SDK is outside the main checkout.
Otherwise the script uses `.tools/dotnet/dotnet.exe` from the worktree or the main
checkout, then `dotnet` from PATH. Pull requests, reviews, and merges use the GitHub
credential git already uses for pushes, read through `git credential fill`; it is
never printed. Public release verification stays unauthenticated. No GitHub CLI is
needed. `-TimeoutMinutes` defaults to 60. Verification records and downloaded
installers remain under ignored `artifacts/`. Failed check copies are retained at
the printed path for investigation. A failed workflow stops the script. It never
rewrites a tag, overwrites a published installer, or installs the app locally.

### Manual fallback

Use this only to recover when the scripts cannot run:

1. Work on a branch in a worktree. Keep personal HTML investigations, library
   snapshots, credentials, and `sites` changes out of commits.
2. Choose a version higher than the latest published stable release. Update
   `<Version>` in `Directory.Build.props` (the single source of the app version).
   Use three numeric components, such as `0.2.1`; the release tag must be exactly
   `v0.2.1`. The workflow rejects tags that disagree with the source version.
3. Run the checks below from the worktree root and resolve failures. For playback
   or room-networking changes, also run the full smoke suite against a fresh test
   directory. Do not reuse previous smoke data.
4. Push the branch, open a pull request, and get the reviewer's approval as above.
   Merge only the approved head. Then tag that exact merge commit with an annotated
   tag and push the tag. Never force-push or move an existing release tag.
5. Wait for the **tag's** `Build and release` Actions run to succeed. A branch or
   pull request build does not publish a release. The tag workflow packages the
   installers, uploads them to a draft release, then publishes it as the latest
   release. It runs the checks first unless a pull request run already passed them
   on the exact same tree, as it has for every merge from `ship.ps1`.
6. Verify without authentication that `/releases/latest` returns the expected
   tag, `draft=false`, and `prerelease=false`. Check that the Windows installer has
   the exact expected filename, a nonzero size, and a `sha256:` digest. Download
   the installer and verify its SHA-256 against the API digest and SHA256SUMS.txt.
   This step matters: a private or incomplete release cannot update users' apps.
7. Check that the public GitHub release page is available and links to the
   installer and checksums.
8. Synchronize local `master` with `origin/master` and confirm it contains the
   published commit. Preserve unrelated staged/unstaged edits, untracked files,
   and `sites` work. Leave the user's installed app unchanged.

PowerShell checks (use `dotnet` instead of the workspace SDK path on other machines):

```powershell
.tools/dotnet/dotnet.exe restore BlackPearl.slnx --locked-mode
.tools/dotnet/dotnet.exe run --project tests/BlackPearl.UpdateChecks -c Release
.tools/dotnet/dotnet.exe run --project tests/BlackPearl.Smoke -c Release -- --metadata-cache
.tools/dotnet/dotnet.exe run --project tests/BlackPearl.DesktopChecks -c Release
```

When voice chat changes, also run `tests/BlackPearl.DesktopChecks -- --voice-processing`; a fresh checkout builds
the voice processing libraries once, which needs Visual Studio's C++ tools and Rust (`cargo`).

When packaging or the updater changes, also run `./scripts/update-package-checks.ps1`.
The release workflow runs it on every packaged build.

Inspect Actions at https://github.com/Svovoniks/BlackPearl/actions and releases at
https://github.com/Svovoniks/BlackPearl/releases. Public REST endpoints work without
authentication; never print credentials when using authenticated APIs.

Public updater verification:

```powershell
$release = Invoke-RestMethod 'https://api.github.com/repos/Svovoniks/BlackPearl/releases/latest'
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
ship a higher version through a new pull request and leave the failed draft
unpublished. Never overwrite bytes for an already published version. For a
defective published version, release a higher patch version with the fix; the
updater refuses downgrades.

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
`%LOCALAPPDATA%\BlackPearl\logs`. Settings offers **Open logs folder**. After
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
`BLACKPEARL_DIAGNOSTICS` still overrides the output directory for test runs.

## Build and updater behavior

GitHub Actions runs the checks on pull requests, split across two runners: the
desktop suites and the full smoke suite. Pushes to `master`, version tags, and
manual runs build the Windows x64 app and installers, and skip the checks when a
pull request run already passed them on the same tree; otherwise the checks run
beside packaging and a tag publishes only after they pass. Successful builds
retain installers as workflow artifacts for 14 days.

Push a version tag such as `v0.2.1` to publish a GitHub Release. Tags must use
`vMAJOR.MINOR.PATCH`; the same version is embedded in the application and installer.
The workflow uploads `BlackPearl-Setup-VERSION-win-x64.exe`, the file list
`BlackPearl-files-VERSION-win-x64.txt` (SHA-256, size and path of every installed
file), update installers `BlackPearl-Update-FROM-to-VERSION-win-x64.exe` from
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
reopens BlackPearl, and a failed step reopens the version installed by then.
Before using update installers the app checks its installed files against its
own release's published file list. If the files differ, any download fails
verification, or an installer cannot be started (antivirus software may hold
back a fresh download briefly, which is retried, or remove it), the full
installer is downloaded instead; update installers that ran without reaching a
version are not offered for it again. The update card shows the download size. After successful verification,
the per-user Inno Setup installer updates the installed app's directory, and
reopens BlackPearl. Only the copy running from the installer-registered folder (or
the default folder when none is registered) updates itself; development builds and
copied folders disable update checks so they cannot take over the installation.
Library data, rooms, queues, and credentials remain outside the installation
directory. Updating closes playback and disconnects the current room.
Installation failures are handled by Inno Setup; its setup log is available for
diagnosis. The binaries currently have no publisher code-signing certificate.
Checksums protect against transfer corruption, not a compromised GitHub account.

Update downloads require a public releases repository. No GitHub token is stored
in the distributed app. `UpdateRepository` is an MSBuild property baked into the
Windows assembly; it defaults to `Svovoniks/BlackPearl;Svovoniks/WatchRoom`, a list of the
repository's names that the updater tries in order.

## Renaming from Watchroom

The app was called Watchroom, and its repository `Svovoniks/WatchRoom`. The code, installers
and release assets are now BlackPearl. What existing installations rely on keeps working:

- **Installations.** The installer keeps the same Inno Setup `AppId`, so it updates a Watchroom
  installation in place, in its existing `Programs\Watchroom` folder. It removes the old
  `Watchroom.exe` and `Watchroom.*.dll`, the Watchroom Start menu shortcut, and the
  Watchroom file-association registration. It relaunches the app when asked with either
  `/BLACKPEARLUPDATE=1` or the older `/WATCHROOMUPDATE=1`.
- **Data.** A library in `%LOCALAPPDATA%\Watchroom` stays there and stays in use; new
  installations use `%LOCALAPPDATA%\BlackPearl`. A TMDB key saved under the Watchroom
  credential name moves to the BlackPearl name when it is first read. Every `WATCHROOM_*`
  environment setting still applies when the matching `BLACKPEARL_*` one is not set, in the app
  and on the room server, and plugins receive both names.
- **Compatibility with older versions.** The single-instance lock, the peer control channel
  (`watchroom-control-v2`) and the `X-Watchroom-Request-Id` header keep their names, so
  BlackPearl and Watchroom still work together in rooms and on one library.
- **Updates.** Watchroom 0.3.90 and older look only for `Watchroom-Setup-*` and
  `Watchroom-Update-*` assets, and accept downloads only from URLs under
  `Svovoniks/WatchRoom`. While the repository still has that name, the release workflow also
  publishes each installer under its Watchroom name, and `ship.ps1` verifies those copies.
  BlackPearl tries `Svovoniks/BlackPearl` first, then `Svovoniks/WatchRoom`, and accepts
  downloads under either name.

To rename the repository:

1. Ship the first BlackPearl release while the repository is still `Svovoniks/WatchRoom`.
2. Wait until every installation that should keep updating runs that release or later. After
   the rename, GitHub lists downloads under `Svovoniks/BlackPearl`, which Watchroom 0.3.90 and
   older reject, so they can only be updated by running the full installer by hand.
3. Rename the repository on GitHub to `BlackPearl`. Never create another repository named
   `WatchRoom` under the account: GitHub would stop redirecting the old name.
4. Point local clones at the new name: `git remote set-url origin
   https://github.com/Svovoniks/BlackPearl.git`. The scripts accept either name, and pick the
   one that exists through `Use-Repository` in `scripts/github-pr.ps1`.
5. Optionally rename the `WATCHROOM_TMDB_API_KEY` Actions secret to
   `BLACKPEARL_TMDB_API_KEY`; the workflow reads either.

From then on releases carry only BlackPearl names, and the workflow stops publishing the
Watchroom-named copies by itself.

To test: `dotnet run --project tests/BlackPearl.UpdateChecks -c Release`.
For isolated desktop tests set `BLACKPEARL_DISABLE_UPDATES=1`; normal users do not
need this environment variable.
