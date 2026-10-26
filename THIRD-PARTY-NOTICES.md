# Third-party components

Anime title aliases use AniDB's public title index (https://anidb.net/api/anime-titles.xml.gz), cached locally with a maximum daily download attempt. AniDB metadata and images are not fetched through its registered HTTP API. AniDB does not endorse Watchroom.

Automatic series artwork uses [TVmaze](https://www.tvmaze.com), whose API data is provided under [CC BY-SA](https://www.tvmaze.com/api#licensing). Movie artwork uses Wikipedia page images. Images retain their individual copyright and license terms; each downloaded poster's source page is shown in the title details. These services do not endorse Watchroom.

Watchroom dynamically links the following components. Package versions and content hashes are recorded in each project's `packages.lock.json`.

| Component | Version | Upstream |
| --- | --- | --- |
| LibVLCSharp / WPF | 3.10.1 | https://code.videolan.org/videolan/LibVLCSharp |
| VideoLAN LibVLC Windows | 3.0.23.1 | https://code.videolan.org/videolan/vlc |
| DataChannelDotnet | 1.3.1 | https://www.nuget.org/packages/DataChannelDotnet |
| libdatachannel and bundled OpenSSL | Included in DataChannelDotnet | https://github.com/paullouisageneau/libdatachannel |
| Concentus (managed Opus voice codec) | 2.2.2 | https://github.com/lostromb/concentus |
| NAudio Core / WASAPI (voice microphone and playback) | 3.3.0 | https://github.com/naudio/NAudio |
| SkiaSharp / native Skia (artwork decoding) | 2.88.9 | https://github.com/mono/SkiaSharp |
| Whisper.net / whisper.cpp runtime (spoken-language identification) | 1.9.1 | https://github.com/sandrohanea/whisper.net |
| Whisper tiny model, ggml `ggml-tiny-q5_1.bin` (MIT, OpenAI) | whisper.cpp revision 5359861c | https://huggingface.co/ggerganov/whisper.cpp |
| Panlingo CLD3 / Google CLD3 (subtitle-language identification) | 0.8.1 | https://github.com/gluschenko/panlingo, https://github.com/google/cld3 |
| Microsoft.Data.Sqlite | 10.0.12 | https://github.com/dotnet/efcore |
| SQLitePCLRaw | 3.0.5 | https://github.com/ericsink/SQLitePCL.raw |
| .NET / WPF | Runtime selected by .NET 10 SDK | https://github.com/dotnet |
| Microsoft Visual C++ x64 runtime | 14.44.35211.0 (pinned signed redistributable) | https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files |

Licenses supplied by downloaded packages are copied into `licenses/` by the packaging script. LibVLC plugins can have different licenses from the embedding API; review the exact Windows build and provide corresponding source/required notices before public redistribution. This local preview is not a completed redistribution-license audit.

The Windows publisher extracts app-local x64 runtime DLLs from Microsoft's signed redistributable, verifies their signatures, and records provenance in `native-runtime.json`. These files remain subject to Microsoft's Visual Studio redistribution terms; distribution of the Microsoft runtime requires the applicable Visual Studio license.

TMDB supplies optional metadata and images. The official, unmodified blue full logo is displayed in About on Windows. This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB. Logo source: https://www.themoviedb.org/assets/2/v4/logos/v2/blue_long_2-9665a76b1ae401a510ec1e0ca40ddcb3b0cfe45f1d51b77a308fea0845885648.svg . Terms: https://www.themoviedb.org/api-terms-of-use .

Streaming-provider data used to disambiguate release filenames is supplied by **JustWatch** through TMDB. The app credits JustWatch in About/Settings on Windows. Source: https://www.justwatch.com/ . API attribution requirements: https://developer.themoviedb.org/reference/movie-watch-providers .
