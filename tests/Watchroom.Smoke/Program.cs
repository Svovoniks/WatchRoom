using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Watchroom.Core;
using LibVLCSharp.Shared;
using System.Diagnostics;

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
RoomLibraryChecks.Run(Check);
Check(MediaBridge.TryRange("bytes=-30", 100, out var s, out var e) && s == 70 && e == 99, "suffix byte ranges");
Check(MediaBridge.TryRange("bytes=40-999", 100, out s, out e) && s == 40 && e == 99, "range end clamped");
Check(!MediaBridge.TryRange("bytes=100-", 100, out _, out _) && !MediaBridge.TryRange("bytes=0-1,3-4", 100, out _, out _), "invalid ranges rejected");
var episode = FileNames.Parse(Path.Combine(root, "[Group] Example.Show.S02E03.1080p.mkv"));
Check(episode.Series == "Example Show" && episode.Season == 2 && episode.Episode == 3, "series filename parsing");
Check(FileNames.Parse(Path.Combine(root, "[Group] Anime - 12 [1080p].mkv"), "Anime").Episode == 12, "anime episode parsing");
Check(SyncMath.TargetPosition(new(1, "a", true, 2000, 1000), 1500) == 2500, "clock based playback target");
var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 57);
var path = Path.Combine(root, "fixture.mp4"); await File.WriteAllBytesAsync(path, bytes);
var library = new LibraryStore(Path.Combine(root, "db"));
await library.ScanAsync([new(root, "Movie")]);
Check(library.All().Any(x => x.Path == path && x.Available), "SQLite scan persistence");
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
await Task.WhenAll(host.ControlReady, guest.ControlReady).WaitAsync(TimeSpan.FromSeconds(20));
var controlReceived = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
host.ControlMessage += message => controlReceived.TrySetResult(message);
guest.SendControl(new("library-view", Data: Wire.Serialize(new GuestLibraryView(1, true, "", "Movie", 2, 2, 0, ["a", "b", "c", "d"], "a", "d"))));
Check((await controlReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type == "library-view", "separate native WebRTC library control channel");
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
var serverUrl = $"http://localhost:{port}";
var sdkPath = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
var start = new ProcessStartInfo(sdkPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add(Path.GetFullPath("src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(serverUrl);
using var server = Process.Start(start)!;
var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
try
{
    for (int i = 0; i < 80; i++) { try { if ((await http.GetAsync(serverUrl + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(100); }
    await using var roomHost = new RoomClient(); await using var roomGuest = new RoomClient();
    var libraryHostReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var libraryGuestReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var libraryReceived = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    roomHost.LibraryPeerReady += id => libraryHostReady.TrySetResult(id);
    roomGuest.LibraryPeerReady += id => libraryGuestReady.TrySetResult(id);
    roomHost.LibraryMessage += (_, message) => libraryReceived.TrySetResult(message);
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
    await Task.WhenAll(libraryHostReady.Task, libraryGuestReady.Task).WaitAsync(TimeSpan.FromSeconds(20));
    roomGuest.SendLibrary(roomHost.Identity.Peer, new("library-command", Data: "direct"));
    var direct = await libraryReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(direct.Sender == roomGuest.Identity.Peer && !hostMessages.Any(m => m.Type == "library-command"), "room negotiates library capability and bypasses server for guest commands");
    var peerHostLibrary = new RoomLibrary(true, roomHost.Identity.Peer);
    var peerGuestLibrary = new RoomLibrary(false, roomGuest.Identity.Peer);
    var libraryTraffic = new ConcurrentQueue<Action>();
    roomHost.LibraryMessage += (id, message) => libraryTraffic.Enqueue(() => peerHostLibrary.Receive(id, message));
    roomGuest.LibraryMessage += (id, message) => libraryTraffic.Enqueue(() => peerGuestLibrary.Receive(id, message));
    peerHostLibrary.Send += roomHost.SendLibrary; peerGuestLibrary.Send += roomGuest.SendLibrary;
    peerHostLibrary.SetPeople(roomHost.Snapshot!.People);
    peerHostLibrary.Publish(Enumerable.Range(0, 43).Select(i => new SharedLibraryItem("shared-" + i, "Room title " + i, "Movie", 2026, null, null, null, null, true)));
    peerHostLibrary.SetAccess(roomGuest.Identity.Peer, new(true, true, true));
    bool PeerCondition(Func<bool> condition) { while (libraryTraffic.TryDequeue(out var action)) action(); return condition(); }
    await Wait(() => PeerCondition(() => peerGuestLibrary.Catalog.Count == 43), "full paged catalog crosses native dedicated bulk channel");
    peerGuestLibrary.Request(roomHost.Identity.Peer, "add", "shared-10");
    await Wait(() => PeerCondition(() => peerGuestLibrary.Queue.Entries.Length == 1), "guest queue edit completes through real room peer channel");
    peerGuestLibrary.SendView(roomHost.Identity.Peer, new(1, true, "", "Movie", 4, 2, 0, peerGuestLibrary.Catalog.Keys.Take(8).ToArray(), "shared-1", "shared-4"));
    await Wait(() => PeerCondition(() => peerHostLibrary.Views.GetValueOrDefault(roomGuest.Identity.Peer)?.HoveredId == "shared-4"), "guest hover reaches host over native peer channel");
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
    var priorMedia = roomHost.HostedMedia;
    bool missingDenied = false;
    try { roomHost.SetHostedFile(Path.Combine(root, "missing.avi"), "Missing"); } catch (FileNotFoundException) { missingDenied = true; }
    Check(missingDenied && ReferenceEquals(roomHost.HostedMedia, priorMedia), "failed file preparation preserves host current media");
    var queuedMedia = RoomClient.PrepareHostedFile(videoPath, "Video with subtitle");
    roomHost.PublishHostedFile(queuedMedia, true);
    await Wait(() => roomGuest.Snapshot?.Media?.Subtitles?.Length == 1, "external subtitle advertised without local path");
    Check(roomHost.Snapshot!.People.All(p => !p.Ready), "new video start waits for participant readiness");
    roomHost.Send(new("ready")); roomGuest.Send(new("ready"));
    await Wait(() => guestMessages.Any(m => m.Type == "playback" && Wire.Read<PlaybackState>(m.Data!).MediaId == queuedMedia.Media.Media.Id && Wire.Read<PlaybackState>(m.Data!).Playing), "published video starts automatically when all participants are ready");
    var subtitleSource = await roomGuest.GetSourceAsync(roomGuest.Snapshot!.Media!.Subtitles![0], CancellationToken.None);
    Check(System.Text.Encoding.UTF8.GetString(await subtitleSource.ReadAsync(0, (int)subtitleSource.Media.Length, CancellationToken.None)) == subtitleText, "subtitle transferred over authorized peer channel");
    roomHost.Send(new("remove", Target: roomGuest.Identity.Peer));
    await Wait(() => roomHost.Snapshot!.People.All(x => x.Id != roomGuest.Identity.Peer), "guest removed");
    bool revoked = false; try { await roomSource.ReadAsync(100, 100, CancellationToken.None); } catch { revoked = true; }
    Check(revoked, "removal revokes existing media channel");
    await using var wrongInvitation = new RoomClient();
    var errors = new ConcurrentQueue<WireMessage>(); wrongInvitation.Message += errors.Enqueue;
    await wrongInvitation.ConnectAsync(serverUrl, "Unknown Guest", "NOT-A-ROOM");
    await Wait(() => errors.Any(x => x.Type == "error"), "unknown invitations rejected");
}
finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); }
Console.WriteLine($"{passed} checks passed.");

async Task Wait(Func<bool> condition, string name)
{
    var until = DateTime.UtcNow.AddSeconds(20);
    while (!condition()) { if (DateTime.UtcNow > until) throw new Exception("Timeout: " + name); await Task.Delay(50); }
    Check(true, name);
}
