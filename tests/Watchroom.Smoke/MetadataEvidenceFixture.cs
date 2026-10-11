using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Watchroom.Core;

static class MetadataEvidenceFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        async Task<MediaItem> FileItem(string name, string title, double minutes, int? year = null)
        {
            var path = Path.Combine(directory, name); await File.WriteAllBytesAsync(path, name.EndsWith(".mkv") ? Mkv(minutes) : Mp4(minutes));
            return new(name, path, title, "Movie", year, SourceLibraryKind: "Movie");
        }
        var mp4 = await FileItem("Kavkazskaia Plenica.mp4", "Kavkazskaia Plenica", 79.887);
        var island = await FileItem("Ostrov Sokrovich.mp4", "Ostrov Sokrovich", 106.847);
        var poison = await FileItem("Poison.2023.1080p.NF.WEBRip.mkv", "Poison", 17.85, 2023);
        check(Math.Abs(MediaDuration.Minutes(mp4.Path)!.Value - 79.887) < .001 && Math.Abs(MediaDuration.Minutes(poison.Path)!.Value - 17.85) < .001,
            "MP4 and Matroska header durations require no decoder or external tools");
        var large = Mp4(90); BinaryPrimitives.WriteUInt32BigEndian(large, uint.MaxValue); await File.WriteAllBytesAsync(Path.Combine(directory,"invalid.mp4"),large);
        check(MediaDuration.Minutes(Path.Combine(directory,"invalid.mp4")) is null && MediaDuration.Minutes(Path.Combine(directory,"absent.mkv")) is null,
            "invalid or missing containers cannot supply matching evidence");
        await File.WriteAllBytesAsync(Path.Combine(directory,"nan.mkv"), Mkv(double.NaN));
        check(MediaDuration.Minutes(Path.Combine(directory,"nan.mkv")) is null, "non-finite container durations are ignored");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = false; try { MediaDuration.Minutes(mp4.Path, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "duration inspection respects cancellation");
        check(MetadataTitles.Queries(island).Contains("остров сокровищ") && !MetadataTitles.Queries(new("church","The Church.mkv","The Church","Movie")).Any(x=>x.Contains('щ')),
            "informal Russian ch spelling expands without changing English titles");
        using var handler = new EvidenceHandler(); using var tmdb = new MetadataClient("fixture", handler);
        async Task<MetadataMatch?> Fresh(MediaItem item, IEnumerable<MediaItem>? entries = null)
        { using var fresh = new MetadataClient("fixture", handler); return await fresh.MatchAsync(item, entries: entries); }
        check((await Fresh(mp4))?.Id == 20878, "misspelled Caucasian title uses native prefix and actual duration to reject the remake");
        check((await Fresh(island))?.Id == 49418, "Treasure Island transliteration and duration distinguish adaptations without a filename year");
        check((await Fresh(poison))?.Id == 1172676, "Poison title-year tie resolves using duration and Netflix evidence with an unknown-runtime rival");
        check(handler.ProviderReads == 4, "every tied streaming-provider candidate is checked");
        handler.Netflix = false;
        check(await Fresh(poison) is null, "Netflix filename alone cannot select an unverified provider candidate");
        handler.Netflix = true;
        var generic = await FileItem("Poison.2023.WEBRip.mkv", "Poison",17.85,2023);
        check(await Fresh(generic) is null, "unknown-runtime rival remains unresolved without independent streaming evidence");
        var tie = await FileItem("Same.2020.mp4","Same",90,2020);
        check(await Fresh(tie) is null, "matching runtimes cannot resolve a genuine title-year tie");
        handler.CloseRuntimes=true;
        check(await Fresh(island) is null, "near runtimes remain ambiguous even when only one is within the normal tolerance"); handler.CloseRuntimes=false;
        check(await Fresh(mp4 with { Year = 2020 }) is null, "runtime evidence cannot override a conflicting filename year");
        check(await Fresh(poison with { Path = Path.Combine(directory,"missing.mkv") }) is null, "missing files never acquire guessed durations");
        handler.FailProviders = true;
        var failed = false; try { await Fresh(poison); } catch (HttpRequestException) { failed = true; }
        check(failed, "incomplete provider responses cannot eliminate unchecked rivals"); handler.FailProviders = false;

        var folder = Path.Combine(directory,"Movies","12 стульев"); Directory.CreateDirectory(folder);
        var parts = Enumerable.Range(1,4).Select(n => FileNames.Parse(Path.Combine(folder,$"Dvenadcat Stulev (1977) {n:00}.mp4"),"Movie") with { Available=true }).ToArray();
        foreach(var part in parts) await File.WriteAllBytesAsync(part.Path,Mp4(65));
        check(MetadataTitles.Queries(parts[0]).Contains("12 стульев"), "native multiword movie folder supplies a title alias");
        check(!MetadataTitles.Queries(parts[0] with { Path = Path.Combine(directory,"Советские фильмы",Path.GetFileName(parts[0].Path)) }).Contains("Советские фильмы"),
            "library category folders cannot identify titles");
        check((await Fresh(parts[0], entries:parts)) is { Id:44210,NumberedSeries:true }, "complete four-part movie-folder group can identify a matching TV miniseries");
        check(await Fresh(parts[0], entries:[parts[0]]) is null, "a single file in Movies cannot become an unrelated show");
        check(await Fresh(parts[0], entries:[parts[0],parts[2]]) is null, "gaps in numbered files do not establish a complete series");
        check(await Fresh(parts[0] with { LockedFields=["Kind"] },entries:parts) is null, "locked movie classification prevents miniseries recovery");
        check(await Fresh(parts[0],entries:parts.Select(x=>x.Id==parts[3].Id?x with { LockedFields=["Episode"] }:x)) is null,
            "a numbering lock on another file blocks automatic group reinterpretation");
        handler.EpisodeCount=5;
        check(await Fresh(parts[0],entries:parts) is null, "provider episode count must agree with the local numbered group"); handler.EpisodeCount=4;
        handler.SeasonGap=true;
        check(await Fresh(parts[0],entries:parts) is null, "aggregate counts cannot hide missing provider episode numbers"); handler.SeasonGap=false;
        var store = new LibraryStore(Path.Combine(directory,"db")); new MetadataOptions(Providers:["tmdb"]).Save(store);
        store.Setting("queues","preserved");
        foreach(var part in parts) store.Save(part);
        using var http = new HttpClient(handler);
        await AutomaticArtwork.FetchAsync(store,directory,"fixture",null,default,http,metadata:tmdb);
        var updated=store.All();
        check(updated.All(x=>x.MetadataId==44210 && x.MetadataType=="tv" && x.Season==1) && updated.Select(x=>x.Episode).Order().SequenceEqual(new int?[]{1,2,3,4}),
            "automatic fetch identifies and numbers all four local files");
        check(updated.All(x=>x.EpisodeTitle is not null) && updated.Select(x=>x.ShowId).Distinct().Count()==1,
            "recovered files share one show and receive episode metadata");
        check(updated.Select(x=>x.Id).Order().SequenceEqual(parts.Select(x=>x.Id).Order()), "matching preserves file IDs and queue references");
        var reopened=new LibraryStore(Path.Combine(directory,"db"));
        check(reopened.All().All(x=>x.MetadataId==44210 && x.Episode is > 0), "recovered episode identities survive reopening the library");
        await reopened.ScanAsync([new(folder,"Movie")]);
        check(reopened.All().All(x=>x.MetadataId==44210 && x.Season==1 && x.Episode is > 0), "rescanning a movie folder preserves recovered series numbering");
        check(reopened.Setting("queues")=="preserved", "series recovery leaves saved queues unchanged");
    }
    private static byte[] Mp4(double minutes)
    {
        byte[] Atom(string type,byte[] payload) { var bytes=new byte[payload.Length+8]; BinaryPrimitives.WriteUInt32BigEndian(bytes,(uint)bytes.Length); Encoding.ASCII.GetBytes(type).CopyTo(bytes,4); payload.CopyTo(bytes,8); return bytes; }
        var header=new byte[20]; BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12),1000); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16),(uint)(minutes*60_000));
        return Atom("moov",Atom("mvhd",header));
    }
    private static byte[] Mkv(double minutes)
    {
        byte[] bytes=[0x18,0x53,0x80,0x67,0xff,0x15,0x49,0xa9,0x66,0x8b,0x44,0x89,0x88,0,0,0,0,0,0,0,0];
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(13),BitConverter.DoubleToInt64Bits(minutes*60_000)); return bytes;
    }
    private sealed class EvidenceHandler : HttpMessageHandler
    {
        public bool Netflix=true,FailProviders,SeasonGap,CloseRuntimes; public int ProviderReads,EpisodeCount=4;
        private static object Movie(int id,string title,string original,int? year,int? runtime=null) => new {id,title,original_title=original,release_date=year is null ? "" : $"{year}-01-01",runtime,overview="Plot",poster_path="/poster.jpg"};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(request.RequestUri.Host=="image.tmdb.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6hQsAAAAASUVORK5CYII="))});
            var query=System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["query"];
            object body;
            if(path.EndsWith("/search/movie")) body=new {results=query switch {
                "Poison" => new[]{Movie(1172676,"Poison","Poison",2023),Movie(1553365,"Poison","Poison",2023),Movie(1467660,"Poison","Poison",2023),Movie(1756588,"Poison","Poison",2023)},
                "кавказская" => new[]{Movie(20878,"Kidnapping, Caucasian Style","Кавказская пленница, или Новые приключения Шурика",1967),Movie(330878,"Kidnapping, Caucasian Style","Кавказская пленница!",2014)},
                "остров сокровищ" or "остров" => new[]{Movie(49418,"Treasure Island","Остров сокровищ",1988),Movie(207720,"Treasure Island","Остров сокровищ",1982),Movie(1731466,"Treasure Island","Остров сокровищ",DateTime.UtcNow.Year+3)},
                "12 стульев" => new[]{Movie(1731435,"12 Chairs","12 стульев",null)},
                "Same" => new[]{Movie(800,"Same","Same",2020),Movie(801,"Same","Same",2020)}, _ => Array.Empty<object>()}};
            else if(path.EndsWith("/search/tv")) body=new {results=query=="12 стульев" ? new object[]{new{id=44210,name="The Twelve Chairs",original_name="12 стульев",first_air_date="1977-01-02",overview="Plot",poster_path="/poster.jpg"}}:[]};
            else if(path.EndsWith("/alternative_titles")) body=new {titles=Array.Empty<object>(),results=Array.Empty<object>()};
            else if(path.EndsWith("/watch/providers"))
            {
                ProviderReads++; if(FailProviders) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("unavailable")});
                body=new {results=new {US=new {flatrate=Netflix && path.Contains("/1172676/") ? new[]{new{provider_name="Netflix"}}:[]}}};
            }
            else if(path.EndsWith("/tv/44210")) body=new {id=44210,name="The Twelve Chairs",original_name="12 стульев",first_air_date="1977-01-02",number_of_episodes=EpisodeCount,number_of_seasons=1,overview="Plot",poster_path="/poster.jpg",seasons=new[]{new{season_number=1}}};
            else if(path.EndsWith("/tv/44210/season/1")) body=new {season_number=1,name="Season 1",overview="Plot",poster_path="/poster.jpg",air_date="1977-01-02",episodes=Enumerable.Range(1,4).Select(n=>new{episode_number=SeasonGap && n==4 ? 5:n,name=$"Episode {n}",overview="Episode plot",still_path=(string?)null,air_date="1977-01-02",runtime=65}).ToArray()};
            else if(path.Contains("/movie/"))
            {
                var id=int.Parse(path.Split('/')[3]);body=id switch {
                    20878=>Movie(id,"Kidnapping, Caucasian Style","Кавказская пленница, или Новые приключения Шурика",1967,82),
                    330878=>Movie(id,"Kidnapping, Caucasian Style","Кавказская пленница!",2014,95),
                    49418=>Movie(id,"Treasure Island","Остров сокровищ",1988,107),207720=>Movie(id,"Treasure Island","Остров сокровищ",1982,CloseRuntimes?113:190),
                    1731435=>Movie(id,"12 Chairs","12 стульев",null),
                    800 or 801=>Movie(id,"Same","Same",2020,90),
                    _=>Movie(id,"Poison","Poison",2023,id==1172676?17:id==1553365?10:id==1467660?2:null)};
            }
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("not found")});
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")});
        }
    }
}
