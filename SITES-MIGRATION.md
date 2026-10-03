# Watchroom on Sites

The native Windows player supports the Sites room service as well as the original ASP.NET WebSocket server. Sites coordinates invitations, host approval and WebRTC signaling using D1-backed HTTP sessions. Updated native clients send playback, readiness, buffering, queues and chat through a separate reliable WebRTC data channel. The host owns the room state and clock, validates guest requests and distributes authoritative updates to admitted guests. Files and LibVLC playback remain on each computer.

The pre-migration preview is saved in root commit `d1ea5d4`. Migration work is on `sites-migration`. The Site has its own deployment repository; this branch references its source commit as the `sites` Git submodule. The local Site checkout is retained for recovery.

## Try the migrated Windows app

Run `artifacts/Watchroom-Sites/Watchroom.exe`. This is a separate self-contained build; the original installer and published app remain available.

For a local test, start `node sites/scripts/preview.mjs`, use `http://localhost:5091` in both app instances, and use different `WATCHROOM_DATA` directories. The preview uses an in-memory SQLite database and applies the same migration as production. It is a test adapter, not the production service.

Hosted server address: `https://watchroom-rooms.svovoniks.chatgpt.site`

Sites starts owner-private. Its browser sign-in gate prevents the Windows client from connecting until the owner explicitly enables public access or a native-client authentication flow is added. Session bearer tokens do not bypass Sites' access gate. The app reports this condition. Changing the Site audience requires the user's explicit instruction; no audience change is part of this migration.

The web page provides room creation, joining, admission, removal, chat and service status for testing. It does not play movies in the browser. Use the Windows app to stream video.

## Implementation

- `POST /sessions`: create/join with a display name and invitation; returns a random 256-bit bearer token.
- `GET /session?since=N`: ordered discovery event polling; native clients poll every 100 ms during connection establishment and every second after the direct control handshake.
- `POST /session`: existing Watchroom wire messages.
- `DELETE /session`: leave; host leaving closes the room.
- `/health`: protocol and transport discovery.

D1 stores discovery and admission state with a version for compare-and-swap updates. Concurrent requests retry against the latest state. Session secrets are hashed before storage. Events are acknowledged with monotonic cursors; each participant has a bounded 128-message queue. Rooms expire after six hours; stalled peers are removed after 60 seconds. No movie files or local folder paths are uploaded to Sites. Updated native clients keep movie descriptors, playback state and chat on the host and its admitted peers. Legacy control endpoints remain available for the service test page and older clients; new native rooms require updated participants.

Once direct connections are established, playback continues if discovery becomes unavailable. New joins require the discovery service. Host disconnection ends the room and pauses guest playback; host migration is not implemented. See `DIRECT-CONTROLS.md` for validation of the direct control path.

Sites runtime environment values can configure `WATCHROOM_STUN`, `WATCHROOM_TURN`, `WATCHROOM_TURN_SECRET`, and `WATCHROOM_FORCE_RELAY`. Use Sites' secret/environment settings; never commit a relay secret. Coturn remains external. The deployment has no relay credentials or STUN address configured, so public-network connectivity is not validated. Hosted access and real network-separated Windows testing remain required before treating this as an internet-ready release. Polling also consumes D1 reads/writes and should be measured before broader use.

## Validation

- 37 existing end-to-end checks passed against the migrated local HTTP service, including native WebRTC media transfer and LibVLC playback.
- 37 existing checks passed against the original ASP.NET service.
- Three additional Node tests passed: protocol/admission/control/buffering/revocation, concurrent updates and token storage, and TURN credential construction/host expiry.
- The Windows solution builds without warnings/errors; the Sites artifact exports a callable Worker `fetch` handler.

Run `node --test sites/tests/rooms.test.mjs` for the D1 adapter tests. Set `WATCHROOM_TEST_SERVER=http://localhost:5091` before running the existing smoke executable to use the migration instead of its temporary ASP.NET server.

## Build and publish

The Site source is in `sites/`. It uses the Sites Worker ESM starter, with D1 schema managed by Drizzle. `node sites/scripts/build.mjs` must run from `sites/`; it emits `dist/server/index.js` and hosting metadata. `node scripts/validate-artifact.mjs` validates the generated module. Production migrations are packaged by the Sites hosting workflow from `drizzle/`.

The standalone development page can be previewed with `node scripts/preview.mjs` from `sites/`. The production service runs entirely in the Sites Worker runtime and D1.


