# Watchroom

An additional macOS preview is available; see [Mac client setup and validation status](MAC-CLIENT.md).

A Windows desktop preview for watching local movies together. C# / WPF, LibVLCSharp, SQLite and native libdatachannel. Only the host needs the video file.

## Run the app

Open `artifacts/Watchroom/Watchroom.exe` after publishing, or build from source below. The self-contained publish includes the .NET runtime and libVLC; installing VLC separately is unnecessary.

1. On first launch, add movie, show or anime folders and click **Scan library**. You can skip this when only joining friends.
2. Browse the poster grid, open a series, then a season, then an episode. On Windows, selecting a movie or episode opens its own information page with artwork, metadata, and **Play locally** / **Watch together** actions. **Back to library** restores the same season, search, and filter.
3. Posters are fetched automatically after scanning, using TVmaze for series and Wikipedia for movies without a token. Anime releases are recognized in Mixed libraries; unmatched anime can use AniDB's locally cached title index to find English aliases, then retry TVmaze. The index downloads at most daily; this is title matching, not AniDB's registered metadata API. An optional TMDB **API read access token** uses TMDB instead. Missing or ambiguous matches remain manual. Local PNG/JPEG artwork (`poster`, `folder`, `cover`, or a matching filename) takes priority. Put series artwork in the series folder and season artwork in its season folder.
4. For a room, configure a deployed HTTPS server in Settings. Create the room, copy its invitation, and admit friends when they join.
5. Host controls playback by default; **Allow shared controls** permits guests to play, pause and seek. Volume, audio and subtitle selections are personal for original media. Buffering pauses everyone and resumes when participants report ready.

The default localhost server address only supports testing on this computer. There is no hosted Watchroom service yet. See [server deployment](deploy/README.md).

## Build and test

Requires the .NET 10 SDK on Windows x64. This workspace has a local SDK in `.tools/dotnet`.

```powershell
./build.ps1
./scripts/test.ps1
./build.ps1 -Publish
./scripts/package.ps1 -SkipPublish
```

Run the local coordination service with `./scripts/run-server.ps1`. Open two app instances using separate `WATCHROOM_DATA` directories to test host and guest independently. Tests generate their own media and never scan personal folders.

The smoke executable validates SQLite scans, byte ranges and authorization, real native WebRTC transfers, remote LibVLC AVI playback and seeking, room admission, host-only playback, automatic buffering recovery, and revocation. It starts a temporary loopback server and shuts it down afterward.

## Current implementation boundaries

- Original-file streaming and remote seeking work in automated same-machine tests. Public-network, forced-TURN and multi-machine synchronization targets are **not yet validated**.
- External `.srt`, `.ass`, and `.ssa` sidecars matching the video filename are shared with the room. Embedded subtitles, audio tracks and fonts are delivered as part of the original file. Full HEVC/AV1/ASS/PGS compatibility still needs a representative media corpus.
- Smaller copies can be prepared using a user-supplied FFmpeg executable. Preparation completes before streaming; this is **not live adaptive transcoding**. The current action prepares a 720p H.264/AAC MKV with copied subtitle tracks and attachments.
- The host manages the queue; its titles are shared with admitted guests. Guests can suggest titles from their own library or in chat. Only the host's selected movie is shared; browsing entire remote collections is not yet implemented.
- Reconnection is manual: leave/rejoin the invitation and obtain host approval again. Host disconnect closes the room.
- The UI has dark/light themes. The shell uses software rendering for reliable WPF/native-video composition; libVLC retains hardware decoding.
- No signed automatic updater or production code-signing credentials are configured. The Inno Setup build creates a per-user installer; public distribution must complete signing and third-party license/branding work.

These boundaries distinguish this working preview from the full release described in [the design plan](design-review.md).

## Data and privacy

Library/settings: `%LOCALAPPDATA%/Watchroom`, or `WATCHROOM_DATA` when set. The TMDB token is held only for the current app session. Poster searches send titles to TVmaze/Wikipedia, or TMDB when configured; movie files stay on the host. Disable automatic artwork in Settings to stop these lookups. The loopback media bridge uses an unguessable per-session path and serves authorized media IDs. Room signaling uses HTTPS/WebSockets; media uses WebRTC encryption, with TURN relay fallback when configured.

Room codes expire after six hours. Relay bandwidth and host upload increase with each viewer. A public server and a tested relay are necessary for dependable internet use.

See [third-party notices](THIRD-PARTY-NOTICES.md) and [deployment steps](deploy/README.md).
