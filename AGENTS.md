# Watchroom agent instructions

## Shipping updates

When the user asks to ship, publish, or release an app update, follow
[UPDATES.md](UPDATES.md), including its release checklist and verification steps.
Prefer the automated release flow in `scripts/ship.ps1` and the offline safety
check in `scripts/ship-checks.ps1`; agents should do the bulk of the work through
those scripts instead of manually repeating git, version, tag, and verification
commands. A source push or successful workflow artifact alone does not ship an
update. Finish by verifying the published release and the unauthenticated updater
feed. Shipping does not authorize opening, controlling, or updating the user's
desktop app as part of shipping. Verify the public GitHub release page and
assets; do not use computer use for release verification. Install locally only
when the user explicitly requests it.

The public release repository is `Svovoniks/WatchRoom`; its release branch is
`master`. Windows x64 installers are built by `.github/workflows/build.yml`.
Never move an existing version tag or overwrite an installer for a published
version. Use a higher version for every shipped change.

Keep local investigation reports, personal library snapshots, credentials,
`artifacts/`, `.tools/`, and generated media out of release commits. The `sites`
submodule is an independently deployed room service; do not include its unrelated
working changes when releasing the desktop app. Preserve unrelated local changes.

Use the installed .NET SDK, or `.tools/dotnet/dotnet.exe` in this workspace.
Run the checks listed in UPDATES.md; fix failures before tagging. Prefer the
scripted path for the whole release and reserve manual commands for exceptions,
inspection, or recovery only. Do not add confirmation prompts for an already
authorized release.
