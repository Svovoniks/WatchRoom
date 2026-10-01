# Test findings investigation — 1 October 2026

Reviewed `TEST-REPORT.html`, its saved VM evidence, current application and service code, and the published Windows binaries. No application fixes or service deployment were made. Existing workspace changes were preserved.

## 1. Clock estimation: confirmed defect, highest playback priority

`src/Watchroom.Core/RoomClient.cs:90–111` sends wall-clock timestamps and calculates RTT from `Wire.Now`, which is `DateTimeOffset.UtcNow`. It keeps only a strictly lower RTT for the entire connection. There is no sample expiry, clock-jump detection, or rejection of negative RTTs.

Two failure modes follow:

- A wall-clock change after a good measurement leaves the old offset in place until an even faster sample arrives. Equally fast valid samples are ignored.
- A backward jump during a ping can produce a negative RTT. That becomes the best sample; subsequent ordinary positive RTTs cannot replace it. A deterministic calculation using the exact production formula gave RTT −9,980 ms and offset +5,000 ms during a 10-second backward jump. A subsequent valid sample requiring +10,000 ms correction left +5,000 ms unchanged. This reproduces the algorithmic defect; the saved traces do not include ping send/receive measurements to prove this exact sequence occurred in the VM.

The guest trace contains offsets +656, +1,310, −5,113, +10,258, and +11,447 ms. The independent final clock probe bounds the guest lag at 5,405–7,235 ms. Its retained +11,447 ms offset exceeds that range by at least 4,212 ms, agreeing with the testers' finding.

Windows `MainWindow.xaml.cs:608–615` and the Mac player both use local wall time plus this offset to schedule playback and choose seek positions. Thus an incorrect offset can cause multi-second scheduling waits or seeks ahead of the actual room position.

Recommended implementation: measure RTT with a monotonic clock, correlate responses with outstanding probes, reject invalid/stale samples, expire the sample window, detect local wall-clock changes, and expose server time from a monotonic anchor. Capture ping timing and offset decisions for a repeatable VM comparison. Preserve server UTC timestamps in the wire protocol.

## 2. Windows runtime packaging: confirmed dependency omission

The published `artifacts/Watchroom/datachannel.dll` PE import table explicitly names `MSVCP140.dll`, `VCRUNTIME140.dll`, and `VCRUNTIME140_1.dll`. None of those three files is present in the current publish directory. `vcruntime140_cor3.dll`, bundled with .NET, is a differently named file and does not fulfill those imports.

The saved guest log records native load error `0x8007007E`. After the testers added these runtime files, the guest connected directly and read 32,768 bytes with a RIFF header. This is strong evidence for the dependency diagnosis.

`build.ps1`, `scripts/package.ps1`, and `packaging/Watchroom.iss` neither bundle these dependencies nor install/check the Visual C++ runtime. Self-contained .NET publishing covers the managed runtime but does not remove this native prerequisite.

Recommended implementation: deliberately distribute supported app-local runtime dependencies or install/detect the x64 Visual C++ Redistributable, matching the installer privilege model. Validate the packaged and portable distributions on a clean VM, and provide a specific native-load error before attempting a peer connection.

## 3. Public service failure: client behavior confirmed; backend cause unresolved

The exact reported message, “Room service unavailable. Please try again.”, is emitted by `sites/worker/index.js:199` for an unexpected exception. The worker logs that exception with `console.error`. Ordinary CAS contention instead returns “Room is busy. Try again.”; therefore the reported text does not establish contention as the cause.

`SitesRoomConnection.PollAsync` completes its incoming channel on any polling exception. `RoomClient.ReadLoop` then cancels the client and disposes the media peers. `SendLoop` likewise cancels on transport failure. A single transient service error therefore requires rejoining, even if the peer media connection was healthy.

During this investigation, a read-only request to the public `/health` endpoint returned HTTP 200 with `transport: http-poll`, and STUN/relay configuration reported false. The health route checks whether the DB binding exists; it does not execute a database query. This cannot establish database health or explain the earlier failure.

The provided artifacts do not contain the underlying worker exception. The backend cause requires deployed worker logs around the failed request; database errors or other unexpected exceptions remain possibilities, not established diagnoses.

Recommended implementation: log request correlation IDs and backend errors; retry transient GET polling failures within a bounded window using the same session and cursor. Keep authentication failures, removed sessions, and expired cursors terminal. Do not blindly retry playback/chat POSTs after ambiguous responses: they need idempotency protection to avoid duplicate effects. Recovery must respect the service's 60-second heartbeat expiry.

## 4. Remaining buffering: credible correction loop, not proven transport starvation

`MainWindow.xaml.cs:611–615` seeks whenever drift exceeds 1,200 ms, including repeated ticks while a seek is still settling. New revisions can also seek at differences over 200 ms. The `Buffering` handler at lines 124–128 treats cache below 10% as room buffering during playback, without distinguishing a corrective seek from insufficient media throughput. The server then pauses everyone and resumes when ready. These paths can reinforce one another when the clock estimate is wrong or a seek takes time.

The extra host playback interval contains 99 Playing and 21 Paused trace samples. That supports interrupted playback, but these samples are not a precise duration measure. No matched, trustworthy live host/guest timing exists for that run. The traces contain no per-range latency, buffered bytes, seek completion, or buffering-event timing, so they cannot determine how much interruption came from seek correction versus network/decoder behavior.

Recommended next step after clock repair: record monotonic seek request/completion timing, VLC buffering events, room ready/buffering transitions, and remote range latency. Serialize or debounce corrective seeks while they settle and reevaluate drift afterward. Verify planned seeks do not create a repeated room pause/resume loop while retaining pause behavior for actual starvation. Repeat with realistic audio/video, then internet and relay topologies.

## Validation and limits

- `./scripts/test.ps1`: Release build succeeded with zero warnings/errors; all 101 smoke checks passed, including native peer transfer, VLC remote playback/seek, admission, control permissions, buffering recovery, and revocation.
- `node --test tests/rooms.test.mjs` in `sites`: all four service tests passed.
- `node artifacts/vm-sync-test/investigate.mjs`: inspected saved clock/trace evidence, parsed actual native imports, and reproduced the clock formula failure. This diagnostic is saved in the ignored test-artifact directory.
- The existing smoke checks exercise local transport and protocol buffering transitions. They do not exercise VM wall-clock changes, transient HTTP polling recovery, or two live desktop players maintaining synchronization.
- No new host-to-VM playback run was performed; the existing VM/server/player state was left alone. Synchronization remains unverified. Paused convergence and a zero median do not justify a live synchronization pass.

Suggested order: fix runtime packaging and clock estimation, add bounded polling recovery and service diagnostics, then repeat the control/playback matrix and isolate remaining buffering. Keep the public-service outage diagnosis separate from the local synchronization failure.
