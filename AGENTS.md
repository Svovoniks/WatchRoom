# BlackPearl agent instructions

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

## Check visual changes by looking at them

Passing checks do not show that a UI change looks right. Small visual bugs, a
pixel of jump or clipping, pass checks that compare element positions and still
show on screen. Before you report a UI change as done:

- Render the affected screens and look at them, zoomed in where detail matters.
  `tests/BlackPearl.DesktopChecks` renders the main screens with
  `-- --render-screens <output folder> <width> <height>`; point `BLACKPEARL_DATA`
  at a scratch data folder. It may not exit on its own; stop it once the images
  are written.
- Look again after every follow-up fix, however small. A fix for one visual bug
  can cause the next.
- For anything that animates, look at frames during the animation and at the
  moment it ends, not only the resting states before and after.
- Check what is drawn, not only where elements are laid out. An element can sit in
  the right place while its glyph or image is clipped, or lands a fraction of a
  pixel off. Rendered pixels (`RenderTargetBitmap`) show what the user sees.
- Check at the user's display scaling. Fractional sizes round differently there.
- When a measurement differs from what you expected, find out why before loosening
  a tolerance. An unexplained number is often the bug itself: a folded sidebar
  measured 38.7 instead of 40 because of its 1 DIP border. Waving that off as
  rounding hid an icon jump and then clipped icons.
- When you write a check for a visual bug, confirm that it fails without the fix.

## Shipping updates

"Ship" means: pull request → independent review → fixes until approved → merge →
release. When the user asks to ship, publish, or release an app update, follow
[UPDATES.md](UPDATES.md). Do the bulk of the work through `scripts/ship.ps1`, not
manual git, version, tag, and verification commands:

1. Commit the intended changes on your worktree's branch. Write a pull request
   description to a file outside the repository. Cover what changed and why, any
   intentional behavior changes, removals, or performance costs, and how you tested.
   Then run `./scripts/ship.ps1 -Message '<release note>' -BodyFile <path>`. It
   bumps the version, pushes the branch, opens the pull request, and starts the full
   release checks on a clean copy of the commit in the background.
   `./scripts/ship.ps1 -Status` shows them, the workflow, and the review verdict.
2. Ask a separate agent with fresh context to review it, for example with
   `delegate_task`, `Agent`, or `spawn_agent`. Do not use a fork of yourself. Ask
   right away; the checks run during the review. Give it the PR number, the head
   commit, and the instruction to follow the `review-pr` skill in
   `.claude/skills/review-pr/SKILL.md`. Do not include your conclusions
   about the change.
3. Fix every blocking finding, and any failed check, in your worktree, commit, and
   rerun `./scripts/ship.ps1`, which pushes again, updates the same pull request,
   and checks the new head. Then ask for a re-review of the new head. Give the
   reviewer its earlier findings and your reply to each one. If you dispute a finding, explain why in
   that reply rather than ignoring it. Repeat until the reviewer approves. If you
   and the reviewer disagree on the same point twice, or there is no approval after
   five rounds, stop and ask the user.
4. After approval, run `./scripts/ship.ps1 -Merge`. It merges only the approved
   head, after CI and the release checks pass on it, and only if it still contains
   `master`. Then it tags the release, waits for the tag build, verifies the public release and the
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

The public release repository is `Svovoniks/BlackPearl` (named `Svovoniks/WatchRoom`
until it is renamed; see "Renaming from Watchroom" in UPDATES.md); its release branch is
`master`. Windows x64 installers are built by `.github/workflows/build.yml`.
Never move an existing version tag or overwrite an installer for a published
version. Use a higher version for every shipped change.

Keep local investigation reports, personal library snapshots, credentials,
`artifacts/`, `.tools/`, and generated media out of pull requests. The retired
Sites room service may remain as an untracked `sites` folder in the main
checkout; keep it out of pull requests. Preserve unrelated local changes.

Use the installed .NET SDK, or `.tools/dotnet/dotnet.exe` from the main checkout.
Fix check failures before asking for review. Reserve manual commands for
exceptions, inspection, or recovery only.

After the public release and updater feed are verified, `ship.ps1 -Merge`
synchronizes local `master` with `origin/master`, including the published commit,
in the checkout that owns `master`. It preserves unrelated local work and keeps a recovery snapshot if synchronization fails. If the release is
verified but local synchronization failed, report exactly that. Complete the local
synchronization before reporting the shipping workflow as finished.
