using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Watchroom.Core;

public static class LocalMetadata
{
    private static XDocument? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 2_000_000 });
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException) { return null; }
    }
    public static MediaItem Apply(MediaItem item)
    {
        if (item.IsVirtual) return item;
        var folder = Path.GetDirectoryName(item.Path)!;
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, string>(item.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
        var paths = item.Series is not null
            ? new[] { Path.Combine(item.SeriesPath ?? folder, "tvshow.nfo"), Path.Combine(folder, "season.nfo"), Path.ChangeExtension(item.Path, ".nfo") }
            : new[] { Path.Combine(folder, "movie.nfo"), Path.ChangeExtension(item.Path, ".nfo") };
        foreach (var path in paths.Distinct())
        {
            var document = Read(path); var root = document?.Root;
            if (root is null)
            {
                // ID-only NFO files may contain a plain provider URL instead of XML.
                if (File.Exists(path) && new FileInfo(path).Length < 64_000 && (item.Series is null || path.EndsWith("tvshow.nfo", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var contents = File.ReadAllText(path);
                        if (!contents.Contains('<')) foreach (Match match in Regex.Matches(contents, @"(?:themoviedb\.org/(?:tv|movie)/(?<tmdb>\d+)|imdb\.com/title/(?<imdb>tt\d+))"))
                            foreach (var provider in new[] { "tmdb", "imdb" }) if (match.Groups[provider].Success) ids[provider] = match.Groups[provider].Value;
                    }
                    catch (IOException) { }
                }
                continue;
            }
            string? Text(params string[] names) => root.Elements().LastOrDefault(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value.Trim();
            int? Number(params string[] names) => int.TryParse(Text(names), out var number) ? number : null;
            var show = root.Name.LocalName.Equals("tvshow", StringComparison.OrdinalIgnoreCase);
            var season = root.Name.LocalName.Equals("season", StringComparison.OrdinalIgnoreCase);
            var episode = root.Name.LocalName.Equals("episodedetails", StringComparison.OrdinalIgnoreCase);
            foreach (var field in new[] { "year", "runtime", "season", "episode" })
                if (Number(field) is not null) fields.Add(field switch { "year" => "Year", "runtime" => "RuntimeMinutes", "season" => "Season", _ => "Episode" });
            string? Value(string field, string? current, params string[] tags)
            { var text = Text(tags); if (string.IsNullOrWhiteSpace(text)) return current; fields.Add(field); return text; }
            item = item with
            {
                Title = episode || season ? item.Title : Value("Title", item.Title, "title", "name")!,
                Series = show ? Value("Title", item.Series, "title", "name") : episode ? Text("showtitle") ?? item.Series : item.Series,
                Overview = episode || season ? item.Overview : Value("Overview", item.Overview, "plot", "review"),
                SeasonTitle = season ? Value("SeasonTitle", item.SeasonTitle, "title", "name") : item.SeasonTitle,
                SeasonOverview = season ? Value("SeasonOverview", item.SeasonOverview, "plot", "review") : item.SeasonOverview,
                EpisodeTitle = episode ? Value("EpisodeTitle", item.EpisodeTitle, "title", "name") : item.EpisodeTitle,
                EpisodeOverview = episode ? Value("EpisodeOverview", item.EpisodeOverview, "plot", "review") : item.EpisodeOverview,
                Season = episode ? Number("season") ?? item.Season : season ? Number("seasonnumber") ?? item.Season : item.Season,
                Episode = episode ? Number("episode") ?? item.Episode : item.Episode,
                EpisodeEnd = episode ? Number("episodeend") ?? item.EpisodeEnd : item.EpisodeEnd,
                AbsoluteEpisode = episode ? Number("absoluteepisode") ?? item.AbsoluteEpisode : item.AbsoluteEpisode,
                AbsoluteEpisodeEnd = episode ? Number("absoluteepisodeend") ?? item.AbsoluteEpisodeEnd : item.AbsoluteEpisodeEnd,
                Year = !episode && !season ? Number("year") ?? item.Year : item.Year,
                OriginalTitle = Value("OriginalTitle", item.OriginalTitle, "originaltitle"),
                AirDate = episode ? Value("AirDate", item.AirDate, "aired") : item.AirDate,
                RuntimeMinutes = Number("runtime") ?? item.RuntimeMinutes,
                NumberingOrder = show ? Text("displayorder") ?? item.NumberingOrder : item.NumberingOrder,
                AirsBeforeSeason = Number("airsbefore_season") ?? item.AirsBeforeSeason,
                AirsBeforeEpisode = Number("airsbefore_episode") ?? item.AirsBeforeEpisode,
                AirsAfterSeason = Number("airsafter_season") ?? item.AirsAfterSeason,
                MetadataLocked = item.MetadataLocked || string.Equals(Text("lockdata"), "true", StringComparison.OrdinalIgnoreCase),
                LockedFields = (item.LockedFields ?? []).Concat((Text("lockedfields") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Equals("Name", StringComparison.OrdinalIgnoreCase) ? "Title" : x.Equals("Runtime", StringComparison.OrdinalIgnoreCase) ? "RuntimeMinutes" : x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
            var genres = root.Elements("genre").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToArray();
            var cast = root.Elements("actor").Select(x => x.Element("name")?.Value).OfType<string>().ToArray();
            if (genres.Length > 0) { item = item with { Genres = genres }; fields.Add("Genres"); }
            if (cast.Length > 0) { item = item with { Cast = cast }; fields.Add("Cast"); }
            if (double.TryParse(Text("rating"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rating))
            { item = item with { Rating = rating }; fields.Add("Rating"); }
            // Episode and season IDs are different entities; never store them as show IDs.
            if (!episode && !season)
            {
                foreach (var provider in new[] { "tmdb", "imdb", "tvdb", "tvmaze", "anidb", "anilist" })
                {
                    var id = root.Elements("uniqueid").LastOrDefault(e => string.Equals((string?)e.Attribute("type"), provider, StringComparison.OrdinalIgnoreCase))?.Value.Trim()
                        ?? Text(provider + "id", provider + "_id");
                    if (!string.IsNullOrWhiteSpace(id)) ids[provider] = id;
                }
                foreach (Match match in Regex.Matches(root.Value, @"(?:themoviedb\.org/(?:tv|movie)/(?<tmdb>\d+)|imdb\.com/title/(?<imdb>tt\d+)|thetvdb\.com/series/[^\s]+\?id=(?<tvdb>\d+))"))
                    foreach (var provider in new[] { "tmdb", "imdb", "tvdb" }) if (match.Groups[provider].Success) ids[provider] = match.Groups[provider].Value;
            }
            var thumb = root.Elements("thumb").FirstOrDefault(e => (string?)e.Attribute("aspect") is null or "poster")?.Value.Trim();
            try
            {
                if (!string.IsNullOrWhiteSpace(thumb) && !Uri.TryCreate(thumb, UriKind.Absolute, out _)) thumb = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, thumb));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { thumb = null; }
            if (File.Exists(thumb))
            {
                if (episode) { item = item with { EpisodePoster = thumb }; fields.Add("EpisodePoster"); }
                else if (season) { item = item with { SeasonPoster = thumb }; fields.Add("SeasonPoster"); }
                else { item = item with { Poster = thumb, SeriesPoster = show ? thumb : item.SeriesPoster, PosterSource = "Local artwork" }; fields.Add("Poster"); }
            }
            else if (Uri.TryCreate(thumb, UriKind.Absolute, out var imageUri) && imageUri.Scheme == "https")
            {
                if (episode) { item = item with { LocalEpisodePosterUrl = thumb }; fields.Add("EpisodePoster"); }
                else if (season) { item = item with { LocalSeasonPosterUrl = thumb }; fields.Add("SeasonPoster"); }
                else { item = item with { LocalPosterUrl = thumb }; fields.Add("Poster"); }
            }
            if (show) item = item with { MetadataType = "tv" };
            else if (!season && !episode) item = item with { MetadataType = "movie", Series = null, Season = null, Episode = null };
        }
        return LibraryIdentity.Ensure(item with { ProviderIds = ids, LocalMetadataFields = fields.ToArray() });
    }

    public static void Export(MediaItem item)
    {
        if (item.IsVirtual) return;
        var episode = item.Series is not null;
        var root = new XElement(episode ? "episodedetails" : "movie");
        void Add(string key, object? value) { if (value is not null) root.Add(new XElement(key, value)); }
        Add("title", episode ? item.EpisodeTitle ?? item.EpisodeDisplayTitle : item.Title);
        Add("plot", episode ? item.EpisodeOverview : item.Overview); Add("showtitle", item.Series);
        Add("season", item.Season); Add("episode", item.Episode); Add("episodeend", item.EpisodeEnd);
        Add("absoluteepisode", item.AbsoluteEpisode); Add("aired", item.AirDate); Add("year", item.Year); Add("runtime", item.RuntimeMinutes);
        Add("absoluteepisodeend", item.AbsoluteEpisodeEnd); Add("airsbefore_season", item.AirsBeforeSeason);
        Add("airsbefore_episode", item.AirsBeforeEpisode); Add("airsafter_season", item.AirsAfterSeason);
        Add("originaltitle", item.OriginalTitle); Add("rating", item.Rating); Add("lockdata", item.MetadataLocked);
        Add("lockedfields", string.Join('|', item.LockedFields ?? []));
        if (!episode) foreach (var id in item.ProviderIds ?? []) root.Add(new XElement("uniqueid", new XAttribute("type", id.Key), id.Value));
        foreach (var genre in item.Genres ?? []) Add("genre", genre);
        foreach (var actor in item.Cast ?? []) root.Add(new XElement("actor", new XElement("name", actor)));
        Add("thumb", episode ? item.EpisodePoster : item.Poster);
        Write(Path.ChangeExtension(item.Path, ".nfo"), root);
        if (item.SeriesPath is { } folder)
        {
            var show = new XElement("tvshow", new XElement("title", item.Series!), new XElement("displayorder", item.NumberingOrder));
            if (item.Overview is { } plot) show.Add(new XElement("plot", plot));
            if (item.Year is { } year) show.Add(new XElement("year", year));
            if (item.SeriesPoster is { } poster) show.Add(new XElement("thumb", new XAttribute("aspect", "poster"), poster));
            show.Add(new XElement("lockdata", item.MetadataLocked), new XElement("lockedfields", string.Join('|', item.LockedFields ?? [])));
            foreach (var genre in item.Genres ?? []) show.Add(new XElement("genre", genre));
            foreach (var id in item.ProviderIds ?? []) show.Add(new XElement("uniqueid", new XAttribute("type", id.Key), id.Value));
            Write(Path.Combine(folder, "tvshow.nfo"), show);
            var seasonFolder = Path.GetDirectoryName(item.Path)!;
            // A flat show directory can contain several seasons: do not overwrite one season.nfo there.
            if (seasonFolder != folder && item.Season is { } seasonNumber)
            {
                var season = new XElement("season", new XElement("seasonnumber", seasonNumber));
                if (item.SeasonTitle is { } title) season.Add(new XElement("title", title));
                if (item.SeasonOverview is { } plotText) season.Add(new XElement("plot", plotText));
                if (item.SeasonPoster is { } seasonPoster) season.Add(new XElement("thumb", seasonPoster));
                Write(Path.Combine(seasonFolder, "season.nfo"), season);
            }
        }
    }
    private static void Write(string path, XElement root)
    {
        // Preserve unknown tags owned by other media managers.
        var existing = Read(path)?.Root;
        if (existing is not null && existing.Name == root.Name)
            foreach (var child in existing.Elements().Where(e => !root.Elements(e.Name).Any())) root.Add(new XElement(child));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { new XDocument(root).Save(temporary); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
