# Direct room controls

The room service registers the host, admits guests and exchanges WebRTC connection information. Each admitted guest connects to the host with separate media and reliable control data channels. Host commands apply locally and broadcast to guests. Guest commands go to the host, which checks permissions, assigns the playback revision and time, then broadcasts the resulting state. Guest-provided sender IDs and revisions do not grant authority.

The host owns playback, shared-control permissions, readiness, buffering, queues and chat. Pending guests cannot read media or block playback. Explicit stop cancels automatic buffering resume. Direct clock probes synchronize guests with the host; discovery snapshots cannot overwrite established playback state. Established peers continue controlling playback during a service outage, while new joins require discovery. Host loss pauses guests and ends the room. All participants must use the updated app.

Validation on October 1, 2026:

- Full smoke suite: 153 checks passed, including native WebRTC media, LibVLC playback, admission, removal and host-state behavior.
- Direct-control integration: 14 checks passed with two guests. A guest stop reached the host and the other guest in 10 ms while a media read was deliberately blocked. Controls still worked after service shutdown and retry exhaustion. Recorded service traffic contained only discovery messages.
- Real Sites service with the Windows VM: the host applied a guest stop in 5 ms; the next paused host sample arrived in 35 ms. Both players paused and rewound. Timing describes this test, not a guarantee for every network.
- Sites database/protocol suite: seven tests passed. Windows and Mac builds passed without warnings.

Run the smoke executable with `--direct-controls` to exercise the local Sites adapter and service-outage scenario. The implementation remains compatible with existing discovery endpoints and requires no Sites deployment.
