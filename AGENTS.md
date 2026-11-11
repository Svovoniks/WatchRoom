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

Launch with `./scripts/dev-launch.ps1` (`-Exe <path>` for a build in another
folder), not by starting the exe yourself. It closes only this checkout's dev build.
It refuses a build older than the sources, and it waits for the window, reporting a
crash at startup instead of success. It starts the build with self-update and
auto-download's Windows registration turned off.

- "Old development instance" never means the installed app (under
  `%LOCALAPPDATA%\Programs`). Never close the installed app without asking the
  user. If it has the library open, `dev-launch.ps1` stops and says so. Ask the
  user, or launch on a copy with `-Scratch`.
- Builds made here have no bundled TMDB key unless `BLACKPEARL_TMDB_KEY_FILE` was
  set at build time. On the user's library, such a build re-matches titles to
  TVmaze and Wikipedia as soon as anything refreshes metadata. This happened
  twice, to 3,305 files and to 34 movies. `dev-launch.ps1` refuses that case. For
  a change that touches no metadata, pass `-AllowKeyless`, then refresh, rescan,
  and identify nothing in that build. For anything else, use `-Scratch`.
- Anything you run for your own testing, such as timing launches, opening files,
  or driving the UI, goes on a scratch copy (`-Scratch`, or `-Data <copy>`). A
  file launched on the user's library opens in the user's running app.
- Report what the script printed: the build time, the commit, and the library it
  started on. "Started" is not "running".

## Check visual changes by looking at them

Passing checks do not show that a UI change looks right. Small visual bugs, a
pixel of jump or clipping, pass checks that compare element positions and still
show on screen. Before you report a UI change as done:

- Render the affected screens and look at them, zoomed in where detail matters.
  `tests/BlackPearl.DesktopChecks` renders the main screens with
  `-- --render-screens <output folder> <width> <height> [--scale 1.25]`. Point
  `BLACKPEARL_DATA` at a copy from `scripts/scratch-library.ps1`, which has the
  user's real posters and titles. Fixtures without artwork hid a header stretched
  thousands of pixels tall and an empty band under every Home row. Look at its
  `CLIPPED` lines as well. It may not exit on its own; stop it once the images
  are written. It draws at the display's own scaling (150% on the user's PC), or
  at the one `--scale` sets. A simulated scaling is close but not exact: text and
  elements created after it is set can still draw at the display's scaling.
- Render every state your change can be in, not only the resting one. Look at
  each that applies: hover, keyboard focus, pressed, selected with a real value,
  open menus, empty, offline or nothing installed, long and short text, a short
  window (600 DIP tall or less), and 100%, 125%, 150% (the user's), and 175%
  scaling.
  Users found these bugs in states nobody rendered: a filter pill showing
  `SearchChoice { Name = French, Code = fr }` once a value was chosen; a square
  focus ring on a round pill; a sidebar that hid Settings in short windows; a
  minimum height of 300 DIP that is 450 px at 150%.
- Drive each new or changed control once with real input: click it, click
  another one while it is open, and drag it. Menus that close with a fade kept
  the mouse captured, so a click on a second pill did nothing. That bug came
  back twice. Interaction bugs need a click, not a render.
- Popups and separate windows are drawn outside the main window, and layout checks
  do not see them. WPF caps a popup at 75% of the monitor's area. The player's
  overlay window does not inherit the text color. Measure the real window
  rectangle.
- Look again after every follow-up fix, however small. A fix for one visual bug
  can cause the next.
- For anything that animates, look at frames during the animation and at the
  moment it ends, not only the resting states before and after.
- Check what is drawn, not only where elements are laid out. An element can sit in
  the right place while its glyph or image is clipped, or lands a fraction of a
  pixel off. Rendered pixels (`RenderTargetBitmap`) show what the user sees.
- Check every common display scaling, not only the one your machine has. CI runs
  at 100% and the user's display at 150%, and each scaling rounds fractional
  sizes its own way. The toolbar text was 2 px off its line only at 150%, and the
  folded sidebar's icons jumped only at 125%. A check that reads drawn pixels
  belongs in `CheckScalings` (`tests/BlackPearl.DesktopChecks/Program.cs`). That
  runs it at 100%, 125%, 150%, and 175% on any machine. Line things up by
  measuring where they are drawn at the current scaling, not with fixed nudges
  tuned at one scaling.
- When a measurement differs from what you expected, find out why before loosening
  a tolerance. An unexplained number is often the bug itself: a folded sidebar
  measured 38.7 instead of 40 because of its 1 DIP border. Waving that off as
  rounding hid an icon jump and then clipped icons.
- When you write a check for a visual bug, confirm that it fails without the fix.

## Try it on real data

Fixtures prove that a bug stays fixed. They do not show that a feature works on
the user's library. Most bugs the user sent back had passed every fixture:
- a made-up English subtitle on a real MP4;
- a "Long night" filter with no movies, because runtimes were never saved;
- plugin results hidden that were visible on the site;
- a voice gate that let thuds through and then cut the user's quiet microphone;
- an "Add video" panel that froze at 4 GB on a library-sized list.

For changes to identification, metadata, track detection, grouping, plugins,
voice, or anything that walks the whole library:

- Run it on a copy: `./scripts/scratch-library.ps1` copies the library with its
  artwork, then prints how to point the app or a check at the copy. The copy's
  stored paths point at the copy, so nothing that runs on it can change the
  user's library or posters. Never point an experiment at the user's live data
  folder.
- Report before and after numbers from that run: titles matched, cards split,
  tracks identified, rows changed. Look at the changed rows, not only the totals.
  For identification, run the full corpus and the library copy, and report any
  name that worked before and fails now. Another narrow rule that fixes one naming
  family usually breaks a neighbour. That cost about 13 review rounds on one pull
  request and abandoned another.
- A build without the bundled TMDB key gets other results than the user's app. Say
  so when you report numbers from one.
- Agents cannot hear audio. For voice changes, measure with real recordings made
  the way the user actually talks and listens: their devices, routing, and levels,
  including quiet input and wireless output. Say plainly what you could not check.

## Before you report a change as done

- Check every other place the same pattern lives, and fix those too or list them.
  Fixes made only on the screen the user pointed at came back as "still broken"
  for dotted focus lines, clipped borders, the item count in Search, fullscreen
  corners, and the default English label. Search for the style, control, string,
  or rule, and name what you found in your report. A new mode or window, such as
  picture-in-picture, the promo, or the room panel, must keep what the old one did:
  hotkeys, dragging, next-episode autoplay, and voice controls.
- Back every claim with what you saw. A performance change needs a measured before
  and after number, or it is reported as unmeasured. Before you name a cause or
  call a process yours or not yours, check it. If you could not test something,
  such as Windows 10, two networks, real audio, or a real download, say that
  instead of "works".
- When the user says something is still broken, reproduce it yourself before the
  next attempt. Drive the real window with real input on a scratch copy. Each
  guessed fix that does not reproduce the bug costs the user another round.
- Before a fix, find the cause, not the first plausible component. Ask why the
  user's other tools behave differently, such as regular VLC or Discord. Do not
  answer from documentation when you can measure on this machine.
- A failure is "flaky" only when the same check fails on `master` on the same
  machine, or the cause is known. Load from another process or a stale folder is a
  cause. Name it.
- Keep the user's private details out of the repository and pull requests. That
  includes their server addresses, domains, paths, and library contents.

## Understand the request

- When a request could mean two things, ask. "The numbers look out of place" meant
  the Missing and Unmatched counts, not the item count.
- When the user points at a reference ("like VLC's button"), match all of its
  behavior. VLC's loop button has three states.
- Prefer a warning to a block when the user may know better. Prefer a visible,
  explained empty state to hiding a feature: a hidden hero image and a voice
  button that did nothing were both reported as bugs.
- Keep existing controls and screen space unless asked. Removed buttons, and
  headings or subtitles nobody asked for, were sent back.

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

Stay with a ship until it finishes. Do not end your turn while `ship.ps1`, its
release checks, CI, or `-Merge` are still running. Wait on them, and answer every
completion notice by reading the end of that output. A notice is never "no response
requested": twice, a failed tag build was left that way. Report once, after the
release is verified and local `master` is synchronized, or say exactly which
step failed.

The main desktop suite's startup gate ("startup library loading keeps processing
input", 350 ms) fails under load from other processes. The app's normal pause is
250 to 330 ms. `scripts/desktop-main-suite.ps1` reruns the suite when only that
gate fails, and lists the busiest other processes. A gate that fails on every
attempt is a real regression. Look at what your change runs on the UI thread at
startup.

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
