using System.Text.Json;
using System.Text.Json.Nodes;
using BlackPearl.Core;

// The kinds of name the reader has to handle. Names are synthetic, so no library path ends up in the repository.
static class FilenameReadingFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        MediaItem Read(params string[] parts) => FileNames.Parse(Path.Combine([directory, .. parts]), "Mixed", directory);
        void Numbered(string[] parts, string title, int? season, int episode, int? end = null)
        {
            var item = Read(parts);
            check(MetadataTitles.Normalize(item.DisplayTitle) == MetadataTitles.Normalize(title) && (season is null || item.Season == season) && (item.Episode == episode || item.AbsoluteEpisode == episode || item.SourcePartEpisode == episode) && item.EpisodeEnd == end,
                $"{parts[^1]} reads as {title} {season}x{episode}{(end is null ? "" : "-" + end)} (got {item.DisplayTitle} {item.Season}x{item.Episode}-{item.EpisodeEnd})");
        }
        void Film(string[] parts, string title, int? year)
        {
            var item = Read(parts);
            check(item.Series is null && item.Episode is null && MetadataTitles.Normalize(item.Title) == MetadataTitles.Normalize(title) && (year is null || item.Year == year),
                $"{parts[^1]} reads as the film {title} {year} (got {item.Title} {item.Year}, series {item.Series}, episode {item.Episode})");
        }

        // What's named after the number. Only words that name the episode become its title.
        void Named(string name, string? title)
        {
            var item = Read(name.Split('/'));
            check(item.EpisodeTitle == title, $"{name} has the episode title {title ?? "(none)"} (got {item.EpisodeTitle ?? "(none)"})");
        }
        Named("Harbor Lights - S06E25 - Who Shot Mr. Burns (1).mkv", "Who Shot Mr Burns (1)");
        Named("Harbor Lights - S01E02 - Sealed With a Kick (Pilot) (480p DVD x265 Group).mkv", "Sealed With a Kick (Pilot)");
        var harborCartoons = Path.Combine(directory, "Anime", "Harbor Cartoons"); Directory.CreateDirectory(harborCartoons);
        string[] cartoons = ["The Quiet Tide", "Pale Stamps", "Mooring Lines", "Red Lantern", "Old Ferry", "Salt Wind"];
        foreach (var (cartoon, index) in cartoons.Select((name, index) => (name, index))) File.WriteAllBytes(Path.Combine(harborCartoons, $"{index + 1:000} {cartoon} (Dec {index + 1}, 1964).mp4"), [0]);
        Named("Anime/Harbor Cartoons/001 The Quiet Tide (Dec 1, 1964).mp4", "The Quiet Tide");
        Named("Harbor.Lights.03.(01.seriya.iz.08).2018.HDTV.(1080i).Group.ts", null);
        Named("Harbor.Lights.S02.(05.seriya.iz.08).2017.HDTV.(1080i).Group.ts", null);
        Named("Harbor.Lights.s04e01.2018.WEB-DL.(1080p).Group.mkv", null);
        Named("Harbor Lights - S02E01 (14) - Dawn and Mist [1080p].mkv", "Dawn and Mist");
        Named("Harbor Lights - S01E05 - 1984.mkv", "1984");
        Named("Harbor Lights - S01E06 - Of 2 Minds.mkv", "Of 2 Minds");
        Named("Harbor Lights - S01E07 - The Last Light.mkv", "The Last Light");
        // A year after a one-digit number still marks a numbered film rather than giving it a shorter name to search for.
        check(MetadataTitles.FilenameAlias(Path.Combine(directory, "Harbor.Lights.2.2017.1080p.WEB-DL.x264-Group.mkv")) is null, "a numbered film's year doesn't make it a show to search for");

        // A number in a film's own title, with nothing around it to say it's running.
        Film(["Calm 13 (1995).mkv"], "Calm 13", 1995);
        Film(["Harbor's 11 (2001).mkv"], "Harbor's 11", 2001);
        Film(["12 Harbors.mkv"], "12 Harbors", null);
        Film(["300.mkv"], "300", null);
        Film(["12 Harbors", "12 Harbors.mkv"], "12 Harbors", null);
        Film(["300", "300.mkv"], "300", null);
        Film(["Stuff", "12 Harbors.mkv"], "12 Harbors", null);
        Film(["12 Harbors (1995)", "12 Harbors.mkv"], "12 Harbors", null);
        Film(["The 39 Harbors.mkv"], "The 39 Harbors", null);
        Film(["Calm-22.mkv"], "Calm-22", null);
        Film(["Calm.13.1080p.WEB-DL.x265-GRP.mkv"], "Calm 13", null);
        Film(["Stuff", "12.Harbors.1080p.WEB-DL.x265-GRP.mkv"], "12 Harbors", null);
        Film(["Stuff", "300.mkv"], "300", null);
        // A clip word opening a film's name is part of its title.
        Film(["Opening Night (1977).mkv"], "Opening Night", 1977);
        Film(["Opening.Night.1977.1080p.BluRay.mkv"], "Opening Night", 1977);
        Film(["The Menu (2022).mkv"], "The Menu", 2022);
        Film(["The Menu.mkv"], "The Menu", null);
        Film(["The Interview.mkv"], "The Interview", null);
        Film(["Interview with the Harbor (1994).mkv"], "Interview with the Harbor", 1994);
        Film(["Film Socialisme (2010).mkv"], "Film Socialisme", 2010);
        // What still marks an episode: a list number and a season before it.
        Directory.CreateDirectory(Path.Combine(directory, "Calm Harbor"));
        Numbered(["Calm Harbor", "1. Блокада.mkv"], "Calm Harbor", null, 1);
        Numbered(["Calm Harbor", "S04.17.Дело о победителе.mkv"], "Calm Harbor", 4, 17);
        Film(["Calm 13 (1995) Director's Cut.mkv"], "Calm 13", 1995);
        Film(["Hanks", "1", "Calm 13 (1995).mkv"], "Calm 13", 1995);
        Film(["Collections", "Vol 2", "Calm 13 (1995).mkv"], "Calm 13", 1995);
        Film(["Tide", "2", "02 - Tides (1986).mkv"], "Tides", 1986);
        // A film called only a number keeps it, however the folder is named.
        Film(["Kubrick", "2001 (1968).mkv"], "2001", 1968);
        // Numbers between unrelated files aren't a run.
        void Folder(string folder, params string[] files) { Directory.CreateDirectory(Path.Combine(directory, folder)); foreach (var file in files) File.WriteAllBytes(Path.Combine(directory, folder, file), [0]); }
        Folder("Flat Films", "12 Harbors.mkv", "300.mkv");
        foreach (var (file, title) in new[] { ("12 Harbors.mkv", "12 Harbors"), ("300.mkv", "300") })
            check(FileNames.Parse(Path.Combine(directory, "Flat Films", file), "Mixed", Path.Combine(directory, "Flat Films")) is { Series: null, Episode: null } flat && flat.Title == title, $"{file} is a film in a flat library");
        Folder("HD Films", "12 Harbors.mkv", "127 Tides.mkv", "Heat.mkv", "Calm.mkv");
        foreach (var (file, title) in new[] { ("12 Harbors.mkv", "12 Harbors"), ("127 Tides.mkv", "127 Tides"), ("Heat.mkv", "Heat"), ("Calm.mkv", "Calm") }) Film(["HD Films", file], title, null);
        Folder("Sequels", "Calm's 11 (2001).mkv", "Calm's 12 (2004).mkv", "Calm's 13 (2007).mkv");
        Film(["Sequels", "Calm's 12 (2004).mkv"], "Calm's 12", 2004);
        Folder("My Collection", "12 Harbors.mkv", "13 Tides.mkv", "21 Grams.mkv", "Heat.mkv", "Calm.mkv");
        foreach (var (file, title) in new[] { ("12 Harbors.mkv", "12 Harbors"), ("21 Grams.mkv", "21 Grams"), ("Heat.mkv", "Heat") }) Film(["My Collection", file], title, null);
        // A long show counts up: 111 isn't season 1, episode 11.
        var longShow = Path.Combine("Harbor Long", "Season 1"); Folder(longShow, Enumerable.Range(1, 220).Select(n => $"Harbor Long - {n:000}.mkv").ToArray());
        var longEpisodes = Enumerable.Range(1, 220).Select(n => Read("Harbor Long", "Season 1", $"Harbor Long - {n:000}.mkv")).ToArray();
        check(longEpisodes.Select(x => x.Episode).Distinct().Count() == 220 && longEpisodes[110].Episode == 111 && longEpisodes.All(x => x.Season == 1), $"a long show's episodes stay distinct (got {longEpisodes.Select(x => x.Episode).Distinct().Count()})");
        Folder("Harbor Flat", Enumerable.Range(1, 150).Select(n => $"Harbor Flat - {n:000}.mkv").ToArray());
        check(Read("Harbor Flat", "Harbor Flat - 111.mkv").Episode == 111, "so does one in a flat folder");
        // Seasons written into the number still count as a run of the same show.
        Folder("Harbor Packed", "Harbor Packed 101.mkv", "Harbor Packed 102.mkv", "Harbor Packed 201.mkv", "Harbor Packed 202.mkv");
        check(new[] { "101", "102", "201", "202" }.Select(n => Read("Harbor Packed", $"Harbor Packed {n}.mkv")).All(x => x.Series == "Harbor Packed" && x.Episode is not null), "numbers jumping by hundreds are still a show's files");
        // A number in the show's own name is neither its season nor its episode.
        Numbered(["Calm No. 8", "Season 1", "Calm No. 8 05 [1080p].mkv"], "Calm No. 8", 1, 5);
        Numbered(["Room.104.S01.1080p", "Room.104.12.mkv"], "Room 104", 1, 12);
        // A folder called only a number can be the show.
        check(Read("24", "24 - 12.mkv") is { Series: "24", Season: 1, Episode: 12 }, $"a show called 24 isn't season 24 (got {Read("24", "24 - 12.mkv").Series} {Read("24", "24 - 12.mkv").Season}x{Read("24", "24 - 12.mkv").Episode})");
        check(Read("My Shows", "24", "24 - 12.mkv") is { Series: "24", Season: 1, Episode: 12 }, $"nor is it under a named folder (got {Read("My Shows", "24", "24 - 12.mkv").Series} {Read("My Shows", "24", "24 - 12.mkv").Season}x{Read("My Shows", "24", "24 - 12.mkv").Episode})");
        // Only a folder called just a number can be the show like that.
        Numbered(["Harbor Lights", "Season 1", "Season 1 - 05.mkv"], "Harbor Lights", 1, 5);
        check(Read("Specials", "Specials - 01.mkv").Series != "Specials - 01", "a Specials folder doesn't name a show from its files");
        Numbered(["Calm No. 8", "Calm No. 8 Season 1 - 12.mkv"], "Calm No. 8", null, 12);
        // Season and episode written together, padded: 0101 to 0112 is season 1.
        var packedSeason = Path.Combine("Harbor Pack", "Season 1"); Folder(packedSeason, Enumerable.Range(1, 12).Select(n => $"Harbor Pack {100 + n:0000}.mkv").ToArray());
        check(Read("Harbor Pack", "Season 1", "Harbor Pack 0104.mkv") is { Season: 1, Episode: 4 } && Read("Harbor Pack", "Season 1", "Harbor Pack 0112.mkv") is { Season: 1, Episode: 12 }, "a padded season and episode run still reads that way");
        // Three digits on their own, or among plain numbers, count up.
        Numbered(["Harbor.S01.1080p", "125.mkv"], "Harbor", 1, 125);
        Numbered(["Harbor Lone", "Season 2", "Harbor Lone - 201.mkv"], "Harbor Lone", 2, 201);
        Numbered(["Harbor Lone", "Season 1", "Harbor Lone - 150.mkv"], "Harbor Lone", 1, 150);
        Numbered(["Harbor Lone", "Season 2", "203.mkv"], "Harbor Lone", 2, 203);
        Folder("Harbor Mixed", Enumerable.Range(1, 39).Select(n => $"Harbor Mixed - {n}.mkv").Concat(Enumerable.Range(101, 29).Select(n => $"Harbor Mixed - {n}.mkv")).ToArray());
        check(Enumerable.Range(1, 39).Concat(Enumerable.Range(101, 29)).Select(n => Read("Harbor Mixed", $"Harbor Mixed - {n}.mkv").Episode).Distinct().Count() == 68, "plain numbers above 100 stay distinct");
        // Numbers that merely jump by hundreds aren't a show.
        Folder("Stuff Numbers", "1408.mkv", "1917.mkv", "2012.mkv");
        Film(["Stuff Numbers", "1408.mkv"], "1408", null);
        Folder("Stuff Titles", "127 Tides.mkv", "101 Harbors.mkv", "1408.mkv");
        Film(["Stuff Titles", "127 Tides.mkv"], "127 Tides", null);
        Film(["Stuff Titles", "101 Harbors.mkv"], "101 Harbors", null);
        // A numbered folder holding another show's file isn't that folder's season.
        Numbered(["Stuff", "90", "Ninety - 05.mkv"], "Ninety", null, 5);
        check(Read("Backup", "1", "Harbor S01E05.mkv") is { Series: "Harbor", Season: 1, Episode: 5 } && Read("Backup", "2", "Calm S02E03.mkv") is { Series: "Calm", Season: 2, Episode: 3 }, "a backup folder's numbered folders don't merge their shows");
        check(FileNames.Parse(Path.Combine(Path.GetPathRoot(directory)!, "86", "05.mkv"), "Mixed", Path.GetPathRoot(directory)) is { Series: "86", Season: 1, Episode: 5 }, "a folder directly on a drive isn't a season");
        // A number in the show's name, short or long, against a one-digit episode.
        var eightySix = Path.Combine("86", "Season 1"); Folder(eightySix, Enumerable.Range(1, 12).Select(n => $"86 - {n}.mkv").ToArray());
        check(Enumerable.Range(1, 12).Select(n => Read("86", "Season 1", $"86 - {n}.mkv").Episode).Distinct().Count() == 12, "a show called 86 keeps its one-digit episodes");
        Numbered(["Room 104", "Room 104 - 5.mkv"], "Room 104", null, 5);
        // A numeric show under a folder that names nothing.
        Folder(Path.Combine("TV", "24"), "01 - Midnight.mkv", "02 - One AM.mkv", "03 - Two AM.mkv");
        check(new[] { "01 - Midnight.mkv", "02 - One AM.mkv", "03 - Two AM.mkv" }.All(file => Read("TV", "24", file).Series == "24"), $"a numeric show's folder holds it together (got {Read("TV", "24", "01 - Midnight.mkv").Series})");
        // A file that names its own show keeps it in a folder called only a number.
        Numbered(["Downloads", "000", "Harbor.Lights.S01E06.1080p.WEB-DL.mkv"], "Harbor Lights", 1, 6);
        // A release group's tag isn't an episode's title.
        Named("Harbor - 05 [SubsPlease].mkv", null);
        Named("Harbor S01E05 [Grp].mkv", null);
        Named("Harbor - 05 - The Name [SubsPlease].mkv", "The Name");
        Film(["Special 26.mkv"], "Special 26", null);
        Film(["Stuff", "Special.26.1080p.BluRay.x264-GRP.mkv"], "Special 26", null);
        // Words with a number after them are numbered clips and films, not new shows.
        Folder("Harbor Clips", "Harbor Clips - 01.mkv", "Harbor Clips - 02.mkv", "Harbor Clips - 03.mkv", "Opening 01 - Song Name.mkv", "Extra 01 - Deleted Scenes.mkv", "Movie 01 Two Heroes.mkv");
        foreach (var file in new[] { "Opening 01 - Song Name.mkv", "Extra 01 - Deleted Scenes.mkv", "Movie 01 Two Heroes.mkv" }) check(Read("Harbor Clips", file) is { Series: "Harbor Clips", Episode: null }, $"{file} stays with its show (got {Read("Harbor Clips", file).Series} {Read("Harbor Clips", file).Episode})");
        Film(["Special 26 (2013).mkv"], "Special 26", 2013);
        Film(["The Last Movie Star (2017).mkv"], "The Last Movie Star", 2017);
        // A real run still counts, with gaps and with names beside the numbers.
        Folder("Harbor Kept", "Harbor Kept - 01.mkv", "Harbor Kept - 12.mkv");
        Numbered(["Harbor Kept", "Harbor Kept - 12.mkv"], "Harbor Kept", null, 12);
        Folder("Harbor Named", "1 Pilot.mkv", "2 Return.mkv", "3 Storm.mkv");
        Numbered(["Harbor Named", "2 Return.mkv"], "Harbor Named", null, 2);
        // A folder called "1" or "Vol 2" only makes a season of a file with a number of its own.
        Film(["Tide", "1", "Tide.1979.1080p.mkv"], "Tide", 1979);
        Film(["Tide", "02 - Tides", "Tides.1986.mkv"], "Tides", 1986);
        Film(["Collections", "Vol 2", "Some Film (1999).mkv"], "Some Film", 1999);
        Numbered(["Harbor Lights", "2", "Harbor Lights - 05.mkv"], "Harbor Lights", 2, 5);
        Numbered(["Harbor Lights", "2", "06.mkv"], "Harbor Lights", 2, 6);
        // Short folder names can be a show's own name, and folders above the library say nothing about its files.
        Folder(Path.Combine("Harbor Kept", "NC"), "NCOP1.mkv"); Folder(Path.Combine("Harbor Kept", "Bonus"), "Making Of.mkv"); Folder(Path.Combine("Harbor Kept", "Extras"), "Making Of.mkv");
        Folder(Path.Combine("Family", "CM"), "Birthday.mkv"); Folder(Path.Combine("Directors", "PV"), "Some Film (1999).mkv"); Folder(Path.Combine("Comedy", "Bonus"), "Bonus - 01.mkv", "Bonus - 02.mkv");
        Folder("Interviews with Harbor Girls", "Interviews with Harbor Girls - 01.mkv", "Interviews with Harbor Girls - 02.mkv"); Folder("Interviews Before Dawn (2012)", "Interviews Before Dawn (2012).mkv");
        check(Read("Family", "CM", "Birthday.mkv") is { IsExtra: false, IsBonus: false } && Read("Directors", "PV", "Some Film (1999).mkv") is { IsExtra: false, IsBonus: false, Title: "Some Film" } && Read("Comedy", "Bonus", "Bonus - 01.mkv") is { IsExtra: false, IsBonus: false }, "a short name under a folder that only groups things isn't an extras folder");
        var harborGirls = Read("Interviews with Harbor Girls", "Interviews with Harbor Girls - 01.mkv"); var beforeDawn = Read("Interviews Before Dawn (2012)", "Interviews Before Dawn (2012).mkv");
        check(harborGirls is { IsExtra: false, IsBonus: false, Episode: 1 }, $"a show starting with Interviews isn't an extras folder (got extra {harborGirls.IsExtra}, bonus {harborGirls.IsBonus}, episode {harborGirls.Episode})");
        check(beforeDawn is { IsExtra: false, IsBonus: false, Title: "Interviews Before Dawn" }, $"a film starting with Interviews isn't an extras folder (got extra {beforeDawn.IsExtra}, bonus {beforeDawn.IsBonus}, {beforeDawn.Title})");
        check(Read("Extra", "Extra S01E01.mkv") is { IsExtra: false, IsBonus: false, Series: "Extra", Season: 1, Episode: 1 }, "a show called Extra isn't an extras folder");
        check(Read("Bonus", "Bonus - 01.mkv") is { IsExtra: false, IsBonus: false, Series: "Bonus", Episode: 1 }, "a show called Bonus isn't an extras folder");
        check(new[] { Read("Harbor Kept", "NC", "NCOP1.mkv"), Read("Harbor Kept", "Bonus", "Making Of.mkv"), Read("Harbor Kept", "Extras", "Making Of.mkv") }.All(x => x.IsExtra || x.IsBonus), "those names still mean extras inside a show's folder");
        check(!FileNames.IsExtra(Path.Combine("C:", "Users", "cm", "Videos", "Calm Waters.mkv"), Path.Combine("C:", "Users", "cm", "Videos")) && !FileNames.IsExtra(Path.Combine(directory, "Family", "CM", "Birthday.mkv"), directory), "folders above the library don't make its files extras");
        // No name stops a scan: a title can start after the number, and the reader has nothing else to go on here.
        var oddRoot = Path.Combine(directory, "Odd", "cm", "Videos"); Directory.CreateDirectory(oddRoot);
        Directory.CreateDirectory(Path.Combine(oddRoot, "99999999999 сезон")); await File.WriteAllBytesAsync(Path.Combine(oddRoot, "99999999999 сезон", "Odd Season.mkv"), [0]);
        string[] oddNames = ["(Ep 03) Harbor Lights.mkv", "(E05) Harbor Lights.mkv", "(#3) Harbor Lights.mkv", "(3 of 12) Harbor Lights.mkv", "[S02][E05] Harbor Lights.mkv", "[S02][NCOP] Harbor Lights.mkv", "(S02)(E05) Harbor Lights.mkv", "Calm Waters (2001).mkv"];
        foreach (var odd in oddNames) await File.WriteAllBytesAsync(Path.Combine(oddRoot, odd), [0]);
        var oddStore = new LibraryStore(Path.Combine(directory, "OddData"));
        await oddStore.ScanAsync([new(oddRoot)]);
        check(oddStore.All().Count(x => !x.IsVirtual && !x.IsExtra) == oddNames.Length + 1, $"a name starting with its numbers doesn't stop a scan, in a library under a folder called cm (indexed {oddStore.All().Count(x => !x.IsVirtual && !x.IsExtra)})");
        check(oddStore.All().Single(x => x.Path.EndsWith("[S02][E05] Harbor Lights.mkv")) is { Season: 2, Episode: 5 }, "numbers ahead of the title are still read");

        // Release groups, tags and separators.
        Numbered(["[Group] Harbor Lights - 07 (1080p) [A1B2C3D4].mkv"], "Harbor Lights", null, 7);
        Numbered(["[Group]_Harbor_Lights_07_[DVD][10bit][A1B2C3D4].mkv"], "Harbor Lights", null, 7);
        Numbered(["[Group]Harbor_Lights_07_BD(720p_10bit)[A1B2C3D4].mkv"], "Harbor Lights", null, 7);
        Numbered(["[Group]_Harbor_Lights_07v2_[DVD][A1B2C3D4].mkv"], "Harbor Lights", null, 7);
        Numbered(["[Group] Harbor Lights - 07 v2 [1080p].mkv"], "Harbor Lights", null, 7);
        Numbered(["[Group][Harbor_Lights][07][CHT][720p].mp4"], "Harbor Lights", null, 7);
        Numbered(["[Harbor_Lights][DVDRIP][X264_AAC][OVA][02].mp4"], "Harbor Lights", null, 2);
        Numbered(["zx.harbor.lights.tv.07.divx5.ogm"], "zx harbor lights", null, 7);
        Numbered(["harbor.lights.s02e07.1080p.web.h264-grp[EZTVx.to].mkv"], "harbor lights", 2, 7);
        Numbered(["Harbor.Lights.S02E07WEB-DL.1080p.Group.mkv"], "Harbor Lights", 2, 7);
        Numbered(["Harbor Lights S02E07 Episode Name 1080p AMZN WEB-DL DDP5 1 H 264-GRP.mkv"], "Harbor Lights", 2, 7);
        Numbered(["grp-harborlights-0204-720-web.mkv"], "grp-harborlights", 2, 4);
        // Season markers apart from the episode.
        Numbered(["[Group] Harbor Lights S2 - 21 (480p) [A1B2C3D4].mkv"], "Harbor Lights", 2, 21);
        Numbered(["[Group] Harbor Lights Season 2 - 04 [720p].mkv"], "Harbor Lights", 2, 4);
        Numbered(["Harbor Lights TV-2 - 13 (WEB-DL 1920x1080 x264 AAC).mkv"], "Harbor Lights", 2, 13);
        Numbered(["Harbor Lights S02 (2025) - E05 - Name [1080p].mkv"], "Harbor Lights", 2, 5);
        Numbered(["Harbor Lights Episode 03 [S02].mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor.Lights.Ep03.(Season.2).mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights S01x02 name.mkv"], "Harbor Lights", 1, 2);
        Numbered(["Harbor.Lights.T02E03.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights Cap.203.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor.Lights.2.03.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor.Lights.[2][03].mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights - 6.01 - Name.mkv"], "Harbor Lights", 6, 1);
        Numbered(["Harbor Lights1x01.HDTV.XviD.avi"], "Harbor Lights", 1, 1);
        Numbered(["Harbor Lights 5-й сезон 09-я серия.avi"], "Harbor Lights", 5, 9);
        Numbered(["Harbor Lights. Сезон №9. Серия №180.avi"], "Harbor Lights", 9, 180);
        Numbered(["Harbor Lights Sezonul 2 Episodul 3.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights Musim 2 Episod 3.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights Series 2 Episode 3.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights 2. évad 3. rész.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights Odcinek 3.mkv"], "Harbor Lights", 1, 3);
        Numbered(["Harbor Lights 第3話.mkv"], "Harbor Lights", 1, 3);
        Numbered(["Harbor Lights 3화.mkv"], "Harbor Lights", 1, 3);
        Numbered(["Harbor Lights Ep3of12.mkv"], "Harbor Lights", 1, 3);
        Numbered(["Harbor.Lights.03.seriya.iz.08.2018.HDTV.ts"], "Harbor Lights", 1, 3);
        Numbered(["Harbor.Lights.03.(01.seriya.iz.08).2018.HDTV.(1080i).ts"], "Harbor Lights", 3, 1);
        Numbered(["Harbor.Lights.S02.(05.seriya.iz.08).2017.HDTV.(1080i).ts"], "Harbor Lights", 2, 5);
        // Multi-episode files.
        Numbered(["Harbor Lights S03E01 S03E02 720p.mkv"], "Harbor Lights", 3, 1, 2);
        Numbered(["Harbor Lights 1x02.1x03.HDTV.mkv"], "Harbor Lights", 1, 2, 3);
        Numbered(["[Group]Harbor73-74[A1B2C3D4].avi"], "Harbor", null, 73, 74);
        // Numbers in titles aren't episodes.
        Numbered(["[Group] Knight Ramune & 40 - 20 [480p].mkv"], "Knight Ramune & 40", null, 20);
        Numbered(["[Group] Precure '23 - 07 (1080p) [A1B2C3D4].mkv"], "Precure '23", null, 7);
        Numbered(["Harbor Lights 2 - 01 [WEB-DL 1080p].mkv"], "Harbor Lights 2", null, 1);
        Numbered(["[Group] Harbor Lights - Kimi e - 24.mkv"], "Harbor Lights - Kimi e", null, 24);
        Numbered(["The Series - S01E02 - Pilot.mkv"], "The Series", 1, 2);
        Numbered(["30 Rooms - S01E01 - Pilot.mkv"], "30 Rooms", 1, 1);
        // A number after a season marker, in several writings, and with a dashed release number before it.
        Numbered(["[Group] Harbor Lights - 05.5 - S00E01 - Name.mkv"], "Harbor Lights", 0, 1);
        Numbered(["[Group] Harbor Lights (2025) - 13v2 (S01E13) (1080p).mkv"], "Harbor Lights", 1, 13);
        // Special numbering.
        Numbered(["[Group] Harbor Lights S3 - OVA1 (1080p) [A1B2C3D4].mkv"], "Harbor Lights", 0, 1);
        Numbered(["Harbor Lights - OVA - Episode 02[A1B2C3D4].mkv"], "Harbor Lights", 0, 2);
        Numbered(["Harbor Lights (OVA) 01 [BDRemux].mkv"], "Harbor Lights", 0, 1);
        // Numbers at the front.
        Numbered(["Anime", "Harbor Lights", "050 Name Here (Oct 2, 1968).mp4"], "Harbor Lights", null, 50);
        Numbered(["Anime", "Harbor Lights", "179. Name Here.mp4"], "Harbor Lights", null, 179);
        Numbered(["Anime", "Harbor Lights", "30. Name Here (Серия 1).mp4"], "Harbor Lights", null, 30);
        Numbered(["Anime", "Harbor Lights", "149-150. Name Here.mp4"], "Harbor Lights", null, 149, 150);
        // Folders.
        Numbered(["[Group] Harbor Lights (01-24) (480p) [Batch]", "[Group] Harbor Lights - 21 (480p) [A1B2C3D4].mkv"], "Harbor Lights", null, 21);
        Numbered(["Shows", "Harbor Lights", "Harbor.Lights.S02.1080p.BluRay.x265-GRP", "Harbor.Lights.S02E05.1080p.BluRay.x265-GRP.mp4"], "Harbor Lights", 2, 5);
        Numbered(["Shows", "Harbor Lights", "01. Season 1", "S01E01-Name [A1B2C3D4].mkv"], "Harbor Lights", 1, 1);
        Numbered(["Harbor Lights", "2", "03.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights", "T2", "E03.mkv"], "Harbor Lights", 2, 3);
        Numbered(["Harbor Lights", "Сезон 5", "Серия 11.mkv"], "Harbor Lights", 5, 11);
        Numbered(["Harbor Lights", "Season 2009", "S2009x02 name.mkv"], "Harbor Lights", 2009, 2);
        Numbered(["Harbor Lights", "Harbor Lights 112.avi"], "Harbor Lights", null, 112);
        Numbered(["Harbor.Lights.S01E02.Name.720p.WEB", "gNWDXow11s7E0X7GTDrZ.mkv"], "Harbor Lights", 1, 2);

        // Films.
        Film(["Movies", "Harbor.Lights.2024.1080p.BluRay.x265-GRP.mp4"], "Harbor Lights", 2024);
        Film(["Harbor.Lights.2.Second.Tide.2024.WEB-DL.1080p.mkv"], "Harbor Lights 2 Second Tide", 2024);
        Film(["Madame Web (2024) 1080p WEBRip x265.mkv"], "Madame Web", 2024);
        Film(["Harbor Lights Web 2024 UHD BluRay 2160p.mkv"], "Harbor Lights Web", 2024);
        Film(["Harbor Lights 357.mkv"], "Harbor Lights 357", null);
        Film(["BluRay 1080p French Harbor 1955 Director Name.mkv"], "French Harbor", 1955);
        Film(["BluRay 1080p Le port des brumes 1983 Director Name.mkv"], "Le port des brumes", 1983);
        Film(["Клевый порт (Harbor Lights)(1999)(ДБ, ПМx4, ПД).mkv"], "Клевый порт", 1999);
        Film(["Тихая гавань  (драма, комедия 2024 год).avi"], "Тихая гавань", 2024);
        Film(["Harbor Lights (1952, ru_MVO,MVO+en+ru,en_sub).mkv"], "Harbor Lights", 1952);
        Film(["Тихая гавань (Stille Hafen, 2022, ru_DVO).mkv"], "Тихая гавань", 2022);
        Film(["Harbor.Lights.EXTENDED.2022.2160p.REMUX.mkv"], "Harbor Lights", 2022);
        Film(["5ACV13-16 «Harbor Lights Forever» (2008) [BDRemux].mkv"], "Harbor Lights Forever", 2008);
        Film(["Grand.Harbor.1995.2012.DVDRip.x264.mkv"], "Grand Harbor 1995", 2012);
        Film(["Scary Harbor Movie (2000).mkv"], "Scary Harbor Movie", 2000);
        Film(["[Group] Harbor Lights Movie 01 - Two Tides (BD 1080p) [A1B2C3D4].mkv"], "Harbor Lights - Two Tides", null);
        Film(["www.Example.com - 14.Harbor.Lights.2021.1080p.WEB.h264-GRP.mkv"], "14 Harbor Lights", 2021);
        Film(["Movies", "Taxi", "Taxi 2 - 1080p.mkv"], "Taxi 2", null);
        // Years survive what surrounds them.
        Film(["Harbor.Lights.2025.x264.WEB-DLRip-AVC_New-Team.mkv"], "Harbor Lights", 2025);
        Film(["Movies", "Harbor Tale", "02 Harbor Tale 2 - Comedy 2001 Eng Subs 1080P.mp4"], "Harbor Tale 2", 2001);
        Film(["1975 - Тихая гавань [HDTV, 1080i].ts"], "Тихая гавань", 1975);
        check(Read(["Harbor Lights S02 (2025) - E05 - Name [1080p].mkv"]).Year == 2025, "a year after the season marker dates the show");
        check(Read(["[Group].Harbor.Lights.OVA-01.2007.480p.DVD.mkv"]) is { Year: 2007, Season: 0, Episode: 1 }, "a year after a numbered special dates it");
        check(Read(["Harbor Lights (2019)", "S01E01.mkv"]).Year == 2019, "a name with only numbering is dated by its folder");
        // Aliases reach provider search.
        check(MetadataTitles.Queries(Read(["Les.Portes.AKA.The.Harbor.Gates.1991.1080p.mkv"])).Contains("The Harbor Gates"), "AKA titles are searched");
        check(MetadataTitles.Queries(Read(["Клевый порт (Harbor Lights)(1999).mkv"])).Contains("Harbor Lights"), "a title in brackets is searched");
        var dashed = Read(["[Group] Main Title - Part Title - 04 [1080p].mkv"]);
        check(!MetadataTitles.Queries(dashed).Contains("Main Title") && MetadataTitles.PartQueries(dashed).Contains("Main Title") && MetadataTitles.PartQueries(dashed).Contains("Part Title"), "a dashed title's sides are a last search, not a first one");
        check(!MetadataTitles.PartQueries(Read(["[Group] Main Title Movie 2 - Chapter 2 [1080p].mkv"])).Contains("Chapter 2"), "a label like Chapter 2 is never searched alone");
        check(MetadataTitles.AsPart(Read(["[Group] Main Title Movie 1 - Part Title [1080p].mkv"]), "Main Title").MetadataType == "movie", "a labelled film can only match films through a dashed title");
        check(Read(["2026", "Harbor Lights Season 2", "[Group] Harbor Lights Season 2 - 01 [1080p].mkv"]).Title == "Harbor Lights", "a folder named with a year isn't a show");
        Numbered(["[Group]Harbor_Lights_s.1_ep.01(2022)DVB.mkv"], "Harbor Lights", 1, 1);
        check(MetadataTitles.Queries(Read(["Season Title", "Season Title - S01E01 [1080p].mkv"])).Length >= 1, "a folder-owned show still has a query");
        var pack = FilenameEpisodes.AnalyzeFolder("[Group] Boku No Harbor (My Harbor) (S01-S07+Movies+OVAs) [BD] [Dual Audio][1080p]");
        check(pack.Title == "Boku No Harbor" && pack.Alternates.Contains("My Harbor"), "a pack folder names its show and alias, not its seasons");
        foreach (var (folder, title) in new[] { ("[Group] Harbor Complete Series (Season 01-25+Movies+Specials) [DVD+BD][1080p]", "Harbor"), ("Harbor Lights TV 01-48, OVA 01-03, Movie 01-03 [ARR][720p]", "Harbor Lights"), ("Harbor Lights [TV-1] [2019] [BDRip] [1080p] [RUS + JAP + ENG]", "Harbor Lights"), ("[Group] Harbor Lights Vol 1-3 (BDRip)", "Harbor Lights"), ("[Group] Edge Runners 2 - 01 ~ 10 [1080p]", "Edge Runners 2"), ("Harbor Lights 2018-2025", "Harbor Lights"), ("[1963-2007] Harbor 28_港28號(港28号)_TV.MOVIE", "Harbor 28"), ("1080p.CR.WEB-DL.AAC2.0.H.264-GRP", "") })
            check(FilenameEpisodes.AnalyzeFolder(folder).Title == title, $"folder {folder} is titled {title} (got {FilenameEpisodes.AnalyzeFolder(folder).Title})");

        // Air dates, in the writings names use.
        foreach (var (name, date) in new[] { ("Harbor.News.2026.11.02.Guest.Name.1080p.WEB.h264-GRP.mkv", "2026-11-02"), ("Harbor News 2026 10 29 720p WEBRip h264-GRP.mp4", "2026-10-29"), ("Harbor News 4th December 2020 WEBRip h264-GRP.mkv", "2020-12-04"), ("Harbor News 20200116.mp4", "2020-01-16") })
        {
            var item = Read([name]);
            check(item.AirDate == date && item.Episode is null, $"{name} airs on {date} (got {item.AirDate}, episode {item.Episode})");
        }

        // Evidence from sibling files: the number that runs is the episode, in a scheme with no other hint.
        var runs = Path.Combine(directory, "Lib", "Knight Harbor 28");
        Directory.CreateDirectory(runs);
        foreach (var n in new[] { 1, 2, 3, 4, 5, 6 }) await File.WriteAllBytesAsync(Path.Combine(runs, $"knight.harbor.28.pt{n:00}.x.mkv"), [0]);
        foreach (var n in new[] { 3, 9, 14 }) await File.WriteAllBytesAsync(Path.Combine(runs, $"knight harbor 28 {n:00} zz.mkv"), [0]);
        var running = FileNames.Parse(Path.Combine(runs, "knight harbor 28 09 zz.mkv"), "Mixed", Path.Combine(directory, "Lib"));
        check(running.Episode == 9, $"the varying number beats the constant one (got {running.Episode})");
        // A folder of many different titles is a collection, not a show.
        var collection = Path.Combine(directory, "Lib", "Anthology");
        Directory.CreateDirectory(collection);
        foreach (var title in new[] { "Aaa", "Bbb", "Ccc", "Ddd", "Eee", "Fff", "Ggg" })
            foreach (var extension in new[] { ".mp4", ".mpg" }) await File.WriteAllBytesAsync(Path.Combine(collection, $"[2001-09-25] [TV Series] {title} Show - Song Name [A1B2C3D4]{extension}"), [0]);
        var anthology = FileNames.Parse(Path.Combine(collection, "[2001-09-25] [TV Series] Ccc Show - Song Name [A1B2C3D4].mp4"), "Mixed", Path.Combine(directory, "Lib"));
        check(anthology.Title == "Ccc Show" && anthology.Episode is null, $"a collection's files keep their own titles (got {anthology.Title})");
        // Disc structures name streams, not episodes.
        var stream = Path.Combine(directory, "Lib", "[BDMV] Harbor Lights", "OVA", "BDMV", "STREAM");
        Directory.CreateDirectory(stream); await File.WriteAllBytesAsync(Path.Combine(stream, "00005.m2ts"), [0]);
        var disc = FileNames.Parse(Path.Combine(stream, "00005.m2ts"), "Mixed", Path.Combine(directory, "Lib"));
        check(disc.Title == "Harbor Lights" && disc.Episode is null, $"a disc stream takes its title from the disc folder (got {disc.Title}, episode {disc.Episode})");
        // Clip folders belong to the show above them, however deep.
        var show = Path.Combine(directory, "Lib", "Harbor Lights TV");
        var clips = Path.Combine(show, "Extras", "CM");
        Directory.CreateDirectory(clips);
        foreach (var n in new[] { 1, 2, 3 }) await File.WriteAllBytesAsync(Path.Combine(show, $"Harbor Lights TV - {n:00}.mkv"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(clips, "Harbor Lights TV - CM 07.mkv"), [0]);
        var clip = FileNames.Parse(Path.Combine(clips, "Harbor Lights TV - CM 07.mkv"), "Mixed", Path.Combine(directory, "Lib"));
        check(clip.IsBonus && clip.Episode is null && !clip.IsExtra, $"a nested clip folder's files are the show's clips (got bonus {clip.IsBonus}, episode {clip.Episode})");
        // A special dated later than its show says nothing about the show's year; the show's folder does.
        foreach (var (folderName, expected) in new[] { ("Calm Harbor", (int?)null), ("Calm Harbor (2010)", 2010) })
        {
            var season = Path.Combine(directory, "Lib", folderName, "Calm.Harbor.S04.1080p.BluRay.x265");
            Directory.CreateDirectory(season);
            foreach (var n in new[] { 1, 2, 3 }) await File.WriteAllBytesAsync(Path.Combine(season, $"Calm.Harbor.S04E{n:00}.1080p.BluRay.x265.mkv"), [0]);
            var specialPath = Path.Combine(season, "Calm.Harbor.The.Long.Night.2016.1080p.BluRay.x264.mkv");
            await File.WriteAllBytesAsync(specialPath, [0]);
            var special = FileNames.Parse(specialPath, "Mixed", Path.Combine(directory, "Lib"));
            check(special.IsBonus && special.Year == expected, $"a special in {folderName} doesn't date the show (got bonus {special.IsBonus}, year {special.Year})");
        }
        // Results differing only in capitals aren't tied; results spelled the same still are, and rank decides before spelling.
        MetadataMatch Candidate(int id, string title, string year) => new(id, "tv", title, year, null, "");
        check(MetadataMatching.Choose([Candidate(1, "SHOUt", "2023"), Candidate(2, "Shout", "2010")], "Shout", null)?.Id == 2, "the result spelled like the file wins over one differing in capitals");
        check(MetadataMatching.Choose([Candidate(1, "SHOUt", "2023"), Candidate(2, "ShOuT", "2010")], "Shout", null) is null, "results that all differ in capitals stay tied");
        check(MetadataMatching.Choose([Candidate(1, "Shout", "1998"), Candidate(2, "Shout", "2007")], "Shout", null) is null, "results spelled the same stay tied");
        check(MetadataMatching.Choose([Candidate(1, "SHOUt", "2010"), Candidate(2, "Shout", "2023")], "Shout", 2010)?.Id == 1, "a matching year outranks spelling");

        // A library from before this reader drops what the old parser stored, once, keeping everything else.
        var upgrade = Path.Combine(directory, "Upgrade"); var upgradeData = Path.Combine(upgrade, "data"); var media = Path.Combine(upgrade, "media", "Harbor Lights");
        Directory.CreateDirectory(media); Directory.CreateDirectory(upgradeData);
        var episodePath = Path.Combine(media, "Harbor Lights - 05 [1080p].mkv"); var filmPath = Path.Combine(upgrade, "media", "Calm.Waters.2019.1080p.mkv"); var lockedPath = Path.Combine(media, "Harbor Lights - 06 [1080p].mkv");
        var mappedPath = Path.Combine(media, "Harbor Lights - 07 [1080p].mkv");
        foreach (var file in new[] { episodePath, filmPath, lockedPath, mappedPath }) await File.WriteAllBytesAsync(file, [0]);
        var mediaRoot = Path.Combine(upgrade, "media");
        var seed = new LibraryStore(upgradeData);
        var oldEpisode = FileNames.Parse(episodePath, "Mixed", mediaRoot) with { Season = 9, Episode = 99, EpisodeTitle = "Wrong plot's name", EpisodeOverview = "Wrong plot", EpisodeSource = "fixture", Available = true };
        var oldFilm = FileNames.Parse(filmPath, "Mixed", mediaRoot) with { Title = "Calm Waters 2019 1080p", Year = null, MetadataFetchedAt = 5 };
        var oldLocked = FileNames.Parse(lockedPath, "Mixed", mediaRoot) with { Season = 7, Episode = 77, LockedFields = ["Season", "Episode"] };
        var oldMapped = FileNames.Parse(mappedPath, "Mixed", mediaRoot);
        oldMapped = oldMapped with { Season = 3, Episode = 2, AbsoluteEpisode = oldMapped.AbsoluteEpisode ?? 7, NumberingOrder = "absolute", MetadataProvider = "tvmaze", MetadataId = 44, MetadataType = "tv", ProviderIds = new() { ["tvmaze"] = "44" }, Overview = "Provider plot", MetadataFetchedAt = 5000 };
        var partFolder = Path.Combine(upgrade, "media", "Movies", "Tide"); Directory.CreateDirectory(partFolder);
        var partPath = Path.Combine(partFolder, "Harbor Tale (1977) 01.mp4"); await File.WriteAllBytesAsync(partPath, [0]);
        seed.Save(FileNames.Parse(partPath, "Mixed", mediaRoot) with { Series = "Harbor Tale", Season = 1, Episode = 1, MetadataProvider = "tvmaze", MetadataId = 55, MetadataType = "tv", ProviderIds = new() { ["tvmaze"] = "55" } });
        foreach (var item in new[] { oldEpisode, oldFilm, oldLocked, oldMapped }) seed.Save(item);
        // An episode the old parser matched to a film, and a numbered film it matched correctly.
        var wrongFilmPath = Path.Combine(media, "Harbor Lights - S01E03 - Name.mkv"); var numberedFilmPath = Path.Combine(mediaRoot, "Calm 13 (1995).mkv");
        var sequelFolder = Path.Combine(mediaRoot, "Rock"); Directory.CreateDirectory(sequelFolder);
        var sequelPath = Path.Combine(sequelFolder, "Rock 2.mkv"); await File.WriteAllBytesAsync(sequelPath, [0]); await File.WriteAllBytesAsync(Path.Combine(sequelFolder, "Rock 3.mkv"), [0]);
        seed.Save(new MediaItem(FileNames.Parse(sequelPath, "Mixed", mediaRoot).Id, sequelPath, "Rock 2", "Movie", MetadataProvider: "tvmaze", MetadataId: 72, MetadataType: "movie", ProviderIds: new() { ["tvmaze"] = "72" }, Overview: "The sequel's plot", MetadataFetchedAt: 5000));
        await File.WriteAllBytesAsync(wrongFilmPath, [0]); await File.WriteAllBytesAsync(numberedFilmPath, [0]);
        seed.Save(FileNames.Parse(wrongFilmPath, "Mixed", mediaRoot) with { Series = null, Episode = null, Season = null, MetadataProvider = "tvmaze", MetadataId = 70, MetadataType = "movie", ProviderIds = new() { ["tvmaze"] = "70" }, Overview = "The wrong film's plot", Poster = "wrong-film.jpg", PosterSource = "https://example.org/movie/70", MetadataFetchedAt = 5000 });
        seed.Save(FileNames.Parse(numberedFilmPath, "Mixed", mediaRoot) with { MetadataProvider = "tvmaze", MetadataId = 71, MetadataType = "movie", ProviderIds = new() { ["tvmaze"] = "71" }, Overview = "The right film's plot", MetadataFetchedAt = 5000 });
        seed.Setting("watched:" + oldEpisode.Id, "true"); seed.Setting("position:" + oldFilm.Id, "120"); seed.Setting("metadata-retry:title:" + oldFilm.Id, "{}");
        var queues = Wire.Serialize(new SavedQueue[] { new("fixture", "Queue", [oldEpisode.Id]) }); seed.Setting("queues", queues);
        // A library from before this reader has no record of it.
        static void FromBeforeReader(string data)
        {
            using var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(data, "library.db"));
            database.Open(); using var remove = database.CreateCommand();
            remove.CommandText = "DELETE FROM settings WHERE key='filename-reader-version'"; remove.ExecuteNonQuery();
        }
        FromBeforeReader(upgradeData);
        var reopened = new LibraryStore(upgradeData);
        check(reopened.Setting("filename-reader-version") == "pending", "an existing library is marked to drop old readings");
        check(new LibraryStore(Path.Combine(directory, "EmptyLibrary")).Setting("filename-reader-version") == "1", "a new library has no old readings to drop");
        await reopened.ScanAsync([new(mediaRoot)]);
        var read = reopened.All().Where(x => !x.IsVirtual).ToDictionary(x => x.Path);
        check(read[episodePath] is { Episode: 5, EpisodeOverview: null, EpisodeSource: null } && read[episodePath].Season != 9 && read[episodePath].EpisodeTitle != "Wrong plot's name", "a wrong reading and the plot it pointed at are dropped");
        check(read[filmPath] is { Title: "Calm Waters", Year: 2019 }, "a film takes the new title and year");
        check(read[wrongFilmPath] is { MetadataId: null, Poster: null, PosterSource: null, Season: 1, Episode: 3 } && read[wrongFilmPath].Overview != "The wrong film's plot", $"an episode matched to the wrong film loses that film's identity, plot and poster (got {read[wrongFilmPath].MetadataId} {read[wrongFilmPath].Overview} {read[wrongFilmPath].Poster} {read[wrongFilmPath].PosterSource} {read[wrongFilmPath].Season}x{read[wrongFilmPath].Episode})");
        check(read[numberedFilmPath] is { MetadataId: 71, Overview: "The right film's plot", Series: null, Title: "Calm 13" }, $"a film with a number in its title keeps its match (got {read[numberedFilmPath].MetadataId} {read[numberedFilmPath].Overview} {read[numberedFilmPath].Series} {read[numberedFilmPath].Title})");
        check(read[sequelPath] is { MetadataId: 72, Overview: "The sequel's plot", Series: null, Episode: null }, $"a bare number doesn't overturn a film a provider identified (got {read[sequelPath].MetadataId} {read[sequelPath].Series} {read[sequelPath].Episode})");
        check(read[lockedPath] is { Season: 7, Episode: 77 }, "locked numbering isn't touched");
        check(read[mappedPath] is { Season: 3, Episode: 2, MetadataId: 44, Overview: "Provider plot", MetadataFetchedAt: 5000 }, "a provider's identity, text, mapped numbering and refresh time stay when the file reads the same number");
        check(read[partPath] is { MetadataId: 55, MetadataType: "tv", Season: 1, Episode: 1 }, "a numbered film part verified as a miniseries keeps its identity and numbering");
        check(read[episodePath].Id == oldEpisode.Id && reopened.Setting("watched:" + oldEpisode.Id) == "true" && reopened.Setting("position:" + oldFilm.Id) == "120" && reopened.Setting("queues") == queues, "file IDs, watch state, positions and queues stay");
        check(reopened.Setting("metadata-retry:title:" + oldFilm.Id) is null, "retry cooldowns from old readings are dropped");
        check(File.Exists(Path.Combine(upgradeData, "library-before-filename-reader-v1.db")), "the old readings are backed up first");
        check(reopened.Setting("filename-reader-version") == "1", "the drop happens once");
        reopened.Save(read[filmPath] with { Title = "Provider Title", MetadataProvider = "tvmaze", MetadataId = 9, MetadataType = "movie" });
        await reopened.ScanAsync([new(mediaRoot)]);
        check(reopened.All().Single(x => x.Path == filmPath) is { MetadataId: 9 } after && after.Title == "Provider Title", "later scans merge as usual");
        var offline = Path.Combine(directory, "Offline"); Directory.CreateDirectory(offline);
        var offlineStore = new LibraryStore(offline); offlineStore.Save(FileNames.Parse(Path.Combine(offline, "Calm.Waters.2019.mkv")));
        FromBeforeReader(offline);
        var offlineReopened = new LibraryStore(offline);
        await offlineReopened.ScanAsync([new(Path.Combine(offline, "missing-drive"))]);
        check(offlineReopened.Setting("filename-reader-version") == "pending", "a folder that's offline keeps the drop pending");
        await offlineReopened.ScanAsync([new(Path.Combine(offline, "missing-music"), "Music")]);
        check(offlineReopened.Setting("filename-reader-version") == "1", "a missing music folder holds no filename readings, so it doesn't");

        // No name can stop a scan.
        foreach (var hostile in new[] { "", " ", ".", "...", "[", "]", "()", "[[[[", "))))", "00000", "99999999999999999999", "S99E9999999", "1-2-3-4", "9999-99-99", "第", "«", "www. - ", "\ud83d", "a\udc00b", "́", new string('a', 2000), new string('1', 2000), string.Concat(Enumerable.Repeat("[a]", 700)), string.Concat(Enumerable.Repeat("1 ", 700)), string.Concat(Enumerable.Repeat("(", 1000)), string.Concat(Enumerable.Repeat("S01E01 ", 300)), string.Concat(Enumerable.Repeat("- ", 1000)) })
        {
            var ok = true;
            try
            {
                _ = FilenameEpisodes.AnalyzeFolder(hostile);
                _ = FilenameEpisodes.AnalyzeName(hostile, FilenameFolders.FolderEvidence.None, true);
                var name = string.Concat((hostile.Length > 200 ? hostile[..200] : hostile).Select(c => c < 32 || "<>:\"|?*/\\".Contains(c) ? '_' : c)).TrimEnd(' ', '.');
                _ = MetadataTitles.Queries(FileNames.Parse(Path.Combine(directory, (name.Length == 0 ? "x" : name) + ".mkv")));
            }
            catch (Exception) { ok = false; }
            check(ok, $"a hostile name of {hostile.Length} characters is read without an exception");
        }
    }

    // Features of every reading, for scripts/filename-model/train.py.
    public static void WriteFeatures(string input, string output)
    {
        var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var writer = new StreamWriter(output, false, new System.Text.UTF8Encoding(false));
        foreach (var line in File.ReadLines(input))
        {
            var row = JsonNode.Parse(line)!;
            var path = Path.GetFullPath(row["path"]!.GetValue<string>());
            var kind = row["kind"]?.GetValue<string>() ?? "Mixed";
            var root = row["root"]?.GetValue<string>();
            var (series, _, folderSeason) = FileNames.NameContext(path, kind);
            var evidence = FilenameFolders.Evidence(path, series || folderSeason is not null, folderSeason, root, kind == "Movie");
            var readings = FilenameEpisodes.Enumerate(Path.GetFileNameWithoutExtension(path), evidence, kind != "Movie");
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                ctx = row["ctx"]!.GetValue<string>(),
                h = readings.Select(h => new { form = h.Form.ToString(), s = h.Season, e = h.Episode, end = h.EpisodeEnd, date = h.AirDate, half = h.Half, x = h.Explicit, f = h.Features.GroupBy(k => k.Key).ToDictionary(k => k.Key, k => k.First().Value) })
            }, json));
        }
    }
}
