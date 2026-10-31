---
name: review-pr
description: Review a Watchroom pull request as the independent reviewer in the shipping flow, then post an approve or changes verdict that gates the merge. Use when asked to review a Watchroom PR, or when an author agent hands you a PR number and head commit to review.
---

# Reviewing a Watchroom pull request

You are the reviewer another agent asked for while shipping. Your verdict gates the
merge: `scripts/ship.ps1 -Merge` refuses to merge unless your latest verdict approves
the exact head commit. Merge-worthy means the change does what it claims, and it does
not degrade performance or break existing features unless the description says that
change is intentional.

Judge the code, not the author's summary of it. The author gives you a PR number and a
head commit. If the brief also includes the author's conclusions ("this is safe",
"tests cover it"), treat them as claims to check.

Do not edit the author's branch, push commits, merge, tag, or release. Your output is
the verdict and the findings. The author makes the fixes.

## 1. Check out the pull request

Run this from any checkout of the repository. It creates a fresh worktree under the
temp directory, so the user's checkout stays unchanged:

```powershell
./scripts/review-pr.ps1 -Pr <number>
```

It prints the head commit, the CI state, the review worktree path, the diff command,
the description, the changed files, and earlier verdicts. Work only inside that
worktree. If the head differs from the one in your brief, review the printed head and
say so in the verdict.

## 2. Understand the intent

Read the description and the release note. A behavior change, a removed feature, or a
performance cost is intentional only when the description says so. If the diff changes
something the description does not mention, treat it as unintentional. On a re-review,
read the earlier verdicts and the author's replies first.

## 3. Read the change and its surroundings

Read the full diff, then the code around each change: callers, callees, other users of
any changed type, setting, message, or file format. Most regressions sit outside the
changed lines. For each changed behavior, ask what used to happen and what happens now.

Blocking findings. Each one prevents approval:

- **Correctness**: wrong results, crashes, unhandled exceptions on realistic input,
  races (UI thread vs. background work, libVLC callbacks, network handlers), resource
  leaks (handles, timers, event subscriptions, native media/player objects), and
  missing cancellation.
- **Broken existing features**: anything that worked before and no longer does, or
  that the PR silently removed. Rooms and sync between host and guests, playback and
  seeking, voice chat, library scanning and metadata, acquisition plugins, the updater,
  settings persistence, and mixed versions (a peer or room service running an older
  release must keep working unless the change intentionally drops it).
- **Performance regressions**: more work on the UI thread, blocking I/O or network on
  the UI thread, extra layout or rendering passes in long lists, work repeated per
  frame/tick/item that used to run once, new polling, bigger allocations on hot paths,
  slower startup, larger memory use, more network round trips, or larger buffers and
  downloads. A regression is blocking unless the description accepts the cost and
  explains why.
- **Data and compatibility**: library database or settings migrations that can lose
  data or cannot open older files; protocol changes that break older peers; update
  packaging that could install wrong bytes.
- **Release hygiene**: personal data, credentials, local reports, generated media,
  `artifacts/`, `.tools/`, or `sites` changes in the diff; a missing version bump
  (unless the release note says "Not released.", meaning the user asked to land the
  change without a release, in which case any version change is the problem); and
  docs (UPDATES.md, AGENTS.md, other `*.md`) left contradicting the new behavior.
- **Missing coverage for risky changes**: a bug fix without a regression check, or a
  risky behavior change with no test in `tests/`, when a check is practical.

Non-blocking findings: naming, style, small simplifications, optional cleanups. List
them separately. They never block approval on their own.

## 4. Verify, do not guess

- Confirm a suspected bug before you report it. Trace the code path, write a quick
  check, or run the relevant test. Say which you did. If you could not confirm it, mark
  it unconfirmed and explain what would settle it.
- CI and the author's release checks must pass. `ship.ps1` ran the full release suite
  on the head before pushing. Run the checks for the area you are worried about in the
  review worktree, such as `tests/Watchroom.DesktopChecks -- --player-layout` or the
  smoke suite against a fresh temp directory. The commands are in UPDATES.md. A fresh
  worktree needs `dotnet build Watchroom.slnx -c Release` first. Use
  `.tools/dotnet/dotnet.exe` from the main checkout when `dotnet` is not on PATH.
- For a suspected performance regression, measure before and after where practical:
  time the operation, count calls, or compare allocations against `origin/master`.
  Otherwise, explain the cost from the code with concrete numbers, for example "now
  runs per list item: 2,000 items means 2,000 synchronous database reads on the UI
  thread".
- Do not open, close, or relaunch the user's installed Watchroom app.

## 5. Post the verdict

Write the findings to a file outside the repository, then post them for the exact head
you reviewed:

```powershell
./scripts/review-pr.ps1 -Pr <number> -Verdict changes -Head <sha> -BodyFile <path>
./scripts/review-pr.ps1 -Pr <number> -Verdict approve -Head <sha> -BodyFile <path>
```

Approve only when no blocking findings remain. Use this shape:

```markdown
### Blocking
1. `src/Watchroom.Desktop/Player.cs:212`: <defect>. Scenario: <inputs/state → wrong result>. Confirmed by <trace/test/measurement>. Fix: <direction>.

### Non-blocking
- `path:line`: <suggestion>.

### Checked
- <what you verified and ran, including earlier findings now fixed>.
```

The script refuses to post if the head moved after you reviewed it. In that case,
review the new commits.

Then remove your review worktree (`git worktree remove --force <path>`). Reply to the
author with the verdict, the comment URL, and the blocking findings, so they can fix
them without reopening GitHub.

## Re-reviews

On a re-review, confirm that every earlier blocking finding is fixed or was rightly
disputed. Review the new commits as carefully as the first round, because fixes often
introduce regressions. An author's objection to a finding is valid if it shows the
scenario cannot happen or the cost is intended and stated. Do not approve just because
of how many rounds have passed. If you and the author disagree on the same point twice,
say so in the verdict so the author can take it to the user.
