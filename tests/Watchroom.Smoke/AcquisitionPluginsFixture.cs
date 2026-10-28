using System.Text.Json;
using Watchroom.Core;

// Acquisition plugins: finding them, what they're asked for and where it belongs, and running them.
static class AcquisitionPluginsFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(directory, "plugins"); var anime = Path.Combine(directory, "Anime"); var shows = Path.Combine(directory, "Shows");
        var movies = Path.Combine(directory, "Movies"); var mixed = Path.Combine(directory, "Mixed");
        void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

        // Discovery: a manifest names the plugin, its command and what it gets; broken ones are reported, not loaded.
        Write(Path.Combine(plugins, "logger", "plugin.json"), """{ "name": "Logger", "description": "Writes requests down", "contentTypes": ["anime", "Shows"], "command": "acquire.ps1" }""");
        Write(Path.Combine(plugins, "logger", "acquire.ps1"), """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $copy = Get-Content -Raw $env:WATCHROOM_REQUEST_FILE | ConvertFrom-Json
            Set-Content -Path (Join-Path $env:WATCHROOM_PLUGIN_DIR 'last.json') -Value (ConvertTo-Json $request -Depth 6) -Encoding UTF8
            Set-Content -Path (Join-Path $env:WATCHROOM_PLUGIN_DIR 'request-file.txt') -Value $env:WATCHROOM_REQUEST_FILE -Encoding UTF8
            if ($copy.name -ne $request.name) { exit 3 }
            Write-Output 'working on it'
            Write-Output ('{"success": true, "message": "Queued ' + $request.name + ' into ' + $request.targetDirectory.Replace('\', '/') + '"}')
            """);
        Write(Path.Combine(plugins, "films", "plugin.json"), """{ "id": "films", "name": "Films", "contentTypes": ["movie"], "command": "fail.cmd", "arguments": ["{request}"] }""");
        Write(Path.Combine(plugins, "films", "fail.cmd"), "@echo off\r\nif not exist \"%~1\" exit /b 9\r\necho No release found 1>&2\r\nexit /b 2\r\n");
        Write(Path.Combine(plugins, "nothing", "plugin.json"), """{ "name": "Nothing", "contentTypes": ["music"], "command": "x.exe" }""");
        Write(Path.Combine(plugins, "broken", "plugin.json"), "{ not json");
        Write(Path.Combine(plugins, "nocommand", "plugin.json"), """{ "contentTypes": ["anime"] }""");
        Directory.CreateDirectory(Path.Combine(plugins, "empty"));
        var (found, problems) = ContentAcquisition.Discover(plugins);
        check(found.Select(x => x.Name).SequenceEqual(["Films", "Logger"]), "plugins with a valid plugin.json are found");
        check(found.Single(x => x.Name == "Logger") is { Id: "logger", TimeoutSeconds: 120 } logger && logger.Supports("show") && logger.Supports("anime") && !logger.Supports("movie"),
            "a plugin supports the content types its manifest lists, whatever their spelling");
        check(problems.Count == 3 && problems.Any(x => x.StartsWith("nothing:")) && problems.Any(x => x.StartsWith("broken:")) && problems.Any(x => x.StartsWith("nocommand:")),
            "unreadable manifests, missing commands and unknown content types are reported");
        check(ContentAcquisition.Discover(Path.Combine(directory, "absent")).Plugins.Count == 0, "no plugins folder means no plugins");

        var folders = new LibraryFolder[] { new(anime, "Anime"), new(shows, "Show"), new(movies, "Movie"), new(mixed, "Mixed") };
        MediaItem OnDisk(string path, string series, string kind, int season, int episode, string? seriesPath = null) =>
            new(Guid.NewGuid().ToString("N"), path, series, kind, 2023, series, season, episode, ShowId: "show-" + series, SeriesPath: seriesPath);
        MediaItem Missing(string series, string kind, int season, int episode, string? aired = "2020-01-01") =>
            new("missing-" + Guid.NewGuid().ToString("N"), Path.Combine(directory, ".missing", series, $"{season}-{episode}"), series, kind, 2023, series, season, episode,
                EpisodeTitle: $"Episode {episode}", AirDate: aired, Available: false, IsVirtual: true, ShowId: "show-" + series, MetadataProvider: "tmdb", MetadataId: 209867);

        // A show kept in season folders: an episode goes in its season's folder, a season in the show's folder.
        var frieren = Path.Combine(anime, "Frieren (2023)");
        Directory.CreateDirectory(Path.Combine(frieren, "Season 01"));
        var library = new List<MediaItem>
        {
            OnDisk(Path.Combine(frieren, "Season 01", "Frieren S01E01.mkv"), "Frieren", "Anime", 1, 1, frieren),
            OnDisk(Path.Combine(frieren, "Season 01", "Frieren S01E02.mkv"), "Frieren", "Anime", 1, 2, frieren),
            Missing("Frieren", "Anime", 1, 3), Missing("Frieren", "Anime", 2, 1), Missing("Frieren", "Anime", 2, 2, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd")),
        };
        var episode = ContentAcquisition.ForEpisode(library[2], library, folders);
        check(episode is { Scope: "episode", ContentType: "anime", Name: "Frieren S01E03", TargetExists: true, Season: 1 } && episode.TargetDirectory == Path.Combine(frieren, "Season 01") &&
            episode.ShowDirectory == frieren && episode.Episodes.Single() is { Season: 1, Episode: 3, Title: "Episode 3", Upcoming: false } && episode.ProviderIds["tmdb"] == "209867",
            "a missing episode belongs in the folder its season's files are in");
        var nextSeason = ContentAcquisition.ForEpisode(library[3], library, folders);
        check(nextSeason is { Name: "Frieren S02E01", TargetExists: false } && nextSeason.TargetDirectory == Path.Combine(frieren, "Season 02"),
            "an episode of a season with no files goes in a new season folder beside the others");
        var season = ContentAcquisition.ForSeason(library[3], 2, library, folders);
        check(season is { Scope: "season", Name: "Frieren S02", TargetExists: true, SeasonDirectory: null } && season.TargetDirectory == frieren &&
            season.Episodes.Select(x => (x.Episode, x.Upcoming)).SequenceEqual([(1, false), (2, true)]), "a season belongs in the show's folder and lists its missing and upcoming episodes");
        check(ContentAcquisition.ForSeason(library[0], 1, library, folders)?.Episodes.Length == 1 && ContentAcquisition.ForSeason(library[0], 1, library.Where(x => !x.IsVirtual), folders) is null,
            "a season with files asks only for what's missing, and a complete season asks for nothing");
        check(episode!.Libraries.Select(x => x.Kind).SequenceEqual(["anime", "show", "movie", "mixed"]) && episode.Libraries[0].Path == anime,
            "plugins are told every library folder and what it holds");

        // A show whose episodes all sit in one folder keeps new seasons there too.
        var flat = Path.Combine(shows, "Bluey");
        var flatLibrary = new[] { OnDisk(Path.Combine(flat, "Bluey S01E01.mkv"), "Bluey", "Show", 1, 1, flat), Missing("Bluey", "Show", 2, 1) };
        check(ContentAcquisition.ForEpisode(flatLibrary[1], flatLibrary, folders) is { ContentType: "show" } flatEpisode && flatEpisode.TargetDirectory == flat,
            "a show without season folders gets new episodes in its folder");
        // A show added before any of its files: its folder is named after it in the library folder for its kind.
        var wanted = new[] { Missing("Severance", "Show", 1, 1) };
        check(ContentAcquisition.ForSeason(wanted[0], 1, wanted, folders) is { TargetExists: false } wantedSeason && wantedSeason.TargetDirectory == Path.Combine(shows, "Severance (2023)"),
            "a show with no files goes in a folder of its name in a show library folder");
        check(ContentAcquisition.ForEpisode(wanted[0], wanted, [new(mixed, "Mixed")])?.TargetDirectory == Path.Combine(mixed, "Severance (2023)", "Season 01"),
            "without a folder of its kind, a show goes in a mixed one");
        check(ContentAcquisition.ForEpisode(wanted[0], wanted, []) is null, "with no library folders there's nowhere to put anything");

        var film = new MediaItem("wanted-1", Path.Combine(directory, ".missing", "dune"), "Dune: Part Two", "Movie", 2024, Available: false, IsVirtual: true, MetadataProvider: "tmdb", MetadataId: 693134);
        var movie = ContentAcquisition.ForMovie(film, folders);
        check(movie is { Scope: "movie", ContentType: "movie", Name: "Dune Part Two (2024)", Season: null } && movie.TargetDirectory == movies && movie.Episodes.Length == 0,
            "a film belongs in the movie library folder, named so it can be a file name");

        // Running: the request arrives on standard input and in a file; the last line of output may carry the result.
        var plugin = found.Single(x => x.Name == "Logger");
        var result = await ContentAcquisition.RunAsync(plugin, episode!, CancellationToken.None);
        check(result.Success && result.Message == $"Queued Frieren S01E03 into {Path.Combine(frieren, "Season 01").Replace('\\', '/')}", "a plugin's JSON reply is its result");
        using (var sent = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(plugins, "logger", "last.json")).TrimStart('﻿')))
            check(sent.RootElement.GetProperty("scope").GetString() == "episode" && sent.RootElement.GetProperty("libraries").GetArrayLength() == 4 &&
                sent.RootElement.GetProperty("episodes")[0].GetProperty("episode").GetInt32() == 3, "the plugin receives the whole request as JSON");
        var failed = await ContentAcquisition.RunAsync(found.Single(x => x.Name == "Films"), movie!, CancellationToken.None);
        check(!failed.Success && failed.Message == "No release found", "a failing plugin's exit code and error output are its result");
        check(!System.IO.File.Exists(System.IO.File.ReadAllText(Path.Combine(plugins, "logger", "request-file.txt")).Trim().TrimStart('﻿')), "the request file is removed once the plugin finishes");
        var missingCommand = await ContentAcquisition.RunAsync(plugin with { Command = "does-not-exist-" + Guid.NewGuid().ToString("N") + ".exe" }, episode!, CancellationToken.None);
        check(!missingCommand.Success && missingCommand.Message.Contains("couldn't start"), "a plugin whose command can't start fails without throwing");
    }
}
