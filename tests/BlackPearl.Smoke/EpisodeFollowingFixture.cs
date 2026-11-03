using System.Xml.Linq;
using BlackPearl.Core;

// Auto-download of new episodes: what counts as new, the read-only release search, the notification and its token, the
// download of exactly the releases shown, countdowns, skipping, giving up, stalled downloads and arrivals.
static class EpisodeFollowingFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var data = Path.Combine(directory, "data"); var anime = Path.Combine(directory, "Anime");
        var store = new LibraryStore(data);
        store.Setting("folders", Wire.Serialize(new[] { new LibraryFolder(anime, "Anime") }));
        var show = Path.Combine(anime, "Frieren (2023)"); var season1 = Path.Combine(show, "Season 01"); var season2 = Path.Combine(show, "Season 02");
        Directory.CreateDirectory(season1);
        MediaItem File(int season, int episode, string name, string folder) => new("frieren-" + season + "-" + episode, Path.Combine(folder, name), "Frieren", "Anime", 2023, "Frieren", season, episode,
            ShowId: "show-frieren", SeriesPath: show, MetadataProvider: "tmdb", MetadataId: 209867, MetadataType: "tv");
        store.Save(File(1, 1, "[SubsPlease] Sousou no Frieren - 01 (1080p) [AAAA1111].mkv", season1));
        store.Save(File(1, 2, "[SubsPlease] Sousou no Frieren - 02 (1080p) [AAAA2222].mkv", season1));
        store.Save(File(1, 3, "[SubsPlease] Sousou no Frieren - 03 (1080p) [AAAA3333].mkv", season1));
        var member = store.All().First(x => x.Id == "frieren-1-1");

        var now = DateTimeOffset.UtcNow; var start = now;
        string Day(int offset) => DateOnly.FromDateTime(start.UtcDateTime).AddDays(offset).ToString("yyyy-MM-dd");
        var listing = new List<MetadataSeason>
        {
            new(1, null, null, null, null, [new(1, "E1", null, null, "2023-09-29", 24), new(2, "E2", null, null, "2023-09-29", 24), new(3, "E3", null, null, "2023-10-06", 24)]),
            new(2, null, null, null, null, [new(1, "E1", null, null, Day(-14), 24), new(2, "E2", null, null, Day(-1), 24), new(3, "E3", null, null, Day(7), 24), new(4, "E4", null, null, Day(14), 24)]),
        };
        var listings = 0;
        Task<FollowSource?> Seasons(MediaItem item, CancellationToken ct) { listings++; return Task.FromResult<FollowSource?>(new("tmdb", 209867, listing.ToArray())); }

        // A fake interactive plugin: titles, then releases of the requested episodes, then a download of what was chosen.
        var requests = new List<AcquisitionRequest>();
        var noReleasesFor = new HashSet<int>();
        Task<AcquisitionResult> Run(AcquisitionPlugin plugin, AcquisitionRequest request, CancellationToken ct)
        {
            requests.Add(request);
            if (request.Action == "search")
                return Task.FromResult(new AcquisitionResult(true, "Choose a title", "shows", [new("frieren-s2", "Frieren"), new("frieren-specials", "Frieren Specials")]));
            if (request.Action == "releases")
            {
                var choices = new List<AcquisitionChoice>();
                foreach (var episode in request.Episodes.Where(x => !noReleasesFor.Contains(x.Episode)))
                {
                    var n = 28 + episode.Episode;
                    choices.Add(new($"erai-{episode.Episode}", $"[Erai-raws] Sousou no Frieren - {n} [1080p][Multiple Subtitle].mkv", Matches: true, Episode: $"S02E{episode.Episode:00}"));
                    choices.Add(new($"subsplease-{episode.Episode}", $"[SubsPlease] Sousou no Frieren - {n} (1080p) [BBBB{n}00].mkv", Matches: true, Episode: $"S02E{episode.Episode:00}"));
                    choices.Add(new($"720-{episode.Episode}", $"[SubsPlease] Sousou no Frieren - {n} (720p).mkv", Matches: false, Episode: $"S02E{episode.Episode:00}"));
                }
                choices.Add(new("batch", "[SubsPlease] Sousou no Frieren S2 (01-12) (1080p) [Batch]", Assignable: true));
                return Task.FromResult(new AcquisitionResult(true, "Choose releases", "releases", choices.ToArray()));
            }
            if (request.Action == "acquire") return Task.FromResult(new AcquisitionResult(true, $"Queued {request.SelectedReleases!.Length} releases"));
            return Task.FromResult(new AcquisitionResult(false, "Unexpected request"));
        }
        var sparrow = new AcquisitionPlugin("sparrow", "Sparrow", "", ["anime"], "sparrow.exe", [], directory, 120, Interactive: true);
        var engine = new EpisodeFollowing(store, [sparrow], Seasons, Run, () => now);

        // Off: nothing is checked.
        FollowStore.Follow(store, member, "sparrow", new AcquisitionOptions("Frieren", Resolution: 1080), null, DateOnly.FromDateTime(start.UtcDateTime).AddDays(-3));
        var off = await engine.CheckAsync(CancellationToken.None);
        check(off.Ready is null && listings == 0 && requests.Count == 0, "with auto-download off, a check asks nobody anything");
        FollowStore.SaveSettings(store, new FollowSettings(Enabled: true));

        // A check holding the lock keeps a second one out.
        using (var held = FollowLock.Check(store))
            check(held is not null && (await engine.CheckAsync(CancellationToken.None)).Summary == "Another check is already running.", "two checks never run at once");

        // The first check: only the episode that aired since following started is new; it's searched for, read-only.
        var first = await engine.CheckAsync(CancellationToken.None);
        var followed = FollowStore.Show(store, "show-frieren")!;
        check(followed.Episodes.Select(x => x.Code).SequenceEqual(["S02E02"]), "an episode that aired since following started is new; older, upcoming and on-disk ones aren't");
        check(requests.Select(x => x.Action).SequenceEqual(["search", "releases"]) && requests[1].Options!.SourceUrl == "frieren-s2" && requests[1].Options!.Resolution == 1080,
            "without a saved provider title, the title that is clearly this show is searched for and its releases listed with the saved quality");
        check(requests[1] is { Scope: "episode", Season: 2, Name: "Frieren S02E02" } && requests[1].TargetDirectory == season2 && requests[1].Episodes.Single().Episode == 2,
            "the release search asks for the new episode, going into its season's folder");
        check(requests.All(x => x.Action != "acquire"), "finding releases downloads nothing");
        var notice = first.Ready!;
        check(notice is { Kind: "ready", StartAt: 0 } && notice.Episodes.Single() == new FollowEpisodeRef("show-frieren", 2, 2) && EpisodeFollowing.IsToken(notice.Token),
            "the found episode is in one notification with a random token");
        check(notice.Lines.Single() == "Frieren S02E02 · [SubsPlease] Sousou no Frieren - 30 (1080p) [BBBB3000].mkv" && notice.Footer == "Nothing downloads until you choose Download.",
            "the notification names the release it would download, picked like the files already on disk, and says nothing downloads yet");
        check(followed.Episodes[0] is { Status: FollowStatus.Pending, Releases: [{ Id: "subsplease-2" }] } && followed.Episodes[0].Token == notice.Token,
            "the episode waits for the user with the release it was shown");
        check(store.Setting(ContentAcquisition.PreferenceKey("sparrow", requests[1])) is { } saved && Wire.Read<AcquisitionOptions>(saved).SourceUrl == "frieren-s2",
            "the title found by searching is remembered for the season");

        // Checking again soon: nothing new, and the provider's list isn't read again yet.
        requests.Clear();
        var again = await engine.CheckAsync(CancellationToken.None);
        check(again.Ready is null && requests.Count == 0 && listings == 1, "a second check soon after finds nothing new and asks nobody again");

        // Tokens: only the notification's own token acts on its episodes.
        check(engine.Knows(notice.Token) && !engine.Knows("not-a-token") && engine.WithToken(EpisodeFollowing.NewToken()).Length == 0 && !engine.Knows(notice.Token.ToUpperInvariant()),
            "a made-up or malformed token matches nothing");
        var outcomes = await engine.ApproveAsync(engine.WithToken(notice.Token), CancellationToken.None);
        var acquire = requests.Single(x => x.Action == "acquire");
        check(acquire.SelectedReleases!.SequenceEqual(["subsplease-2"]) && acquire.Options!.SourceUrl == "frieren-s2" && acquire.Episodes.Single().Episode == 2,
            "Download downloads exactly the release the user was shown");
        check(outcomes.Single() is { Success: true, Name: "Frieren S02E02", Message: "Queued 1 releases" }, "the download's result is reported");
        followed = FollowStore.Show(store, "show-frieren")!;
        check(followed.Episodes[0] is { Status: FollowStatus.Sent, Token: null } && followed.Episodes[0].Target == season2, "a downloaded episode is sent, and its notification is done");
        check((await engine.ApproveAsync(notice.Episodes, CancellationToken.None)).Length == 0 && requests.Count(x => x.Action == "acquire") == 1 && !engine.Knows(notice.Token),
            "the same notification can't download twice, from another window or a second click");

        // A week later: S02E03 aired. With a countdown, the notification says when it downloads; Skip stops it.
        FollowStore.SaveSettings(store, new FollowSettings(Enabled: true, Countdown: true, CountdownMinutes: 5));
        now = start.AddDays(8); requests.Clear();
        var week = await engine.CheckAsync(CancellationToken.None);
        check(listings == 2 && requests.Select(x => x.Action).SequenceEqual(["releases"]), "the episode list is read again later, and a remembered title goes straight to its releases");
        check(week.Ready is { StartAt: > 0 } countdown && countdown.StartAt == now.AddMinutes(5).ToUnixTimeMilliseconds() && countdown.Footer!.StartsWith("Downloads at "),
            "with a countdown, the notification says when the download starts");
        check(engine.DueCountdowns().Length == 0, "nothing downloads before the countdown ends");
        now = now.AddMinutes(6);
        check(engine.DueCountdowns().SequenceEqual([new FollowEpisodeRef("show-frieren", 2, 3)]), "an ended countdown is due, even for a check that has stopped running");
        check(engine.Pause(engine.WithToken(week.Ready!.Token)) == 1 && engine.DueCountdowns().Length == 0 && engine.Knows(week.Ready!.Token) &&
            FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 3) is { Status: FollowStatus.Pending, StartAt: 0 }, "Review pauses a countdown: the episode waits for the user");
        check(engine.Skip(engine.WithToken(week.Ready!.Token)) == 1 && engine.DueCountdowns().Length == 0 && FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 3).Status == FollowStatus.Skipped,
            "Skip stops a countdown");

        // Sent a week ago and never in the library: it counted as stalled and needed attention.
        check(FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 2).Status == FollowStatus.Stalled && week.Attention is { Kind: "attention" } attention &&
            attention.Lines.Single() == "Frieren S02E02 · hasn't arrived yet", "a sent episode that hasn't arrived after three days needs attention");
        // Its file turns up in the season folder before the library scans it: it arrived.
        Directory.CreateDirectory(season2);
        var arrived = Path.Combine(season2, "[SubsPlease] Sousou no Frieren - 30 (1080p) [BBBB3000].mkv");
        System.IO.File.WriteAllText(arrived, "video"); System.IO.File.SetLastWriteTimeUtc(arrived, now.UtcDateTime);
        await engine.CheckAsync(CancellationToken.None);
        check(FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 2).Status == FollowStatus.Arrived, "a sent episode's file in its folder counts as arrived");

        // No release for a week: it's given up on, and needs attention; Look again starts over.
        noReleasesFor.Add(4); now = start.AddDays(15); requests.Clear();
        var waiting = await engine.CheckAsync(CancellationToken.None);
        var e4 = FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 4);
        check(waiting.Ready is null && e4 is { Status: FollowStatus.Waiting, Attempts: 1, Message: "No matching release yet." } && e4.NextCheckAt == now.AddHours(1).ToUnixTimeMilliseconds(),
            "an episode with no release yet is looked for again later");
        check(requests.Single().Episodes.Single().Episode == 4, "skipped episodes aren't searched for again");
        now = start.AddDays(23);
        var lost = await engine.CheckAsync(CancellationToken.None);
        check(FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 4).Status == FollowStatus.NotFound && lost.Attention!.Lines.Single() == "Frieren S02E04 · no release found",
            "a week after airing without a release, the episode is given up on and needs attention");
        engine.Retry(new("show-frieren", 2, 4));
        check(FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 4) is { Status: FollowStatus.Waiting, Attempts: 0, NextCheckAt: 0 }, "Look again looks for it at the next check");

        // An episode whose file the library already has counts as arrived.
        store.Save(File(2, 4, "Frieren S02E04.mkv", season2));
        await engine.CheckAsync(CancellationToken.None);
        check(FollowStore.Show(store, "show-frieren")!.Episodes.Single(x => x.Episode == 4).Status == FollowStatus.Arrived, "an episode the library has is arrived");

        // A plugin that's gone: the show says so and nothing runs.
        var bare = new EpisodeFollowing(store, [], Seasons, Run, () => now);
        FollowStore.Update(store, "show-frieren", x => x with { Episodes = [.. x.Episodes, new FollowEpisode(2, 5, Day(20), FollowStatus.Waiting, now.ToUnixTimeMilliseconds())] });
        requests.Clear(); await bare.CheckAsync(CancellationToken.None);
        check(requests.Count == 0 && FollowStore.Show(store, "show-frieren")!.Problem!.Contains("isn't installed"), "without its plugin, a followed show reports it and downloads nothing");

        // Backlog: following with the season's aired episodes asked for gets those too.
        var bocchi = Path.Combine(anime, "Bocchi"); Directory.CreateDirectory(bocchi);
        store.Save(new MediaItem("bocchi-1-1", Path.Combine(bocchi, "Bocchi S01E01.mkv"), "Bocchi", "Anime", 2022, "Bocchi", 1, 1, ShowId: "show-bocchi", SeriesPath: bocchi, MetadataProvider: "tmdb", MetadataId: 1));
        listing = [new(1, null, null, null, null, [new(1, null, null, null, Day(-30), 24), new(2, null, null, null, Day(-23), 24), new(3, null, null, null, Day(-16), 24)])];
        FollowStore.Follow(store, store.All().First(x => x.Id == "bocchi-1-1"), "sparrow", null, 1, DateOnly.FromDateTime(now.UtcDateTime));
        await engine.CheckAsync(CancellationToken.None);
        check(FollowStore.Show(store, "show-bocchi")!.Episodes.Select(x => x.Code).SequenceEqual(["S01E02", "S01E03"]), "a backlog season's aired episodes are looked for too");

        // Files numbered another way than the provider: nothing counts as new.
        var odd = Path.Combine(anime, "Odd"); Directory.CreateDirectory(odd);
        foreach (var n in new[] { 13, 14, 15 })
            store.Save(new MediaItem("odd-" + n, Path.Combine(odd, $"Odd {n}.mkv"), "Odd", "Anime", 2020, "Odd", 1, n, ShowId: "show-odd", SeriesPath: odd, MetadataProvider: "tmdb", MetadataId: 2));
        listing = [new(1, null, null, null, null, Enumerable.Range(1, 12).Select(n => new MetadataEpisode(n, null, null, null, Day(-1), 24)).ToArray())];
        FollowStore.Follow(store, store.All().First(x => x.Id == "odd-13"), "sparrow", null, 1, DateOnly.FromDateTime(now.UtcDateTime));
        await engine.CheckAsync(CancellationToken.None);
        check(FollowStore.Show(store, "show-odd") is { Episodes.Length: 0, Problem: { } problem } && problem.Contains("numbered differently"), "files numbered unlike the provider's list never look new");

        // Files matched with TMDB and a TVmaze list with the same numbers: they fit, and the next season's episode is new.
        var guide = Path.Combine(anime, "Guide"); Directory.CreateDirectory(guide);
        foreach (var n in new[] { 1, 2, 3 })
            store.Save(new MediaItem("guide-" + n, Path.Combine(guide, $"Guide S01E0{n}.mkv"), "Guide", "Anime", 2024, "Guide", 1, n, ShowId: "show-guide", SeriesPath: guide, MetadataProvider: "tmdb", MetadataId: 3));
        var tvmaze = new EpisodeFollowing(store, [sparrow], (_, _) => Task.FromResult<FollowSource?>(new("tvmaze", 3,
            [new(1, null, null, null, null, Enumerable.Range(1, 3).Select(n => new MetadataEpisode(n, null, null, null, Day(-400), 60)).ToArray()),
             new(2, null, null, null, null, [new(1, null, null, null, Day(23), 60)])])), Run, () => now);
        FollowStore.Follow(store, store.All().First(x => x.Id == "guide-1"), "sparrow", null, null, DateOnly.FromDateTime(now.UtcDateTime));
        await tvmaze.CheckAsync(CancellationToken.None);
        check(FollowStore.Show(store, "show-guide") is { Problem: null, Episodes: [{ Season: 2, Episode: 1 }] }, "files matched with another provider fit when their numbers are in its list");

        // A download cut off before the plugin answers waits for the user again, and so does one a killed process left starting.
        var guideToken = EpisodeFollowing.NewToken();
        FollowStore.Update(store, "show-guide", x => x with { Episodes = [x.Episodes[0] with { Status = FollowStatus.Pending, Releases = [new("guide-1", "Guide.S02E01.1080p-GRP")], Token = guideToken }] });
        var cut = new EpisodeFollowing(store, [sparrow], (_, _) => Task.FromResult<FollowSource?>(null),
            (_, request, _) => request.Action == "acquire" ? throw new OperationCanceledException() : Task.FromResult(new AcquisitionResult(false, "unused")), () => now);
        var stopped = false;
        try { await cut.ApproveAsync(cut.WithToken(guideToken), CancellationToken.None); } catch (OperationCanceledException) { stopped = true; }
        check(stopped && FollowStore.Show(store, "show-guide")!.Episodes[0] is { Status: FollowStatus.Pending, StartAt: 0, Message: { } note } && note.Contains("interrupted") && cut.Knows(guideToken),
            "a download interrupted before it started waits for the user again");
        FollowStore.Update(store, "show-guide", x => x with { Episodes = [x.Episodes[0] with { Status = FollowStatus.Downloading, UpdatedAt = now.AddHours(-2).ToUnixTimeMilliseconds() }] });
        await tvmaze.CheckAsync(CancellationToken.None);
        check(FollowStore.Show(store, "show-guide")!.Episodes[0].Status == FollowStatus.Pending, "a download left starting by a process that ended waits for the user again after an hour");

        // Requests for several episodes, one, or none (the setup's source check).
        var items = store.All(); var folders = new[] { new LibraryFolder(anime, "Anime") };
        check(ContentAcquisition.ForEpisodes(member, 2, [new(2, 5, null, null, false), new(2, 4, null, null, false)], items, folders) is { Scope: "season", Name: "Frieren S02" } several &&
            several.Episodes.Select(x => x.Episode).SequenceEqual([4, 5]) && several.SeasonDirectory == season2 && several.TargetDirectory == show &&
            ContentAcquisition.ForEpisodes(member, 3, [], items, folders) is { Scope: "season", Episodes.Length: 0, SeasonDirectory: null }, "auto-download asks for several episodes of a season, or for none at setup");

        // Links from notification buttons.
        var token = EpisodeFollowing.NewToken();
        check(FollowNotifications.ParseLink("blackpearl-downloads", "blackpearl-downloads:approve?token=" + token) == ("approve", token) &&
            FollowNotifications.ParseLink("blackpearl-downloads", "BLACKPEARL-DOWNLOADS:skip?token=" + token) == ("skip", token) &&
            FollowNotifications.ParseLink("blackpearl-downloads", "blackpearl-downloads:open") == ("open", null) &&
            FollowNotifications.ParseLink("blackpearl-downloads", "blackpearl-downloads:desktop?token=" + token) == ("desktop", token), "notification and desktop icon links say what to do and for which token");
        check(new[] { "blackpearl-downloads:approve", "blackpearl-downloads:approve?token=xyz", "blackpearl-downloads:approve?token=" + token + "&then=other", "other:approve?token=" + token,
            "blackpearl-downloads:delete?token=" + token, "blackpearl-downloads-1234abcd:approve?token=" + token }.All(x => FollowNotifications.ParseLink("blackpearl-downloads", x) is null),
            "links without a valid token, with extra parts, or of another scheme are ignored");

        // Notification XML: escaped, with buttons that open our links; it stays on screen while it waits for the user.
        var xml = XDocument.Parse(FollowNotifications.ToastXml(new FollowNotice("ready", token, "1 new <episode>", ["Tom & Jerry S01E01 · [Group] \"x\""], "Nothing downloads.", 0, []), "blackpearl-downloads"));
        check(xml.Root!.Attribute("scenario")?.Value == "reminder" && xml.Descendants("text").First().Value == "1 new <episode>" &&
            xml.Descendants("action").Select(x => x.Attribute("arguments")!.Value).SequenceEqual(["blackpearl-downloads:approve?token=" + token, "blackpearl-downloads:open?token=" + token, "blackpearl-downloads:skip?token=" + token]) &&
            xml.Descendants("action").All(x => x.Attribute("activationType")!.Value == "protocol"), "the notification is valid XML with Download, Review and Skip");
        var timed = XDocument.Parse(FollowNotifications.ToastXml(new FollowNotice("ready", token, "x", ["y"], "z", 1, []), "s"));
        check(timed.Root!.Attribute("scenario") is null && timed.Descendants("action").First().Attribute("content")!.Value == "Download now", "a countdown's notification can be downloaded at once and doesn't wait on screen");

        // The hourly task: for this user, every hour and after signing in, on battery, catching up after sleep.
        var task = XDocument.Parse(FollowNotifications.TaskXml(@"PC\Tom & Jerry", @"C:\Apps & Co\BlackPearl.exe", "--check-episodes --data-dir \"C:\\Data\"", new DateTime(2026, 11, 3, 12, 0, 0)));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        check(task.Descendants(ns + "Command").Single().Value == @"C:\Apps & Co\BlackPearl.exe" && task.Descendants(ns + "Arguments").Single().Value == "--check-episodes --data-dir \"C:\\Data\"" &&
            task.Descendants(ns + "Interval").Single().Value == "PT1H" && task.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value == @"PC\Tom & Jerry" &&
            task.Descendants(ns + "StartWhenAvailable").Single().Value == "true" && task.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value == "false" &&
            task.Descendants(ns + "RunLevel").Single().Value == "LeastPrivilege", "the hourly task runs the check for this user without administrator rights");
    }
}
