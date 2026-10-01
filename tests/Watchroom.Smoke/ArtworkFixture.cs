using System.Net;
using System.Text;

internal sealed class ArtworkFixture(bool failImages = false, bool omitImages = false, bool anime = false, bool animatedMovie = false, bool ambiguous = false) : HttpMessageHandler
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII=");
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!; Requests.Add(uri);
        if (uri.Host == "static.tvmaze.com" || uri.Host == "upload.wikimedia.org") return Task.FromResult(new HttpResponseMessage(failImages ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        var json = uri.AbsolutePath.EndsWith("/seasons")
            ? """[{"number":1,"name":"First season","summary":"<p>Season one story.</p>","url":"https://www.tvmaze.com/seasons/11","image":{"medium":"https://static.tvmaze.com/season1.png"}},{"number":2,"name":"Second season","summary":"<p>Season two story.</p>","url":"https://www.tvmaze.com/seasons/12","image":{"medium":"https://static.tvmaze.com/season2.png"}}]"""
            : uri.AbsolutePath.EndsWith("/episodes")
            ? """[{"season":1,"number":1,"name":"Pilot","summary":"<p>Episode one &amp; its plot.</p>","airdate":"2020-01-01","runtime":45,"url":"https://www.tvmaze.com/episodes/101","image":{"medium":"https://static.tvmaze.com/episode1.png"}},{"season":2,"number":1,"name":"Return","summary":"<p>Second season premiere.</p>","airdate":"2021-01-01","runtime":null,"url":"https://www.tvmaze.com/episodes/201","image":{"medium":"https://static.tvmaze.com/episode2.png"}},{"season":1,"number":null,"name":"Unnumbered special","summary":"Special plot","image":null}]"""
            : uri.Host == "api.tvmaze.com"
            ? """[{"show":{"id":1,"name":"Example Show","type":"Scripted","language":"English","summary":"<p>A <b>show</b> &amp; its story.</p>","premiered":"2020-01-01","url":"https://www.tvmaze.com/shows/1/example-show","image":{"medium":"https://static.tvmaze.com/poster.png"}}}]"""
            : """{"query":{"pages":[{"title":"Example Movie (2020 film)","extract":"A movie and its story.","categories":[{"title":"Category:2020 films"}],"thumbnail":{"source":"https://upload.wikimedia.org/poster.png"}}]}}""";
        if (anime && uri.Host == "api.tvmaze.com") json = json.Replace("\"type\":\"Scripted\",\"language\":\"English\"", "\"type\":\"Animation\",\"language\":\"Japanese\"");
        if (animatedMovie && uri.Host == "en.wikipedia.org") json = json.Replace("Category:2020 films", "Category:Japanese animated films");
        if (ambiguous && uri.Host == "en.wikipedia.org") json = json.Replace("Example Movie (2020 film)", "Example Show (2020 film)");
        if (uri.Host == "api.tvmaze.com" && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/shows/\d+$"))
        {
            using var document = System.Text.Json.JsonDocument.Parse(json); json = document.RootElement[0].GetProperty("show").GetRawText();
        }
        if (omitImages) json = System.Text.RegularExpressions.Regex.Replace(json, "\"image\":\\{[^}]*\\}", "\"image\":null");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

internal sealed class TmdbFixture(bool anime = false, bool movie = false, bool ambiguous = false) : HttpMessageHandler
{
    public bool ImageReceivedToken { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "image.tmdb.org")
        {
            ImageReceivedToken |= request.Headers.Authorization is not null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) });
        }
        var json = uri.AbsolutePath.Contains("/season/")
            ? """{"name":"TMDB Season","overview":"TMDB season plot","poster_path":"/tmdb-season.jpg","episodes":[{"episode_number":1,"name":"TMDB Pilot","overview":"TMDB episode plot","still_path":"/tmdb-episode.jpg","air_date":"2020-02-03","runtime":42},{"episode_number":2,"name":"No artwork","overview":"","still_path":null,"air_date":null,"runtime":null}]}"""
            : """{"results":[{"id":1,"name":"Example Show","first_air_date":"2020-01-01","overview":"TMDB show plot","poster_path":"/show.jpg"}]}""";
        if (uri.AbsolutePath == "/3/search/movie")
            json = movie || ambiguous ? """{"results":[{"id":2,"title":"TITLE","release_date":"2020-01-01","overview":"Movie plot","poster_path":"/movie.jpg"}]}""".Replace("TITLE", ambiguous ? "Example Show" : "Example Movie") : """{"results":[]}""";
        if (uri.AbsolutePath is "/3/tv/1" or "/3/movie/2")
        {
            json = """{"id":1,"name":"Example Show","title":"Example Movie","first_air_date":"2020-01-01","release_date":"2020-01-01","overview":"Details plot","poster_path":"/details.jpg","genres":[{"id":GENRE}],"original_language":"LANGUAGE"}""".Replace("GENRE", anime ? "16" : "18").Replace("LANGUAGE", anime ? "ja" : "en");
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
