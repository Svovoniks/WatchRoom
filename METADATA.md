# Metadata and library behavior

WatchRoom resolves local structure first, then enriches it with enabled remote providers. Files stay local; title searches and provider IDs are sent to the selected metadata services.

## Shows, seasons, and files

The SQLite library has persistent show and season records. Video records keep their existing file IDs and reference those records. Existing libraries migrate automatically; saved queue file IDs remain valid.

In structured TV/anime folders, the show folder owns membership, so different filename aliases remain in one show. Flat libraries use normalized title and year, or a known provider identity. Different show folders remain separate by default. **Combine show folders when provider IDs match** optionally merges them; conflicting TMDB identities block merging through secondary IDs. Names alone never merge separate structured folders.

Seasons use the show identity and season number. Unknown seasons have their own group. An explicit `S02E03` filename takes precedence over a conflicting season folder and reports the conflict in Details. Specials use season 0. Extras/trailers/sample folders and conventional trailer/sample filenames are excluded from scans.

NCOP/NCED, CM collections, promos, previews and pilot films inside confirmed show folders remain accessible as bonus clips under **Specials**. They inherit show membership without guessed episode numbers and are excluded from automatic matching and ordinary episode counts. Explicit numbered specials retain precedence. Rescan repairs old range and bonus records while preserving manual identities and numbering locks.

Numbering provenance distinguishes explicit filename numbering, season-relative folders, absolute anime numbering, and restarted parts. Season-relative numbers do not map through an absolute provider list. Unresolved/conflicting identities are not collapsed into versions. An anime/TV root or franchise folder alone does not create a show for an unnumbered film. Movie-root evidence prevents an automatic TV-only provider from claiming a standalone movie; explicit numbered episodes and manual matches retain precedence.

Filename scanning and metadata search aliases share the same episode extraction rules. Dots, underscores, ordinary/Unicode dashes, release versions and technical tags are accepted. Episode subtitles stay separate from show titles, so `Azumanga Daioh - 01 - Miss Yukari [KM-Dual-DVDRIP]` searches for **Azumanga Daioh** and retains **Miss Yukari** as its episode title.

| Filename example | Interpretation |
| --- | --- |
| `Show S02E03`, `Show S02-E03`, `Show S02.E03`, `Show 2x03` | Season 2, episode 3 |
| `Show Season 2 Episode 3`, `Show Сезон 2 Серия 3` | Explicit season and episode; French, Spanish, German and Italian labels are also accepted |
| `Show EP03`, `Show E03v2`, `Show - 03 - Pilot` | Episode 3, with an optional subtitle |
| `Show [03] Pilot`, `Show (03) Pilot`, `Show 03 Pilot`, `Show-03-Pilot` | Episode 3 in a TV/anime context |
| `03 - Pilot`, `003 Pilot`, `079-080. Subtitle`, `E03`, `S02E03` | Show identity comes from the containing show/season folder |
| `Show S01E02-E04`, `Show S01E02E03`, `Show 1x02-1x04`, `Show EP02-EP04` | Contiguous multi-episode ranges |
| `Show 203` or `Show 0203` inside `Season 02` | Season 2, episode 3; without a confirming season folder this stays absolute numbering |
| `Show 2021.01.01`, `Show 2021-01-01`, `Show 01.01.2021` | TV-folder air date; provider numbering is applied only when exactly one episode has that date |

Bare movie years, resolution/audio numbers and one-digit movie sequels with subtitles are not enough to infer TV identity. Non-contiguous lists such as `S01E02E04` and cross-season files retain a correction warning instead of inventing included episodes. Rescanning reparses previously unrecognized entries without renaming files or changing queue IDs; NFO metadata, manual matches and locked fields retain precedence.

The grouping audit repair runs once on startup. It repairs automatic season collisions, acronym/year episode mistakes, film hierarchies, and TV matches conflicting with movie-root evidence. SQLite creates `library-before-grouping-repair-v1.db` before these repairs, and `grouping-repair-v1.json` records changed identities. Manual matches and identity/numbering locks are retained. Previously indexed excluded extras remain stored with their IDs and disk availability, but are hidden from the main catalog and remote metadata fetching; numbered S00 specials remain visible.

Season packs (`Show.S02.1080p`) and individual episode release folders (`Show.S02E03.1080p`) belong to their enclosing show folder. Loose releases can join one unambiguous existing show through matching provider IDs or title/year; conflicting IDs or multiple candidate show folders prevent that attachment. Existing split release-folder identities repair automatically on startup while preserving file IDs and metadata.

Multi-episode files such as `S01E03-E04` store a range and combine episode titles, plots, and runtimes. Episode counts count unique logical numbers. Duplicate releases appear as one episode card; Windows offers **Choose version** in its context menu. Playback queues select an available release and exclude metadata-only placeholders. Original files and saved queues are not deleted or rewritten.

## Matching and providers

Settings controls provider order (`tmdb, tvmaze, wikipedia`), language, country, and the refresh interval. Empty provider order disables remote metadata. Packaged builds include WatchRoom’s application credential. An optional personal key overrides it; Windows stores that override in Credential Manager, while Mac keeps it for the session. Developers can build without the bundled credential and supply their own key. Bundled credentials are extractable from binaries. A failure or unresolved match allows the next enabled provider to run. Fallback retains other provider IDs and a manually selected primary identity.

TMDB searches consider localized and original titles, exact/prefix title similarity, and exact/adjacent release years. Weak and tied candidates require a manual match. IMDb/TVDB IDs can identify a TMDB show or movie; TVmaze can identify shows through IMDb/TVDB IDs. Anime alias lookup is available to both TMDB and TVmaze through the locally cached AniDB title index. Movie/show ambiguity remains unresolved.

Unresolved TMDB and TVmaze searches check provider aliases for at most four compatible candidates. Crowded results are left unresolved rather than truncating away possible competitors. Alias lookups run concurrently within each provider's request limit. If a lookup fails, incomplete alias evidence cannot produce an automatic winner. Independent title queries can continue after an alias request fails; if none verifies a winner, the failed request is reported as a provider failure. Canonical provider titles remain the displayed result.

After exact title, original-title, filename/folder and provider-alias searches, a typo fallback permits one insertion, deletion, substitution or adjacent-letter transposition. It requires at least five normalized characters, an exact release year, known movie/show type, unchanged numeric title components and no competing title within two edits. Anime needs positive animation evidence. Candidates found by earlier searches remain competitors when later queries broaden the search. Missing years, ties, contradictory evidence and incomplete candidate checks stay unresolved.

Wikipedia movies use edition-qualified provider IDs, cheap exact-page lookup and up to eight search candidates when exact lookup fails. A title without a local year also checks search competitors before accepting an exact page. Film categories, title or verified aliases, release year and media type determine confidence; search rank alone never selects a film. Russian title queries use Russian Wikipedia and verified language links supply English text when requested. Russian page IDs use `wikipedia:ru`, separately from English `wikipedia`. Release tags are removed before searching. Russian transliteration processes digraphs before individual letters and requires Russian spelling evidence rather than English `ch`/`sh` alone.

Literal exact titles score above article-stripped equivalents, so **One Piece** outranks **The One Piece** while same-name remakes still require identification. Dated release folders can corroborate a missing year.

**Find artwork** works with enabled TVmaze and Wikipedia providers without TMDB credentials. The picker displays poster preview, year, animation/live action and provider, and can suggest an already identified local release. Choosing a result saves its provider ID for the current show without automatically merging separate folders. Existing title, artwork and numbering locks remain authoritative.

Series searches also use up to four distinct titles extracted directly from numbered filenames across the same show. This preserves an original-language filename alias when a localized folder supplies the displayed title, including when the first episode has only an episode number. Aliases are fallback search evidence: they do not change folder membership or file IDs, and candidates still need to pass the existing confidence and ambiguity checks.

Successful JSON responses are shared across clients within a fetch and saved under the library's `metadata-http-cache` directory. Ordinary responses expire after one hour; provider aliases expire after seven days. Cache keys separate provider URLs, language, region, provider configuration and credentials, using hashed filenames without saving request credentials. Empty searches, HTTP errors and provider-error JSON are not cached. Corrupt/unwritable cache files fall back to the provider. All explicit metadata refresh modes bypass saved responses while sharing fresh responses within that run. Duplicate concurrent requests reuse one successful response; cancelling a waiter does not cancel another caller's work.

The blanket 550 ms delay after every title is replaced by provider request pacing: TMDB requests start at least 100 ms apart, TVmaze 550 ms, and Wikipedia 350 ms across language editions. TMDB/TVmaze allow at most two active requests each; Wikipedia allows one. HTTP 429 increases provider pacing by 100 ms up to 1.5 seconds and backs off the whole provider, respecting Retry-After (or a short increasing delay if absent); 5xx responses also retry, up to twice. These are conservative client settings, not promises about provider capacity. Cancellation stops queued requests and backoff waits. JSON responses are limited to 10 MB; artwork is limited to 20 MB, 8192 pixels per axis and 32 million pixels. TVmaze medium images are preferred over oversized originals. WebP is decoded to PNG; JPEG/PNG are checked before atomic storage. Artwork redirects allow up to five hops, validating every hop against permitted HTTPS image hosts; API authorization is never attached to image CDN requests. Updated image paths avoid stale desktop thumbnail caches.

Provider references: [TMDB TV aliases](https://developer.themoviedb.org/reference/tv-series-alternative-titles), [movie aliases](https://developer.themoviedb.org/reference/movie-alternative-titles), [TMDB rate limits](https://developer.themoviedb.org/docs/rate-limiting), and [TVmaze aliases and rate limits](https://www.tvmaze.com/api).

AniDB dumps are parsed once per cached file version into an in-memory title index. Lookups share that index until the dump's size or modification time changes; a failed refresh keeps the previous validated dump. Repeated aliases within one anime remain valid, while aliases shared by different anime remain ambiguous. Metadata fetching re-reads affected file IDs and their current show/season records after provider requests, avoiding repeated whole-library reads while retaining protection against concurrent identity and lock edits.

## Refreshing and locks

- **Retry missing artwork** retries images for existing identities, preserving text and numbering and bypassing automatic retry cooldowns.
- **Fill missing metadata** preserves existing text and artwork.
- **Refresh text metadata** updates unlocked text while keeping images.
- **Refresh text and replace downloaded artwork** also fetches fresh provider images. Local/NFO artwork and locked fields retain precedence.

Automatic fetching checks the configured freshness interval (seven days by default). Set it to 0 to disable periodic refresh; newly scanned episodes still fetch metadata. Text and identity are saved before artwork. Each failed season/episode image is isolated so later episodes still receive text and artwork. Failed providers do not erase existing metadata.

Windows card context menus offer **Lock metadata**, episode numbering, version selection, and NFO export. **Correct title** and local poster selection protect those fields against later remote refreshes. Settings on both desktop clients exposes provider, refresh, and placeholder controls.

## Numbering and missing episodes

Aired season/episode numbering is the default. Absolute anime numbers map through the provider's full ordered episode list, excluding specials. The original absolute number remains stored and rescans retain the resolved number. A multi-episode absolute range crossing provider seasons is reported and left unresolved rather than assigning the wrong plot.

`Part 1`/`Part 2` folders belong to their parent show. Restarted numbering retains its original part and episode number, then maps through provider season boundaries or broadcast breaks of at least six weeks. If the provider boundaries do not uniquely fit the known parts, numbering remains unresolved with a Details message. Part 2 is never assumed to mean season 2 or silently deduplicated against part 1.

DVD order uses a unique TMDB DVD episode group. If the group is unavailable or ambiguous, the show remains unresolved; TVmaze aired order is not substituted. Choose numbering from the Windows show context menu or `displayorder` in `tvshow.nfo`.

**Show missing episodes** and **Show upcoming episodes** are opt-in and default off. Placeholders have metadata but cannot play or enter queues. Disabling imports removes them on the next fetch, even if normal metadata is fresh. **Show specials within their aired seasons** uses known NFO `airsbefore_season`, `airsbefore_episode`, and `airsafter_season` placement for both browsing and Windows season queues.

## NFO files

The scanner reads `tvshow.nfo`, `season.nfo`, `movie.nfo`, and video-name `.nfo` sidecars. Supported data includes titles, plots, dates, numbering, original title, genres, cast, ratings, provider IDs, local artwork, and metadata locks. Plain IMDb/TMDB URL NFO files can identify shows or movies. Episode IDs are never mistaken for show IDs. XML external entities are prohibited. NFO remote artwork supports the TMDB, TVmaze, and Wikimedia image hosts already used by WatchRoom.

NFO fields take precedence over remote fields, including during explicit refresh. Edit the NFO to change its authoritative values. **Save metadata beside videos as NFO files** is opt-in. Export preserves unknown tags belonging to other tools and saves atomically. It writes episode/movie sidecars, show metadata in known show folders, and season metadata in separate season folders.

## Fetch diagnostics

On Windows, metadata diagnostics are written to `%LOCALAPPDATA%\Watchroom\logs\diagnostics.jsonl` by default. `WATCHROOM_DATA` changes the app data directory, and `WATCHROOM_DIAGNOSTICS` overrides the log directory. The log rotates at 5 MB and retains four files, including the current file.

Metadata events include fetch start/completion, title search aliases, missing fields, request endpoints, HTTP status, response content type/length, elapsed time, retry decisions, candidate IDs/names/years/scores/rejection reasons, deferred counts and retry times, redirect destinations/hops, conversions, and exception types, messages, stacks, and inner exceptions. Request context identifies the title, media ID, stage, season, and episode. Provider fallback, season/episode, unsupported image, and cache read/write failures are recorded even when the app handles them and continues. URL credentials, query strings, and fragments are omitted; authorization headers and response bodies are never logged.

`metadata-fetch-report.json` in the app data directory summarizes the latest completed fetch and retains unresolved issues across cooldown-only runs. Its `issues` include title/episode identity outcomes, missing fields, provider failures and separate artwork failures with retry times. `currentIssues` covers only the current title run; `deferredTitles` counts skipped title groups. Outstanding issues are persisted in SQLite and discarded when resolved, removed or invalidated by identity changes. Unmatched titles normally retry after 24 hours, failed requests after one hour; explicit refresh bypasses these cooldowns. Season/episode and intermediate provider failures also appear in the rotating diagnostic log.

## Verification

`dotnet run --project tests/Watchroom.Smoke -- --filename-schemas` verifies filename formats, movie safeguards, rescan repair and date/range mapping against fixture providers. `--metadata-pipeline` runs isolated metadata, migration, identity, provider, refresh, numbering, and placeholder checks without live provider credentials. The regular smoke suite also runs these checks. `tests/Watchroom.DesktopChecks` verifies incremental library updates and dispatcher responsiveness. Native macOS UI behavior requires testing on a Mac; the Mac client can be compiled on Windows.

`dotnet run --project tests/Watchroom.Smoke -- --metadata-diagnostics` checks request context, retries, HTTP/image/cache failures, fallback and season/episode errors, report details, and URL credential redaction with synthetic providers.

`dotnet run --project tests/Watchroom.Smoke -- --metadata-discovery` runs the labeled alias/typo corpus and response-cache/pacing checks in isolation. It writes a synthetic accuracy report under `artifacts/metadata-discovery`; these results do not estimate accuracy on a personal library.

`dotnet run --project tests/Watchroom.Smoke -- --grouping-audit` reproduces the audited failures against fresh and historical records, including repeated rescans, backups, repair logs, queue IDs, manual matches, locks, extras, and movie/TV ambiguity.

`dotnet run --project tests/Watchroom.Smoke -c Release -- --metadata-fixes` replays punctuation, Russian language links, remakes, ranges, bonus content, WebP, image budgets/dimensions, redirects, isolated asset failures, image-only retry, keyless identification and retained cooldown issues. `dotnet run --project tests/Watchroom.DesktopChecks -c Release -- --metadata-picker` verifies the Windows picker layout, explicit selection and asynchronous preview behavior.

## TMDB attribution, builds and retention

Windows About and Mac Settings include the official TMDB logo, source link and non-endorsement notice. Builds embed a key from an external UTF-8 file specified by `WATCHROOM_TMDB_KEY_FILE` (or MSBuild `WatchroomTmdbKeyFile`); never commit that file. GitHub packaging uses the `WATCHROOM_TMDB_API_KEY` repository secret, only in trusted non-PR publish steps. A missing packaging secret fails the build rather than producing an installer without default metadata access.

TMDB-derived fields retain provenance and their previous local values. After 175 days they revert to those local values; independently changed fields and other providers are preserved. Downloaded TMDB artwork expires by file age and is fetched again when needed. Cleanup runs at startup, during library reads and hourly while either desktop client is running, even with fetching disabled or offline. Locks do not extend retention. Older library records acquire conservative provenance from their provider/source and original filename; unknown fetch dates expire immediately. The short-lived HTTP response cache is pruned locally without requiring a fetch. Repair backups keep local records while expiring their TMDB fields. Old generated reports, outstanding issues and metadata diagnostic entries also expire.

NFO export excludes tracked TMDB content so exported snapshots do not become permanent local copies. Existing user-supplied NFOs remain authoritative. TMDB’s classification of this project and approval of its application credential remain separate from these implementation checks.
