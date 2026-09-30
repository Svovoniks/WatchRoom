# Implementation status — September 30, 2026

## Delivered

- `artifacts/Watchroom-Setup-0.1.0-win-x64.exe`: per-user Windows installer, compiled with Inno Setup 7.1.0. Installer generation succeeded; installation/uninstallation on a clean Windows machine has not been tested.
- `artifacts/Watchroom/Watchroom.exe`: self-contained Windows x64 app. Startup was checked without the workspace SDK in `DOTNET_ROOT`.
- WPF folder setup, persisted SQLite library, file watching/rescan, search/category filters, grouped series/episode selection, artwork lookup and match correction, local poster selection, dark/light themes.
- Bundled LibVLCSharp/libVLC playback, native video surface, seeking, volume, audio/subtitle selectors, external subtitles and fullscreen.
- ASP.NET Core private rooms: expiring codes, host admission/removal, host/shared playback permissions, chat, shared queue titles, scheduled playback commands, buffering pause/resume and server-clock estimation.
- Native libdatachannel peer transport: ICE signaling, configurable STUN/TURN, relay-only configuration, bounded chunk pipeline, seekable loopback HTTP bridge, authorized per-title and subtitle access.
- Optional FFmpeg preparation of a 720p copy. The source is preserved; prepared output is cached locally.
- Docker/Caddy/coturn deployment configuration and instructions. No server was purchased or deployed.

## Verified

Release build completed with **zero warnings and zero errors**. **37 smoke/integration checks passed** on this Windows machine:

- byte-range parsing, end-of-file seeks, HTTP range responses and secret-path authorization;
- filename parsing and SQLite indexing;
- native WebRTC connection, byte-exact encrypted transfer and rejection of unshared media;
- actual LibVLC playback of a generated AVI over WebRTC, duration detection, seeking and stable pause;
- room creation, pending/admitted guest permissions, host-only playback, server-assigned timestamps/revisions;
- pause and automatic resume around reported buffering;
- shared queue visibility and host-only queue changes;
- external subtitle descriptor and data transfer without exposing local paths;
- guest removal revoking media access, and invalid invitation rejection.

Windows UI inspection verified folder setup rendering, library selection, movie details, actual video playback in the native surface, and closing without the earlier lifecycle exception. It also caught a GPU/shell rendering problem; the WPF shell now uses software rendering while libVLC retains hardware decoding. Native folder-dialog input automation was unreliable in the inspection tool, so fixture setup was supplied separately; the native picker itself opens correctly. No personal media was scanned or used for testing.

## Release work still open

1. **Public internet validation:** no VPS is available yet. Test separate networks, forced TURN, restrictive firewalls, sustained playback, and observed synchronization drift. Same-machine success does not satisfy this gate.
2. **Transcoding:** live/adaptive transcoding, automatic quality choice, 1080p preparation, hardware encoder selection and a bundled FFmpeg distribution are not complete. Current 720p preparation requires a configured FFmpeg executable and finishes before playback.
3. **Media compatibility:** test real MKV/HEVC/AV1, styled ASS with font attachments, PGS, multitrack audio and variable-framerate files. Generated AVI playback is verified; it is not proof of the entire format matrix.
4. **Recovery/catalog features:** rejoin is manual and host departure closes the room. Browsing host-approved collections and a richer guest suggestion/queue interface remain beyond the current selected-title sharing flow.
5. **Distribution:** installer clean-machine and upgrade tests, code signing, a signed update service, complete bundled-component license/source review, and official TMDB logo attribution before metadata-enabled public release.

These gaps are not reported as completed. The artifact is a working preview for local use and controlled testing with a configured server, not a production release.
# macOS preview

An Avalonia client and architecture-specific `.app` ZIP packages have been added on the `sites-migration` branch. Both Mac architectures compile. The shared room/media checks and Windows-hosted interface playback were exercised; no actual Mac launch or cross-device validation was possible. Native VLC/libdatachannel setup and remaining Mac work are documented in [MAC-CLIENT.md](MAC-CLIENT.md).
