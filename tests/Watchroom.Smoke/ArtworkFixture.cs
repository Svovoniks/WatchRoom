using System.Net;
using System.Text;

internal sealed class ArtworkFixture : HttpMessageHandler
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII=");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "static.tvmaze.com" || uri.Host == "upload.wikimedia.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        var json = uri.Host == "api.tvmaze.com"
            ? "[{\"show\":{\"name\":\"Example Show\",\"premiered\":\"2020-01-01\",\"url\":\"https://www.tvmaze.com/shows/1/example-show\",\"image\":{\"medium\":\"https://static.tvmaze.com/poster.png\"}}}]"
            : "{\"query\":{\"pages\":[{\"title\":\"Example Movie (2020 film)\",\"categories\":[{\"title\":\"Category:2020 films\"}],\"thumbnail\":{\"source\":\"https://upload.wikimedia.org/poster.png\"}}]}}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
