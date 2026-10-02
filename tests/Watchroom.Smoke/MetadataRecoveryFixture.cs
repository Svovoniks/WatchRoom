using System.Net;
using System.Text;
using System.Text.Json;
using Watchroom.Core;

static class MetadataRecoveryFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        check(MetadataMatching.Score("The Naked Gun 2 1 2 The Smell of Fear",1991,"The Naked Gun 2½: The Smell of Fear",null,1991)==10,
            "fraction glyph and filename fraction identify the same sequel");
        check(MetadataMatching.Score("Brilliantovaya Ruka",1968,"The Diamond Arm","Бриллиантовая рука",1969)==9,
            "romanized source and Cyrillic original compare with regional release year");
        check(MetadataMatching.Score("Ivan Vasilievich Menyaet Professiu",1973,"Ivan Vasilyevich Changes His Profession","Иван Васильевич меняет профессию",1973)==10,
            "common Russian romanization variants preserve title identity");
        check(MetadataTitles.RussianSearchAlias("Politseyskiy s Rublevki")=="полицейский с рублевки", "Russian aliases distinguish vowel-following and final y from ы");
        check(MetadataTitles.Queries(new("t",Path.Combine(directory,"Taxi","Taksi.3.2003.mkv"),"Taksi 3","Movie",2003)).Contains("Taxi 3"),
            "franchise folder spelling retains sequel number");
        check(MetadataTitles.FolderYear(new("f",Path.Combine(directory,"Обыкновенное чудо_1978-DVDRip","Чудо-1.mkv"),"Чудо-1","Movie"))==1978,
            "split-film release folder supplies the missing year");
        var split = new MediaItem("s",Path.Combine(directory,"parts","Film (1977) 02.mkv"),"Film","Movie",1977);
        var identified = split with { Id="first",Path=Path.Combine(directory,"parts","Film (1977) 01.mkv"),Matched=true,MetadataProvider="tmdb",MetadataId=42,MetadataType="tv" };
        check(MetadataTitles.WithSplitIdentity(split,[identified]).MetadataId==42, "explicit split files reuse a unique manual sibling identity");
        check(MetadataTitles.WithSplitIdentity(split,[identified with { Path=Path.Combine(directory,"parts","Other (1977) 01.mkv") }]).MetadataId is null,
            "a different film in the same folder cannot supply a split identity");
        check(MetadataMatching.Choose([new(1,"movie","На Дерибасовской хорошая погода, или На Брайтон Бич опять идут дожди","1993",null,"")],
            "На Дерибасовской хорошая погода",null)?.Id==1, "long unique title prefix resolves an omitted subtitle");
        check(MetadataMatching.Choose([new(1,"movie","На Дерибасовской хорошая погода One","1993",null,""),new(2,"movie","На Дерибасовской хорошая погода Two","2000",null,"")],
            "На Дерибасовской хорошая погода",null) is null, "long title prefixes still reject tied records");
        check(MetadataMatching.Score("Spy x Family", null, "Spy×Family", null, null) == 8, "multiplication sign and x identify the same title");
        check(MetadataMatching.Score("Mr and Mrs Smith", 2024, "Mr. & Mrs. Smith", null, 2024) == 10, "ampersand and and compare equally");
        check(MetadataMatching.Score("Leon The Professional", 1994, "Léon: The Professional", null, 1994) == 10, "accent and punctuation variants compare equally");
        check(MetadataMatching.Score("Your Friends and Neighbours", null, "Your Friends & Neighbors", null, null) == 8, "regional spelling does not reject an otherwise exact show");
        check(MetadataTitles.IsAnimeSource(new("mix",Path.Combine(directory,"Anime","show.mkv"),"Example","Show",SourceLibraryKind:"Mixed")), "Mixed roots retain anime-folder matching evidence");
        check(MetadataTitles.Queries(new("scary","scary.mkv","01 Scary Movie 1 - Comedy","Movie",2000)).Contains("Scary Movie"), "collection ordinal and genre label are search noise, not a movie identity");
        var blade = FileNames.Parse(Path.Combine(directory,"Blade.Runner.2049.2017.2160p.BluRay.mkv"));
        check(blade.Title == "Blade Runner 2049" && blade.Year == 2017, "title number 2049 is retained while actual release year 2017 is parsed");
        var historic = new LibraryStore(Path.Combine(directory,"historic"));
        historic.Save(blade with {Title="Blade Runner",Year=2049,MetadataProvider="wikipedia",MetadataId=10,Overview="Blade Runner is a 1982 science fiction film.",MetadataType="movie"});
        historic.Save(new("taxi","taxi.mkv","Taksi","Movie",1998,Overview:"Taksi is a 1990 Indonesian drama film.",MetadataProvider:"wikipedia",MetadataId:20));
        historic.Save(new("locked","locked.mkv","Taksi","Movie",1998,Overview:"Taksi is a 1990 Indonesian drama film.",Matched:true,MetadataProvider:"wikipedia",MetadataId:20));
        historic.Setting("queues", "preserved"); historic.Setting("metadata-identity-repair","0"); historic = new LibraryStore(Path.Combine(directory,"historic"));
        check(historic.All().Single(x=>x.Id==blade.Id) is {Title:"Blade Runner 2049",Year:2017,MetadataProvider:null,Overview:null}, "old contradictory title/year metadata resets for a fresh match");
        check(historic.All().Single(x=>x.Id=="taxi").MetadataId is null && historic.All().Single(x=>x.Id=="locked").MetadataId==20 && historic.Setting("queues")=="preserved",
            "repair preserves manual matches and saved queues");
        check(File.Exists(Path.Combine(directory,"historic","library-before-metadata-repair-v1.db")), "metadata identity repair backs up SQLite before modifying records");
        check(MetadataMatching.Choose(new[] { new MetadataMatch(1,"tv","Example", "2020",null,"", "Show"),new MetadataMatch(2,"tv","Example","2020",null,"", "Anime") },
            new MediaItem("a","a","Example","Anime",2020,Series:"Example",Season:1,Episode:1))?.Id == 2, "anime evidence resolves live-action versus anime tie");
        check(MetadataMatching.Choose(new[] { new MetadataMatch(1,"tv","Example", "2020",null,""),new MetadataMatch(2,"tv","Example","2020",null,"") }, "Example",2020) is null,
            "same-type remakes remain ambiguous");
        check(MetadataMatching.Choose(new[] {new MetadataMatch(1,"tv","Wall Street",null,null,"", "Anime")},new MediaItem("w","w","Wall","Anime")) is null,
            "anime preference cannot promote a weak prefix match");
        var noisy = new MediaItem("n","n","Example (Example Film) (Dual-Audio DDP5.1) (MultiSub) (Web)","Movie");
        check(MetadataTitles.Queries(noisy).Contains("Example Film") && MetadataTitles.Queries(noisy).Contains("Example (Example Film)"), "embedded alternate title retained while release qualifiers are removed");
        var store = new LibraryStore(Path.Combine(directory,"db")); new MetadataOptions(Providers:["tvmaze"]).Save(store);
        store.Save(new("k","k.mkv","Komi Can't Communicate","Anime",Series:"Komi Can't Communicate",Season:1,Episode:1));
        using var http = new HttpClient(new RecoveryHandler());
        var aliasesDirectory = Path.Combine(directory,"aliases"); Directory.CreateDirectory(aliasesDirectory);
        using (var file = File.Create(Path.Combine(aliasesDirectory,"anidb-titles.xml.gz")))
        using (var gzip = new System.IO.Compression.GZipStream(file,System.IO.Compression.CompressionMode.Compress))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("<animetitles><anime><title xml:lang='x-jat'>Suzume no Tojimari</title><title xml:lang='en'>Suzume</title></anime></animetitles>"));
        check((await AnimeTitles.AliasesAsync(http,aliasesDirectory,"Suzume no Tojimari",default)).Contains("Suzume"), "failed title-index refresh still uses validated cached aliases");
        await AutomaticArtwork.FetchAsync(store,directory,null,null,default,http);
        check(store.All().Single().MetadataId == 2 && ArtworkCache.IsUsable(store.All().Single().Poster), "TVmaze anime tie resolves and artwork loads");
        var poster = store.All().Single().Poster!; await File.WriteAllTextAsync(poster,"<html>cached error page</html>");
        await AutomaticArtwork.FetchAsync(store,directory,null,null,default,http);
        check(ArtworkCache.IsUsable(store.All().Single().Poster), "nonempty invalid cached image is downloaded again");
        var episode = store.All().Single().EpisodePoster!; File.Delete(episode);
        await AutomaticArtwork.FetchAsync(store,directory,null,null,default,http);
        check(ArtworkCache.IsUsable(store.All().Single().EpisodePoster), "deleted episode artwork retries despite fresh fetch timestamp");
        using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"metadata-fetch-report.json")));
        check(!diagnostic.RootElement.GetProperty("tmdbAvailable").GetBoolean(), "diagnostics record unavailable TMDB without exposing a token");
        var movies = new LibraryStore(Path.Combine(directory,"movie-db")); new MetadataOptions(Providers:["wikipedia"]).Save(movies);
        movies.Save(new("m","movie.mkv","Example Movie","Movie",2020,SourceLibraryKind:"Movie"));
        await AutomaticArtwork.FetchAsync(movies,directory,null,null,default,http);
        check(movies.All().Single() is { MetadataId:42, MetadataType:"movie", Overview:"Movie plot." } && ArtworkCache.IsUsable(movies.All().Single().Poster),
            "Action API rate limit falls back to validated film summary and poster");
        var wrongYear = new LibraryStore(Path.Combine(directory,"wrong-year")); new MetadataOptions(Providers:["wikipedia"]).Save(wrongYear);
        wrongYear.Save(new("y","y.mkv","Example Movie","Movie",1990,SourceLibraryKind:"Movie"));
        await AutomaticArtwork.FetchAsync(wrongYear,directory,null,null,default,http);
        check(wrongYear.All().Single().MetadataProvider is null, "summary fallback rejects a remake with the wrong year");
        var missing = new LibraryStore(Path.Combine(directory,"partial")); new MetadataOptions(Providers:["tmdb","tvmaze"]).Save(missing);
        missing.Save(new("p","p.mkv","Komi Can't Communicate","Anime",Series:"Komi Can't Communicate",Season:1,Episode:1));
        using var tmdb = new MetadataClient("fixture",new RecoveryHandler());
        await AutomaticArtwork.FetchAsync(missing,directory,null,null,default,http,metadata:tmdb);
        check(missing.All().Single().MetadataProvider == "tmdb" && ArtworkCache.IsUsable(missing.All().Single().Poster), "secondary provider fills missing poster while primary identity is retained");
        var compilation = new LibraryStore(Path.Combine(directory,"compilation")); new MetadataOptions(Providers:["tvmaze"]).Save(compilation);
        compilation.Save(new("c","01.Mr.Bean.Collection.1990.1995.avi","Mr Bean","Show",Series:"Mr Bean",Season:1,Episode:1,EpisodeTitle:"Incorrect pilot title",EpisodeOverview:"Incorrect plot"));
        await SeriesMetadata.FetchAsync(compilation,directory,null,null,default,http);
        check(compilation.All().Single() is { EpisodeTitle:null, EpisodeOverview:null, NumberingConflict:not null }, "unverified compilation numbers do not acquire ordinary episode plots");
        check(!MetadataTitles.IsUnverifiedCompilation(compilation.All().Single() with {Matched=true}) &&
            !MetadataTitles.IsUnverifiedCompilation(compilation.All().Single() with {NumberingSource="filename"}), "manual compilation mapping and explicit episode numbers retain authority");
        var concurrent = new LibraryStore(Path.Combine(directory,"concurrent")); new MetadataOptions(Providers:["tvmaze"]).Save(concurrent);
        concurrent.Save(new("race","race.mkv","Komi Can't Communicate","Anime",Series:"Komi Can't Communicate",Season:1,Episode:1));
        using var racingHttp = new HttpClient(new RecoveryHandler(() => concurrent.Save(concurrent.All().Single() with {Title="New manual title",Series="New manual title",Matched=true,MetadataLocked=true})));
        await AutomaticArtwork.FetchAsync(concurrent,directory,null,null,default,racingHttp);
        check(concurrent.All().Single() is {Title:"New manual title",Matched:true,MetadataLocked:true,MetadataProvider:null}, "provider response cannot overwrite a newer manual identity or lock");
        var renumbered = new LibraryStore(Path.Combine(directory,"renumbered")); new MetadataOptions(Providers:["tvmaze"]).Save(renumbered);
        renumbered.Save(new("number","number.mkv","Komi Can't Communicate","Anime",Series:"Komi Can't Communicate",Season:1,Episode:1,MetadataProvider:"tvmaze",MetadataId:2));
        using var renumberHttp = new HttpClient(new RecoveryHandler(() => renumbered.Save(renumbered.All().Single() with {Episode=5,Matched=true,MetadataLocked=true})));
        await SeriesMetadata.FetchAsync(renumbered,directory,null,null,default,renumberHttp);
        check(renumbered.All().Single() is {Episode:5,EpisodeTitle:null,Matched:true}, "season response cannot overwrite numbering changed while fetching");
        await AdaptationFixture.Run(Path.Combine(directory,"adaptations"), check);
    }
    private sealed class RecoveryHandler(Action? duringLookup = null) : HttpMessageHandler
    {
        private bool invoked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; string json;
            if (!invoked && uri.Host == "api.tvmaze.com" && duringLookup is not null) { invoked=true; duringLookup(); }
            if (uri.Host is "static.tvmaze.com" or "upload.wikimedia.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(ArtworkFixture.Png)});
            if (uri.AbsolutePath == "/w/api.php") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content=new StringContent("limited") });
            if (uri.AbsolutePath.Contains("/page/summary/")) json = """{"type":"standard","pageid":42,"title":"Example Movie (2020 film)","description":"2020 animated film","extract":"Movie plot.","thumbnail":{"source":"https://upload.wikimedia.org/poster.png"}}""";
            else if (uri.Host == "api.themoviedb.org") json = uri.AbsolutePath.Contains("search") ?
                """{"results":[{"id":2,"name":"Komi Can't Communicate","first_air_date":"2021-01-01","overview":"Primary plot","poster_path":null,"genre_ids":[16],"original_language":"ja"}]}""" :
                uri.AbsolutePath.Contains("/season/") ? """{"episodes":[]}""" : """{"name":"Komi Can't Communicate","overview":"Primary plot","poster_path":null,"genres":[{"id":16}],"original_language":"ja","first_air_date":"2021-01-01"} """;
            else if (uri.AbsolutePath.EndsWith("/seasons")) json = """[{"number":1,"name":"Season 1","image":{"medium":"https://static.tvmaze.com/season.png"}}]""";
            else if (uri.AbsolutePath.EndsWith("/episodes")) json = """[{"season":1,"number":1,"name":"Pilot","summary":"Episode plot","image":{"medium":"https://static.tvmaze.com/episode.png"}}]""";
            else
            {
                var anime = """{"id":2,"name":"Komi Can't Communicate","type":"Animation","language":"Japanese","premiered":"2021-01-01","summary":"Anime plot","url":"https://www.tvmaze.com/shows/2","image":{"medium":"https://static.tvmaze.com/poster.png"}}""";
                json = uri.AbsolutePath.Contains("/search/") ? "[{\"show\":" + anime + "},{\"show\":" + anime.Replace("\"id\":2", "\"id\":1").Replace("Animation","Scripted") + "}]" : anime;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(json,Encoding.UTF8,"application/json")});
        }
    }
}
