# Watchroom agent instructions

## Shipping updates

When the user asks to ship, publish, or release an app update, follow
[UPDATES.md](UPDATES.md), including its release checklist and verification steps.
A source push or successful workflow artifact alone does not ship an update.
Finish by verifying the published release and the unauthenticated updater feed.

The public release repository is `Svovoniks/WatchRoom`; its release branch is
`master`. Windows x64 installers are built by `.github/workflows/build.yml`.
Never move an existing version tag or overwrite an installer for a published
version. Use a higher version for every shipped change.

Keep local investigation reports, personal library snapshots, credentials,
`artifacts/`, `.tools/`, and generated media out of release commits. The `sites`
submodule is an independently deployed room service; do not include its unrelated
working changes when releasing the desktop app. Preserve unrelated local changes.

Use the installed .NET SDK, or `.tools/dotnet/dotnet.exe` in this workspace.
Run the checks listed in UPDATES.md; fix failures before tagging. Do not add
confirmation prompts for an already authorized release.
