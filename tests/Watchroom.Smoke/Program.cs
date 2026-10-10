using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Watchroom.Core;
using LibVLCSharp.Shared;
using System.Diagnostics;

if (args.FirstOrDefault() == "--track-languages")
{
    TrackLanguageFixture.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }); return;
}

if (args.FirstOrDefault() == "--guest-library-peer")
{
    var count = 0;
    await GuestLibraryPeerFixture.RunIsolated(Path.GetFullPath(".tools/dotnet/dotnet.exe"),
        Path.GetFullPath("src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll"),
        Path.Combine("artifacts", "guest-peer-" + Guid.NewGuid().ToString("N")), Path.GetFullPath(args[1]),
        (condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"{count} guest peer checks passed."); return;
}

if (args.FirstOrDefault() == "--shared-library")
{
    var checks = 0;
    SharedLibraryFixture.Run((condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} shared library checks passed."); return;
}

if (args.FirstOrDefault() == "--episode-playback")
{
    var checks = 0;
    EpisodePlaybackFixture.Run((condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} episode continuation and watched-state checks passed."); return;
}

if (args.FirstOrDefault() == "--metadata-discovery")
{
    var checks = 0;
    await MetadataDiscoveryFixture.Run(Path.Combine("artifacts/metadata-discovery", Guid.NewGuid().ToString("N")), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} metadata discovery checks passed."); return;
}

if (args.FirstOrDefault() == "--metadata-cache")
{
    var checks = 0;
    await MetadataCacheFixture.Run(Path.Combine("artifacts/metadata-cache-check", Guid.NewGuid().ToString("N")), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} metadata cache checks passed."); return;
}

if (args.FirstOrDefault() == "--room-reconnect")
{
    var checks = 0;
    await RoomReconnectFixture.Run(Path.Combine("artifacts/room-reconnect-check", Guid.NewGuid().ToString("N")), args.Contains("--sites"), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} room reconnect checks passed."); return;
}

if (args.FirstOrDefault() == "--peer-recovery")
{
    var checks = 0;
    await PeerRecoveryFixture.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/peer-recovery"), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} peer recovery checks passed."); return;
}

if (args.FirstOrDefault() == "--library-snapshot")
{
    var store = new LibraryStore(Path.GetFullPath(args[1]));
    await File.WriteAllTextAsync(Path.GetFullPath(args[2]), Wire.Serialize(store.All()));
    Console.WriteLine($"Library snapshot: {store.All().Count} files, {LibraryCatalog.Browse(store.All()).Count} cards."); return;
}

if (args.FirstOrDefault() == "--metadata-recovery")
{
    var checks = 0;
    await MetadataRecoveryFixture.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/metadata-recovery-check"), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} metadata recovery checks passed."); return;
}

if (args.FirstOrDefault() == "--episode-artwork")
{
    var checks = 0;
    await EpisodeArtworkFixture.Run(Path.GetFullPath(args[1]), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} episode artwork checks passed."); return;
}

if (args.FirstOrDefault() == "--refresh-metadata")
{
    var data = Path.GetFullPath(args[1]); var report = Path.GetFullPath(args[2]); Directory.CreateDirectory(report);
    var store = new LibraryStore(data); var before = store.All();
    await File.WriteAllTextAsync(Path.Combine(report, "before-refresh.json"), Wire.Serialize(before));
    var selected = before.Where(x => x.Available && !x.IsExtra && !x.IsVirtual).GroupBy(LibraryIdentity.ShowKey)
        .Where(g => args.Length > 3 ? System.Text.RegularExpressions.Regex.IsMatch(g.First().DisplayTitle, args[3], System.Text.RegularExpressions.RegexOptions.IgnoreCase) :
            !g.Any(x => ArtworkCache.IsUsable(x.SeriesPoster) || ArtworkCache.IsUsable(x.Poster)) || !g.Any(x => !string.IsNullOrWhiteSpace(x.Overview)))
        .SelectMany(g => g).ToArray();
    var temporary = Path.Combine(report, "refresh-" + Guid.NewGuid().ToString("N")); var subset = new LibraryStore(temporary);
    MetadataOptions.Load(store).Save(subset); foreach (var entry in selected) subset.Save(entry);
    Console.WriteLine($"Refreshing {selected.Length} files in {selected.GroupBy(LibraryIdentity.ShowKey).Count()} title groups.");
    var progress = new Progress<string>(Console.WriteLine);
    var summary = await AutomaticArtwork.FetchAsync(subset, data, null, progress, default);
    File.Copy(Path.Combine(data,"metadata-fetch-report.json"),Path.Combine(report,"metadata-fetch-report.json"),true);
    foreach (var entry in subset.All()) store.Save(entry);
    await File.WriteAllTextAsync(Path.Combine(report, "after-refresh.json"), Wire.Serialize(store.All()));
    Console.WriteLine(summary); return;
}

if (args.FirstOrDefault() == "--grouping-audit")
{
    var checks = 0;
    await GroupingAuditFixture.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/grouping-audit-check"), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} grouping audit checks passed."); return;
}

if (args.FirstOrDefault() == "--metadata-pipeline")
{
    var checks = 0;
    await MetadataPipelineFixture.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/metadata-pipeline"), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} metadata pipeline checks passed."); return;
}

if (args.FirstOrDefault() == "--library-ingestion")
{
    var checks = 0;
    await LibraryIngestionFixture.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/library-ingestion-check"), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} library ingestion checks passed."); return;
}

if (args.FirstOrDefault() == "--library-search")
{
    var checks = 0;
    await LibrarySearchFixture.Run((condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} library search checks passed."); return;
}

if (args.FirstOrDefault() == "--direct-controls")
{
    var checks = 0;
    await DirectControlFixture.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/direct-controls"), (condition, name) =>
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; });
    Console.WriteLine($"{checks} direct control checks passed."); return;
}

if (args.FirstOrDefault() == "--ui-fixture")
{
    var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
    var mediaDirectory = Path.Combine(directory, "media"); Directory.CreateDirectory(mediaDirectory);
    VideoFixture.Write(Path.Combine(mediaDirectory, "Test Movie (2026).avi"), 120);
    VideoFixture.Write(Path.Combine(mediaDirectory, "Test Show S01E01.avi"), 120);
    VideoFixture.Write(Path.Combine(mediaDirectory, "Test Show S01E02.avi"), 120);
    await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "poster.png"), ArtworkFixture.Png);
    var store = new LibraryStore(directory);
    store.Setting("folders", Wire.Serialize(new[] { new LibraryFolder(mediaDirectory) }));
    store.Setting("artwork", "false"); store.Setting("name", "Review Host"); store.Setting("server", "http://localhost:5127");
    await store.ScanAsync([new(mediaDirectory)]);
    store.Save(new("missing", Path.Combine(mediaDirectory, "Missing.avi"), "Unavailable Video", "Movie", Available: false));
    Console.WriteLine(directory); return;
}

if (args.FirstOrDefault() == "--desktop-artwork")
{
    var directory = Path.GetFullPath("artifacts/desktop-artwork-check");
    var store = new LibraryStore(directory);
    await store.ScanAsync([new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"), "Mixed")]);
    if (!args.Contains("--scan-only")) Console.WriteLine(await AutomaticArtwork.FetchAsync(store, directory, null, new Progress<string>(Console.WriteLine), CancellationToken.None));
    foreach (var group in store.All().Where(x => x.Available).GroupBy(x => x.DisplayTitle)) Console.WriteLine($"{group.Key} | year={group.First().Year} | episodes={group.Count()} | poster={group.Any(x => File.Exists(x.Poster))}");
    return;
}

if (args.FirstOrDefault() == "--live-classification")
{
    var directory = Path.GetFullPath("artifacts/live-classification-check"); var store = new LibraryStore(directory);
    store.Save(new("anime-series", Path.Combine(directory, "Death Note S01E01.mkv"), "Death Note", "Show", 2006, "Death Note", 1, 1));
    store.Save(new("anime-film", Path.Combine(directory, "Spirited Away.mkv"), "Spirited Away", "Movie", 2001));
    store.Save(new("regular-series", Path.Combine(directory, "Breaking Bad S01E01.mkv"), "Breaking Bad", "Anime", 2008, "Breaking Bad", 1, 1));
    Console.WriteLine(await AutomaticArtwork.FetchAsync(store, directory, null, new Progress<string>(Console.WriteLine), CancellationToken.None, force: true));
    foreach (var item in store.All()) Console.WriteLine(item.DisplayTitle + " -> " + item.Kind + " (" + item.MetadataType + ")");
    Environment.ExitCode = store.All().Single(x => x.Id == "anime-series").Kind == "Anime" && store.All().Single(x => x.Id == "anime-film").Kind == "Anime" && store.All().Single(x => x.Id == "regular-series").Kind == "Show" ? 0 : 1;
    return;
}

if (args.FirstOrDefault() == "--live-artwork")
{
    var directory = Path.GetFullPath("artifacts/live-artwork-check");
    var store = new LibraryStore(directory);
    store.Save(new("live-show", Path.Combine(directory, "Breaking Bad S01E01.mkv"), "Breaking Bad", "Show", 2008, "Breaking Bad", 1, 1));
    store.Save(new("live-movie", Path.Combine(directory, "The Matrix (1999).mkv"), "The Matrix", "Movie", 1999));
    Console.WriteLine(await AutomaticArtwork.FetchAsync(store, directory, null, new Progress<string>(Console.WriteLine), CancellationToken.None, force: true));
    var liveEpisode = store.All().Single(x => x.Id == "live-show");
    Console.WriteLine("Episode metadata: " + liveEpisode.EpisodeTitle + " · season overview=" + !string.IsNullOrWhiteSpace(liveEpisode.SeasonOverview) + " · episode overview=" + !string.IsNullOrWhiteSpace(liveEpisode.EpisodeOverview) + " · episode artwork=" + File.Exists(liveEpisode.EpisodePoster));
    if (string.IsNullOrWhiteSpace(liveEpisode.EpisodeTitle) || string.IsNullOrWhiteSpace(liveEpisode.EpisodeOverview) || !File.Exists(liveEpisode.EpisodePoster)) { Environment.ExitCode = 1; return; }
    foreach (var item in store.All()) Console.WriteLine(item.DisplayTitle + ": " + (File.Exists(item.Poster) ? "image cached" : "no image") + " · overview=" + !string.IsNullOrWhiteSpace(item.Overview));
    Environment.ExitCode = store.All().All(x => File.Exists(x.Poster) && !string.IsNullOrWhiteSpace(x.Overview)) ? 0 : 1; return;
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

if (args.FirstOrDefault() == "--relay-streaming")
{
    await RelayStreamingFixture.Run(args[1], (condition, name) => {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    });
    return;
}
if (args.FirstOrDefault() == "--streaming-audio")
{
    var checks = 0;
    await StreamingAudioFixture.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/streaming-audio"), (condition, name) =>
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); checks++;
    }, int.TryParse(args.ElementAtOrDefault(2), out var cacheMs) ? cacheMs : MediaStreaming.NetworkCacheMs);
    Console.WriteLine($"{checks} streaming audio checks passed.");
    return;
}
var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/smoke");
Directory.CreateDirectory(root);
PlaybackDiagnostics.Initialize(Path.Combine(root, "session-logs"));
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
TrackLanguageFixture.Run(Check);
EpisodePlaybackFixture.Run(Check);
RoomLibraryChecks.Run(Check);
await DiagnosticLogFixture.Run(Path.Combine(root, "diagnostic-log"), Check);
await RecoveryFixture.Run(Check);
await MediaCacheFixture.Run(Check);
await BridgeShutdownFixture.Run(Check);
await LibrarySearchFixture.Run(Check);
await MetadataPipelineFixture.Run(Path.Combine(root, "metadata-pipeline"), Check);
await MetadataRecoveryFixture.Run(Path.Combine(root, "metadata-recovery"), Check);
await MetadataCacheFixture.Run(Path.Combine(root, "metadata-cache"), Check);
await LibraryIngestionFixture.Run(Path.Combine(root, "ingestion"), Check);
await GroupingAuditFixture.Run(Path.Combine(root, "grouping-audit"), Check);
HostRoomFixture.Run(Check);
RoomTrackFixture.Run(Check);
SharedLibraryFixture.Run(Check);
await StreamingAudioFixture.Run(Path.Combine(root, "streaming-audio"), Check);
await PeerMediaFixture.Run(Check);
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
var correctedPath = Path.Combine(root, "Original Show S01E02.mkv");
await File.WriteAllBytesAsync(correctedPath, bytes);
await library.ScanAsync([new(root, "Mixed")]);
var corrected = library.All().Single(x => x.Path == correctedPath);
library.Save(corrected with { Title = "Corrected Show", Series = "Corrected Show", Matched = true });
await library.ScanAsync([new(root, "Mixed")]);
Check(library.All().Single(x => x.Path == correctedPath).Series == "Corrected Show", "manual series match survives rescan");
var invitationAddress = RoomAddress.Parse(" https://example.com#abcdef0123456789abcdef01 ", "http://localhost:5000");
Check(invitationAddress.Server == "https://example.com" && invitationAddress.Code == "ABCDEF0123456789ABCDEF01", "invitation link chooses its own server and normalizes code");
foreach (var invalid in new[] { "garbage", "https://example.com#", "http://example.com#ABCDEF0123456789ABCDEF01", "https://user:password@example.com#ABCDEF0123456789ABCDEF01" })
{
    bool invalidRejected = false; try { RoomAddress.Parse(invalid, "https://example.com"); } catch (ArgumentException) { invalidRejected = true; }
    Check(invalidRejected, "malformed or unsafe invitation rejected before connection");
}
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
var seasonCard = LibraryCatalog.Browse(catalogItems, "Example Show", "Show").First();
Check(LibraryCatalog.CardItems(catalogItems, seasonCard).Select(x => x.Episode).SequenceEqual(new int?[] { 2, 10 }), "season queue adds every episode in order");
Check(LibraryCatalog.CardItems(catalogItems, LibraryCatalog.Browse(catalogItems).Single()).Select(x => x.Episode).SequenceEqual(new int?[] { 2, 10, 3 }), "show queue orders seasons then episodes");
Check(LibraryCatalog.Search(catalogItems, "Episode 10", "Example Show", "Show", 1).Single().Media.Id == "second", "episode search preserves season scope");
Check(LibraryCatalog.Search(catalogItems, "Season 2", "Example Show", "Show").Single().Season == 2, "show search matches season labels");
var playbackEntries = new[] { "first", "missing", "last" };
var cursorIndex = QueuePlayback.FindNext(playbackEntries, -1, 1, x => x != "missing");
Check(cursorIndex == 0 && QueuePlayback.FindNext(playbackEntries, cursorIndex, 1, x => x != "missing") == 2, "queue advances without deleting entries and skips unavailable videos");
Check(QueuePlayback.FindNext(playbackEntries, 2, -1, x => x != "missing") == 0 && playbackEntries.Length == 3, "previous video preserves queue contents");
Check(QueuePlayback.FindNext(playbackEntries, 2, 1, _ => true) == -1 && QueuePlayback.FindNext(playbackEntries, 0, -1, _ => true) == -1, "queue stops at first and last videos");
var persistentRegistryPath = Path.Combine(root, "persistent-registry");
var registry = new PersistentRooms(persistentRegistryPath); var reusableCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
var ownerKey = registry.Create(reusableCode);
Check(new PersistentRooms(persistentRegistryPath).Verify(reusableCode, ownerKey), "persistent room owner survives registry restart");
registry.Update(reusableCode, [new("admitted-guest-hash", "Guest")], true, "Movie night");
var restoredRoom = new PersistentRooms(persistentRegistryPath).All().Single();
Check(restoredRoom.SharedControls && restoredRoom.Guests?.Single().Name == "Guest", "room settings and admitted roster survive server registry restart");
Check(restoredRoom.Name == "Movie night", "waiting-room name survives server registry restart");
Check(!registry.Verify(reusableCode, new string('0', 64)) && !Wire.Serialize(registry.All()).Contains(ownerKey), "server stores only hashed host keys and rejects wrong owners");
var queueDefinition = new SavedQueue("test", "Weekend", ["third", "second", "third"]);
library.Setting("queues", Wire.Serialize(new[] { queueDefinition }));
Check(Wire.Read<SavedQueue[]>(new LibraryStore(Path.Combine(root, "db")).Setting("queues")!).Single().MediaIds.SequenceEqual(queueDefinition.MediaIds), "saved queue persists order and duplicate entries");
var exclusionRoot = Path.Combine(root, "exclusion-check");
var excludedPath = Path.Combine(exclusionRoot, "excluded"); var siblingPath = Path.Combine(exclusionRoot, "excluded-sibling");
Directory.CreateDirectory(excludedPath); Directory.CreateDirectory(siblingPath);
await File.WriteAllBytesAsync(Path.Combine(excludedPath, "Hidden.mp4"), bytes);
await File.WriteAllBytesAsync(Path.Combine(siblingPath, "Visible.mp4"), bytes);
var exclusionStore = new LibraryStore(Path.Combine(root, "exclusion-db"));
await exclusionStore.ScanAsync([new(exclusionRoot)]);
await exclusionStore.ScanAsync([new(exclusionRoot)], exclusions: [excludedPath]);
Check(exclusionStore.All().Count == 1 && exclusionStore.All().Single().Title == "Visible", "excluded folder removed while similarly named sibling remains");
await exclusionStore.ScanAsync([new(exclusionRoot)]);
Check(exclusionStore.All().Count == 2, "removing exclusion restores videos on rescan");
exclusionStore.Prune([new(siblingPath)]);
Check(exclusionStore.All().Count == 1, "removing root retains videos covered by another library folder");
exclusionStore.Prune([]);
Check(exclusionStore.All().Count == 0, "removing last library folder deletes indexed titles");
var artworkStore = new LibraryStore(Path.Combine(root, "automatic-artwork"));
artworkStore.Save(new("show1", Path.Combine(root, "show1.mkv"), "Example Show", "Show", 2020, "Example Show", 1, 1));
artworkStore.Save(new("show2", Path.Combine(root, "show2.mkv"), "Example Show", "Show", 2020, "Example Show", 2, 1));
artworkStore.Save(new("movie1", Path.Combine(root, "movie1.mkv"), "Example Movie", "Movie", 2020));
using (var artworkHttp = new HttpClient(new ArtworkFixture()))
{
    await AutomaticArtwork.FetchAsync(artworkStore, Path.Combine(root, "automatic-artwork"), null, null, CancellationToken.None, artworkHttp);
    Check(artworkStore.All().All(x => File.Exists(x.Poster) && x.PosterSource is not null), "token-free movie and series posters downloaded and persisted");
    Check(artworkStore.All().Where(x => x.Series is not null).Select(x => x.SeriesPoster).Distinct().Count() == 1, "automatic series artwork shared across seasons");
    Check(artworkStore.All().Where(x => x.Series is not null).All(x => x.Overview == "A show & its story."), "series overviews strip HTML and decode entities across seasons");
    Check(artworkStore.All().Single(x => x.Id == "movie1").Overview == "A movie and its story.", "movie overview persisted from Wikipedia extract");
    var posterBefore = artworkStore.All().First(x => x.Series is not null).Poster;
    foreach (var entry in artworkStore.All()) artworkStore.Save(entry with { Overview = null, Matched = true });
    await AutomaticArtwork.FetchAsync(artworkStore, Path.Combine(root, "automatic-artwork"), null, null, CancellationToken.None, artworkHttp);
    Check(artworkStore.All().All(x => !string.IsNullOrWhiteSpace(x.Overview)) && artworkStore.All().First(x => x.Series is not null).Poster == posterBefore, "backfill overviews for matched titles without replacing existing posters");
    var custom = artworkStore.All().Single(x => x.Id == "movie1"); artworkStore.Save(custom with { Overview = "Custom overview" });
    await AutomaticArtwork.FetchAsync(artworkStore, Path.Combine(root, "automatic-artwork"), null, null, CancellationToken.None, artworkHttp);
    Check(artworkStore.All().Single(x => x.Id == "movie1").Overview == "Custom overview", "existing overview survives automatic metadata fetch");
}
var classificationDirectory = Path.Combine(root, "classified-library"); Directory.CreateDirectory(classificationDirectory);
var classifiedAnimePath = Path.Combine(classificationDirectory, "Example Show S01E01.mkv");
var classifiedMoviePath = Path.Combine(classificationDirectory, "Example Movie (2020).mkv");
await File.WriteAllBytesAsync(classifiedAnimePath, [0]); await File.WriteAllBytesAsync(classifiedMoviePath, [0]);
var classificationStore = new LibraryStore(Path.Combine(root, "classification-db"));
await classificationStore.ScanAsync([new(classificationDirectory, "Show")]);
using (var classificationHttp = new HttpClient(new ArtworkFixture(anime: true, animatedMovie: true)))
    await AutomaticArtwork.FetchAsync(classificationStore, Path.Combine(root, "classification-db"), null, null, CancellationToken.None, classificationHttp);
var classifiedAnime = classificationStore.All().Single(x => x.Path == classifiedAnimePath);
var classifiedMovie = classificationStore.All().Single(x => x.Path == classifiedMoviePath);
Check(classifiedAnime.Kind == "Anime" && classifiedAnime.MetadataKind == "Anime" && classifiedAnime.Series == "Example Show", "Japanese animation metadata reclassifies show filenames as Anime");
Check(classifiedMovie.Kind == "Anime" && classifiedMovie.MetadataType == "movie" && classifiedMovie.Series is null && classifiedMovie.Episode is null, "anime film metadata keeps film separate from show hierarchy");
await classificationStore.ScanAsync([new(classificationDirectory, "Movie")]);
Check(classificationStore.All().All(x => x.Kind == "Anime") && classificationStore.All().Single(x => x.Path == classifiedMoviePath).Series is null, "metadata classification and film hierarchy survive rescan and folder category changes");
var standaloneStore = new LibraryStore(Path.Combine(root, "standalone-classification"));
standaloneStore.Save(new("standalone", Path.Combine(root, "standalone.mkv"), "Example Show", "Movie", 2020, Poster: localPoster, Overview: "Existing summary"));
using (var standaloneHttp = new HttpClient(new ArtworkFixture()))
    await AutomaticArtwork.FetchAsync(standaloneStore, Path.Combine(root, "standalone-classification"), null, null, CancellationToken.None, standaloneHttp);
Check(standaloneStore.All().Single().Kind == "Show" && standaloneStore.All().Single().Series == "Example Show" && standaloneStore.All().Single().Poster == localPoster, "metadata classifies movies without episode patterns as shows while preserving artwork");
var ambiguousStore = new LibraryStore(Path.Combine(root, "ambiguous-classification"));
ambiguousStore.Save(new("ambiguous", Path.Combine(root, "ambiguous.mkv"), "Example Show", "Movie", 2020));
using (var ambiguousHttp = new HttpClient(new ArtworkFixture(ambiguous: true)))
    await AutomaticArtwork.FetchAsync(ambiguousStore, Path.Combine(root, "ambiguous-classification"), null, null, CancellationToken.None, ambiguousHttp);
Check(ambiguousStore.All().Single().Kind == "Movie" && ambiguousStore.All().Single().MetadataKind is null, "ambiguous movie and show match retains fallback classification");
using (var westernAnimation = System.Text.Json.JsonDocument.Parse("""{"type":"Animation","language":"English","network":{"country":{"code":"US"}}}"""))
    Check(MetadataClassification.Tvmaze(westernAnimation.RootElement) == "Show", "Western animation stays Show rather than Anime");
using (var animeTmdb = System.Text.Json.JsonDocument.Parse("""{"genres":[{"id":16}],"original_language":"ja"}"""))
    Check(MetadataClassification.Tmdb(animeTmdb.RootElement, "movie") == "Anime", "TMDB Japanese animated films classify as Anime");
using (var liveActionTmdb = System.Text.Json.JsonDocument.Parse("""{"genres":[{"id":18}],"original_language":"ja","origin_country":["JP"]}"""))
    Check(MetadataClassification.Tmdb(liveActionTmdb.RootElement, "tv") == "Show", "Japanese live action stays Show");
using (var animeMatcher = new MetadataClient("fixture-token", new TmdbFixture(anime: true)))
{
    var match = await animeMatcher.MatchAsync(new("a", "a.mkv", "Example Show", "Show", 2020, "Example Show", 1, 1));
    Check(match?.Kind == "Anime" && match.Type == "tv", "TMDB fetches animation and origin metadata before classifying");
}
using (var movieMatcher = new MetadataClient("fixture-token", new TmdbFixture(movie: true)))
{
    var match = await movieMatcher.MatchAsync(new("m", "m.mkv", "Example Movie", "Show", 2020));
    Check(match?.Kind == "Movie" && match.Type == "movie", "TMDB searches movie and show metadata regardless of folder category");
}
using (var ambiguousMatcher = new MetadataClient("fixture-token", new TmdbFixture(ambiguous: true)))
{
    Check(await ambiguousMatcher.MatchAsync(new("b", "b.mkv", "Example Show", "Movie", 2020)) is null, "TMDB ambiguous standalone titles require a manual match");
}
// Season and episode metadata are independent of the show's shared overview.
var enriched = artworkStore.All().Single(x => x.Id == "show1");
Check(enriched.EpisodeTitle == "Pilot" && enriched.EpisodeOverview == "Episode one & its plot." && enriched.SeasonOverview == "Season one story.", "TVmaze saves distinct episode and season metadata");
Check(enriched.AirDate == "2020-01-01" && enriched.RuntimeMinutes == 45 && File.Exists(enriched.EpisodePoster) && File.Exists(enriched.SeasonPoster), "episode dates runtimes and separate artwork cached");
Check(artworkStore.All().Single(x => x.Id == "show2").EpisodeTitle == "Return", "episode matching uses both season and episode numbers");
Check(LibraryCatalog.Browse(artworkStore.All(), "Example Show", "Show", 1).Single().DisplayTitle.Contains("Pilot"), "episode grid shows fetched title");
Check(LibraryCatalog.EpisodeArtwork(enriched) == enriched.EpisodePoster && enriched.DetailOverview == enriched.EpisodeOverview, "episode artwork and overview take priority over show fallback");
using (var cachedFixture = new ArtworkFixture())
using (var cachedHttp = new HttpClient(cachedFixture))
{
    await SeriesMetadata.FetchAsync(artworkStore, Path.Combine(root, "automatic-artwork"), null, null, CancellationToken.None, cachedHttp);
    Check(cachedFixture.Requests.Count == 0, "recent metadata cached across startup and scans");
    await SeriesMetadata.FetchAsync(artworkStore, Path.Combine(root, "automatic-artwork"), null, null, CancellationToken.None, cachedHttp, true);
    Check(cachedFixture.Requests.Any(x => x.AbsolutePath.EndsWith("/episodes")) && !cachedFixture.Requests.Any(x => x.AbsolutePath.Contains("search")), "manual refresh fetches episodes using persisted provider ID");
}
var incompleteStore = new LibraryStore(Path.Combine(root, "incomplete-metadata"));
incompleteStore.Save(new("fallback", Path.Combine(root, "fallback.mkv"), "Example Show", "Show", 2020, "Example Show", 1, 99, Overview: "Show fallback"));
incompleteStore.Save(new("text-only", Path.Combine(root, "text-only.mkv"), "Example Show", "Show", 2020, "Example Show", 1, 1, Poster: localPoster));
using (var noImageHttp = new HttpClient(new ArtworkFixture(omitImages: true)))
    await SeriesMetadata.FetchAsync(incompleteStore, Path.Combine(root, "incomplete-metadata"), null, null, CancellationToken.None, noImageHttp);
var fallback = incompleteStore.All().Single(x => x.Id == "fallback");
Check(fallback.EpisodeOverview is null && fallback.DetailOverview == "Season one story.", "unknown episode keeps season overview as fallback");
var textOnly = incompleteStore.All().Single(x => x.Id == "text-only");
Check(textOnly.EpisodeTitle == "Pilot" && textOnly.EpisodePoster is null && LibraryCatalog.EpisodeArtwork(textOnly) == localPoster, "missing provider image still saves metadata and preserves local artwork");
var imageFailureStore = new LibraryStore(Path.Combine(root, "metadata-image-failure"));
imageFailureStore.Save(new("failure", Path.Combine(root, "failure.mkv"), "Example Show", "Show", 2020, "Example Show", 1, 1));
using (var failureHttp = new HttpClient(new ArtworkFixture(failImages: true)))
    await SeriesMetadata.FetchAsync(imageFailureStore, Path.Combine(root, "metadata-image-failure"), null, null, CancellationToken.None, failureHttp);
Check(imageFailureStore.All().Single().EpisodeOverview == "Episode one & its plot." && imageFailureStore.All().Single().MetadataFetchedAt == 0, "image failure retains fetched text and allows retry");
var tmdbStore = new LibraryStore(Path.Combine(root, "tmdb-episodes"));
tmdbStore.Save(new("tmdb1", Path.Combine(root, "tmdb1.mkv"), "Example Show", "Show", 2020, "Example Show", 1, 1));
tmdbStore.Save(new("tmdb2", Path.Combine(root, "tmdb2.mkv"), "Example Show", "Show", 2020, "Example Show", 1, 2, Overview: "TMDB show fallback"));
using (var tmdbFixture = new TmdbFixture())
using (var tmdbMetadata = new MetadataClient("fixture-token", tmdbFixture))
using (var unusedHttp = new HttpClient(new ArtworkFixture()))
{
    await SeriesMetadata.FetchAsync(tmdbStore, Path.Combine(root, "tmdb-episodes"), "fixture-token", null, CancellationToken.None, unusedHttp, metadata: tmdbMetadata);
    var tmdbEpisode = tmdbStore.All().Single(x => x.Id == "tmdb1");
    Check(tmdbEpisode.EpisodeTitle == "TMDB Pilot" && tmdbEpisode.SeasonOverview == "TMDB season plot" && tmdbEpisode.RuntimeMinutes == 42 && File.Exists(tmdbEpisode.EpisodePoster), "TMDB season response maps episode metadata and stills");
    var emptyEpisode = tmdbStore.All().Single(x => x.Id == "tmdb2");
    Check(emptyEpisode.EpisodePoster is null && emptyEpisode.RuntimeMinutes is null && emptyEpisode.DetailOverview == "TMDB season plot", "TMDB handles null images runtimes and empty episode overview");
    Check(!tmdbFixture.ImageReceivedToken, "TMDB image requests never contain the API token");
}
Check(Wire.Read<MediaItem>(Wire.Serialize(enriched)).EpisodeTitle == "Pilot", "episode metadata survives library serialization");
var file = new FileMediaSource(path);
Check(file.LocalPath == Path.GetFullPath(path) && !Wire.Serialize(file.Media).Contains(file.LocalPath, StringComparison.OrdinalIgnoreCase),
    "host retains a local playback path without exposing it in shared media");
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
await Task.WhenAll(host.ControlReady, guest.ControlReady).WaitAsync(TimeSpan.FromSeconds(10));
var directStop = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
guest.ControlMessage += message => directStop.TrySetResult(message);
host.SendControl(new("playback", Data: Wire.Serialize(new PlaybackState(1, file.Media.Id, false, 0, Wire.Now))));
Check((await directStop.Task.WaitAsync(TimeSpan.FromSeconds(1))).Type == "playback", "separate native control channel delivers stop directly");
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
    // Exercise the same AVI demux path used by room playback, including seeking
    // and replay through a remote peer rather than a direct local file.
    videoMedia.AddOption(":demux=avformat");
    player.Play(videoMedia);
    await Wait(() => player.Time > 1000, "LibVLC remote playback");
    Check(player.Length >= 11000, "LibVLC reads AVI duration through peer bridge");
    player.Time = 7000; await Wait(() => player.Time >= 6900, "LibVLC remote seeking");
    player.SetPause(true); await Task.Delay(350); var paused = player.Time; await Task.Delay(350);
    Check(Math.Abs(player.Time - paused) < 200, "LibVLC pause remains stable");
    player.SetPause(false); player.Time = player.Length - 500;
    await Wait(() => player.State == VLCState.Ended, "LibVLC remote fixture reaches end of media");
    player.Stop();
    Check(player.Play(), "LibVLC ended input restarts after Stop");
    await Wait(() => player.IsPlaying && player.Time > 300 && player.Time < 3000, "LibVLC replay decodes advancing frames from the beginning");
    player.Stop();
}
cts.Cancel(); try { await pump; } catch (OperationCanceledException) { }

var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var externalServer = Environment.GetEnvironmentVariable("WATCHROOM_TEST_SERVER");
if (string.IsNullOrWhiteSpace(externalServer)) externalServer = null;
var serverUrl = externalServer ?? $"http://localhost:{port}";
var sdkPath = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
var start = new ProcessStartInfo(sdkPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.Environment["WATCHROOM_DATA"] = Path.Combine(root, "server-data");
start.ArgumentList.Add(Path.GetFullPath(Environment.GetEnvironmentVariable("WATCHROOM_TEST_SERVER_DLL") ?? "src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(serverUrl);
await GuestLibraryPeerFixture.RunIsolated(sdkPath, start.ArgumentList[0], root, videoPath, Check);
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
    var pendingRejected = false;
    try { roomGuest.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, file.Media.Id, true, 1000, 0)))); }
    catch (IOException) { pendingRejected = true; }
    Check(pendingRejected, "unapproved guest cannot send direct room controls");
    await Task.Delay(150); Check(!hostMessages.Any(x => x.Type == "playback"), "pending guests cannot control playback");
    roomHost.Send(new("admit", Target: roomGuest.Identity!.Peer));
    await Wait(() => roomGuest.Snapshot?.Media is not null, "guest admitted");
    var roomSource = await roomGuest.GetSourceAsync(file.Media, CancellationToken.None);
    Check(roomSource is not ILocalMediaSource, "guest playback cannot take the host's local file shortcut");
    Check((await roomSource.ReadAsync(900, 100, CancellationToken.None)).SequenceEqual(bytes[900..1000]), "signaled room serves host media");
    roomGuest.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, file.Media.Id, true, 1000, 0))));
    await Task.Delay(150); Check(!hostMessages.Any(x => x.Type == "playback"), "admitted guest respects host-only controls");
    roomHost.Send(new("ready")); roomGuest.Send(new("ready"));
    await Wait(() => roomHost.Snapshot!.People.All(x => x.Ready), "all participants ready");
    roomHost.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, file.Media.Id, true, 2000, 0))));
    await Wait(() => guestMessages.Any(x => x.Type == "playback"), "host playback broadcast");
    var playback = Wire.Read<PlaybackState>(guestMessages.Last(x => x.Type == "playback").Data!);
    Check(playback.Revision > 0 && playback.AtUnixMs > 0 && playback.PositionMs == 2000, "host assigns playback revision and clock");
    Check(playback.AtUnixMs <= Wire.Now + 200, "playback starts with a 200 ms lead instead of the old 600 ms delay");
    roomGuest.Send(new("buffering"));
    await Wait(() => guestMessages.Any(x => x.Type == "notice"), "buffering pauses room");
    Check(!Wire.Read<PlaybackState>(guestMessages.Last(x => x.Type == "playback").Data!).Playing, "shared buffering state paused");
    roomGuest.Send(new("ready"));
    await Wait(() => Wire.Read<PlaybackState>(guestMessages.Last(x => x.Type == "playback").Data!).Playing, "automatic resume after buffering");
    roomHost.Send(new("queue", Data: Wire.Serialize(new[] { "Next movie", "Episode two" })));
    await Wait(() => roomHost.Snapshot?.Queue?.Length == 2 && roomGuest.Snapshot?.Queue?.Length == 2, "shared queue visible to host and admitted guests");
    roomGuest.Send(new("queue", Data: Wire.Serialize(new[] { "Unauthorized change" })));
    await Task.Delay(100); Check(roomHost.Snapshot!.Queue![0] == "Next movie", "guests cannot overwrite host queue");
    var subtitleText = "1\n00:00:00,000 --> 00:00:04,000\nWatchroom subtitle test\n";
    await File.WriteAllTextAsync(Path.ChangeExtension(videoPath, ".srt"), subtitleText);
    roomHost.SetHostedFile(videoPath, "Video with subtitle");
    var hostedSource = await roomHost.GetSourceAsync(roomHost.HostedMedia!.Media, CancellationToken.None);
    var hostedBytes = await hostedSource.ReadAsync(0, 128, CancellationToken.None);
    var videoBytes = await File.ReadAllBytesAsync(videoPath);
    Check(hostedSource is ILocalMediaSource localVideo && localVideo.LocalPath == Path.GetFullPath(videoPath)
        && hostedBytes.SequenceEqual(videoBytes[..128]),
        "host video with subtitles supports direct playback and authorized peer reads");
    roomHost.Send(new("media", Data: Wire.Serialize(roomHost.HostedMedia!.Media)));
    await Wait(() => roomGuest.Snapshot?.Media?.Subtitles?.Length == 1, "external subtitle advertised without local path");
    var subtitleSource = await roomGuest.GetSourceAsync(roomGuest.Snapshot!.Media!.Subtitles![0], CancellationToken.None);
    Check(System.Text.Encoding.UTF8.GetString(await subtitleSource.ReadAsync(0, (int)subtitleSource.Media.Length, CancellationToken.None)) == subtitleText, "subtitle transferred over authorized peer channel");
    roomHost.Send(new("remove", Target: roomGuest.Identity.Peer));
    await Wait(() => roomHost.Snapshot!.People.All(x => x.Id != roomGuest.Identity.Peer), "guest removed");
    bool revoked = false; try { await roomSource.ReadAsync(100, 100, CancellationToken.None); } catch { revoked = true; }
    Check(revoked, "removal revokes existing media channel");
    var roomSettings = new LibraryStore(Path.Combine(root, "room-settings"));
    var guestSettingsPath = Path.Combine(root, "guest-room-settings");
    var persistentHost = new RoomClient(roomSettings);
    await persistentHost.ConnectAsync(serverUrl, "Persistent Host", persistent: true, roomName: "Friday movie night");
    var savedIdentity = persistentHost.Identity!;
    Check(savedIdentity.HostKey?.Length == 64, "persistent host receives a private reopening key");
    var admittedGuest = new RoomClient(new LibraryStore(guestSettingsPath));
    await admittedGuest.ConnectAsync(serverUrl, "Remembered Guest", savedIdentity.Room);
    persistentHost.Send(new("admit", Target: admittedGuest.Identity!.Peer));
    await Wait(() => admittedGuest.DirectControlsReady.IsCompletedSuccessfully, "persistent guest admitted");
    await Wait(() => admittedGuest.Snapshot?.Name == "Friday movie night", "guest receives the host's room name");
    Check(persistentHost.Snapshot?.Name == admittedGuest.Snapshot?.Name, "host and guest share the same room name");
    persistentHost.Send(new("room-name", Text: "Anime night"));
    await Wait(() => admittedGuest.Snapshot?.Name == "Anime night", "host rename reaches the connected guest");
    admittedGuest.Send(new("room-name", Text: "Guest override", Sender: savedIdentity.Peer));
    persistentHost.Send(new("controls", Number: 1));
    await Wait(() => admittedGuest.Snapshot?.SharedControls == true, "persistent controls enabled");
    Check(persistentHost.Snapshot?.Name == "Anime night" && admittedGuest.Snapshot?.Name == "Anime night", "guest rename is rejected even with shared playback controls");
    Check(admittedGuest.Identity.GuestKey?.Length == 64, "guest receives private reconnect credential");
    await admittedGuest.DisposeAsync();
    await using (var duplicateHost = new RoomClient())
    {
        bool ownerRejected = false;
        try { await duplicateHost.ConnectAsync(serverUrl, "Duplicate", savedIdentity.Room, savedIdentity.HostKey); } catch (IOException) { ownerRejected = true; }
        Check(ownerRejected && persistentHost.IsConnected, "duplicate owner connection rejected without closing existing host");
    }
    await persistentHost.DisposeAsync();
    await Task.Delay(350);
    await using (var wrongOwner = new RoomClient())
    {
        bool ownerRejected = false;
        try { await wrongOwner.ConnectAsync(serverUrl, "Wrong Owner", savedIdentity.Room, new string('0', 64)); } catch (IOException) { ownerRejected = true; }
        Check(ownerRejected, "persistent room rejects an incorrect host key");
    }
    await using (var reopened = new RoomClient(roomSettings))
    {
        await reopened.ConnectAsync(serverUrl, "Persistent Host", savedIdentity.Room, savedIdentity.HostKey);
        Check(reopened.Identity!.Host && reopened.Identity.Room == savedIdentity.Room, "persistent room reconnects as host with same invitation");
        Check(reopened.Snapshot?.SharedControls == true, "shared controls restored when host reconnects");
        Check(reopened.Snapshot?.Name == "Anime night", "host room name survives reconnect");
        await using (var rememberedGuest = new RoomClient(new LibraryStore(guestSettingsPath)))
        {
            await rememberedGuest.ConnectAsync(serverUrl, "Renamed Guest", savedIdentity.Room);
            await rememberedGuest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(15));
            Check(rememberedGuest.Snapshot?.Name == "Anime night", "returning guest receives the canonical name");
            Check(rememberedGuest.Snapshot?.SharedControls == true, "admitted guest reconnects without approval and receives saved controls");
            await Wait(() => reopened.Snapshot?.AdmittedGuests?.Length == 1, "remembered roster visible to host");
            Check(rememberedGuest.Snapshot?.AdmittedGuests is null, "remembered guest credentials and roster are host-only");
        }
        await using (var impostor = new RoomClient())
        {
            await impostor.ConnectAsync(serverUrl, "Remembered Guest", savedIdentity.Room);
            await Wait(() => impostor.Snapshot is not null, "impostor waiting");
            Check(!impostor.Snapshot!.People.Single(p => p.Id == impostor.Identity!.Peer).Approved, "same display name cannot inherit admission");
        }
        reopened.Send(new("remove", Target: reopened.Snapshot!.AdmittedGuests![0].Id));
        await Wait(() => reopened.Snapshot?.AdmittedGuests?.Length == 0, "offline admission revoked");
        await using (var removedGuest = new RoomClient(new LibraryStore(guestSettingsPath)))
        {
            await removedGuest.ConnectAsync(serverUrl, "Renamed Guest", savedIdentity.Room);
            await Wait(() => removedGuest.Snapshot is not null, "removed guest waiting");
            Check(!removedGuest.Snapshot!.People.Single(p => p.Id == removedGuest.Identity!.Peer).Approved, "removed guest needs fresh host approval on rejoin");
        }
        reopened.Send(new("controls", Number: 0));
        await Wait(() => reopened.Snapshot?.SharedControls == false, "controls disabled and saved");
        await using var returningGuest = new RoomClient();
        await returningGuest.ConnectAsync(serverUrl, "Returning Guest", savedIdentity.Room);
        Check(returningGuest.Identity?.HostKey is null, "persistent guest can rejoin without receiving the owner key");
    }
    await Task.Delay(350);
    await using (var hostAgain = new RoomClient(roomSettings))
    {
        await hostAgain.ConnectAsync(serverUrl, "Persistent Host", savedIdentity.Room, savedIdentity.HostKey);
        Check(hostAgain.Snapshot?.SharedControls == false, "disabled shared controls remain disabled on next reconnect");
    }
    await using var wrongInvitation = new RoomClient();
    var errors = new ConcurrentQueue<WireMessage>(); wrongInvitation.Message += errors.Enqueue;
        bool rejected = false;
        try { await wrongInvitation.ConnectAsync(serverUrl, "Unknown Guest", "NOT-A-ROOM"); } catch (IOException) { rejected = true; }
        Check(rejected, "unknown invitations rejected");
}
finally { if (server is not null) { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); } }
await PlaybackDiagnostics.FlushAsync();
using var diagnosticStream = new FileStream(Path.Combine(PlaybackDiagnostics.DirectoryPath!, "diagnostics.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
using var diagnosticReader = new StreamReader(diagnosticStream);
var diagnosticText = await diagnosticReader.ReadToEndAsync();
Check(new[] { "room-connect", "transport-state", "peer-handshake", "peer-departed" }.All(kind => diagnosticText.Contains("\"kind\":\"" + kind + "\"")),
    "real room connections record setup, native transport, handshake and departure events");
Check(new[] { "hostKey", "guestKey", "password", "sdp", "localPath" }.All(key => !diagnosticText.Contains("\"" + key + "\":", StringComparison.OrdinalIgnoreCase)),
    "connection diagnostics omit credential, raw signaling and local media path fields");
Console.WriteLine($"{passed} checks passed.");

async Task Wait(Func<bool> condition, string name)
{
    var until = DateTime.UtcNow.AddSeconds(20);
    while (!condition()) { if (DateTime.UtcNow > until) throw new Exception("Timeout: " + name); await Task.Delay(50); }
    Check(true, name);
}
