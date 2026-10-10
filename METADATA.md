# Metadata and library behavior

WatchRoom resolves local structure first, then enriches it with enabled remote providers. Files stay local; title searches and provider IDs are sent to the selected metadata services.

## Shows, seasons, and files

The SQLite library has persistent show and season records. Video records keep their existing file IDs and reference those records. Existing libraries migrate automatically; saved queue file IDs remain valid.

In structured TV/anime folders, the show folder owns membership, so different filename aliases remain in one show. Flat libraries use normalized title and year, or a known provider identity. Different show folders remain separate by default. **Combine show folders when provider IDs match** optionally merges them; conflicting TMDB identities block merging through secondary IDs. Names alone never merge separate structured folders.

Seasons use the show identity and season number. Unknown seasons have their own group. An explicit `S02E03` filename takes precedence over a conflicting season folder and reports the conflict in Details. Specials use season 0. Extras/trailers/sample folders and conventional trailer/sample filenames are excluded from scans.

Numbering provenance distinguishes explicit filename numbering, season-relative folders, absolute anime numbering, and restarted parts. Season-relative numbers do not map through an absolute provider list. Unresolved/conflicting identities are not collapsed into versions. An anime/TV root or franchise folder alone does not create a show for an unnumbered film. Movie-root evidence prevents an automatic TV-only provider from claiming a standalone movie; explicit numbered episodes and manual matches retain precedence.

The grouping audit repair runs once on startup. It repairs automatic season collisions, acronym/year episode mistakes, film hierarchies, and TV matches conflicting with movie-root evidence. SQLite creates `library-before-grouping-repair-v1.db` before these repairs, and `grouping-repair-v1.json` records changed identities. Manual matches and identity/numbering locks are retained. Previously indexed excluded extras remain stored with their IDs and disk availability, but are hidden from the main catalog and remote metadata fetching; numbered S00 specials remain visible.

Season packs (`Show.S02.1080p`) and individual episode release folders (`Show.S02E03.1080p`) belong to their enclosing show folder. Loose releases can join one unambiguous existing show through matching provider IDs or title/year; conflicting IDs or multiple candidate show folders prevent that attachment. Existing split release-folder identities repair automatically on startup while preserving file IDs and metadata.

Multi-episode files such as `S01E03-E04` store a range and combine episode titles, plots, and runtimes. Episode counts count unique logical numbers. Duplicate releases appear as one episode card; Windows offers **Choose version** in its context menu. Playback queues select an available release and exclude metadata-only placeholders. Original files and saved queues are not deleted or rewritten.

## Matching and providers

Settings controls provider order (`tmdb, tvmaze, wikipedia`), language, country, and the refresh interval. Empty provider order disables remote metadata. TMDB requires the user's API read access token, kept for the session. A failure or unresolved match allows the next enabled provider to run. Fallback retains other provider IDs and a manually selected primary identity.

TMDB searches consider localized and original titles, exact/prefix title similarity, and exact/adjacent release years. Weak and tied candidates require a manual match. IMDb/TVDB IDs can identify a TMDB show or movie; TVmaze can identify shows through IMDb/TVDB IDs. Anime alias lookup is available to both TMDB and TVmaze through the locally cached AniDB title index. Movie/show ambiguity remains unresolved.

Unresolved TMDB and TVmaze searches check provider aliases for at most four compatible candidates. Crowded results are left unresolved rather than truncating away possible competitors. Alias lookups run concurrently within each provider's request limit. If a lookup fails, incomplete alias evidence cannot produce an automatic winner. Canonical provider titles remain the displayed result.

After exact title, original-title, filename/folder and provider-alias searches, a typo fallback permits one insertion, deletion, substitution or adjacent-letter transposition. It requires at least five normalized characters, an exact release year, known movie/show type, unchanged numeric title components and no competing title within two edits. Anime needs positive animation evidence. Candidates found by earlier searches remain competitors when later queries broaden the search. Missing years, ties, contradictory evidence and incomplete candidate checks stay unresolved.

Series searches also use up to four distinct titles extracted directly from numbered filenames across the same show. This preserves an original-language filename alias when a localized folder supplies the displayed title, including when the first episode has only an episode number. Aliases are fallback search evidence: they do not change folder membership or file IDs, and candidates still need to pass the existing confidence and ambiguity checks.

Successful JSON responses are shared across clients within a fetch and saved under the library's `metadata-http-cache` directory. Ordinary responses expire after one hour; provider aliases expire after seven days. Cache keys separate provider URLs, language, region, provider configuration and credentials, using hashed filenames without saving request credentials. Empty searches, HTTP errors and provider-error JSON are not cached. Corrupt/unwritable cache files fall back to the provider. All explicit metadata refresh modes bypass saved responses while sharing fresh responses within that run. Duplicate concurrent requests reuse one successful response; cancelling a waiter does not cancel another caller's work.

The blanket 550 ms delay after every title is replaced by provider request pacing: TMDB requests start at least 100 ms apart, TVmaze 550 ms, and Wikipedia 200 ms. TMDB/TVmaze allow at most two active requests each; Wikipedia allows one. HTTP 429 backs off the whole provider, respecting Retry-After (or a short increasing delay if absent); 5xx responses also retry, up to twice. These are conservative client settings, not promises about provider capacity. Cancellation stops queued requests and backoff waits. Downloaded images remain size-limited, validated, and saved atomically; API authorization is never attached to image CDN requests. Updated image paths avoid stale desktop thumbnail caches.

Provider references: [TMDB TV aliases](https://developer.themoviedb.org/reference/tv-series-alternative-titles), [movie aliases](https://developer.themoviedb.org/reference/movie-alternative-titles), [TMDB rate limits](https://developer.themoviedb.org/docs/rate-limiting), and [TVmaze aliases and rate limits](https://www.tvmaze.com/api).

AniDB dumps are parsed once per cached file version into an in-memory title index. Lookups share that index until the dump's size or modification time changes; a failed refresh keeps the previous validated dump. Repeated aliases within one anime remain valid, while aliases shared by different anime remain ambiguous. Metadata fetching re-reads affected file IDs and their current show/season records after provider requests, avoiding repeated whole-library reads while retaining protection against concurrent identity and lock edits.

## Refreshing and locks

- **Fill missing metadata** preserves existing text and artwork.
- **Refresh text metadata** updates unlocked text while keeping images.
- **Refresh text and replace downloaded artwork** also fetches fresh provider images. Local/NFO artwork and locked fields retain precedence.

Automatic fetching checks the configured freshness interval (seven days by default). Set it to 0 to disable periodic refresh; newly scanned episodes still fetch metadata. Text is saved before artwork so image failures do not discard it. Failed providers do not erase existing metadata.

Windows card context menus offer **Lock metadata**, episode numbering, version selection, and NFO export. **Correct title** and local poster selection protect those fields against later remote refreshes. Settings on both desktop clients exposes provider, refresh, and placeholder controls.

## Numbering and missing episodes

Aired season/episode numbering is the default. Absolute anime numbers map through the provider's full ordered episode list, excluding specials. The original absolute number remains stored and rescans retain the resolved number. A multi-episode absolute range crossing provider seasons is reported and left unresolved rather than assigning the wrong plot.

`Part 1`/`Part 2` folders belong to their parent show. Restarted numbering retains its original part and episode number, then maps through provider season boundaries or broadcast breaks of at least six weeks. If the provider boundaries do not uniquely fit the known parts, numbering remains unresolved with a Details message. Part 2 is never assumed to mean season 2 or silently deduplicated against part 1.

DVD order uses a unique TMDB DVD episode group. If the group is unavailable or ambiguous, the show remains unresolved; TVmaze aired order is not substituted. Choose numbering from the Windows show context menu or `displayorder` in `tvshow.nfo`.

**Show missing episodes** and **Show upcoming episodes** are opt-in and default off. Placeholders have metadata but cannot play or enter queues. Disabling imports removes them on the next fetch, even if normal metadata is fresh. **Show specials within their aired seasons** uses known NFO `airsbefore_season`, `airsbefore_episode`, and `airsafter_season` placement for both browsing and Windows season queues.

## NFO files

The scanner reads `tvshow.nfo`, `season.nfo`, `movie.nfo`, and video-name `.nfo` sidecars. Supported data includes titles, plots, dates, numbering, original title, genres, cast, ratings, provider IDs, local artwork, and metadata locks. Plain IMDb/TMDB URL NFO files can identify shows or movies. Episode IDs are never mistaken for show IDs. XML external entities are prohibited. NFO remote artwork supports the TMDB, TVmaze, and Wikimedia image hosts already used by WatchRoom.

NFO fields take precedence over remote fields, including during explicit refresh. Edit the NFO to change its authoritative values. **Save metadata beside videos as NFO files** is opt-in. Export preserves unknown tags belonging to other tools and saves atomically. It writes episode/movie sidecars, show metadata in known show folders, and season metadata in separate season folders.

## Verification

`dotnet run --project tests/Watchroom.Smoke -- --metadata-pipeline` runs isolated metadata, migration, identity, provider, refresh, numbering, and placeholder checks without live provider credentials. The regular smoke suite also runs these checks. `tests/Watchroom.DesktopChecks` verifies incremental library updates and dispatcher responsiveness. Native macOS UI behavior requires testing on a Mac; the Mac client can be compiled on Windows.

`dotnet run --project tests/Watchroom.Smoke -- --metadata-discovery` runs the labeled alias/typo corpus and response-cache/pacing checks in isolation. It writes a synthetic accuracy report under `artifacts/metadata-discovery`; these results do not estimate accuracy on a personal library.

`dotnet run --project tests/Watchroom.Smoke -- --grouping-audit` reproduces the audited failures against fresh and historical records, including repeated rescans, backups, repair logs, queue IDs, manual matches, locks, extras, and movie/TV ambiguity.
