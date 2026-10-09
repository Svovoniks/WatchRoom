# Watchroom

An additional macOS preview is available; see [Mac client setup and validation status](MAC-CLIENT.md).

A Windows desktop preview for watching local movies together. C# / WPF, LibVLCSharp, SQLite and native libdatachannel. Only the host needs the video file.

## Run the app

Open `artifacts/Watchroom/Watchroom.exe` after publishing, or build from source below. The self-contained publish includes the .NET runtime, libVLC, and app-local Microsoft Visual C++ x64 runtime dependencies; installing VLC or copying runtime DLLs separately is unnecessary.

1. On first launch, choose **Add my videos** to set up folders and scan your library, or **Join a friend** to enter an invitation. Only the host needs video files.
2. Browse the poster grid, open a series, then a season, then an episode. On Windows, selecting a movie or episode opens its own information page with artwork, metadata, and **Play locally** / **Watch together** actions. **Back to library** restores the same season, search, and filter.
3. Artwork and metadata are fetched automatically after scanning. Configurable providers try TMDB (with an optional API key or token), TVmaze, and Wikipedia in priority order. NFO files and locked fields take priority. Settings offer fill missing, refresh text, and replace downloaded artwork, plus language, refresh interval, and optional missing/upcoming episodes. Shows and seasons have stable identities; filename aliases in one show folder stay together, while same-name remakes stay separate. See [metadata and library behavior](METADATA.md) for numbering, duplicate versions, NFO support, and migration details.
4. For a room, configure a deployed HTTPS server in Settings and choose **Save changes**. Create the room, copy its invitation, and admit friends when they join. Settings drafts, including the TMDB key, apply when saved; **Cancel** restores saved preferences.
5. Host controls playback and room tracks by default; **Allow shared controls** permits guests to play, pause, seek and change tracks. Guests follow the host's audio and subtitle selections; volume remains personal. Buffering pauses everyone and resumes when participants report ready.
6. Open **Host library / Guest views** in the room panel. The host chooses shared category collections and grants each admitted guest **Browse**, **Manage queue**, and **Start videos** permissions. Guests can search titles/seasons, edit a shared room queue, and start a title or the next video. The host's saved queues remain available separately.

In Windows **Settings → Playback**, set a default subtitle priority and add rules for specific audio languages. For example, Japanese audio can use `English, Russian, off`, while English audio uses `off`. The default list applies to audio languages without a rule. Names and codes are accepted; blank lists keep the video's original defaults. Saved preferences apply to the next video, and manual subtitle choices last for that video.

Library metadata, resized posters, queue edits, start requests and browsing previews travel directly over WebRTC; catalog transfers have a separate channel from control messages and video ranges. The host preview shows the exact ordered titles on each guest's current page, automatically matching its rows, columns and grid dimensions while scaling the entire page to fit. Gold outlines show hover and blue outlines show selection. Search, filters and selection are visible to the host, and guests see an explicit notice. This is a reconstructed library view. Browsing presence expires after connection loss; the activity tab retains 200 room actions. The host can revoke permissions immediately and undo room queue edits.

Room playback uses a dedicated, reliable WebRTC control channel alongside the media channel. The host owns playback revisions, readiness, queue and chat state, and guests synchronize to the host clock. Guest commands go to the host for authorization and distribution to every participant. Sites (or the local server) handles room registration, admission, signaling and discovery heartbeats; playback commands do not pass through it. Established peer playback continues if discovery becomes unavailable, but new joins require discovery. All participants must use the updated app; incompatible control channels time out with an update/rejoin message. A dropped host connection pauses guests, and the host must reopen the room before they rejoin.

Run `dotnet run --project tests/Watchroom.Smoke -c Release -- --direct-controls` from the repository root to test the actual Sites worker and native peer channels with two guests, blocked media reads, clock differences and a discovery outage (requires Node.js on PATH).

The app includes a default HTTPS room-service address. Its availability and public-network playback must be validated separately before release. An HTTP localhost address supports testing on this computer. See [server deployment](deploy/README.md).

Local playback remembers your position and volume. Details offer **Resume** and **Start over**; a **Return to player** strip keeps playback reachable while browsing. Playback options include an editable, persistent queue with episode labels, reordering, removal, and automatic advancement. Room invitation and chat actions appear when connected to a room.

Search covers the whole library, including when browsing a season. Missing files have an **Unavailable** filter and disabled playback actions. **Correct title** works without a metadata token and survives rescanning. Folder categories can be edited in place; folder changes trigger scanning, and an active scan can be cancelled while keeping videos already indexed.

## Build and test

Requires the .NET 10 SDK on Windows x64. This workspace has a local SDK in `.tools/dotnet`.

Publishing downloads and verifies Microsoft's signed Visual C++ runtime and restores a pinned WiX extraction tool into `.tools`. The payload hash is pinned in `scripts/copy-native-runtime.ps1`; if Microsoft's download changes, verify and update the hash deliberately. The extracted runtime is included in both portable and installer builds. `native-runtime.json` records its version and source.

```powershell
./build.ps1
./scripts/test.ps1
./build.ps1 -Publish
./scripts/package.ps1 -SkipPublish
```

Run the local coordination service with `./scripts/run-server.ps1`. Open two app instances using separate `WATCHROOM_DATA` directories to test host and guest independently. Tests generate their own media and never scan personal folders.

The smoke executable validates SQLite scans, byte ranges and authorization, real native WebRTC transfers, remote LibVLC AVI playback and seeking, room admission, host-only playback, automatic buffering recovery, and revocation. It starts a temporary loopback server and shuts it down afterward.

## Playback diagnostics and VM retesting

Set `WATCHROOM_DIAGNOSTICS` to an isolated directory before starting the app to write `diagnostics.jsonl`. Logging is off by default and disk writes run off the UI/native callback threads. Events include playback revisions, raw and briefly interpolated positions, rate-setting results, seeks and settling completion, buffering, range-read latency, clock correction, and dropped-log counts. Separate directories are required for separate app instances.

The default synchronization policy retains the 1,200 ms seek threshold and ±3% rate correction. Set `WATCHROOM_SYNC_EXPERIMENTAL=1` to test the tighter policy: interpolate between VLC time events for at most 500 ms, use bounded proportional rate correction (0.95–1.05), and seek above 600 ms of per-client error. The tighter policy met the measured H.264/AAC public-room target but caused repeated corrections on the original silent AVI; it is therefore opt-in. Seek settling waits for native timeline advancement with a two-second fallback. Room replay resets an ended native input before starting it, then waits for native readiness before applying the revision. The measured fixture target is a pair-position p95 of at most 500 ms; this is not a guarantee for arbitrary peers, codecs, displayed frames or audio.

`scripts/playback-retest/prepare.py` creates isolated instrumented copies without changing playback control flow. Its test driver uses `WATCHROOM_TEST_MEDIA` on the host. The VM matrix waits for guest readiness, records both clients' unified logs, and requests native frame snapshots at shared server times. `run-matrix.ps1` takes a credential **file path** through `-PasswordFile`; do not commit credentials. To test the experimental policy, set `WATCHROOM_SYNC_EXPERIMENTAL=1` before launching the host and pass `-ExperimentalSync` to the matrix for the guest. `analyze.py` reports full and revision-steady distributions, client target errors, unpaired outliers, pairing coverage, native read/seek diagnostics and control assertions. Reports retain startup/transition results separately rather than discarding them.

Room AVI playback selects VLC's `avformat` demuxer because its native AVI demuxer exposes coarse read-ahead time over HTTP. The updated investigation and same-machine default/experimental retests are in [AVI-INVESTIGATION.md](AVI-INVESTIGATION.md). Host-to-VM validation of this change remains pending.

## Current implementation boundaries

- Original-file streaming and remote seeking work in automated same-machine tests. A Windows host/VM public-room H.264/AAC run met the experimental 500 ms p95 pair-position target. Silent AVI still fails that target; forced-TURN and wider multi-machine/media coverage remain unvalidated.
- External `.srt`, `.ass`, and `.ssa` sidecars matching the video filename are shared with the room. Embedded subtitles, audio tracks and fonts are delivered as part of the original file. Full HEVC/AV1/ASS/PGS compatibility still needs a representative media corpus.
- Smaller copies can be prepared using a user-supplied FFmpeg executable. Preparation completes before streaming; this is **not live adaptive transcoding**. The current action prepares a 720p H.264/AAC MKV with copied subtitle tracks and attachments.
- Guest library access defaults off per person. Hosts can share up to 10,000 titles in category collections; the shared room queue holds 50 entries. Saved host queues remain separate. Library browsing reveals metadata/thumbnails; video bytes remain authorized only for the selected video and subtitles. Older clients retain their existing room controls without the guest library feature.
- HTTP room coordination retries transient network/server failures for up to 30 seconds using the existing session. Updated services deduplicate retried commands. Expired, revoked, or lost sessions still require leaving/rejoining and obtaining host approval. Host disconnect closes the room.
- The UI has dark/light themes. The shell uses software rendering for reliable WPF/native-video composition; libVLC retains hardware decoding.
- Windows update checks and a per-user GitHub installer are available; see [builds and updates](UPDATES.md). Publisher code-signing credentials are not configured.

These boundaries distinguish this working preview from the full release described in [the design plan](design-review.md).

## Data and privacy

Library/settings: `%LOCALAPPDATA%/Watchroom`, or `WATCHROOM_DATA` when set. On Windows, **Save changes** stores the TMDB key in Windows Credential Manager; clearing it and saving removes it. Poster searches send titles to TVmaze/Wikipedia, or TMDB when configured; movie files stay on the host. Disable automatic artwork in Settings to stop these lookups. The loopback media bridge uses an unguessable per-session path and serves authorized media IDs. Room signaling uses HTTPS/WebSockets; media uses WebRTC encryption, with TURN relay fallback when configured.

Room codes expire after six hours. Relay bandwidth and host upload increase with each viewer. A public server and a tested relay are necessary for dependable internet use.

See [third-party notices](THIRD-PARTY-NOTICES.md) and [deployment steps](deploy/README.md).
