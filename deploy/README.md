# Deploy the internet service

No public service has been provisioned. The app's default `http://localhost:5080` is for development on one machine; it will not connect friends over the internet.

## Requirements

- A Linux VPS with a public IPv4 address, Docker Engine and the Compose plugin.
- Two DNS names pointing to the VPS: `watchroom.example.com` and `turn.example.com` (substitute names you own).
- Open TCP 80/443 for HTTPS; TCP and UDP 3478 plus UDP 49160–49200 for TURN. Keep the coordination backend port 5080 private.
- A provider bandwidth budget. Relayed video can consume several GB per viewer per movie. Monitor provider usage and set spending alerts before inviting users.

## Setup

1. Copy the source tree to the VPS. In `deploy`, copy `.env.example` to `.env`.
2. Fill in DNS names and public IP. Generate a secret using `openssl rand -hex 32` on the VPS and put it in `TURN_SECRET`. Never commit `.env`.
3. Run `docker compose config --quiet` to validate configuration (do not publish the expanded configuration; it contains the relay secret).
4. Run `docker compose up -d --build`. Caddy obtains the HTTPS certificate automatically when DNS and inbound ports are correct.
5. Verify `https://YOUR_DOMAIN/health` returns `status: ok` and `relayConfigured: true`.
6. Put `https://YOUR_DOMAIN` into Watchroom Settings on both computers. Create a room, copy the invitation, join on the other computer, and admit it on the host.

This configuration has been authored but has not been executed against a VPS. Pin container images to verified digests and keep them updated as part of production operations. Room state is in memory; restarting the service closes existing rooms.

## Required internet acceptance test

Use two Windows computers on different internet connections (for example home broadband and a mobile hotspot), not merely two processes on the same LAN.

1. Play a real MKV and MP4 for at least 20 minutes, checking pause, seek, subtitles, and reconnection.
2. Set `FORCE_RELAY=true`, recreate the `watchroom` service, and repeat. The desktop should report “Connected through relay”. Restore `false` afterward.
3. Limit host upload and verify shared buffering and automatic resume. Compare player timestamps; record drift rather than assuming the 250 ms target has been met.
4. Remove a guest while streaming and confirm media access stops. Test an expired invitation and a wrong code.

The baseline relay supports UDP and TCP on port 3478. Networks that allow only outbound TLS on port 443 may still block it. For that case, deploy a TURN/TLS endpoint on a separate public IP/host with a valid certificate on port 443; set `WATCHROOM_TURN` to its `turns:` URL and use the same short-lived-credential secret. Do not advertise support for such networks until it is tested.

TURN forwards encrypted WebRTC packets; it does not store the video. The current architecture sends one copy per viewer, even through TURN, so a relay does not reduce host upload requirements.

## Operations

- Configure container log rotation and provider bandwidth alerts. Chat contents, movie paths and invitation codes are not intentionally logged.
- Invitation codes are random 96-bit capabilities; host approval is still required. Rooms expire after six hours, and TURN credentials expire after eight hours.
- The initial rate limiter sees the Caddy container as the requester. For larger deployments, configure trusted forwarded headers with an explicit proxy address and test per-client rate limiting; do not blindly trust arbitrary `X-Forwarded-For` headers.
- The server does not persist accounts, videos, chat or libraries. Back up deployment configuration and TLS storage; protect the relay secret.
