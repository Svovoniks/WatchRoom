# Third-party components

Watchroom dynamically links the following components. Package versions and content hashes are recorded in each project's `packages.lock.json`.

| Component | Version | Upstream |
| --- | --- | --- |
| LibVLCSharp / WPF | 3.10.1 | https://code.videolan.org/videolan/LibVLCSharp |
| VideoLAN LibVLC Windows | 3.0.23.1 | https://code.videolan.org/videolan/vlc |
| DataChannelDotnet | 1.3.1 | https://www.nuget.org/packages/DataChannelDotnet |
| libdatachannel and bundled OpenSSL | Included in DataChannelDotnet | https://github.com/paullouisageneau/libdatachannel |
| Microsoft.Data.Sqlite | 10.0.12 | https://github.com/dotnet/efcore |
| SQLitePCLRaw | 3.0.5 | https://github.com/ericsink/SQLitePCL.raw |
| .NET / WPF | Runtime selected by .NET 10 SDK | https://github.com/dotnet |

Licenses supplied by downloaded packages are copied into `licenses/` by the packaging script. LibVLC plugins can have different licenses from the embedding API; review the exact Windows build and provide corresponding source/required notices before public redistribution. This local preview is not a completed redistribution-license audit.

FFmpeg is currently an optional user-supplied executable and is not bundled. TMDB is an optional metadata service: this product uses the TMDB API but is not endorsed or certified by TMDB. TMDB imagery requires its attribution and branding requirements; the official TMDB logo must be included before publishing a metadata-enabled release.
