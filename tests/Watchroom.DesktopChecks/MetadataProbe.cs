using System.IO;
using System.Net.Http;
using Watchroom.Core;
using Watchroom.Desktop;

static class MetadataProbe
{
    public static async Task Audit(string directory, string output, bool apply)
    {
        var store = new LibraryStore(directory);
        var all = store.All();
        var missing = all.Where(x => !x.IsVirtual && !x.IsExtra).GroupBy(LibraryIdentity.ShowKey)
            .Where(g => !g.Any(x => ArtworkCache.IsUsable(x.SeriesPoster) || ArtworkCache.IsUsable(x.Poster)) || !g.Any(x => !string.IsNullOrWhiteSpace(x.Overview))).ToArray();
        using var client = new MetadataClient(MetadataCredential.Load(directory), options: MetadataOptions.Load(store));
        var results = new List<object>();
        foreach (var group in missing)
        {
            var item = group.First();
            try
            {
                var match = await client.MatchAsync(MetadataTitles.WithSplitIdentity(item, all));
                results.Add(new { item.Id, Title = item.DisplayTitle, item.Path, Queries = MetadataTitles.Queries(item), Match = match });
                Console.WriteLine(item.DisplayTitle + ": " + (match is null ? "UNRESOLVED" : $"{match.Type}/{match.Id} {match.Title} {match.Year}"));
                if (apply && match is not null)
                {
                    var poster = await client.CachePosterAsync(match, Path.Combine(directory,"posters"));
                    foreach (var entry in group)
                    {
                        if (entry.Matched || entry.MetadataLocked || (entry.LockedFields?.Length ?? 0) > 0 || (entry.LocalMetadataFields?.Length ?? 0) > 0) continue;
                        var updated = MetadataClassification.Apply(entry, match.Kind ?? entry.Kind, match.Type, match.Type == "tv" ? match.Title : null);
                        updated = AutomaticArtwork.MergeDetails(LibraryIdentity.WithProvider(updated,"tmdb",match.Id,true), match);
                        store.Save(updated with { Poster = poster ?? entry.Poster, SeriesPoster = match.Type == "tv" ? poster ?? entry.SeriesPoster : entry.SeriesPoster,
                            Overview = string.IsNullOrWhiteSpace(match.Overview) ? entry.Overview : match.Overview, PosterSource = $"https://www.themoviedb.org/{match.Type}/{match.Id}", ArtworkFetchedAt = Wire.Now });
                    }
                }
            }
            catch (HttpRequestException ex) { Console.WriteLine(item.DisplayTitle + ": HTTP " + (int?)ex.StatusCode); }
        }
        await File.WriteAllTextAsync(output, Wire.Serialize(results));
    }
    public static async Task Run(string directory, string output)
    {
        var token = MetadataCredential.Load(directory);
        Console.WriteLine("Saved TMDB credential available: " + !string.IsNullOrWhiteSpace(token));
        var results = new List<object>();
        using var client = new MetadataClient(token);
        foreach (var query in new[] { "Берегись автомобиля", "Бриллиантовая рука", "Джентльмены удачи", "Иван Васильевич меняет профессию", "Кавказская пленница", "Операция Ы и другие приключения Шурика", "Остров сокровищ", "На Дерибасовской хорошая погода", "Полицейский с Рублёвки", "Полицейский с Рублёвки Новогодний беспредел", "Подозрительная сова", "Обыкновенное чудо", "12 стульев", "Taxi 3", "Taxi 4", "The Naked Gun 2 1/2 The Smell of Fear", "The Naked Gun 33 1/3 The Final Insult" })
        {
            try
            {
                var matches = (await client.SearchAsync(query, true)).Concat(await client.SearchAsync(query, false)).ToList();
                results.Add(new { Query = query, Matches = matches });
                Console.WriteLine(query + ": " + string.Join("; ", matches.Take(8).Select(x => $"{x.Id} {x.Type} {x.Title} [{x.OriginalTitle}] {x.Year} {x.Kind}")));
            }
            catch (HttpRequestException ex) { Console.WriteLine(query + ": HTTP " + (int?)ex.StatusCode); }
        }
        await File.WriteAllTextAsync(output, Wire.Serialize(results));
    }
}
