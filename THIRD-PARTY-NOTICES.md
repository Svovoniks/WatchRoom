# Third-party components

Anime title aliases use AniDB's public title index (https://anidb.net/api/anime-titles.xml.gz), cached locally with a maximum daily download attempt. AniDB metadata and images are not fetched through its registered HTTP API. AniDB does not endorse Watchroom.

Automatic series artwork uses [TVmaze](https://www.tvmaze.com), whose API data is provided under [CC BY-SA](https://www.tvmaze.com/api#licensing). Movie artwork uses Wikipedia page images. Images retain their individual copyright and license terms; each downloaded poster's source page is shown in the title details. These services do not endorse Watchroom.

Watchroom dynamically links the following components. Package versions and content hashes are recorded in each project's `packages.lock.json`.

| Component | Version | Upstream |
| --- | --- | --- |
| LibVLCSharp / WPF | 3.10.1 | https://code.videolan.org/videolan/LibVLCSharp |
| LibVLCSharp / Avalonia (Mac client) | 3.10.1 | https://github.com/videolan/libvlcsharp |
| Avalonia (Mac client) | 11.3.13 | https://github.com/AvaloniaUI/Avalonia |
| Tmds.DBus.Protocol (Mac client dependency) | 0.21.3 | https://github.com/tmds/Tmds.DBus |
| VideoLAN LibVLC Windows | 3.0.23.1 | https://code.videolan.org/videolan/vlc |
| DataChannelDotnet | 1.3.1 | https://www.nuget.org/packages/DataChannelDotnet |
| libdatachannel and bundled OpenSSL | Included in DataChannelDotnet | https://github.com/paullouisageneau/libdatachannel |
| Microsoft.Data.Sqlite | 10.0.12 | https://github.com/dotnet/efcore |
| SQLitePCLRaw | 3.0.5 | https://github.com/ericsink/SQLitePCL.raw |
| .NET / WPF | Runtime selected by .NET 10 SDK | https://github.com/dotnet |

Licenses supplied by downloaded packages are copied into `licenses/` by the packaging script. LibVLC plugins can have different licenses from the embedding API; review the exact Windows build and provide corresponding source/required notices before public redistribution. This local preview is not a completed redistribution-license audit.

FFmpeg is currently an optional user-supplied executable and is not bundled. TMDB is an optional metadata service: this product uses the TMDB API but is not endorsed or certified by TMDB. TMDB imagery requires its attribution and branding requirements; the official TMDB logo must be included before publishing a metadata-enabled release.

The Mac preview uses a user-installed VLC and libdatachannel build. Apple Silicon has no bundled DataChannelDotnet native library; Intel includes the package's fallback dylib. The Mac bundle packaging copies available dependency license/provenance files, but signing, notarization and a complete native redistribution audit remain open.
