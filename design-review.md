# Watchroom — final implementation plan

Status: plan finalized on September 30, 2026; implementation subsequently authorized. A working Windows preview, coordination server, native WebRTC transport, tests, and installer are implemented. See IMPLEMENTATION.md for verified behavior and remaining release work. This document retains the full intended release scope.

## Product

A Windows desktop app for watching video files from one participant’s computer together over the internet. Only the host needs the file. Everyone installs the Windows app; any participant can host a later session. Initial target: Windows 11 x64, two to five people per room, 1080p SDR playback. These are proposed release boundaries, not inherent limits.

## Visual direction

Working name: Watchroom. A quiet, cinema-inspired interface with a left navigation rail, large portrait posters, spacious title details, and a restrained violet accent. Follow system light/dark appearance, with an explicit dark-mode option. Use Segoe UI, accessible contrast, keyboard navigation, and scalable Windows text. No advertisements or discovery feed.

The accompanying preview uses fictional titles and stylized poster placeholders. The actual app uses matched movie and series artwork.

## Main flows

1. Setup: choose display name, then add any number of folders with the Windows folder picker. Assign each Movies, TV, Anime, or Mixed. Show subfolder scanning, metadata language, and the option to fetch online artwork. Joining without adding folders is allowed. Explain that title searches go to the metadata provider; video files remain local.
2. Scan: index in the background, showing progress and immediately usable results. Watch folders for changes and offer Rescan. Missing external drives mark titles unavailable without deleting matches. Folder settings remain editable.
3. Browse: searchable poster grid with Movies, Shows, Anime, and Recently added filters. Series open to seasons and episodes. Details include synopsis, year, runtime, available resolution, audio, and subtitles. Uncertain matches go to Needs review; users can search and select the correct title or use local artwork. Never silently overwrite manual matches.
4. Host: select a movie or episode, create a room, and choose what guests can browse: selected title by default, selected collections optionally. Invite using an expiring link or code. Host approves newcomers. Guests see only the permitted catalog, never local paths.
5. Join: enter invitation, choose display name, wait for admission, then buffer. A lobby shows participants and readiness. Host starts everyone together. Guests can suggest titles or episodes; host confirms the queue.
6. Watch: large video surface; collapsible participant/chat sidebar; shared timeline and play/pause; personal volume, subtitle selection, and fullscreen. Host owns seeking and queue changes by default; an Allow shared controls toggle grants these to members. Chat is text-only in the first release.

## Playback behavior

- The host is authoritative for media ID, play/pause, position, playback speed, and a monotonically increasing session revision. Commands include a future start time against a synchronized clock, so network arrival time does not define playback position.
- All clients, including the host, play against the same buffered timeline. Measure drift periodically; correct small drift gently and seek after larger divergence. Initial acceptance target: within 250 ms under healthy test conditions, to be measured rather than promised universally.
- Default buffering policy is Pause for everyone. Show who is buffering, resume together when ready, and let the host continue without a persistently stalled participant. A rejoining client catches up to current room time.
- Seeking cancels obsolete media requests, flushes old buffers, and waits for readiness at the new position. Concurrent control requests are serialized by the host.
- Host disconnection pauses the room and offers reconnect or leave. No automatic host handoff: another computer may not have the file. Prevent host sleep while actively hosting, with a visible setting.
- Original-quality playback when file format and bandwidth allow; remux or transcode when required. Offer Auto, 1080p, 720p, and Original, subject to source resolution. Clearly show quality reductions and the reason.
- Embedded and external SRT/ASS subtitles, multiple audio tracks, and styled anime subtitles are core requirements. Font attachments and ASS rendering must be included in the early playback validation. Guest subtitle choice is personal; audio choice may initially be room-wide when transcoding.

## Internet architecture

Planned media path: native playback with libVLC through LibVLCSharp, backed by a local media bridge receiving buffered file ranges or fragmented media chunks over reliable WebRTC data channels. ICE/STUN attempts a direct connection; TURN relays encrypted traffic when direct connectivity fails. This preserves the native player’s codec and subtitle strengths instead of screen-sharing a player window.

For original media, the bridge supports seekable byte-range reads without downloading the whole file first, including container metadata reads near the end of a file. Requests use opaque media IDs, bounded chunk sizes, backpressure, and cancellation on seek. External subtitles and font attachments are transferred as authorized companion assets when needed. For transcoded media, the host produces a shared rendition with explicit source-time mapping; seeking restarts preparation at the requested source position. Keep control messages separate from bulk media queues so buffering does not block pause or seek commands.

A small public coordination service manages room discovery, expiring invitations, admission, and signaling over TLS. It does not store video files or the complete library. Credentials for the relay are short-lived. Media access is scoped to approved titles and current room membership. The local bridge binds to loopback only, uses per-session authorization, and never accepts arbitrary filesystem paths from guests.

Direct and relayed modes initially send one stream per guest. Example: a 6 Mb/s rendition sent to three guests uses about 18 Mb/s of host upload before overhead; each guest receives about 5.4 GB during a two-hour movie. A TURN relay does not eliminate the host’s per-guest upload requirement. Server fan-out can be a later enhancement if larger rooms are important.

The hosted coordination and relay services have operating costs. No server is deployed or paid service purchased during design. A self-hosted server configuration can be supported later; friends should not need VPN setup or router port forwarding in the standard flow.

## Implementation components

- Desktop: C#/.NET with a Windows-native WPF shell and local SQLite library index.
- Playback: libVLC through official LibVLCSharp and its WPF integration for local/received media, subtitle rendering, and playback control. Put engine operations behind a small playback interface. LibVLCSharp is the preferred engine; mpv is a contingency only if the initial validation exposes a blocking issue. Do not ship two engines in the first release.
- Media preparation: FFmpeg for probing, remuxing, and optional hardware-assisted transcoding; capability detection and software fallback.
- Networking: a maintained native WebRTC transport binding, chosen after a small feasibility spike validates reliable data-channel throughput, backpressure, seeking, and Windows packaging.
- Service: ASP.NET Core coordination service and coturn relay, deployed separately from the desktop app.
- Metadata: TMDB for movies and series, including available anime matches; local poster.jpg/folder.jpg and manual matching as fallbacks. Cache artwork and metadata, respect provider requirements, include attribution. Additional anime-specific providers are a later extension.

The transport-to-player bridge is the main technical risk. Before building the full UI, validate two remote Windows clients across different residential networks, forced relay mode, seeking, buffering, HEVC/MKV input, ASS subtitles, and limited upload. If that spike fails its targets, revise the media transport design before proceeding. Review redistribution obligations for the exact multimedia dependency builds before packaging.

## Media compatibility targets

Validate the bundled libVLC build against MKV, MP4/M4V, AVI, MOV, WebM, MPEG, and TS containers; H.264, HEVC, AV1, VP8/VP9, MPEG-2, and MPEG-4 video; and AAC, MP3, AC-3/E-AC-3, DTS, FLAC, Opus, and Vorbis audio. Include SRT, ASS/SSA with attached fonts, and embedded PGS subtitles in the test corpus. This is a validation matrix, not a blanket guarantee for every combination. Probe hardware decoding and offer a lower-cost rendition when the host or guest cannot keep up. Unsupported or damaged media produces a clear error. Protected streaming-service downloads are outside scope.

## Delivery sequence

1. **Validate playback and internet transport.** Build a minimal two-client test harness using LibVLCSharp. Prove original-file streaming, range seeking, a transcoded rendition, direct and forced TURN connectivity, buffering, styled subtitles, attached fonts, audio selection, and WPF video controls. Measure drift and bandwidth. Pin dependency versions after validation. Exit gate: a guest with no local file can watch and seek across separate networks, and the player bridge meets the synchronization target under defined healthy conditions. Record results and any architectural change before broader development.
2. **Build the local library.** Implement setup, editable folder lists, SQLite indexing, background scans, metadata/artwork cache, title correction, movie/series/episode pages, search, and local playback. Exit gate: a mixed movie/show/anime collection remains usable through rescans, ambiguous names, offline metadata, and disconnected drives.
3. **Build private rooms.** Implement the coordination service, expiring invitations, host approval, authorized media access, shared catalog selection, authoritative playback state, late joining, reconnecting, and buffering recovery. Exit gate: three-client sessions converge after play/pause, seeking, late joining, and network interruption; revoked participants lose access.
4. **Complete the viewing experience.** Add adaptive quality recommendations, shared queue, text chat, personal subtitles and volume, fullscreen, keyboard controls, host sleep prevention, and actionable error states. Use a shared transcoded rendition in v1 to bound host encoding cost; direct-play audio selection may be personal, while transcoded audio is room-wide.
5. **Package and release.** Produce a Windows installer with bundled media dependencies, dependency notices, configurable server endpoints, and versioned service deployment instructions. Validate clean-machine installation, upgrades, firewall behavior, forced relay, and restricted uploads. Release updates must verify signed manifests/packages; obtain signing credentials and hosting configuration before production distribution. Production hosting purchases or deployment are separate from plan finalization.

## Server operations and access

Start with one public server deployment running the coordination service and coturn, with DNS, TLS, short-lived TURN credentials, rate-limited invitation attempts, room expiry, and bandwidth monitoring. Separate them later if traffic requires it. No permanent user accounts are required for v1: display names, unguessable invitations, host admission, and per-session credentials establish access. Invitation links expire and room closure revokes membership. Keep operational logs free of local paths and chat contents. Room chat is transient; the library database stays on its owner's machine.

The host must remain online throughout playback. A relay improves reachability but does not reduce per-guest host upload in this architecture. Quality selection should reserve upload headroom; if transcoding or available upload cannot sustain a session, explain the limitation rather than promise smooth playback. Server region, provider, cost cap, domain, and production credentials are deployment inputs to settle before publishing, not prerequisites to completing the local development stages.

## First release and deferred work

First release: folder setup, background indexing, poster library, match correction, movie and series details, private invitations, guest admission, internet streaming with relay fallback, synchronized playback, subtitles, audio selection, quality controls, shared queue, text chat, reconnect states, installer and updates.

Deferred: mobile/web clients, public rooms, voice/video chat, cloud media storage, remote downloads, automatic host migration, 4K/HDR guarantees, anime intro detection, and large-room server fan-out.

## Acceptance scenarios

- Two machines on separate networks can join with no router configuration; forced relay is verified separately.
- A guest without the video file can watch, pause, seek when allowed, reconnect, and receive correct subtitles.
- Pause, seek, buffering, and late joins converge to the same room timeline.
- Folder removal, unavailable drives, ambiguous anime filenames, and offline metadata lookup have clear recoverable states.
- Expired invites, rejected guests, and guests requesting unshared media cannot access video or local paths.
- Host upload limitations result in an actionable quality recommendation rather than unexplained stalls.

## Final scope decisions

- Retain the reviewed poster-first UI and three main flows: folder setup, library, and private watch room.
- Windows 11 x64 app for hosts and guests; initial target two to five participants and 1080p SDR.
- Prefer LibVLCSharp; verify the player and transport together before committing to the full build.
- Use a hosted coordination service plus TURN fallback. Keep source media on the host, with no server-side video storage.
- Default to host-controlled playback and pause-for-everyone buffering; personal subtitle and volume controls.
- Implementation was authorized after plan finalization. Release gates and current gaps are tracked in IMPLEMENTATION.md.

## Primary references

- WebRTC relay connectivity: https://webrtc.org/getting-started/turn-server
- WebRTC data channels: https://webrtc.org/getting-started/data-channels
- LibVLCSharp integration: https://docs.videolan.me/libvlcsharp/docs/getting_started.html
- LibVLCSharp playback API: https://docs.videolan.me/libvlcsharp/api/LibVLCSharp.Shared.MediaPlayer.html
- WPF integration constraints: https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.WPF/README.md
- Contingency player reference: https://mpv.io/manual/stable/
- Metadata API and attribution requirements: https://developer.themoviedb.org/docs/faq
