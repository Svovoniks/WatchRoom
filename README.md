# Watchroom

An additional macOS preview is available; see [Mac client setup and validation status](MAC-CLIENT.md).

A Windows desktop preview for watching local movies together. C# / WPF, LibVLCSharp, SQLite and native libdatachannel. Only the host needs the video file.

## Run the app

Open `artifacts/Watchroom/Watchroom.exe` after publishing, or build from source below. The self-contained publish includes the .NET runtime, libVLC, and app-local Microsoft Visual C++ x64 runtime dependencies; installing VLC or copying runtime DLLs separately is unnecessary.

1. On first launch, add movie, show or anime folders and click **Scan library**. You can skip this when only joining friends.
2. Browse the poster grid, open a series, then a season, then an episode. On Windows, selecting a movie or episode opens its own information page with artwork, metadata, and **Play locally** / **Watch together** actions. **Back to library** restores the same season, search, and filter.
3. Posters and missing overviews are fetched automatically after scanning, using TVmaze for series and Wikipedia for movies without a token. Existing artwork is preserved when filling missing overviews. Shows also fetch separate season posters and summaries, and episode titles, summaries, still images, air dates, and runtimes by season/episode number. Local artwork takes priority; missing episode information falls back to its season or show. Matched metadata sets the Movie/Show/Anime category and keeps it through rescans, overriding folder and filename guesses. Japanese animation (or an anime keyword on animated TMDB titles) goes into Anime; anime films remain standalone films. Ambiguous matches keep their guessed category until matched manually. Metadata is cached for seven days; **Fetch artwork and metadata** in Settings refreshes it immediately. Anime releases are recognized in Mixed libraries; unmatched anime can use AniDB's locally cached title index to find English aliases, then retry TVmaze. The index downloads at most daily; this is title matching, not AniDB's registered metadata API. An optional TMDB **API read access token** uses TMDB instead. Missing or ambiguous matches remain manual. Local PNG/JPEG artwork (`poster`, `folder`, `cover`, or a matching filename) takes priority. Put series artwork in the series folder and season artwork in its season folder.
4. For a room, configure a deployed HTTPS server in Settings. Create the room, copy its invitation, and admit friends when they join.
5. Host controls playback by default; **Allow shared controls** permits guests to play, pause and seek. Volume, audio and subtitle selections are personal for original media. Buffering pauses everyone and resumes when participants report ready.

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

The synchronization policy interpolates between VLC time events for at most 500 ms, uses bounded proportional rate correction (0.95–1.05), and seeks above 600 ms of per-client error. Seek settling waits for native timeline advancement with a two-second fallback. Room replay resets an ended native input before starting it, then waits for native readiness before applying the revision. The measured fixture target is a pair-position p95 of at most 500 ms; this is not a guarantee for arbitrary peers, codecs, displayed frames or audio.

`scripts/playback-retest/prepare.py` creates isolated instrumented copies without changing playback control flow. Its test driver uses `WATCHROOM_TEST_MEDIA` on the host. The VM matrix waits for guest readiness, records both clients' unified logs, and requests native frame snapshots at shared server times. `run-matrix.ps1` takes a credential **file path** through `-PasswordFile`; do not commit credentials. `analyze.py` reports full and revision-steady distributions, client target errors, unpaired outliers, pairing coverage, native read/seek diagnostics and control assertions. Reports retain startup/transition results separately rather than discarding them.

## Current implementation boundaries

- Original-file streaming and remote seeking work in automated same-machine tests. Public-network, forced-TURN and multi-machine synchronization targets are **not yet validated**.
- External `.srt`, `.ass`, and `.ssa` sidecars matching the video filename are shared with the room. Embedded subtitles, audio tracks and fonts are delivered as part of the original file. Full HEVC/AV1/ASS/PGS compatibility still needs a representative media corpus.
- Smaller copies can be prepared using a user-supplied FFmpeg executable. Preparation completes before streaming; this is **not live adaptive transcoding**. The current action prepares a 720p H.264/AAC MKV with copied subtitle tracks and attachments.
- The host manages the queue; its titles are shared with admitted guests. Guests can suggest titles from their own library or in chat. Only the host's selected movie is shared; browsing entire remote collections is not yet implemented.
- HTTP room coordination retries transient network/server failures for up to 30 seconds using the existing session. Updated services deduplicate retried commands. Expired, revoked, or lost sessions still require leaving/rejoining and obtaining host approval. Host disconnect closes the room.
- The UI has dark/light themes. The shell uses software rendering for reliable WPF/native-video composition; libVLC retains hardware decoding.
- No signed automatic updater or production code-signing credentials are configured. The Inno Setup build creates a per-user installer; public distribution must complete signing and third-party license/branding work.

These boundaries distinguish this working preview from the full release described in [the design plan](design-review.md).

## Data and privacy

Library/settings: `%LOCALAPPDATA%/Watchroom`, or `WATCHROOM_DATA` when set. The TMDB token is held only for the current app session. Poster searches send titles to TVmaze/Wikipedia, or TMDB when configured; movie files stay on the host. Disable automatic artwork in Settings to stop these lookups. The loopback media bridge uses an unguessable per-session path and serves authorized media IDs. Room signaling uses HTTPS/WebSockets; media uses WebRTC encryption, with TURN relay fallback when configured.

Room codes expire after six hours. Relay bandwidth and host upload increase with each viewer. A public server and a tested relay are necessary for dependable internet use.

See [third-party notices](THIRD-PARTY-NOTICES.md) and [deployment steps](deploy/README.md).
