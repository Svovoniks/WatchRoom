# Watchroom

A Windows desktop preview for watching local movies together. C# / WPF, LibVLCSharp, SQLite and native libdatachannel. Only the host needs the video file.

## Run the app

Open `artifacts/Watchroom/Watchroom.exe` after publishing, or build from source below. The self-contained publish includes the .NET runtime and libVLC; installing VLC separately is unnecessary.

1. On first launch, add movie, show or anime folders and click **Scan library**. You can skip this when only joining friends.
2. Browse the poster grid, select a title, and choose **Play locally** or **Watch together**. Series group into an episode selector.
3. For automatic artwork, enter a TMDB **API read access token** in Settings and enable automatic matching. Ambiguous matches remain manual. You can also use `poster.jpg`, `folder.jpg`, a matching `.jpg`, or **Use local poster** without an API token.
4. For a room, configure a deployed HTTPS server in Settings. Create the room, copy its invitation, and admit friends when they join.
5. Host controls playback by default; **Allow shared controls** permits guests to play, pause and seek. Volume, audio and subtitle selections are personal for original media. Buffering pauses everyone and resumes when participants report ready.
6. In the room, open **Host library / Guest views**. The host chooses shared collections and grants each admitted guest **Browse**, **Manage queue**, and/or **Start videos** access. Guests can search titles and seasons, add/remove/reorder queue entries, and start a title or the next queued video. **Play now** asks the guest before replacing the current video.

Library metadata, resized poster thumbnails, queue edits, start requests, activity and browsing previews travel over WebRTC between the host and each guest. Catalog transfers use a separate channel from control messages and video ranges. The room server still handles admission, connection signaling, playback timing and buffering. A TURN relay can carry peer traffic when a direct connection is unavailable.

The host's **Guest views & access** tab mirrors each guest's exact page of titles. The grid automatically adapts to the guest's window and reports its columns, rows, dimensions and ordered visible IDs; the host scales the complete page to fit its preview. Gold outlines show hovered titles and blue outlines show selected titles. Search/filter state and selection are visible to the host; guests see an explicit notice. This is a reconstructed library view, not screen capture. Browse presence expires after connection loss. The activity tab keeps the last 200 room actions, and the host can undo queue edits or revoke permissions immediately.

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

The smoke executable validates SQLite scans, byte ranges and authorization, real native WebRTC transfers, remote LibVLC AVI playback and seeking, room admission, host-only playback, automatic buffering recovery, and revocation. It also checks peer catalogs, queue revisions and retries, guest hover updates, pending-start revocation, and automatic video starts. It starts a temporary loopback server and shuts it down afterward. The desktop checks render isolated WPF fixtures without opening desktop windows, verifying adaptive grids, complete host previews, hover/selection highlights and catalog revocation; preview PNGs are written to `artifacts/library-ui`.

## Current implementation boundaries

- Original-file streaming and remote seeking work in automated same-machine tests. Public-network, forced-TURN and multi-machine synchronization targets are **not yet validated**.
- External `.srt`, `.ass`, and `.ssa` sidecars matching the video filename are shared with the room. Embedded subtitles, audio tracks and fonts are delivered as part of the original file. Full HEVC/AV1/ASS/PGS compatibility still needs a representative media corpus.
- Smaller copies can be prepared using a user-supplied FFmpeg executable. Preparation completes before streaming; this is **not live adaptive transcoding**. The current action prepares a 720p H.264/AAC MKV with copied subtitle tracks and attachments.
- The host shares category collections (Movie, Show, Anime or Mixed), up to 10,000 titles per room. Guest access is explicitly granted per person and defaults off. Room queues hold up to 50 entries and live on the host; the local solo queue remains separate. Browsing shares metadata and thumbnails, while video-byte access remains limited to the selected video and its subtitles. Old clients retain the original playback flow but cannot use the peer library feature.
- Reconnection is manual: leave/rejoin the invitation and obtain host approval again. Host disconnect closes the room.
- The UI has dark/light themes. The shell uses software rendering for reliable WPF/native-video composition; libVLC retains hardware decoding.
- No signed automatic updater or production code-signing credentials are configured. The Inno Setup build creates a per-user installer; public distribution must complete signing and third-party license/branding work.

These boundaries distinguish this working preview from the full release described in [the design plan](design-review.md).

## Data and privacy

Library/settings: `%LOCALAPPDATA%/Watchroom`, or `WATCHROOM_DATA` when set. The TMDB token is held only for the current app session. Poster searches send titles to TMDB; movie files stay on the host. The loopback media bridge uses an unguessable per-session path and serves authorized media IDs. Room signaling uses HTTPS/WebSockets; media uses WebRTC encryption, with TURN relay fallback when configured.

Room codes expire after six hours. Relay bandwidth and host upload increase with each viewer. A public server and a tested relay are necessary for dependable internet use.

See [third-party notices](THIRD-PARTY-NOTICES.md) and [deployment steps](deploy/README.md).
