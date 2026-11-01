using System.Text.Json;
using BlackPearl.Core;

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
            $copy = Get-Content -Raw $env:BLACKPEARL_REQUEST_FILE | ConvertFrom-Json
            Set-Content -Path (Join-Path $env:BLACKPEARL_PLUGIN_DIR 'last.json') -Value (ConvertTo-Json $request -Depth 6) -Encoding UTF8
            Set-Content -Path (Join-Path $env:BLACKPEARL_PLUGIN_DIR 'request-file.txt') -Value $env:BLACKPEARL_REQUEST_FILE -Encoding UTF8
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

        Write(Path.Combine(plugins, "interactive", "plugin.json"), """{"name":"Interactive","contentTypes":["show"],"command":"browse.ps1","interactive":true,"settingsSchema":"settings.schema.json"}""");
        Write(Path.Combine(plugins, "interactive", "browse.ps1"), """
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            if ($request.action -eq 'search' -and $request.options.query -eq 'Provider title') {
                '{"success":true,"stage":"shows","choices":[{"id":"show-1","title":"Provider title","detail":"Catalog"}],"message":"Choose a title"}'
            } elseif ($request.action -eq 'acquire' -and $request.selectedReleases[0] -eq 'release-1') {
                '{"success":true,"message":"Queued only the selected release"}'
            } else { '{"success":false,"message":"Unexpected interactive request"}'; exit 1 }
            """);
        var interactive = ContentAcquisition.Discover(plugins).Plugins.Single(x => x.Interactive);
        check(interactive.SettingsSchema == "settings.schema.json", "an interactive plugin's manifest advertises its settings schema");
        var browsed = await ContentAcquisition.RunAsync(interactive, episode! with { Action = "search", Options = new(Query: "Provider title") }, CancellationToken.None);
        check(browsed is { Success: true, Stage: "shows", Choices.Length: 1 } && browsed.Choices[0].Id == "show-1", "interactive search actions return typed choices through the plugin process protocol");
        var confirmed = await ContentAcquisition.RunAsync(interactive, episode! with { Action = "acquire", SelectedReleases = ["release-1"] }, CancellationToken.None);
        check(confirmed.Success && confirmed.Message.Contains("only the selected"), "interactive confirmation delivers the selected release IDs to the plugin");

        AcquisitionChoice[] relevanceChoices = [
            new("psa", "Hazbin Hotel S02E01 1080p 10bit WEBRip 6CH x265 HEVC-PSA", Matches: true, Episode: "S02E01"),
            new("megusta", "Hazbin.Hotel.S02E01.1080p.HEVC.x265-MeGusta.EZTV", Matches: true, Episode: "S02E01"),
            new("other-quality", "Hazbin Hotel S02E01 720p MeGusta", Matches: false, Episode: "S02E01"),
            new("other-episode", "Hazbin Hotel S02E02 1080p MeGusta", Matches: false, Episode: "S02E02")];
        var ranked = ContentAcquisition.RankChoices(relevanceChoices, "Hazbin Hotel megusta");
        check(ranked.Select(x => x.Id).SequenceEqual(["megusta", "psa", "other-quality", "other-episode"]),
            "search relevance puts the requested release group first while retaining episode and quality priority");
        check(ContentAcquisition.RankChoices(relevanceChoices, "Házbin.hotel, MEGUSTA")[0].Id == "megusta",
            "search relevance ignores case, punctuation and accents");
        check(ContentAcquisition.RankChoices(relevanceChoices, "Hazbin Hotel me")[0].Id == "megusta",
            "a partially typed release-group name improves its position");
        check(ContentAcquisition.RankChoices(relevanceChoices, " ").SequenceEqual(relevanceChoices),
            "empty searches and equally relevant results preserve their input order");
        var repeated = relevanceChoices[0] with { Title = "Hazbin Hazbin Hazbin Hotel Hotel S02E01 1080p PSA" };
        check(ContentAcquisition.RankChoices([repeated, relevanceChoices[1]], "Hazbin Hotel megusta")[0].Id == "megusta",
            "repeated title words cannot outweigh a missing release-group search term");
        check(ReferenceEquals(ranked[0], relevanceChoices[1]) && ranked.Single(x => x.Id == "other-quality").Matches == false,
            "ranking retains release IDs and download eligibility without modifying the original choices");
        AcquisitionChoice[] seasonChoices = [
            new("other-show", "Dark Matter Unrelated S01E01 1080p"),
            new("e2-720", "Dark Matter S02E02 720p", Episode: "S02E02"),
            new("e10", "Dark Matter S02E10 1080p", Matches: true, Episode: "S02E10"),
            new("e2", "Dark Matter S02E02 1080p", Matches: true, Episode: "S02E02"),
            new("e1-720", "Dark Matter S02E01 720p", Episode: "S02E01"),
            new("e1", "Dark Matter S02E01 1080p", Matches: true, Episode: "S02E01")];
        check(ContentAcquisition.RankChoices(seasonChoices, "Dark Matter").Select(x => x.Id).SequenceEqual(["e1", "e2", "e10", "e1-720", "e2-720", "other-show"]),
            "matching releases come first in episode order, then other releases of the requested episodes, then the rest");
        check(ContentAcquisition.RankChoices([.. seasonChoices, new("pack", "Dark Matter S02 1080p WEB-Grp", Assignable: true)], "Dark Matter").Select(x => x.Id)
            .SequenceEqual(["e1", "e2", "e10", "e1-720", "e2-720", "pack", "other-show"]),
            "season packs and specials follow releases of the requested episodes and come before releases that don't match");
        AcquisitionChoice[] alternates = [
            new("e2-other", "Dark Matter S02E02 1080p PSA", Matches: true, Episode: "S02E02"),
            new("e1-other", "Dark Matter S02E01 1080p PSA", Matches: true, Episode: "S02E01"),
            new("e2-best", "Dark Matter S02E02 1080p MeGusta", Matches: true, Episode: "S02E02"),
            new("e1-best", "Dark Matter S02E01 1080p MeGusta", Matches: true, Episode: "S02E01"),
            new("e1-720", "Dark Matter S02E01 720p MeGusta", Episode: "S02E01")];
        check(ContentAcquisition.RankChoices(alternates, "Dark Matter megusta").Select(x => x.Id).SequenceEqual(["e1-best", "e2-best", "e1-other", "e2-other", "e1-720"]),
            "the releases Select matching picks come first, before alternative versions of the same episodes");
        check(ContentAcquisition.ReleaseGroup("[Erai-raws] Show - 01 [1080p][ABCDEF12].mkv") == "Erai-raws" && ContentAcquisition.ReleaseGroup("Show.S01E01.1080p.HEVC.x265-MeGusta.EZTV") == "MeGusta"
            && ContentAcquisition.ReleaseGroup("Show S01E01 1080p WEB H 264-NTb [eztvx.to].mkv") == "NTb" && ContentAcquisition.ReleaseGroup("Show S01E01 1080p WEB-DL") is null
            && ContentAcquisition.ReleaseGroup("Show - OVA") is null && ContentAcquisition.ReleaseGroup("My Show - S01E01 - The Pilot") is null,
            "release groups are read from leading brackets and scene suffixes, not from titles or sources");
        AcquisitionChoice[] twoGroups = [
            new("e1-b", "Show S01E01 1080p WEB-GrpB", Matches: true, Episode: "S01E01"),
            new("e1-a", "Show S01E01 1080p WEB-GrpA", Matches: true, Episode: "S01E01"),
            new("e2-a", "Show S01E02 1080p WEB-GrpA", Matches: true, Episode: "S01E02"),
            new("e2-b", "Show S01E02 1080p WEB-GrpB", Matches: true, Episode: "S01E02"),
            new("e3-a", "Show S01E03 1080p WEB-GrpA", Matches: true, Episode: "S01E03")];
        string[] Picked(AcquisitionChoice[] list, string[]? existing = null) =>
            ContentAcquisition.RankChoices(list, "Show", existing).Where(x => x.Matches).GroupBy(x => x.Episode).Select(x => x.First().Id).ToArray();
        check(Picked(twoGroups).SequenceEqual(["e1-a", "e2-a", "e3-a"]), "picks come from the one group that provides the most episodes instead of whichever is listed first");
        check(Picked(twoGroups, ["Show.S01E05.1080p.WEB.x264-GrpB", "Show.S01E06.1080p.WEB.x264-GrpB"]).SequenceEqual(["e1-b", "e2-b", "e3-a"]),
            "the group of the show's files on disk is preferred, with another group only for episodes it lacks");
        check(Picked(twoGroups, ["Show - S01E05 - Title (1080p WEB x264 GrpB)"]).SequenceEqual(["e1-b", "e2-b", "e3-a"]),
            "a group named inside a renamed file's brackets counts as the show's group");
        check(ContentAcquisition.NameDistance("Show.S01E01.1080p.HEVC.x265-MeGusta", "Show.S01E02.1080p.HEVC.x265-MeGusta") == 0
            && ContentAcquisition.NameDistance("[Group] Show - 01 [1080p][ABCDEF12].mkv", "[Group] Show - 02v2 [1080p][1234ABCD].mkv") == 0
            && ContentAcquisition.NameDistance("Show.S01E01.1080p.HEVC.x265-MeGusta", "Show.S01E01.1080p.HEVC.x265-ELiTE") == 6
            && ContentAcquisition.NameDistance("Show.S01E01.1080p.HEVC.x265-MeGusta", "Show.S01E01.720p.HEVC.x265-MeGusta") == 2,
            "names differing only in episode numbers, versions or checksums are the same; a different group costs more than a different tag");
        // Same group, two encodes: the batch keeps one encode rather than whichever is listed first for each episode.
        check(Picked([
                new("e1-web", "Show S01E01 1080p WEB H264-Grp", Matches: true, Episode: "S01E01"),
                new("e1-hevc", "Show S01E01 1080p HEVC x265-Grp", Matches: true, Episode: "S01E01"),
                new("e2-hevc", "Show S01E02 1080p HEVC x265-Grp", Matches: true, Episode: "S01E02"),
                new("e2-web", "Show S01E02 1080p WEB H264-Grp", Matches: true, Episode: "S01E02"),
                new("e3-hevc", "Show S01E03 1080p HEVC x265-Grp", Matches: true, Episode: "S01E03")]).SequenceEqual(["e1-hevc", "e2-hevc", "e3-hevc"]),
            "picks are named alike across the batch, down to the encode");
        check(Picked([
                new("e1-web", "Show S01E01 1080p WEB H264-Grp", Matches: true, Episode: "S01E01"),
                new("e1-hevc", "Show S01E01 1080p HEVC x265-Grp", Matches: true, Episode: "S01E01"),
                new("e2-hevc", "Show S01E02 1080p HEVC x265-Grp", Matches: true, Episode: "S01E02"),
                new("e2-web", "Show S01E02 1080p WEB H264-Grp", Matches: true, Episode: "S01E02")], ["Show.S01E07.1080p.WEB.H264-Grp"]).SequenceEqual(["e1-web", "e2-web"]),
            "the batch follows the encode of the files on disk");
        check(Picked([new("h264", "Show S01E01 1080p H264-Grp", Matches: true, Episode: "S01E01"), new("hevc", "Show S01E01 1080p HEVC x265-Grp", Matches: true, Episode: "S01E01")],
            ["Show.S01E04.1080p.HEVC.x265-Grp"]).SequenceEqual(["hevc"]), "within a group, the release named most like the files on disk is preferred");
        check(Picked([new("plain", "Show S01E01 1080p", Matches: true, Episode: "S01E01"), new("named", "Show S01E01 1080p WEB-GrpA", Matches: true, Episode: "S01E01")]).SequenceEqual(["named"]),
            "releases from a named group are preferred to unnamed ones");
        MediaItem ShowFile(string path, int season, bool placeholder = false) => new(path, path, "Show", "Show", Series: "Show", Season: season, Episode: 1, IsVirtual: placeholder);
        var showRequest = episode! with { Scope = "season", Title = "Show", Season = 2 };
        check(ContentAcquisition.ExistingNames(showRequest, [ShowFile(@"D:\Show\S1\Show.S01E01-GrpA.mkv", 1), ShowFile(@"D:\Show\S2\Show.S02E01-GrpB.mkv", 2), ShowFile(@"D:\Show\S2\placeholder", 2, true)])
            .SequenceEqual(["Show.S02E01-GrpB"]) && ContentAcquisition.ExistingNames(showRequest with { Season = 3 }, [ShowFile(@"D:\Show\S1\Show.S01E01-GrpA.mkv", 1)]).SequenceEqual(["Show.S01E01-GrpA"]),
            "releases are compared with the season's files on disk, or the show's when that season has none");
        check(new AcquisitionChoice("x", "A Good Girls Guide to Murder S01E01 480p x264 RUBiK").EpisodeCode == "S01E01" && new AcquisitionChoice("y", "Show.3x07.720p").EpisodeCode == "S03E07"
            && new AcquisitionChoice("z", "Show S02E01 1080p", Episode: "S02E01").EpisodeCode == "S02E01" && new AcquisitionChoice("w", "Movie 2021 1080p x265").EpisodeCode is null,
            "every release shows the episode its name gives, requested or not");
    }
}
