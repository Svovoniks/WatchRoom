using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Watchroom.Core;
using LibVLCSharp.Shared;
using System.Diagnostics;

if (args.FirstOrDefault() == "--desktop-artwork")
{
    var directory = Path.GetFullPath("artifacts/desktop-artwork-check");
    var store = new LibraryStore(directory);
    await store.ScanAsync([new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"), "Mixed")]);
    if (!args.Contains("--scan-only")) Console.WriteLine(await AutomaticArtwork.FetchAsync(store, directory, null, new Progress<string>(Console.WriteLine), CancellationToken.None));
    foreach (var group in store.All().Where(x => x.Available).GroupBy(x => x.DisplayTitle)) Console.WriteLine($"{group.Key} | year={group.First().Year} | episodes={group.Count()} | poster={group.Any(x => File.Exists(x.Poster))}");
    return;
}

if (args.FirstOrDefault() == "--live-artwork")
{
    var directory = Path.GetFullPath("artifacts/live-artwork-check");
    var store = new LibraryStore(directory);
    store.Save(new("live-show", Path.Combine(directory, "Breaking Bad S01E01.mkv"), "Breaking Bad", "Show", 2008, "Breaking Bad", 1, 1));
    store.Save(new("live-movie", Path.Combine(directory, "The Matrix (1999).mkv"), "The Matrix", "Movie", 1999));
    Console.WriteLine(await AutomaticArtwork.FetchAsync(store, directory, null, new Progress<string>(Console.WriteLine), CancellationToken.None));
    foreach (var item in store.All()) Console.WriteLine(item.DisplayTitle + ": " + (File.Exists(item.Poster) ? "image cached" : "no image"));
    Environment.ExitCode = store.All().All(x => File.Exists(x.Poster)) ? 0 : 1; return;
}

if (args.FirstOrDefault() == "--seed-ui")
{
    var folder = Path.GetFullPath("artifacts/demo-media"); Directory.CreateDirectory(folder);
    VideoFixture.Write(Path.Combine(folder, "Watchroom Test.avi"));
    var ui = new LibraryStore(Path.GetFullPath("artifacts/ui-profile"));
    ui.Setting("folders", Wire.Serialize(new[] { new LibraryFolder(folder, "Movie") }));
    await ui.ScanAsync([new(folder, "Movie")]);
    Console.WriteLine("Isolated UI fixture prepared."); return;
}

var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/smoke");
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
Check(MediaBridge.TryRange("bytes=-30", 100, out var s, out var e) && s == 70 && e == 99, "suffix byte ranges");
Check(MediaBridge.TryRange("bytes=40-999", 100, out s, out e) && s == 40 && e == 99, "range end clamped");
Check(!MediaBridge.TryRange("bytes=100-", 100, out _, out _) && !MediaBridge.TryRange("bytes=0-1,3-4", 100, out _, out _), "invalid ranges rejected");
var episode = FileNames.Parse(Path.Combine(root, "[Group] Example.Show.S02E03.1080p.mkv"));
Check(episode.Series == "Example Show" && episode.Season == 2 && episode.Episode == 3, "series filename parsing");
Check(FileNames.Parse(Path.Combine(root, "[Group] Anime - 12 [1080p].mkv"), "Anime").Episode == 12, "anime episode parsing");
var mixedAnime = FileNames.Parse(Path.Combine(root, "[SubsPlease] Super no Ura de Yani Suu Futari - 01 (1080p) [B9F7B295].mkv"), "Mixed");
Check(mixedAnime.Series == "Super no Ura de Yani Suu Futari" && mixedAnime.Episode == 1 && mixedAnime.Kind == "Anime", "Mixed libraries recognize anime release episodes");
using (var titles = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<animetitles><anime aid='1'><title xml:lang='x-jat'>Romaji Title</title><title xml:lang='en'>English Title</title></anime></animetitles>")))
    Check(AnimeTitles.FindAliases(titles, "Romaji.Title").SequenceEqual(new[] { "English Title" }), "AniDB index resolves English aliases locally");
using (var titles = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<animetitles><anime aid='1'><title>Same</title></anime><anime aid='2'><title>Same</title></anime></animetitles>")))
    Check(AnimeTitles.FindAliases(titles, "Same").Length == 0, "ambiguous AniDB titles require a manual match");
Check(SyncMath.TargetPosition(new(1, "a", true, 2000, 1000), 1500) == 2500, "clock based playback target");
var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 57);
var path = Path.Combine(root, "fixture.mp4"); await File.WriteAllBytesAsync(path, bytes);
var library = new LibraryStore(Path.Combine(root, "db"));
await library.ScanAsync([new(root, "Movie")]);
Check(library.All().Any(x => x.Path == path && x.Available), "SQLite scan persistence");
var localPoster = Path.ChangeExtension(path, ".png");
await File.WriteAllBytesAsync(localPoster, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII="));
Check(FileNames.Parse(path).Poster == localPoster, "PNG sidecar poster discovery");
var indexed = library.All().Single(x => x.Path == path);
library.Save(indexed with { Matched = true, Poster = Path.Combine(root, "missing-poster.jpg") });
await library.ScanAsync([new(root, "Movie")]);
Check(library.All().Single(x => x.Path == path).Poster == localPoster, "rescan recovers a missing matched poster from local artwork");
var catalogRoot = Path.Combine(root, "catalog"); Directory.CreateDirectory(catalogRoot);
var showFolder = Path.Combine(catalogRoot, "Example Show (2020)"); var seasonFolder = Path.Combine(showFolder, "Season 02"); Directory.CreateDirectory(seasonFolder);
await File.WriteAllBytesAsync(Path.Combine(showFolder, "poster.png"), ArtworkFixture.Png);
await File.WriteAllBytesAsync(Path.Combine(seasonFolder, "poster.jpg"), ArtworkFixture.Png);
var folderEpisode = FileNames.Parse(Path.Combine(seasonFolder, "S02E03.mkv"));
Check(folderEpisode.Series == "Example Show" && folderEpisode.Season == 2 && folderEpisode.SeriesPoster == Path.Combine(showFolder, "poster.png"), "series folder names and parent posters recognized");
var catalogItems = new[] { folderEpisode, folderEpisode with { Id = "second", Season = 1, Episode = 10 }, folderEpisode with { Id = "third", Season = 1, Episode = 2 } };
Check(LibraryCatalog.Browse(catalogItems).Single().Level == "series" && LibraryCatalog.Browse(catalogItems, "Example Show", "Show").Select(x => x.Season).SequenceEqual(new int?[] { 1, 2 }), "show to season hierarchy sorted numerically");
Check(LibraryCatalog.Browse(catalogItems, "Example Show", "Show", 1).Select(x => x.Media.Episode).SequenceEqual(new int?[] { 2, 10 }), "season contains only its episodes in numeric order");
var artworkStore = new LibraryStore(Path.Combine(root, "automatic-artwork"));
artworkStore.Save(new("show1", Path.Combine(root, "show1.mkv"), "Example Show", "Show", 2020, "Example Show", 1, 1));
artworkStore.Save(new("show2", Path.Combine(root, "show2.mkv"), "Example Show", "Show", 2020, "Example Show", 2, 1));
artworkStore.Save(new("movie1", Path.Combine(root, "movie1.mkv"), "Example Movie", "Movie", 2020));
using (var artworkHttp = new HttpClient(new ArtworkFixture()))
{
    await AutomaticArtwork.FetchAsync(artworkStore, Path.Combine(root, "automatic-artwork"), null, null, CancellationToken.None, artworkHttp);
    Check(artworkStore.All().All(x => File.Exists(x.Poster) && x.PosterSource is not null), "token-free movie and series posters downloaded and persisted");
    Check(artworkStore.All().Where(x => x.Series is not null).Select(x => x.SeriesPoster).Distinct().Count() == 1, "automatic series artwork shared across seasons");
}
var file = new FileMediaSource(path);
await using var localBridge = new MediaBridge();
var localUrl = await localBridge.StartAsync(file);
using var http = new HttpClient();
var request = new HttpRequestMessage(HttpMethod.Get, localUrl); request.Headers.Range = new RangeHeaderValue(110, 543);
using var response = await http.SendAsync(request);
Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[110..544]), "HTTP byte-range content and status");
Check((await http.GetAsync(new Uri(localUrl, "/bad/media"))).StatusCode == HttpStatusCode.NotFound, "bridge authorization token required");

using var host = new PeerTransport([]); using var guest = new PeerTransport([]);
// Signaling is serialized like the WebSocket connection; avoid native callback reentrancy.
var signals = System.Threading.Channels.Channel.CreateUnbounded<(PeerTransport Peer, string Type, string Data)>();
host.Signal += (t, d) => signals.Writer.TryWrite((guest, t, d));
guest.Signal += (t, d) => signals.Writer.TryWrite((host, t, d));
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var pump = Task.Run(async () => { await foreach (var x in signals.Reader.ReadAllAsync(cts.Token)) x.Peer.ReceiveSignal(x.Type, x.Data); });
host.ResolveMedia = id => id == file.Media.Id ? file : null;
host.StartOffer();
await Task.WhenAll(host.Ready, guest.Ready).WaitAsync(TimeSpan.FromSeconds(20));
Check(true, "native WebRTC peers connect");
var remote = new RemoteMediaSource(guest, file.Media);
await using var remoteBridge = new MediaBridge(); var remoteUrl = await remoteBridge.StartAsync(remote);
var received = await http.GetByteArrayAsync(remoteUrl);
Check(received.SequenceEqual(bytes), "full encrypted peer transfer through HTTP bridge");
var seek = new HttpRequestMessage(HttpMethod.Get, remoteUrl); seek.Headers.Range = new RangeHeaderValue(bytes.Length - 101, null);
using var sought = await http.SendAsync(seek);
Check((await sought.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[^101..]), "remote seek to end of file");
bool denied = false;
try { await guest.ReadAsync("not-shared", 0, 32, CancellationToken.None); } catch (IOException) { denied = true; }
Check(denied, "unshared media denied by host");
var videoPath = Path.Combine(root, "Watchroom Test.avi"); VideoFixture.Write(videoPath);
var videoSource = new FileMediaSource(videoPath);
host.ResolveMedia = id => id == file.Media.Id ? file : id == videoSource.Media.Id ? videoSource : null;
await using var videoBridge = new MediaBridge(); var videoUrl = await videoBridge.StartAsync(new RemoteMediaSource(guest, videoSource.Media));
Core.Initialize();
using (var vlc = new LibVLC("--vout=dummy", "--aout=dummy", "--no-video-title-show", "--quiet"))
using (var player = new MediaPlayer(vlc))
using (var videoMedia = new Media(vlc, videoUrl))
{
    player.Play(videoMedia);
    await Wait(() => player.Time > 1000, "LibVLC remote playback");
    Check(player.Length >= 11000, "LibVLC reads AVI duration through peer bridge");
    player.Time = 7000; await Wait(() => player.Time >= 6900, "LibVLC remote seeking");
    player.SetPause(true); await Task.Delay(350); var paused = player.Time; await Task.Delay(350);
    Check(Math.Abs(player.Time - paused) < 200, "LibVLC pause remains stable"); player.Stop();
}
cts.Cancel(); try { await pump; } catch (OperationCanceledException) { }

var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var externalServer = Environment.GetEnvironmentVariable("WATCHROOM_TEST_SERVER");
if (string.IsNullOrWhiteSpace(externalServer)) externalServer = null;
var serverUrl = externalServer ?? $"http://localhost:{port}";
var start = new ProcessStartInfo(Path.GetFullPath(".tools/dotnet/dotnet.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add(Path.GetFullPath("src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(serverUrl);
using var server = externalServer is null ? Process.Start(start)! : null;
var stdout = server?.StandardOutput.ReadToEndAsync(); var stderr = server?.StandardError.ReadToEndAsync();
try
{
    for (int i = 0; i < 80; i++) { try { if ((await http.GetAsync(serverUrl + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(100); }
    await using var roomHost = new RoomClient(); await using var roomGuest = new RoomClient();
    var hostMessages = new ConcurrentQueue<WireMessage>(); var guestMessages = new ConcurrentQueue<WireMessage>();
    roomHost.Message += hostMessages.Enqueue; roomGuest.Message += guestMessages.Enqueue;
    roomHost.HostedMedia = file;
    await roomHost.ConnectAsync(serverUrl, "Test Host"); await Wait(() => roomHost.Identity is not null, "room created");
    roomHost.Send(new("media", Data: Wire.Serialize(file.Media)));
    await roomGuest.ConnectAsync(serverUrl, "Test Guest", roomHost.Identity!.Room);
    await Wait(() => roomGuest.Identity is not null && roomGuest.Snapshot is not null, "guest waiting for admission");
    Check(roomGuest.Snapshot!.Media is null, "pending guests cannot see shared media");
    roomGuest.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, file.Media.Id, true, 1000, 0))));
    await Task.Delay(150); Check(!hostMessages.Any(x => x.Type == "playback"), "pending guests cannot control playback");
    roomHost.Send(new("admit", Target: roomGuest.Identity!.Peer));
    await Wait(() => roomGuest.Snapshot?.Media is not null, "guest admitted");
    var roomSource = await roomGuest.GetSourceAsync(file.Media, CancellationToken.None);
    Check((await roomSource.ReadAsync(900, 100, CancellationToken.None)).SequenceEqual(bytes[900..1000]), "signaled room serves host media");
    roomGuest.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, file.Media.Id, true, 1000, 0))));
    await Task.Delay(150); Check(!hostMessages.Any(x => x.Type == "playback"), "admitted guest respects host-only controls");
    roomHost.Send(new("ready")); roomGuest.Send(new("ready"));
    await Wait(() => roomHost.Snapshot!.People.All(x => x.Ready), "all participants ready");
    roomHost.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, file.Media.Id, true, 2000, 0))));
    await Wait(() => guestMessages.Any(x => x.Type == "playback"), "host playback broadcast");
    var playback = Wire.Read<PlaybackState>(guestMessages.Last(x => x.Type == "playback").Data!);
    Check(playback.Revision > 0 && playback.AtUnixMs > 0 && playback.PositionMs == 2000, "server assigns playback revision and clock");
    roomGuest.Send(new("buffering"));
    await Wait(() => guestMessages.Any(x => x.Type == "notice"), "buffering pauses room");
    Check(!Wire.Read<PlaybackState>(guestMessages.Last(x => x.Type == "playback").Data!).Playing, "shared buffering state paused");
    roomGuest.Send(new("ready"));
    await Wait(() => Wire.Read<PlaybackState>(guestMessages.Last(x => x.Type == "playback").Data!).Playing, "automatic resume after buffering");
    roomHost.Send(new("queue", Data: Wire.Serialize(new[] { "Next movie", "Episode two" })));
    await Wait(() => roomGuest.Snapshot?.Queue?.Length == 2, "shared queue visible to admitted guests");
    roomGuest.Send(new("queue", Data: Wire.Serialize(new[] { "Unauthorized change" })));
    await Task.Delay(100); Check(roomHost.Snapshot!.Queue![0] == "Next movie", "guests cannot overwrite host queue");
    var subtitleText = "1\n00:00:00,000 --> 00:00:04,000\nWatchroom subtitle test\n";
    await File.WriteAllTextAsync(Path.ChangeExtension(videoPath, ".srt"), subtitleText);
    roomHost.SetHostedFile(videoPath, "Video with subtitle");
    roomHost.Send(new("media", Data: Wire.Serialize(roomHost.HostedMedia!.Media)));
    await Wait(() => roomGuest.Snapshot?.Media?.Subtitles?.Length == 1, "external subtitle advertised without local path");
    var subtitleSource = await roomGuest.GetSourceAsync(roomGuest.Snapshot!.Media!.Subtitles![0], CancellationToken.None);
    Check(System.Text.Encoding.UTF8.GetString(await subtitleSource.ReadAsync(0, (int)subtitleSource.Media.Length, CancellationToken.None)) == subtitleText, "subtitle transferred over authorized peer channel");
    roomHost.Send(new("remove", Target: roomGuest.Identity.Peer));
    await Wait(() => roomHost.Snapshot!.People.All(x => x.Id != roomGuest.Identity.Peer), "guest removed");
    bool revoked = false; try { await roomSource.ReadAsync(100, 100, CancellationToken.None); } catch { revoked = true; }
    Check(revoked, "removal revokes existing media channel");
    await using var wrongInvitation = new RoomClient();
    var errors = new ConcurrentQueue<WireMessage>(); wrongInvitation.Message += errors.Enqueue;
    if (externalServer is null)
    {
        await wrongInvitation.ConnectAsync(serverUrl, "Unknown Guest", "NOT-A-ROOM");
        await Wait(() => errors.Any(x => x.Type == "error"), "unknown invitations rejected");
    }
    else
    {
        bool rejected = false;
        try { await wrongInvitation.ConnectAsync(serverUrl, "Unknown Guest", "NOT-A-ROOM"); } catch (IOException) { rejected = true; }
        Check(rejected, "unknown invitations rejected");
    }
}
finally { if (server is not null) { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); } }
Console.WriteLine($"{passed} checks passed.");

async Task Wait(Func<bool> condition, string name)
{
    var until = DateTime.UtcNow.AddSeconds(20);
    while (!condition()) { if (DateTime.UtcNow > until) throw new Exception("Timeout: " + name); await Task.Delay(50); }
    Check(true, name);
}
