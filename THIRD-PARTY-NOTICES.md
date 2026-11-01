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
| WebRTC audio processing, `webrtc-apm.dll` (voice echo cancellation and gain control; BSD-3-Clause, C interface MIT) | 2.1, from SoundFlow.Extensions.WebRtc.Apm 1.4.0 | https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing, https://github.com/LSXPrime/SoundFlow |
| RNNoise, `rnnoise.dll` built from the release (lighter voice noise removal; BSD-3-Clause) | 0.2 | https://github.com/xiph/rnnoise |
| DeepFilterNet 3 and its Rust dependencies, `deepfilter.dll` built from `src/native/deepfilter` (voice noise removal; MIT or Apache-2.0) | revision d375b2d8 | https://github.com/Rikorose/DeepFilterNet |
| Silero VAD model `silero_vad.onnx` (voice speech detection; MIT) | 6.2.3 | https://github.com/snakers4/silero-vad |
| ONNX Runtime (runs the speech detection model; MIT) | 1.31.1 | https://github.com/microsoft/onnxruntime |
| SkiaSharp / native Skia (artwork decoding) | 2.88.9 | https://github.com/mono/SkiaSharp |
| TagLib# (reading music tags and embedded album art; LGPL-2.1) | 2.3.0 | https://github.com/mono/taglib-sharp |
| Whisper.net / whisper.cpp runtime (spoken-language identification) | 1.9.1 | https://github.com/sandrohanea/whisper.net |
| Whisper tiny model, ggml `ggml-tiny-q5_1.bin` (MIT, OpenAI) | whisper.cpp revision 5359861c | https://huggingface.co/ggerganov/whisper.cpp |
| Panlingo CLD3 / Google CLD3 (subtitle-language identification) | 0.8.1 | https://github.com/gluschenko/panlingo, https://github.com/google/cld3 |
| TesseractOCR / Tesseract 5.5 and Leptonica 1.85 natives (reading picture subtitles) | 5.5.2 | https://github.com/Sicos1977/TesseractOCR, https://github.com/tesseract-ocr/tesseract, http://www.leptonica.org |
| Tesseract script models, `tessdata_fast` without word lists (Apache-2.0) | revision 87416418 | https://github.com/tesseract-ocr/tessdata_fast |
| Microsoft.Data.Sqlite | 10.0.12 | https://github.com/dotnet/efcore |
| SQLitePCLRaw | 3.0.5 | https://github.com/ericsink/SQLitePCL.raw |
| .NET / WPF | Runtime selected by .NET 10 SDK | https://github.com/dotnet |
| Microsoft Visual C++ x64 runtime | 14.44.35211.0 (pinned signed redistributable) | https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files |

Licenses supplied by downloaded packages are copied into `licenses/` by the packaging script. LibVLC plugins can have different licenses from the embedding API; review the exact Windows build and provide corresponding source/required notices before public redistribution. This local preview is not a completed redistribution-license audit.

The Windows publisher extracts app-local x64 runtime DLLs from Microsoft's signed redistributable, verifies their signatures, and records provenance in `native-runtime.json`. These files remain subject to Microsoft's Visual Studio redistribution terms; distribution of the Microsoft runtime requires the applicable Visual Studio license.

TMDB supplies optional metadata and images. The official, unmodified blue full logo is displayed in About on Windows. This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB. Logo source: https://www.themoviedb.org/assets/2/v4/logos/v2/blue_long_2-9665a76b1ae401a510ec1e0ca40ddcb3b0cfe45f1d51b77a308fea0845885648.svg . Terms: https://www.themoviedb.org/api-terms-of-use .

Streaming-provider data used to disambiguate release filenames is supplied by **JustWatch** through TMDB. The app credits JustWatch in About/Settings on Windows. Source: https://www.justwatch.com/ . API attribution requirements: https://developer.themoviedb.org/reference/movie-watch-providers .
