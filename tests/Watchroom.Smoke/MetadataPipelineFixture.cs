using System.Net;
using System.Text;
using System.Xml.Linq;
using Watchroom.Core;

static class MetadataPipelineFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var media = Path.Combine(directory, "media");
        var show = Path.Combine(media, "Example (2020)"); var season = Path.Combine(show, "Season 1"); Directory.CreateDirectory(season);
        var first = Path.Combine(season, "Alias S01E01.mkv"); var second = Path.Combine(season, "Different S01E02.mkv");
        await File.WriteAllBytesAsync(first, [0]); await File.WriteAllBytesAsync(second, [0]);
        var remake = Path.Combine(media, "Example (2005)", "Season 1"); Directory.CreateDirectory(remake);
        await File.WriteAllBytesAsync(Path.Combine(remake, "Example S01E01.mkv"), [0]);
        var store = new LibraryStore(Path.Combine(directory, "identity-db"));
        await store.ScanAsync([new(media, "Show")]);
        var entries = store.All();
        check(entries.Where(x => x.Year == 2020).Select(x => x.ShowId).Distinct().Count() == 1 && LibraryCatalog.Browse(entries).Count == 2,
            "folder membership combines filename aliases and separates same-name remakes");
        check(store.Shows().Count == 2 && store.Seasons().Count == 2, "shows and seasons persist as independent records");
        var item = entries.Single(x => x.Path == first); var showId = item.ShowId;
        store.Save(item with { Series = "Corrected", Title = "Corrected", Overview = "Shared plot", SeasonOverview = "Shared season", Matched = true });
        var reopened = new LibraryStore(Path.Combine(directory, "identity-db"));
        check(reopened.All().Where(x => x.ShowId == showId).All(x => x.Series == "Corrected" && x.Overview == "Shared plot" && x.SeasonOverview == "Shared season"),
            "shared show and season metadata survive reopening without copying edits to every episode");
        await reopened.ScanAsync([new(media, "Show")]);
        check(reopened.All().Single(x => x.Path == first).ShowId == showId, "rescan preserves show identity and existing file IDs");
        var releaseRoot = Path.Combine(directory, "releases");
        var releaseShow = Path.Combine(releaseRoot, "Release Show");
        var releases = new[] { Path.Combine(releaseShow, "Release.Show.S01.1080p.WEBRip", "Release.Show.S01E01.mkv"),
            Path.Combine(releaseShow, "Release.Show.S02.1080p.WEBRip", "Release.Show.S02E01.mkv"),
            Path.Combine(releaseShow, "Release.Show.S02E02.1080p", "Release.Show.S02E02.mkv") };
        var releaseStore = new LibraryStore(Path.Combine(directory, "release-db"));
        releaseStore.Setting("folders", Wire.Serialize(new LibraryFolder[] { new(releaseRoot, "Show") }));
        foreach (var path in releases)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [0]);
            var folder = Path.GetDirectoryName(path)!;
            releaseStore.Save(FileNames.Parse(path, "Show", releaseRoot) with { SeriesPath = folder,
                ShowId = "show-" + LibraryIdentity.Hash(OperatingSystem.IsWindows() ? folder.ToUpperInvariant() : folder), Overview = "Retained plot" });
        }
        releaseStore.Setting("release-folder-hierarchy-version", "0");
        releaseStore = new LibraryStore(Path.Combine(directory, "release-db"));
        check(releaseStore.Shows().Count == 1 && releaseStore.Seasons().Count == 2 && releaseStore.All().All(x => x.Overview == "Retained plot") &&
            releaseStore.All().Select(x => x.Id).Order().SequenceEqual(releases.Select(x => FileNames.Parse(x).Id).Order()),
            "existing split release-folder identities repair without losing metadata or file IDs");
        await releaseStore.ScanAsync([new(releaseRoot, "Show")]);
        check(LibraryCatalog.Browse(releaseStore.All()).Single().Caption.Contains("2 seasons") && releaseStore.All().All(x => x.SeriesPath == releaseShow),
            "season packs and individual episode release folders share their parent show on rescan");
        var looseRoot = Path.Combine(directory, "loose");
        var loosePath = Path.Combine(looseRoot, "Release.Show.S02E03.1080p", "Release.Show.S02E03.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!); await File.WriteAllBytesAsync(loosePath, [0]);
        check(FileNames.Parse(loosePath, "Show", looseRoot).SeriesPath is null, "loose episode release folders do not become show folders");
        await releaseStore.ScanAsync([new(releaseRoot, "Show"),new(looseRoot, "Show")]);
        check(releaseStore.Shows().Count == 1 && releaseStore.All().Count == 4,
            "loose releases join one unambiguous existing show without merging separate show folders");
        check(FileNames.Parse(Path.Combine(releaseShow, "02 sezon", "Release.Show.S02E04.mkv"), "Show", releaseRoot).SeriesPath == releaseShow,
            "transliterated season folders belong to the parent show");
        var ambiguousRoot = Path.Combine(directory, "ambiguous-releases");
        foreach (var folder in new[] { "copy-one", "copy-two" })
        {
            var path = Path.Combine(ambiguousRoot, folder, "Release Show", "Release.Show.S01E01.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [0]);
        }
        var ambiguousStore = new LibraryStore(Path.Combine(directory, "ambiguous-db"));
        await ambiguousStore.ScanAsync([new(ambiguousRoot,"Show"),new(looseRoot,"Show")]);
        check(ambiguousStore.Shows().Count == 3, "loose releases stay separate when two existing show folders are ambiguous");
        var beanFolder = Path.Combine(directory,"Shows","Mr. Bean"); Directory.CreateDirectory(beanFolder);
        var beanPath = Path.Combine(beanFolder,"Mr. Bean 01.mp4"); await File.WriteAllBytesAsync(beanPath,[0]);
        var beanDirectory = Path.Combine(directory,"bean-db"); var beanStore = new LibraryStore(beanDirectory);
        var bean = FileNames.Parse(beanPath);
        beanStore.Save(MetadataClassification.Apply(bean,"Movie","movie") with { MetadataProvider="wikipedia",MetadataId=173863,Overview="Existing plot" });
        beanStore.Setting("wikipedia-numbered-episode-repair","0"); beanStore = new LibraryStore(beanDirectory);
        check(beanStore.All().Single() is { Series:"Mr Bean",Season:1,Episode:1,MetadataType:null,Overview:"Existing plot" },
            "automatic Wikipedia movie misclassification repairs numbered TV episodes without losing text");
        new MetadataOptions(Providers:["wikipedia"]).Save(beanStore);
        var rejectWikipedia = new RejectAllHandler();
        using(var blockedWikipedia = new HttpClient(rejectWikipedia))
            await AutomaticArtwork.FetchAsync(beanStore,directory,null,null,default,blockedWikipedia);
        check(beanStore.All().Single().Series == "Mr Bean" && rejectWikipedia.Requests == 0, "Wikipedia film fallback cannot erase numbered episode structure");
        var partsRoot = Path.Combine(directory,"parts"); var partsShow = Path.Combine(partsRoot,"Example Show");
        foreach (var partNumber in new[] { 1,2 })
            foreach (var episodeNumber in new[] { 1,2 })
            {
                var path = Path.Combine(partsShow,"Part "+partNumber,$"[Group] Example Show Part {partNumber} - {episodeNumber:00}.mkv");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path,[0]);
            }
        var partsStore = new LibraryStore(Path.Combine(directory,"parts-db"));
        await partsStore.ScanAsync([new(partsRoot,"Anime")]);
        check(partsStore.Shows().Count == 1 && partsStore.All().Where(x => x.SourcePart == 2).All(x => x.Episode is null && x.SourcePartEpisode is > 0),
            "split anime parts share the show while restarted numbers stay unresolved until provider mapping");
        new MetadataOptions(Providers:["tmdb"]).Save(partsStore);
        foreach(var partEntry in partsStore.All()) partsStore.Save(LibraryIdentity.WithProvider(partEntry,"tmdb",1));
        using(var partTmdb = new MetadataClient("fixture",new PipelineHandler(partTimeline:true)))
        using(var partHttp = new HttpClient(new ArtworkFixture()))
            await SeriesMetadata.FetchAsync(partsStore,directory,"fixture",null,default,partHttp,metadata:partTmdb);
        check(partsStore.All().Where(x => x.SourcePart == 2).All(x => x.Season == 2 && x.Episode == x.SourcePartEpisode && x.AbsoluteEpisode == 2+x.SourcePartEpisode),
            "provider broadcast boundaries map restarted part episodes without duplicating part one");
        await partsStore.ScanAsync([new(partsRoot,"Anime")]);
        check(partsStore.All().Where(x => x.SourcePart == 2).All(x => x.Season == 2 && x.AbsoluteEpisode == 2+x.SourcePartEpisode),
            "rescans retain provider numbering and original part-relative episode numbers");
        var unresolvedParts = new LibraryStore(Path.Combine(directory,"unresolved-parts-db"));
        new MetadataOptions(Providers:["tmdb"]).Save(unresolvedParts);
        foreach(var partFile in Directory.EnumerateFiles(partsShow,"*.mkv",SearchOption.AllDirectories))
            unresolvedParts.Save(LibraryIdentity.WithProvider(FileNames.Parse(partFile,"Anime",partsRoot),"tmdb",1));
        using(var unclearTmdb = new MetadataClient("fixture",new PipelineHandler()))
        using(var partHttp = new HttpClient(new ArtworkFixture()))
            await SeriesMetadata.FetchAsync(unresolvedParts,directory,"fixture",null,default,partHttp,metadata:unclearTmdb);
        check(unresolvedParts.All().Where(x => x.SourcePart == 2).All(x => x.Episode is null && x.NumberingConflict is not null),
            "ambiguous broadcast boundaries leave part-relative episodes unresolved instead of guessing an offset");
        var conflict = FileNames.Parse(Path.Combine(season, "Example S02E03.mkv"));
        check(conflict.Season == 2 && conflict.NumberingConflict is not null, "explicit filename season wins and folder conflict is reported");
        var range = FileNames.Parse(Path.Combine(season, "Example S01E03-E04.mkv"));
        check(range.Episode == 3 && range.EpisodeEnd == 4 && range.Caption.Contains("E04"), "multi-episode filename stores and displays its range");
        var duplicates = new[] { range, range with { Id = "alternate", Path = "alternate.mkv", Available = false }, range with { Id = "episode-five", Episode = 5, EpisodeEnd = null } };
        check(LibraryCatalog.Browse(duplicates).Single().Caption.Contains("3 episodes") && LibraryCatalog.CardItems(duplicates, LibraryCatalog.Browse(duplicates).Single()).Count() == 2,
            "logical episode counts and queues do not double-count duplicate releases");
        var unknown = new MediaItem("unknown", "unknown.mkv", "Unknown", "Show", Series: "Unknown");
        check(LibraryCatalog.Browse([unknown], "Unknown", "Show").Single().DisplayTitle == "Season unknown", "unknown seasons are not silently assigned to season one");
        check(FileNames.IsExtra(Path.Combine(show, "extras", "Making Of.mkv")) && FileNames.IsExtra(Path.Combine(show, "Example-trailer.mkv")), "extras are excluded from episode aggregation");
        var special = range with { Id="special",Episode=1,EpisodeEnd=null,Season=0,AirsBeforeSeason=1,AirsBeforeEpisode=4 };
        var normal = range with { Id="normal",Episode=4,EpisodeEnd=null };
        var specialCard = LibraryCatalog.Browse([special,normal], LibraryIdentity.ShowKey(normal)).Single(x => x.Season==1);
        check(LibraryCatalog.Browse([special,normal],LibraryIdentity.ShowKey(normal),season:1,displaySpecials:true).Select(x=>x.Media.Id).SequenceEqual(["special","normal"]) &&
            LibraryCatalog.CardItems([special,normal],specialCard,true).Select(x=>x.Id).SequenceEqual(["special","normal"]),"special placement is consistent between browsing and season playback queues");
        var legacyDirectory = Path.Combine(directory,"legacy"); Directory.CreateDirectory(legacyDirectory);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(legacyDirectory,"library.db")))
        {
            db.Open(); using var cmd=db.CreateCommand();
            cmd.CommandText="CREATE TABLE media(id TEXT PRIMARY KEY,path TEXT UNIQUE NOT NULL,json TEXT NOT NULL); CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);"; cmd.ExecuteNonQuery();
            foreach(var (key,id) in new[]{("old-one",1),("old-two",2)})
            {
                cmd.Parameters.Clear();cmd.CommandText="INSERT INTO media VALUES($id,$path,$json)";
                cmd.Parameters.AddWithValue("$id",key);cmd.Parameters.AddWithValue("$path",key+".mkv");cmd.Parameters.AddWithValue("$json",Wire.Serialize(new MediaItem(key,key+".mkv","Same","Show",2020,"Same",1,1,MetadataProvider:"tmdb",MetadataId:id)));cmd.ExecuteNonQuery();
            }
        }
        var migrated=new LibraryStore(legacyDirectory);
        check(migrated.All().Select(x=>x.Id).Order().SequenceEqual(["old-one","old-two"]) && migrated.Shows().Count==2,
            "legacy library migration preserves file IDs and separates distinct provider identities");

        await File.WriteAllTextAsync(Path.Combine(show, "tvshow.nfo"), """<tvshow><title>NFO Show</title><plot>Local plot</plot><year>2020</year><uniqueid type="tmdb">1</uniqueid><uniqueid type="imdb">tt123</uniqueid><genre>Drama</genre><customtag>Keep me</customtag></tvshow>""");
        await File.WriteAllTextAsync(Path.ChangeExtension(first, ".nfo"), """<episodedetails><title>Local Pilot</title><plot>Local episode plot</plot><season>1</season><episode>1</episode><runtime>77</runtime><tmdbid>999</tmdbid></episodedetails>""");
        var nfo = FileNames.Parse(first, "Show");
        check(nfo.Series == "NFO Show" && nfo.EpisodeTitle == "Local Pilot" && nfo.ProviderIds?["tmdb"] == "1" && nfo.ProviderIds?["imdb"] == "tt123", "NFO titles and multiple show IDs import without leaking episode IDs");
        var nfoStore = new LibraryStore(Path.Combine(directory, "nfo-db")); nfoStore.Save(nfo);
        using (var http = new HttpClient(new ArtworkFixture()))
        using (var tmdb = new MetadataClient("fixture", new TmdbFixture()))
            await AutomaticArtwork.FetchAsync(nfoStore, directory, "fixture", null, default, http, refresh: MetadataRefresh.RefreshText, metadata: tmdb);
        var enriched = nfoStore.All().Single();
        check(enriched.Overview == "Local plot" && enriched.EpisodeTitle == "Local Pilot" && enriched.EpisodeOverview == "Local episode plot" && enriched.RuntimeMinutes == 77,
            "NFO field precedence survives forced remote metadata refresh");
        LocalMetadata.Export(enriched);
        check(XDocument.Load(Path.Combine(show, "tvshow.nfo")).Root!.Element("customtag")?.Value == "Keep me", "NFO export preserves tags owned by other tools");
        check(!XDocument.Load(Path.ChangeExtension(first, ".nfo")).Root!.Elements("uniqueid").Any(), "NFO export does not mislabel show IDs as episode IDs");
        var urlMovie=Path.Combine(directory,"url-movie.mkv"); await File.WriteAllTextAsync(Path.ChangeExtension(urlMovie,".nfo"),"https://www.imdb.com/title/tt456/");
        check(FileNames.Parse(urlMovie).ProviderIds?["imdb"]=="tt456","plain provider links in NFO files import identity without XML");
        await File.WriteAllTextAsync(Path.ChangeExtension(second, ".nfo"), """<!DOCTYPE episodedetails [<!ENTITY x SYSTEM "file:///missing">]><episodedetails><title>&x;</title></episodedetails>""");
        check(FileNames.Parse(second, "Show").EpisodeTitle is null, "untrusted NFO external entities are rejected");

        var best = MetadataMatching.Choose([new(1,"movie","Localized", "2021",null,"",OriginalTitle:"Original Name")], "Original.Name",2020);
        check(best?.Id == 1, "original titles and adjacent release years resolve automatically");
        check(MetadataMatching.Choose([new(1,"movie","Same","2020",null,""),new(2,"movie","Same","2020",null,"")], "Same",2020) is null,
            "tied metadata candidates remain unresolved");
        check(MetadataMatching.Choose([new(1,"movie","Different","2020",null,"")],"Wanted",2020) is null, "weak metadata candidates never silently win");
        using (var handler = new PipelineHandler())
        using (var tmdb = new MetadataClient("fixture", handler, new(Language:"fr-FR", Country:"FR")))
        {
            var externalItem = new MediaItem("external","external.mkv","Unrelated","Show",Series:"Unrelated",ProviderIds:new() { ["imdb"]="tt123" });
            var match = await tmdb.MatchAsync(externalItem);
            check(match?.Id == 1 && match.ProviderIds?["tvdb"] == "123", "external IMDb IDs resolve and retain cross-provider IDs");
            check(handler.Requests.Any(x => x.Query.Contains("language=fr-FR")), "metadata requests honor the configured language");
            var requestCount = handler.Requests.Count; await tmdb.DetailsAsync(match!);
            check(handler.Requests.Count == requestCount, "metadata responses are cached within a refresh run");
        }

        var fallbackStore = new LibraryStore(Path.Combine(directory,"fallback-db"));
        fallbackStore.Save(new("fallback",Path.Combine(directory,"fallback.mkv"),"Example Show","Show",2020,"Example Show",1,1,Matched:true,MetadataProvider:"tmdb",MetadataId:1,MetadataType:"tv"));
        using (var failed = new PipelineHandler(rejectTmdb:true))
        using (var tmdb = new MetadataClient("bad-token",failed))
        using (var http = new HttpClient(new ArtworkFixture()))
            await AutomaticArtwork.FetchAsync(fallbackStore,directory,"bad-token",null,default,http,metadata:tmdb);
        var fallback = fallbackStore.All().Single();
        check(fallback.EpisodeTitle == "Pilot" && fallback.MetadataProvider == "tmdb" && fallback.MetadataId == 1 && fallback.ProviderIds?["tvmaze"] == "1",
            "failed TMDB falls back to TVmaze without erasing the manually chosen identity");
        using (var http = new HttpClient(new ArtworkFixture())) await SeriesMetadata.FetchAsync(fallbackStore,directory,null,null,default,http,true);
        check(fallbackStore.All().Single().MetadataProvider == "tmdb", "token-free refresh retains a saved TMDB identity");
        var localOnly=new LibraryStore(Path.Combine(directory,"local-only"));localOnly.Save(fallback);new MetadataOptions(Providers:[]).Save(localOnly);
        using(var provider=new ArtworkFixture()) using(var http=new HttpClient(provider))
        { await AutomaticArtwork.FetchAsync(localOnly,directory,null,null,default,http,true);check(provider.Requests.Count==0,"disabling remote providers prevents every metadata network request"); }

        var numberedStore = new LibraryStore(Path.Combine(directory,"numbered-db"));
        var numbered = new MediaItem("absolute",Path.Combine(directory,"absolute.mkv"),"Example Show","Anime",2020,"Example Show",19,3,AbsoluteEpisode:3,NumberingOrder:"absolute",MetadataProvider:"tmdb",MetadataId:1,MetadataType:"tv");
        numberedStore.Save(numbered); new MetadataOptions(ImportMissing:true,ImportUpcoming:true).Save(numberedStore);
        using (var handler = new PipelineHandler())
        using (var tmdb = new MetadataClient("fixture",handler))
        using (var http = new HttpClient(new ArtworkFixture()))
            await SeriesMetadata.FetchAsync(numberedStore,directory,"fixture",null,default,http,true,tmdb);
        var mapped = numberedStore.All().Single(x => !x.IsVirtual);
        check(mapped.Season == 2 && mapped.Episode == 1 && mapped.AbsoluteEpisode == 3 && mapped.EpisodeTitle == "Season two pilot", "absolute anime numbering maps into provider season and episode numbers");
        check(numberedStore.All().Count(x => x.IsVirtual) == 3 && LibraryCatalog.CardItems(numberedStore.All(), LibraryCatalog.Browse(numberedStore.All()).Single()).Count() == 1,
            "missing and upcoming placeholders are opt-in and cannot enter playback queues");
        numberedStore.Prune([new(directory)]);
        check(numberedStore.All().Count(x => x.IsVirtual) == 3, "library pruning preserves placeholders belonging to retained shows");
        new MetadataOptions().Save(numberedStore);
        using (var http = new HttpClient(new ArtworkFixture())) await SeriesMetadata.FetchAsync(numberedStore,directory,null,null,default,http);
        check(numberedStore.All().All(x => !x.IsVirtual), "disabling placeholder imports removes them even when metadata is fresh");

        var refreshStore = new LibraryStore(Path.Combine(directory,"refresh-db"));
        var poster = Path.Combine(directory,"old.png"); await File.WriteAllBytesAsync(poster,ArtworkFixture.Png);
        refreshStore.Save(new("refresh",Path.Combine(directory,"refresh.mkv"),"Example Show","Show",2020,"Example Show",1,1,Poster:poster,Overview:"Old plot",SeriesPoster:poster,MetadataProvider:"tmdb",MetadataId:1,MetadataType:"tv",MetadataKind:"Show",ArtworkFetchedAt:Wire.Now));
        using (var handler = new PipelineHandler())
        using (var tmdb = new MetadataClient("fixture",handler))
        using (var http = new HttpClient(new ArtworkFixture()))
        {
            await AutomaticArtwork.FetchAsync(refreshStore,directory,"fixture",null,default,http,refresh:MetadataRefresh.RefreshText,metadata:tmdb);
            check(refreshStore.All().Single().Overview == "New plot" && refreshStore.All().Single().Poster == poster,"text refresh replaces stale text while preserving artwork");
            await AutomaticArtwork.FetchAsync(refreshStore,directory,"fixture",null,default,http,refresh:MetadataRefresh.ReplaceArtwork,metadata:tmdb);
            check(refreshStore.All().Single().Poster != poster && handler.Requests.Any(x => x.Host == "image.tmdb.org"),"artwork replacement fetches and updates downloaded images");
            var current = refreshStore.All().Single(); refreshStore.Save(current with { MetadataLocked=true, Overview="Locked plot" });
            var count = handler.Requests.Count;
            await AutomaticArtwork.FetchAsync(refreshStore,directory,"fixture",null,default,http,refresh:MetadataRefresh.ReplaceArtwork,metadata:tmdb);
            check(refreshStore.All().Single().Overview == "Locked plot" && handler.Requests.Count == count,"locked metadata is untouched by forced refresh");
        }
        using (var handler = new PipelineHandler(retry:true))
        using (var tmdb = new MetadataClient("fixture",handler))
        {
            await tmdb.SearchAsync("Example Show",true);
            check(handler.Requests.Count == 2,"rate-limited metadata requests retry with Retry-After");
        }
        var cross = new LibraryStore(Path.Combine(directory,"cross-db"));
        cross.Save(numbered with { Id="folder-one",Path=Path.Combine(directory,"one.mkv"),ShowId="folder-one",MetadataProvider="tmdb",MetadataId=1 });
        cross.Save(numbered with { Id="folder-two",Path=Path.Combine(directory,"two.mkv"),ShowId="folder-two",MetadataProvider="tmdb",MetadataId=1 });
        check(cross.Shows().Count == 2,"separate folders remain separate until provider grouping is enabled");
        new MetadataOptions(GroupShowsByProvider:true).Save(cross); cross.GroupShowsByProvider();
        check(cross.Shows().Count == 1 && cross.All().Select(x => x.ShowId).Distinct().Count() == 1,"matching provider IDs optionally combine shows across folders");
        using(var handler=new PipelineHandler()) using(var tmdb=new MetadataClient("fixture",handler))
        {
            var dvd=await tmdb.DvdSeasonsAsync(1,default);
            check(dvd.Single().Number==1 && dvd.Single().Episodes.Single().Number==1 && dvd.Single().Episodes.Single().Title=="DVD first","DVD episode groups map season and episode order");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();var stopped=false;
            try{await tmdb.SearchAsync("Cancelled",true,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped,"metadata cancellation stops requests without becoming a fallback failure");
        }
    }

    private sealed class RejectAllHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new HttpRequestException("Unexpected metadata request"); }
    }
    private sealed class PipelineHandler(bool rejectTmdb=false,bool retry=false,bool partTimeline=false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var uri=request.RequestUri!; Requests.Add(uri);
            cancellationToken.ThrowIfCancellationRequested();
            if(uri.Host=="image.tmdb.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(ArtworkFixture.Png)});
            if(rejectTmdb) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if(retry && Requests.Count==1)
            { var limited=new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero); return Task.FromResult(limited); }
            var body=uri.AbsolutePath switch
            {
                "/3/find/tt123" => """{"tv_results":[{"id":1}],"movie_results":[]}""",
                "/3/tv/1/episode_groups" => """{"results":[{"id":"dvd-group","type":3}]}""",
                "/3/tv/episode_group/dvd-group" => """{"groups":[{"order":1,"name":"DVD Season","episodes":[{"order":0,"name":"DVD first","overview":"DVD plot","still_path":null,"air_date":"2020-01-01","runtime":20}]}]}""",
                "/3/tv/1" => """{"id":1,"name":"Example Show","original_name":"Original Show","first_air_date":"2020-01-01","overview":"New plot","poster_path":"/new.jpg","external_ids":{"imdb_id":"tt123","tvdb_id":123},"seasons":[{"season_number":1},{"season_number":2}]}""",
                "/3/tv/1/season/1" => """{"name":"First","overview":"First plot","poster_path":null,"episodes":[{"episode_number":1,"name":"Pilot","overview":"One","still_path":null,"air_date":"2020-01-01","runtime":20},{"episode_number":2,"name":"Second","overview":"Two","still_path":null,"air_date":"2020-01-02","runtime":20}]}""",
                "/3/tv/1/season/2" => """{"name":"Second","overview":"Second plot","poster_path":null,"episodes":[{"episode_number":1,"name":"Season two pilot","overview":"Three","still_path":null,"air_date":"2021-01-01","runtime":20},{"episode_number":2,"name":"Future","overview":"Soon","still_path":null,"air_date":"2099-01-01","runtime":20}]}""",
                _ => """{"results":[{"id":1,"name":"Example Show","first_air_date":"2020-01-01","overview":"New plot","poster_path":"/new.jpg"}]}"""
            };
            if (partTimeline) body = body.Replace("2099-01-01","2021-01-02");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
}
