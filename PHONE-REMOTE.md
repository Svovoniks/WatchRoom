# Phone remote protocol, version 1

BlackPearl Mobile (a separate Flutter app, repository `Svovoniks/BlackPearlMobile`) controls the BlackPearl
desktop app from a phone, on the same network or across the internet. This file is the contract between the
desktop app, the room server and the phone app. A copy lives in both repositories; change both together.

## Shape

- The phone and the PC talk over one WebRTC data channel, labelled `remote-v1`, reliable and ordered. The phone
  creates it and makes the offer; the PC answers.
- The room server (the `/remote` WebSocket on the server in `deploy/`) only introduces them: it forwards signaling
  messages and hands both sides STUN/TURN servers, the same ones rooms use. Commands and state never pass through it.
- Each signaling message is signed with a key only the phone and the PC know, so the server cannot answer in the
  PC's place or insert itself: the SDP carries the DTLS fingerprints, and a forged SDP fails the signature.
- ICE finds the local network path by itself when both are at home. When no direct path exists, TURN carries the
  link.

## Identities and keys

| Name | Form | Who makes it | Who knows it |
| --- | --- | --- | --- |
| host key | 64 hex characters, random | PC, once | PC and server (the server uses it only to derive the PC id) |
| PC id | first 32 hex characters of upper-case SHA-256 hex of the host key's UTF-8 bytes | derived | PC, server, paired phones |
| pairing code | 32 hex characters (16 random bytes) | PC, for each pairing; one use, 10 minutes | PC, and the phone that scans it |
| phone id | 32 hex characters, random | PC, at pairing | PC, that phone |
| phone secret | 64 hex characters (32 random bytes) | PC, at pairing | PC, that phone |

Hex in ids and keys is compared case-insensitively. The server computes the PC id as
`Convert.ToHexString(SHA256(UTF8(hostKey)))[..32]`, so it is upper case on the wire.

Removing a phone on the PC deletes its secret. Its signatures stop verifying at once, wherever it is.

## Pairing link

The PC shows this as a QR code (and as text to copy):

```
blackpearl://pair?v=1&pc=<PC id>&code=<pairing code>&server=<room server URL, percent-encoded>&name=<PC name, percent-encoded>
```

`server` is the base URL the PC uses for rooms, such as `https://rooms.example.com`. The phone opens `<server>/remote`
with `wss` for `https` (or `ws` for an `http://` server), keeping any path the server URL has. The Android app registers for `blackpearl://pair` so a
camera app can open it.

## Room server: `/remote`

A WebSocket carrying the room service's message envelope: JSON objects with the camelCase fields
`type`, `target`, `sender`, `text`, `data` (a string) and `number`. Fields that don't apply are omitted or null.
Messages are at most 64 KiB. `{"type":"ping","number":n}` is answered with `{"type":"pong","number":n,"data":"<server ms>"}`.

**PC** sends first: `{"type":"remote-pc","data":"<host key>","number":1}`.
The server answers `{"type":"remote-welcome","target":"<PC id>","data":"<json>"}`, where the json is
`{"iceServers":["stun:…","turn://user:pass@host:port?transport=udp", …],"forceRelay":false}`; user and password are
percent-encoded, and `forceRelay` true means both sides use only relay candidates. One connection per PC id: a new one
replaces the earlier. The connection lasts at most 6 hours; the PC reconnects and gets fresh relay credentials.

**Phone** sends first: `{"type":"remote-phone","target":"<PC id>","number":1}`.
The server answers `{"type":"remote-welcome","target":"<this connection's id>","data":"<json with iceServers>"}`,
then `{"type":"remote-online"}` if the PC is connected, or `{"type":"remote-offline"}` if not. While the phone
stays connected it receives `remote-online` and `remote-offline` as the PC comes and goes.

**Signals.** Phone → PC: `{"type":"signal","data":"<signed signal json>"}` (no target needed; the server sends it
to the PC this phone asked for, with `sender` set to the phone's connection id). PC → phone:
`{"type":"signal","target":"<phone connection id>","data":"<signed signal json>"}`; the server delivers it with
`sender` set to the PC id. When a phone's connection closes, the PC gets `{"type":"peer-left","sender":"<connection id>"}`.

An error ends the connection with `{"type":"error","text":"<reason>"}`. A PC connection replaced by a newer one gets
`{"type":"closed","text":"<reason>"}` before it ends. A `number` other than 1 is refused with a
reason telling which side to update. `/health` lists `"remote":1` when the server has this endpoint.

Limits: 8 phone connections per PC at a time, and the same message rate limits as rooms.

## Signed signals

```json
{"phone":"<phone id, or \"pair\" while pairing>","session":"<32 hex>","kind":"sdp|ice|rejected","payload":"<string>","auth":"<base64url, no padding>"}
```

- `session` is new for every connection attempt; the phone makes it. The PC keeps one live session per phone, and
  a new session replaces the old one. A session that began with a pairing code keeps signing as `pair`, with the
  pairing code, for the rest of its life, even after it has paired.
- `payload` for `sdp` is the JSON text `{"type":"offer|answer","sdp":"<SDP>"}`; for `ice` it is
  `{"candidate":"<candidate line, without the a= prefix>","mid":"<media id>"}`. ICE is trickled both ways. There is
  only one media section, the data channel's, so a receiver that needs a line index uses the one in the answer (0).
- `auth` is HMAC-SHA256 with key = the phone secret's bytes (or, while pairing, the pairing code's 16 bytes) over the
  UTF-8 bytes of

  ```
  bp-remote-v1\n<direction>\n<phone>\n<session>\n<kind>\n<payload>
  ```

  where `<direction>` is `phone` for messages the phone sends and `pc` for the PC's, `\n` is a single line feed, and
  `<payload>` is the payload string exactly as sent.
- Both sides drop a signal whose `auth` doesn't verify, without answering, except for one case: when the PC doesn't
  know the phone id, or its pairing code has expired or been used, it answers once per session with an
  **unsigned** `{"phone":…,"session":…,"kind":"rejected","payload":"unknown-phone|pairing-expired","auth":""}`.
  The phone shows "This phone was removed from the PC. Pair it again." or "That code has expired". It is a hint only:
  it can't grant anything.

Test vectors:

| key (hex) | text signed | auth |
| --- | --- | --- |
| `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f` | `bp-remote-v1⏎phone⏎a1b2c3d4e5f60718a1b2c3d4e5f60718⏎0123456789abcdef0123456789abcdef⏎sdp⏎{"type":"offer","sdp":"v=0"}` | `AHCUPPyOcWPNmRtEBJ6D5PQUJwF9CV5-GHlhUpfTkX8` |
| `ffeeddccbbaa99887766554433221100` | `bp-remote-v1⏎pc⏎pair⏎0123456789abcdef0123456789abcdef⏎ice⏎{"candidate":"candidate:1 1 UDP 2122317823 192.168.1.20 50000 typ host","mid":"0"}` | `bIGb4K6s3ztFQAOUL2Q3Oof397tsk3TiGq_W2ZB3I_I` |

(⏎ is a line feed.)

## The data channel, `remote-v1`

UTF-8 JSON text messages, at most 64 KiB, each with a `type`.

### Opening

1. Phone → PC: `{"type":"hello","protocol":1,"app":"<app version>","device":"<phone model or name>"}`
2. PC → phone: `{"type":"welcome","protocol":1,"app":"<desktop version>","pc":"<PC name>","pcId":"<PC id>","canStart":true,"features":["library","watch","queues"]}`.
   `features` lists what the PC offers beyond the remote (see "The library and watching on the phone"); a PC from
   before them sends none.
   If the PC can't speak the phone's protocol it sends `{"type":"error","code":"protocol","message":"…"}` saying which
   app to update, and closes.
3. While pairing (the session was signed with a pairing code), the phone then sends
   `{"type":"pair","name":"<name to show on the PC>"}` and the PC answers
   `{"type":"paired","phoneId":"<phone id>","secret":"<phone secret>","pc":"<PC name>","pcId":"<PC id>"}`. The code is
   used up. The phone saves what it needs and from then on connects with its own id and secret. A paired session
   that sends `pair` gets an error.
4. The PC sends a `state` at once, then whenever it changes.

### PC → phone

`state`:

```json
{"type":"state",
 "media":true,                 // false when nothing is loaded in the player; the other playback fields are then null or absent
 "title":"Night Signal",
 "detail":"Season 2 · Episode 3 · Low Tide",
 "playing":true,
 "positionMs":1122000, "durationMs":2830000,
 "volume":64, "muted":false,
 "loop":"off|all|one",
 "fullscreen":false,
 "hasPrevious":true, "hasNext":true,
 "canControl":true,            // false in a room where only whoever plays the video may control it
 "room":null,                  // or {"name":"Movie club","people":3} while the PC is in a room
 "mediaId":"<library id>"}     // the library video the PC plays on its own, or null (nothing, music, or a room's video)
```

While playing, the phone advances the position itself from when the message arrived; the PC also sends a fresh
`state` every 5 seconds and after every seek.

`error`: `{"type":"error","code":"<short code>","message":"<sentence to show>"}` for something that couldn't be done:
`not-allowed` in a room where only the person playing the video can control it, `nothing-playing`, `not-shown`
(fullscreen while the PC isn't showing the player), `pairing` (a used or expired pairing code, or `pair` on a
paired link) and `protocol`.

### Phone → PC

`command`: `{"type":"command","action":"<action>","value":<number or bool, when the action takes one>}`

| action | value | does what the PC's … does |
| --- | --- | --- |
| `toggle` | | Space |
| `play`, `pause` | | media Play / Pause buttons |
| `seek-by` | milliseconds, negative to go back | Left / Right (±10 s) |
| `seek-to` | milliseconds from the start | dragging the timeline |
| `previous`, `next` | | P / N |
| `stop` | | S |
| `volume` | 0–100 | the volume slider |
| `mute` | | M |
| `loop` | | L (off → all → one) |
| `fullscreen` | | F |

Commands go through the same code as the PC's keys and buttons, so a room's shared-controls rule applies to them
the same way. With `canControl` false, every action except `volume`, `mute` and `fullscreen` is refused with
`not-allowed`: those three change only this PC.

Unknown message types and actions are ignored, so either side can add new ones within version 1. Changing the
meaning of an existing message needs version 2.

## The library and watching on the phone

A PC whose welcome lists `library` answers `browse`; one that lists `watch` also streams videos to the phone and takes
them back. All of these messages travel on `remote-v1`, except the video and picture bytes, which use a second data
channel the phone opens beside it.

### Browsing

Phone → PC: `{"type":"browse","request":<n>,"home":false,"category":0,"query":"","parentId":null,"season":null,"page":0,"seed":0}`.
`category` is 0 for all videos, 1 films, 2 shows, 3 anime, 6 music. `parentId` with a show's entry id lists its
seasons; with a `season` as well, that season's episodes. `home` true lists Home instead: Continue watching, then
suggestions for each kind (shuffled by `seed`), then one random pick.

PC → phone:

```json
{"type":"page","request":7,"total":53,"page":0,"pageSize":40,"location":"All titles",
 "entries":[{"id":"…","title":"Night Signal","caption":"Show · 2021","level":"series|season|episode|movie",
   "season":null,"episode":null,"overview":"…","kind":"Movie|Show|Anime|Music","runtimeMinutes":48,"year":2021,
   "episodes":10,"homeSection":"continue|suggestions|mystery|null","positionMs":0,"watched":false,
   "artwork":"<token or null>","playable":false}]}
```

`playable` entries are files the phone can watch or start on the PC: films, episodes, and Continue watching entries
(which name the episode to resume even when their level is `series`). `positionMs` and `watched` are the PC's own
resume point and watched mark. A failure is an `error` with code `browse` and the `request`.

### Watching on the phone

Phone → PC: `{"type":"watch","request":<n>,"id":"<entry id>","quality":"auto|original|1080p|720p|480p","network":"home|away|relayed","takeOver":false}`.
`auto` plays the file itself on the home network and a 720p conversion elsewhere. `takeOver` true, for the video in
the PC's `mediaId`, makes the PC save where it is and stop, so the phone continues it.

PC → phone, for the file itself:

```json
{"type":"stream","request":3,"id":"…","title":"Night Signal","detail":"Season 2 · Episode 3","positionMs":1122000,
 "durationMs":2830000,"kind":"file","resource":"file/<id>","length":1834567890,"extension":".mkv",
 "quality":"original","qualities":["original","1080p","720p","480p"],"note":null}
```

or for a conversion (H.264 and AAC in MPEG-TS pieces of `segmentMs`, the last one shorter):

```json
{"type":"stream","request":3,"id":"…","title":"…","detail":null,"positionMs":0,"durationMs":2830000,"kind":"hls",
 "streamId":"<32 hex>","segments":708,"segmentMs":4000,"quality":"720p","qualities":["original","720p","480p"],"note":null}
```

Both also carry, for an episode, the episodes the PC's own Previous and Next would play and the show's first episode,
which Loop all starts again from after the last one, each as `{"id":"…","title":"…","detail":"Season 2 · Episode 4"}`
or null (`previous`, `next`, `first`; `first` only when there is no `next`), and the PC's track preferences from
Settings › Playback, as two-letter language codes in priority order: `"tracks":{"audio":["ja","en"],"subtitles":["en"],
"rules":[{"audio":"en","languages":["off"]}]}`. A rule replaces `subtitles` while the audio is in its language; `off`
means no subtitles. The phone picks its starting tracks with them as the PC would. Older PCs send none of these.

`positionMs` is where to start: the PC's saved position, 0 for a watched video. `note` says why the phone got the
file when it asked for a conversion (no converter, or a file already smaller). The PC keeps one stream per phone; a
new `watch` replaces it and `{"type":"stream-stop"}` ends it. A failure is an `error` with the `request` and the code
`gone` (not in the library, or its drive is disconnected).

Phone → PC while watching, every 10 seconds, on pause, and when it stops:
`{"type":"progress","id":"…","positionMs":1180000,"durationMs":2830000,"ended":false,"final":false}`. The PC saves it
as its own playback: the resume point, when it was last played, and the watched mark once the position is within
2 seconds of the end, or `ended` is true within the last minute (or anywhere, when `durationMs` is 0). `final` is true for the last report of a viewing. The PC ignores reports for
the video it is playing itself at that moment.

### Continuing on the PC

Phone → PC: `{"type":"play","request":<n>,"id":"…","positionMs":1180000}` plays the video on the PC from that
position, and answers `{"type":"done","request":<n>}` once it has started. It needs `canStart`; a refusal is an
`error` with code `not-allowed`, `in-room` (the PC is in a room), `gone`, or `opening` (BlackPearl is starting on the PC: ask again once the phone has reconnected to it). The PC ends the phone's stream before it
answers: the phone is no longer watching, and an answer queued behind video it still reads can take seconds to arrive.

### Queues

The PC's saved queues, the ones on its Queues page, are shared with the phone when its `welcome` lists the `queues` feature. A video's place is its position in the
saved queue (from 0), so a video queued twice has two places; videos the library no longer has keep their places but
are never listed.

Phone → PC: `{"type":"queues","request":<n>}`. PC → phone:
`{"type":"queues","request":<n>,"queues":[{"id":"…","name":"Evening","count":12,"upNext":3,"active":true,"artwork":"<token>"}]}`.
`count` is the videos the library has; `upNext` is the place Play continues from, as on the PC's Queues page (the
queue's place when it is the PC's active one, otherwise its first unwatched video), or null; `active` marks the queue
the PC's player uses.

`browse` with `"queue":"<id>"` answers a `page` of that queue's videos in order, and with `"episodesOf":"<entry id>"`
the show's episodes, in the order its Previous and Next follow. Their entries are the usual ones plus `"index"`, the
place (for `episodesOf`, the position in the list). A queue that is gone is an `error` with code `browse`.

`watch` and `play` take `"queue":"<id>","queueIndex":<place>`. The video plays as part of that queue: for `watch`, the
answer's `previous`, `next` and `first` are the queue's playable videos around the place (each with a `queueIndex`),
and `"queue":{"id":"…","name":"…","index":<place>}` echoes it. A place that no longer holds the video falls back to the
video's first place, and a queue that doesn't have it to the show's episodes. On the PC, `play` makes the queue its
player's, as playing it there does; with BlackPearl closed, the phone host saves it so BlackPearl opens on it.

The `state` carries `"queue":{"id":"…","name":"…","index":<place>}` while the PC plays a video from a saved queue (not
in a room), or null.

### The media channel, `media-v1`

The phone opens a second data channel labelled `media-v1`, reliable and ordered, with the first. A PC that doesn't
know it closes it. The phone asks with text messages:

`{"type":"read","id":<u32>,"resource":"<resource>","offset":<bytes>,"count":<1 to 262144>}`, and
`{"type":"cancel","id":<u32>}` for a read it no longer needs.

Resources: `file/<id>` (the video of the phone's current `file` stream), `stream/<stream id>/<piece>` (a piece of
its current conversion) and `art/<token>/<width>` (an entry's picture as JPEG, at most `width` pixels wide, 64 to
1280, never enlarged).

The PC answers reads in the order they were asked, each as binary frames of at most 16 KiB of data after a 24-byte
little-endian header:

| bytes | field |
| --- | --- |
| 0–3 | read id (u32) |
| 4–7 | offset of this frame's data in the reply (i32) |
| 8–11 | the reply's length (i32); shorter than `count` at the end of the resource, 0 past it |
| 12–15 | reserved, 0 |
| 16–23 | the whole resource's length (i64) |

A read that can't be answered gets a text `{"type":"read-error","id":<u32>,"code":"busy|gone|invalid|failed","message":"…"}`.
`busy` means not ready yet (a piece still converting): ask again shortly. The PC queues at most 64 reads per phone and
answers `busy` past that.

## While BlackPearl is closed

With **Phones can browse and watch while BlackPearl is closed** on (Settings › Profile › Phone remote, on by default),
BlackPearl starts a small process of itself with no window, `BlackPearl.exe --phone-host`, once it has closed, and
optionally at Windows sign-in (`--phone-host --signed-in`, from a `Run` entry named `BlackPearl phone access`). It
connects for phones with the same key, so to a phone it is the same PC: its `welcome` offers `library` and `watch`,
its state says nothing is playing, and the remote's commands get `error` `nothing-playing`. Browsing, watching and
progress work as with the app. Continuing on the PC opens BlackPearl with the video (`--play-id <id>`), which plays it
from the position the host saved once its library has loaded.

The host shows an icon in the notification area whose menu opens BlackPearl or turns the host off. It ends once
BlackPearl has the library open again: after the room server has handed the PC's id to the app (at most ten seconds),
so phones are never left with neither. The phones then reconnect to the app as after any dropped link. BlackPearl
doesn't start the host when it closes to install an update or as Windows signs out, and a build started with
`BLACKPEARL_PHONE_HOST=off` (as `scripts/dev-launch.ps1` does unless `-PhoneHost`) never starts it or changes the
sign-in entry.
