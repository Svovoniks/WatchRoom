# Watchroom macOS preview

A macOS desktop client with folder setup, poster browsing, grouped episodes, local LibVLC playback, hosting/joining rooms, admission/removal, shared controls, synchronized play/pause/seek, audio/subtitle selection, sidecar subtitle streaming, queues and chat. It shares the Windows app's core and supports both the original WebSocket server and the Sites HTTP room service.

## Download and launch

Use the Apple Silicon `Watchroom-Mac-arm64-0.2.0.zip` for M1/M2/M3/M4 and later Macs, or `Watchroom-Mac-x64-0.2.0.zip` for Intel Macs. Target: macOS 14 or later. The .NET runtime is included.

This preview requires native media dependencies on your Mac:

1. Install VLC from https://www.videolan.org/vlc/ into `/Applications/VLC.app`, using the download matching your Mac architecture.
2. Install libdatachannel for the same architecture. With Homebrew installed, run `brew install libdatachannel`. Standard Homebrew locations `/opt/homebrew/lib/libdatachannel.dylib` and `/usr/local/lib/libdatachannel.dylib` are recognized.
3. Extract the ZIP and move `Watchroom.app` to Applications, then open it. The preview is unsigned and not notarized; follow macOS's normal user-approved process for opening locally trusted development software if prompted.
4. Add your movie folders on the Folders tab and scan. Choose a title and Play locally, or Watch together. Friends can join without adding folders.

VLC and libdatachannel are not bundled. The Intel NuGet dylib is present as a fallback, but its dependent native libraries are not validated on a clean Mac. Installing the matching Homebrew build is the recommended setup for both architectures. Rosetta builds must use Intel dependencies; a native Apple Silicon app must use arm64 dependencies.

For a nonstandard installation, set `WATCHROOM_VLC_PATH` to VLC's `Contents/MacOS/lib` directory and/or `WATCHROOM_DATACHANNEL_NATIVE` to an absolute matching dylib path before launching the executable. The VLC plugin directory is discovered alongside its library directory.

You can check native dependencies from Terminal:

```sh
/Applications/Watchroom.app/Contents/MacOS/Watchroom.Mac --diagnostics
```

## Watch with Windows friends

Both clients use the same room protocol. Save your display name and room server in Settings. Host a selected movie, copy the invitation, and admit the guest. The guest pastes the invitation in Settings and chooses Join room. A room's host controls playback until shared controls are enabled.

The current Sites deployment remains private and its browser sign-in gate blocks both Windows and Mac native clients. It must be explicitly made accessible to native clients, or an authentication integration added, before using its public URL in the app. The Mac work does not change Site access. A separate TURN relay remains necessary for dependable internet connectivity; network-separated Mac/Windows playback has not been tested.

For tests on one Mac, run the local coordination server at `http://localhost:5080`. For multiple computers, use a reachable HTTPS deployment. `localhost` always means the computer running the client. Test two independent app profiles by setting `WATCHROOM_DATA` to different directories before launching each executable. Keep your host Mac awake during a shared movie; automatic power assertions are not implemented in this preview.

## Build from source

Requires .NET 10 SDK. No Xcode-specific workload is required for the Avalonia build.

```sh
dotnet publish src/Watchroom.Mac -c Release -r osx-arm64 --self-contained true --configfile NuGet.Config -o artifacts/Watchroom-Mac-arm64
python3 scripts/package-mac.py --arch arm64
```

Use `osx-x64` and `--arch x64` for Intel. Windows can cross-publish with the same commands; `scripts/build-mac.ps1` is a wrapper. ZIP packaging preserves Unix executable permissions and checks the Mach-O architecture and bundle manifest. Signing and notarization require a Mac and Apple signing credentials.

Data is stored under `~/Library/Application Support/Watchroom`, or `WATCHROOM_DATA` when set. Library folders and settings are saved locally; TMDB tokens stay in memory. Local poster images are supported without an API token. Manual metadata matching uses the same TMDB API as the Windows client.

## Verification and remaining work

Both architectures were cross-compiled and packaged on Windows. The common core's 37 smoke checks pass. The Avalonia interface was inspected on Windows using a generated test movie; these checks do not validate macOS playback, dylib loading, file permissions, or Mac-to-Windows synchronization.

No Mac was available for a launch test. Run `--diagnostics` and verify local playback, hosting and joining on real Intel/Apple Silicon Macs before treating these builds as validated. Mac code signing/notarization, bundled native dependency redistribution, automatic updates, sleep prevention, FFmpeg preparation, light theme, and TMDB branding verification remain open. The existing Windows preview remains available.
