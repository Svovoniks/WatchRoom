# Deploy the room service on a VPS

The VPS only coordinates rooms: invitations, admission and WebRTC signaling. Video, voice and controls go directly between participants. When a direct path fails, Cloudflare's TURN service relays it, so no media passes through the VPS and its bandwidth use stays small.

Without a registered domain, use [sslip.io](https://sslip.io): a name such as `203-0-113-10.sslip.io` resolves to the IP address it spells (`203.0.113.10`), so the server can still get a public certificate. The examples below use that placeholder; substitute your server's name.

## Layout

- **HAProxy** owns public TCP 443 (`haproxy.cfg`). It forwards TLS connections to Caddy with the PROXY protocol and everything else to sshd, because some networks allow SSH only on 443. Clients that wait for the server's SSH banner connect after a 5 second delay. Because sshd sees these connections from loopback, HAProxy limits SSH attempts to 10 per minute per client address; use key-only SSH authentication on such a server.
- **Caddy** (2.7 or later, for `proxy_protocol`) terminates HTTPS on `127.0.0.1:8443` (`Caddyfile`). It obtains certificates with the TLS-ALPN challenge through 443, so port 80 stays free for other services.
- **BlackPearl.Server** listens on `127.0.0.1:5080` (`blackpearl.service`). It trusts `X-Forwarded-For` only from loopback, so rate limits apply per real client.

## Requirements

- Ubuntu with `haproxy`, `aspnetcore-runtime-10.0`, and Caddy from the [official repository](https://caddyserver.com/docs/install#debian-ubuntu-raspbian). Ubuntu's own Caddy package is too old.
- Public TCP 443 reachable. Nothing else needs to be open.
- A Cloudflare Realtime TURN key: its key ID and API token. Without them the service still works, but only participants who can connect directly will succeed.

## Install or update

1. Publish on a development machine: `dotnet publish src/BlackPearl.Server -c Release -o artifacts/server-publish`. Copy the output to `/opt/blackpearl` on the VPS.
2. Install `blackpearl.service` to `/etc/systemd/system/`. Create `/etc/blackpearl/blackpearl.env` from `blackpearl.env.example` with mode 600, then `systemctl enable --now blackpearl`.
3. Install `Caddyfile` to `/etc/caddy/Caddyfile`. Set `Environment=BLACKPEARL_DOMAIN=203-0-113-10.sslip.io` (your server's name) in a `systemctl edit caddy` drop-in, then restart Caddy.
4. Install `haproxy.cfg` to `/etc/haproxy/haproxy.cfg`. Set `Environment=MULTIPLEX_BIND=:443` in a `systemctl edit haproxy` drop-in. Anything else listening on 443 must be stopped first. Keep a second SSH route open, or schedule an automatic rollback, before switching 443.
5. Verify that `https://203-0-113-10.sslip.io/health` returns `status: ok`, `relayConfigured: true` and `stunConfigured: true`, and that `ssh -p 443` still connects.

A server installed while the app was called Watchroom (`/opt/watchroom`, `watchroom.service`, `/etc/watchroom/watchroom.env`, `/var/lib/watchroom`) keeps working with its `WATCHROOM_*` settings, which the server still reads. To update it without moving anything, publish as in step 1 to `/opt/watchroom` and change `ExecStart` in `watchroom.service` to run `BlackPearl.Server.dll` instead of `Watchroom.Server.dll`. Keep the Caddy drop-in's `WATCHROOM_DOMAIN` unless you also install the new `Caddyfile`, which reads `BLACKPEARL_DOMAIN`.

For an update, repeat step 1 and run `systemctl restart blackpearl`. Rooms live in memory, except persistent rooms stored under `/var/lib/blackpearl`, so a restart closes temporary rooms in progress.

## Internet acceptance test

Use two Windows computers on different internet connections (for example home broadband and a mobile hotspot), not merely two processes on the same LAN.

1. Play a real MKV and MP4 for at least 20 minutes, checking pause, seek, subtitles, and reconnection.
2. Set `BLACKPEARL_FORCE_RELAY=true`, restart `blackpearl`, and repeat to exercise Cloudflare's relay. Restore `false` afterward.
3. Limit host upload and verify shared buffering and automatic resume.
4. Remove a guest while streaming and confirm media access stops. Test an expired invitation and a wrong code.

The current architecture sends one copy per viewer, even through TURN, so a relay does not reduce host upload requirements. Cloudflare bills relayed traffic beyond its free allowance; monitor usage in the Cloudflare dashboard.

## Operations

- Cloudflare credentials are fetched at startup and refreshed in the background. Each credential handed to a participant stays valid for at least 12 hours. If Cloudflare is unreachable, participants receive STUN only and the service retries every minute.
- SSH through HAProxy reaches sshd from `127.0.0.1`, so sshd logs show loopback instead of the client address.
- Invitation codes are random 96-bit capabilities; host approval is still required. Temporary rooms expire after six hours.
- The server does not persist accounts, videos, chat or libraries. Keep `/etc/blackpearl/blackpearl.env` private.
