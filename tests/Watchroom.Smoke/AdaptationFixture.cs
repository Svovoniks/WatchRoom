using System.Net;
using System.Text;
using Watchroom.Core;

static class AdaptationFixture
{
    public static async Task Run(string directory, Action<bool,string> check)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,"Anime","One Piece","One Piece - 001.mkv");
        var anime = new MediaItem("anime",path,"One Piece","Show",Series:"One Piece",Season:1,Episode:1,ShowId:"anime-show",MetadataKind:"Show",MetadataType:"tv",MetadataProvider:"tvmaze",MetadataId:100,ProviderIds:new() { ["tvmaze"]="100" },Overview:"Live action plot",SourceLibraryKind:"Mixed");
        var options = new MetadataOptions(Providers:["tvmaze"]);
        var store = new LibraryStore(Path.Combine(directory,"db")); options.Save(store);
        var oldPoster = Path.Combine(directory,"old.png"); await File.WriteAllBytesAsync(oldPoster,ArtworkFixture.Png);
        store.Save(anime with { Poster=oldPoster,SeriesPoster=oldPoster,EpisodeTitle="Wrong pilot",EpisodeOverview="Wrong episode",EpisodePoster=oldPoster });
        using var handler = new AdaptationHandler(); using var http = new HttpClient(handler);
        await AutomaticArtwork.FetchAsync(store,directory,null,null,default,http);
        var repaired = store.All().Single();
        check(repaired.Kind=="Anime" && repaired.MetadataId==101 && repaired.Year==1999 && repaired.Overview=="Anime plot", "cached live-action identity is rejected for anime source and hierarchy receives the corrected plot and premiere year");
        check(repaired.Poster!=oldPoster && !repaired.ProviderIds!.Values.Contains("100"), "identity correction replaces unrelated artwork and removes conflicting provider IDs");
        check(repaired.EpisodeTitle!="Wrong pilot" && repaired.EpisodeOverview!="Wrong episode", "identity correction clears stale episode metadata before refetching");
        var sequel = anime with { Id="sequel",Path=Path.Combine(directory,"Anime","Смешарики","Новые приключения","01. Пощады Не Будет.mp4"),Title="Новые приключения",Series="Новые приключения",SeriesPath=Path.Combine(directory,"Anime","Смешарики","Новые приключения"),ShowId="sequel-show",MetadataProvider=null,MetadataId=null,ProviderIds=[],MetadataKind=null,Overview=null };
        check(MetadataTitles.Queries(sequel).Contains("Смешарики Новые приключения"), "nested franchise folder supplies a full spin-off search alias");
        var sequelStore = new LibraryStore(Path.Combine(directory,"sequel-db")); options.Save(sequelStore); sequelStore.Save(sequel);
        await AutomaticArtwork.FetchAsync(sequelStore,directory,null,null,default,http);
        check(sequelStore.All().Single() is {Series:"Смешарики. Новые приключения",MetadataId:200,Kind:"Show",ShowId:"sequel-show"}, "full spin-off title matches without merging into the original show or relabeling Russian animation as anime");
        using var tmdb = new MetadataClient("fixture-read-token",handler);
        var cachedWrong = await tmdb.MatchAsync(anime with { MetadataProvider="tmdb",MetadataId=100,ProviderIds=new() { ["tmdb"]="100" } });
        check(cachedWrong is {Id:101,Kind:"Anime",Animated:true}, "TMDB known IDs also revalidate animation evidence");
        var live = await tmdb.MatchAsync(anime with {Path=Path.Combine(directory,"Shows","One Piece","One Piece S01E01.mkv"),MetadataProvider=null,MetadataId=null,ProviderIds=[],Year=2023});
        check(live is {Id:100,Kind:"Show"}, "2023 live-action One Piece remains distinct from the anime");
        var manual = await tmdb.MatchAsync(anime with {Matched=true,MetadataProvider="tmdb",MetadataId=100,ProviderIds=new() { ["tmdb"]="100" }});
        check(manual?.Id==100, "manual identity selection keeps authority over source-folder preferences");
        using var keyClient = new MetadataClient(new string('a',32),handler);
        await keyClient.SearchAsync("One Piece",true);
        check(handler.SawApiKey && handler.SawBearer, "TMDB API keys use api_key authentication while read-access tokens use Bearer authentication");
        var bridged = await tmdb.MatchAsync(new("owl","owl.mkv","Сова","Show",2016,Series:"Сова",MetadataType:"tv",ProviderIds:new() { ["tvmaze"]="999" }));
        check(bridged is { Id:333, Overview:"Описание" }, "external show ID resolves duplicate names and missing translation falls back to native synopsis");
        check(handler.SafeBridge, "TVmaze identity bridge receives no TMDB credential");
        var mergeStore = new LibraryStore(Path.Combine(directory,"merge-db")); new MetadataOptions(GroupShowsByProvider:true).Save(mergeStore);
        mergeStore.Save(anime with {Kind="Anime",MetadataKind="Anime",ProviderIds=new() { ["tmdb"]="999" }});
        mergeStore.Save(anime with {Id="live",Path="live.mkv",Kind="Show",MetadataKind="Show",ShowId="live-show",ProviderIds=new() { ["tmdb"]="999" }});
        mergeStore.GroupShowsByProvider();
        check(mergeStore.All().Select(x=>x.ShowId).Distinct().Count()==2, "provider grouping refuses contradictory anime and live-action classifications");
    }

    private sealed class AdaptationHandler : HttpMessageHandler
    {
        public bool SawApiKey, SawBearer, SafeBridge;
        private const string Anime = """{"id":101,"name":"One Piece","type":"Animation","language":"Japanese","premiered":"1999-10-20","summary":"Anime plot","url":"https://www.tvmaze.com/shows/101","image":{"medium":"https://static.tvmaze.com/anime.png"}}""";
        private const string Live = """{"id":100,"name":"One Piece","type":"Scripted","language":"English","premiered":"2023-08-31","summary":"Live action plot","url":"https://www.tvmaze.com/shows/100","image":{"medium":"https://static.tvmaze.com/live.png"}}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var uri=request.RequestUri!; string json;
            if (uri.Host=="api.tvmaze.com" && uri.AbsolutePath.EndsWith("999"))
            {
                SafeBridge = request.Headers.Authorization is null && !uri.Query.Contains("api_key");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("""{"externals":{"thetvdb":335,"imdb":null}}""",Encoding.UTF8,"application/json") });
            }
            if(uri.Host is "static.tvmaze.com" or "image.tmdb.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(ArtworkFixture.Png) });
            if(uri.Host=="api.themoviedb.org")
            {
                SawApiKey |= uri.Query.Contains("api_key=") && request.Headers.Authorization is null;
                SawBearer |= request.Headers.Authorization?.Scheme=="Bearer" && !uri.Query.Contains("api_key=");
                var live="""{"id":100,"name":"One Piece","first_air_date":"2023-08-31","genre_ids":[28],"genres":[{"id":28}],"original_language":"en","overview":"Live action plot"}""";
                var anime="""{"id":101,"name":"One Piece","first_air_date":"1999-10-20","genre_ids":[16],"genres":[{"id":16}],"original_language":"ja","overview":"Anime plot"}""";
                json=uri.AbsolutePath.Contains("find/") ? """{"tv_results":[{"id":333}]}""" : uri.AbsolutePath.EndsWith("333")
                    ? """{"id":333,"name":"Owl","original_name":"Сова","first_air_date":"2016-01-01","overview":"OVERVIEW"}""".Replace("OVERVIEW",uri.Query.Contains("ru-RU") ? "Описание" : "")
                    : uri.AbsolutePath.Contains("search") ? "{\"results\":["+live+","+anime+"]}" : uri.AbsolutePath.EndsWith("100") ? live : anime;
            }
            else if(uri.AbsolutePath.EndsWith("/episodes") || uri.AbsolutePath.EndsWith("/seasons")) json="[]";
            else if(uri.AbsolutePath.Contains("search")) json=Uri.UnescapeDataString(uri.Query).Contains("приключения")
                ? """[{"show":{"id":200,"name":"Смешарики. Новые приключения","type":"Animation","language":"Russian","premiered":"2012-10-27","summary":"Russian animation","url":"https://www.tvmaze.com/shows/200"}}]"""
                : "[{\"show\":"+Live+"},{\"show\":"+Anime+"}]";
            else json=uri.AbsolutePath.EndsWith("100") ? Live : Anime;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(json,Encoding.UTF8,"application/json") });
        }
    }
}
