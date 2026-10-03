# AVI synchronization investigation — 1 October 2026

The updated tester report's repeated AVI correction loop is reproducible without a VM or remote-network delay. The trigger is VLC's native AVI demuxer operating on Watchroom's seekable HTTP media bridge. Switching room AVI playback to VLC's `avformat` demuxer removes the coarse read-ahead timeline in the tested fixture. Windows and Mac room loaders now select it for `.avi` media; other formats and the synchronization policy are unchanged. Mac runtime playback has not been exercised.

## Evidence for the cause

The same generated 180-second, 64×48, 10 fps raw AVI was played directly and through the actual `MediaBridge`/`FileMediaSource`, with native video output, 200 ms caching, no synchronization corrections and a seek to 60 seconds. Native snapshots were requested every 250 ms. Blue-channel frame indices were resolved against the known playback/seek phase, independently of VLC's reported time; colors repeat every 12.8 seconds.

| Input | Median time-event interval | p95 absolute reported-time / sampled-frame difference |
|---|---:|---:|
| Direct-file silent AVI | 250 ms | 174 ms |
| HTTP silent AVI, original demux | 1,500 ms | 1,402 ms |
| HTTP AVI with added PCM audio, original demux | 1,500 ms | 1,412 ms |
| HTTP silent AVI, forced interleaving | 1,500 ms | 1,503 ms |
| HTTP silent AVI, `avformat` demux | 300 ms | 200 ms |

Adding audio did not remove the behavior. Direct-file playback did not reproduce it. This narrows the cause to the input/demux path rather than absence of audio or peer transfer latency. VLC 3.0.23's [AVI demux source](https://raw.githubusercontent.com/videolan/vlc/3.0.23/modules/demux/avi/avi.c) uses a 25 ms read increment for fast-seekable inputs and 1.5 seconds for other seekable inputs. The observed HTTP cadence matches that branch. The controller then mistakes demux read-ahead for playback drift and seeks backward.

The initial callback-output experiments are supporting diagnostics only. Generated H.264/RGB-plus-audio comparison runs had non-advancing frame colors and are explicitly excluded by the analyzer. A raw-RGB Matroska comparison and a silent H.264 callback run did not yield usable measurements. They are not passing codec results. The useful AVI comparison uses normal native video output and saved snapshots.

## Retest after the fix

Both full desktop matrices use separate host/guest profiles on this Windows machine, actual native WebRTC transfer, a local room service, the original silent AVI, 30 seconds of initial steady playback and the existing control sequence. They are **not host-to-VM runs** and are not directly comparable to the earlier VM p95 of 2,186 ms.

| Policy | Playing pairs | Median raw position difference | p95 | Maximum | Pairs over 500 ms |
|---|---:|---:|---:|---:|---:|
| Experimental | 267 | 98 ms | 300 ms | 302 ms | 0 |
| Default | 267 | 100 ms | 300 ms | 401 ms | 0 |

All six aggregate controls pass in both runs: paused seek to 90 seconds, authorized guest seek to 150 seconds, stop to zero, denial before authorization, denial after revocation and execution of every command. Play, pause, UI toggle and shared controls were exercised. No disconnected samples, dropped diagnostic entries, rate-setting failures or non-cancel range errors were recorded. The experimental host/guest issued 6/5 seeks across the entire control matrix rather than the earlier 39/38 repeated seeks; some explicit paused seeks still use the two-second settling fallback.

Both runs produced all 20 scheduled host/guest snapshot pairs. The maximum sampled frame difference was 200 ms in each run, with snapshot request times up to 197 ms apart experimentally and 180 ms under the default policy. This is sparse frame evidence at 10 fps, not a continuous presentation or audio-alignment guarantee. Pair-position analysis matches the nearest guest sample within 150 ms and can reuse a guest sample; experimental host-playing coverage was 99.26%, with 266 distinct guest samples for 267 pairs.

Validation: full solution build has zero warnings/errors; **129 local and 129 public smoke checks pass**, including AVI seeking, pause and replay using the new demux path; **6 worker tests pass**. The final portable Windows application is `artifacts/timing-investigation/release-app/Watchroom.exe`, with the verified Microsoft native runtime bundled.

The VM was retried using the supplied credential file, but VirtualBox returned “The guest execution service is not ready (yet)” twice. Its guest run level remained 1 and screenshot collection also failed. No new VM playback result is claimed. The temporary extracted password file was deleted. The tighter synchronization controller remains opt-in until cross-machine and broader media testing completes.

## Evidence files

All new evidence is under `artifacts/timing-investigation/`: `summary.json`, `native-analysis.log`, probe source and JSONL/snapshots; `desktop-analysis.log` and `desktop/local-pairs.csv` for experimental playback; `default-analysis.log` and `vm-default/local-pairs.csv` for default playback (the prepared folder's name does not mean it ran in the VM); profiles contain unified native diagnostics and frame images. `desktop/frame-pairs.json` contains the experimental frame pairs. Local/public smoke logs and worker test logs are saved alongside them. `desktop/source-hashes.json` records the tested source, and `local-matrix.ps1` records the exact desktop sequence.
