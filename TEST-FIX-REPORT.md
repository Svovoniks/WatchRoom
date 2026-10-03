**Latest rerun:** current-source build clean; 129 local + 129 public smoke checks and 6 room-service tests pass. Same-machine original-AVI p95 is 200 ms by default and 301 ms experimentally; both control matrices pass 6/6 and have no pairs above 500 ms. VM guest service still unavailable. See TEST-REPORT.html and artifacts/sync-rerun for fresh evidence.

**Latest investigation:** HTTP AVI demux read-ahead reproduced and fixed by selecting avformat for room AVI playback. Same-machine default and experimental matrices both have raw pair-position p95 300 ms and all six control assertions pass. 129 local + 129 public smoke checks and 6 worker tests pass; build has zero warnings/errors. VM retest remains blocked by the guest execution service. See [AVI-INVESTIGATION.md](AVI-INVESTIGATION.md) for complete evidence.

> Follow-up status (1 October 2026): replay and smoke-test queue race fixed; final source passes 129 local and 129 public smoke checks plus six Node tests. Experimental H.264/AAC public-room p95 is 421 ms; silent AVI still fails at 2,186 ms. Tighter tuning is opt-in. Final default-policy VM retest is unverified while the VM completes a Windows update. See TEST-REPORT.html for current evidence; the older notes below are historical.

# Fixes and retest — 1 October 2026

The confirmed clock, runtime packaging, transient coordination, and buffering feedback defects have been addressed. Automated checks and control tests pass. **Reliable live synchronization is still not fully signed off:** the complete desktop matrix retains some drift, and the original host-to-VM retest needs the missing guest-control credential file.

## Changes

- **Clock:** RTT and advancing server time use a monotonic clock. Responses must match an outstanding probe; stale/invalid probes are ignored, and low-latency samples expire after ten seconds. Windows and Mac use `ServerNowMs` directly, so local wall-clock changes cannot move their playback target. Deterministic regressions cover forward/backward clock jumps and replacement of old measurements.
- **Windows deployment:** portable publishing and installer packaging now extract app-local x64 runtime DLLs from Microsoft's signed Visual C++ 14.44.35211 redistributable. The payload hash and individual DLL signatures are checked. The publish contains `msvcp140.dll`, `vcruntime140.dll`, and `vcruntime140_1.dll`, plus the accompanying x64 runtime files. Missing native dependencies produce a specific error. `native-runtime.json` records provenance.
- **Coordination:** transient HTTP failures retry for up to 30 seconds, preserving the session and event cursor. Updated services atomically deduplicate commands by request ID, including a lost response after a committed database write. Legacy services do not receive ambiguous playback retries. Expired/revoked sessions remain terminal.
- **Public service:** production logs located the earlier 503s in D1 read/write operations. Health now queries the database, and unexpected errors log the route, message, cause, and request identifier. The updated service was published successfully as version 4 at https://watchroom-rooms.svovoniks.chatgpt.site, preserving public access. Recovery handles transient infrastructure errors; it does not guarantee D1 never fails.
- **Buffering:** a native seek gets a two-second settling window even when VLC immediately reports the requested time. Playback starts/resumes also receive that grace period. The cache was reduced from 1,500 to 200 ms; only buffering sustained for at least 500 ms pauses the room. Brief seek buffering events no longer trigger room pause/resume feedback. Genuine sustained starvation still pauses and recovers.
- **Additional defects exposed by retesting:** the local server now waits for an error to be sent before closing rejected connections; native cleanup of a departed guest cannot terminate host coordination; repeated client/transport disposal is safe.

## Results

| Check | Result |
| --- | --- |
| Complete Release solution build | Passed; zero warnings and errors |
| Final smoke executable with the workspace SDK | **118 checks passed** |
| Room-service database/protocol tests | **6 tests passed** |
| Public admission and native original-file streaming | Passed; 32,768-byte RIFF read |
| Public host play and authorized guest seek | Passed; both clients received the states |
| Public guest control before authorization | Blocked as expected |
| Public coordination stability and guest departure | Passed; host stayed connected after guest left; helper exited successfully |
| Two desktop instances, local server, full control matrix | Passed; both stayed connected; paused seeks converged; final stop returned both to zero |
| Guest seek with shared controls disabled, then enabled, then disabled again | Blocked, accepted, blocked as expected |
| Portable publish and installer build | Passed; required MSVC DLLs included |
| Original host-to-VM comparison and clean VM install | **Not rerun**; guest-control password file missing |

The desktop matrix used isolated profiles and current-source test copies. Both players used the actual WPF handlers and native peer transport to play the generated host media. No personal media or VM security/network settings were changed. Test commands were transferred atomically to avoid the previous command-file sharing race.

### Live playback measurements

The complete matrix produced **191 paired playing samples**, with the same playback revision and reported server times separated by at most 150 ms:

| Measurement | Result |
| --- | --- |
| Median position difference | **111 ms** |
| 95th percentile | **1,510 ms** |
| Maximum | **2,757 ms** |
| Differences over 1,200 ms | **22 samples** |

The initial 30-second playback interval had a 108 ms median and approximately 1,088 ms at the 95th percentile. The complete matrix, including playback after seeks, is worse; the initial interval alone must not be presented as a synchronization pass. Paused samples are excluded from these live statistics. The 150 ms pairing window contributes uncertainty, and VLC time values do not directly measure audio or displayed-frame alignment.

The fixes remove the demonstrated clock poisoning and repeated room pause feedback. **Remaining drift during native playback/seek transitions still needs investigation and the VM repeat.** These results do not validate audio, realistic movie codecs/bitrates, internet peers on different machines, or TURN relay behavior.

## Outputs and evidence

- Portable app: `artifacts/Watchroom/Watchroom.exe`
- Installer: `artifacts/Watchroom-Setup-0.1.0-win-x64.exe`
- Installer SHA-256: `0FA4CB60A928E746A4D78094D5996671C9593CEC7E05B64B74F7DD26E360BFE3`
- `artifacts/retest/final-smoke.log`
- `artifacts/retest/public-retest.log`
- `artifacts/retest/results.json` and `phases.jsonl`
- `artifacts/retest/startup-host` and `startup-guest`: action, playback, seek, and buffering traces
- `artifacts/retest/src`: isolated instrumentation of the final source

The local test service remains available at `http://localhost:5081`; the final desktop test instances are paused at zero. Existing workspace edits were preserved. No application Git commit or PR was created.

To enable the VM repeat, recreate `artifacts/vm-sync-test/guest-password.tmp` with the existing `vmuser` password, or provide a path to an existing credential file. Do not paste the password into chat.
