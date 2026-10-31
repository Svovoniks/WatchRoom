# Watchroom agent instructions

## Work in your own worktree

Do all work in a git worktree on its own branch. Never edit files in the main
checkout (`C:\Users\svovoniks\Desktop\WatchRoom`): it is the user's, and it often
holds their uncommitted work. Reading it is fine. If your harness has already put you
in a worktree, use that one. Otherwise, fetch `origin/master` without recursing into
submodules and create a worktree from it:

```powershell
git fetch --no-recurse-submodules origin master
git worktree add -b <short-topic-branch> ../WatchRoom-<topic> origin/master
```

A new chat does not establish that a worktree is current. Before you start, check
that your branch contains the latest `origin/master` and merge it if not. Never push
to `master` directly. Changes reach `master` only through a reviewed pull request
merged by `scripts/ship.ps1 -Merge`. Never reset or force-push another agent's or
the user's work. Remove worktrees you created once their pull request is merged or
abandoned. Leave harness-managed worktrees to the harness.

## Relaunch after app changes

The user has requested automatic relaunches after local app changes. Once the
latest development build and relevant checks succeed, gracefully close the old
development instance if running and launch the new build with the user's usual
library and settings. Do not ask for this authorization again. Use the actual
latest build, including when it was built to a separate output directory.

## Shipping updates

"Ship" means: pull request → independent review → fixes until approved → merge →
release. When the user asks to ship, publish, or release an app update, follow
[UPDATES.md](UPDATES.md). Do the bulk of the work through `scripts/ship.ps1`, not
manual git, version, tag, and verification commands:

1. Commit the intended changes on your worktree's branch. Write a pull request
   description to a file outside the repository. Cover what changed and why, any
   intentional behavior changes, removals, or performance costs, and how you tested.
   Then run `./scripts/ship.ps1 -Message '<release note>' -BodyFile <path>`. It
   bumps the version, runs the full release checks on a clean copy of the commit,
   pushes the branch, and opens the pull request.
2. Ask a separate agent with fresh context to review it, for example with
   `delegate_task`, `Agent`, or `spawn_agent`. Do not use a fork of yourself. Give
   it the PR number, the head commit, and the instruction to follow the `review-pr`
   skill in `.claude/skills/review-pr/SKILL.md`. Do not include your conclusions
   about the change.
3. Fix every blocking finding in your worktree, commit, and rerun
   `./scripts/ship.ps1`, which checks and pushes again and updates the same pull
   request. Then ask for a re-review of the new head. Give the reviewer its earlier
   findings and your reply to each one. If you dispute a finding, explain why in
   that reply rather than ignoring it. Repeat until the reviewer approves. If you
   and the reviewer disagree on the same point twice, or there is no approval after
   five rounds, stop and ask the user.
4. After approval, run `./scripts/ship.ps1 -Merge`. It merges only the approved
   head, after CI passes on it, and only if it still contains `master`. Then it tags
   the release, waits for the tag build, verifies the public release and the
   unauthenticated updater feed, and synchronizes the user's local `master`.

If the user wants a change landed without a new release, pass `-NoRelease` to both
`ship.ps1` runs. The PR then keeps `master`'s version, and the merge creates no tag
or release. The review and fix loop is the same. Use it only when the user says not
to release.

Shipping is authorized for the whole flow above, including posting review comments
and merging the approved pull request. Do not add confirmation prompts for an
already authorized release. A pushed branch, an open pull request, or a successful
workflow artifact alone does not ship an update. Shipping does not authorize
opening, controlling, or updating the user's desktop app. Verify the public GitHub
release page and assets, and do not use computer use for release verification.
Install locally only when the user explicitly requests it.

The public release repository is `Svovoniks/WatchRoom`; its release branch is
`master`. Windows x64 installers are built by `.github/workflows/build.yml`.
Never move an existing version tag or overwrite an installer for a published
version. Use a higher version for every shipped change.

Keep local investigation reports, personal library snapshots, credentials,
`artifacts/`, `.tools/`, and generated media out of pull requests. The `sites`
submodule is an independently deployed room service; do not include it when
releasing the desktop app. Preserve unrelated local changes.

Use the installed .NET SDK, or `.tools/dotnet/dotnet.exe` from the main checkout.
Fix check failures before asking for review. Reserve manual commands for
exceptions, inspection, or recovery only.

After the public release and updater feed are verified, `ship.ps1 -Merge`
synchronizes local `master` with `origin/master`, including the published commit,
in the checkout that owns `master`. It preserves unrelated local work and `sites`
changes, and keeps a recovery snapshot if synchronization fails. If the release is
verified but local synchronization failed, report exactly that. Complete the local
synchronization before reporting the shipping workflow as finished.
