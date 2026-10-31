# Acquisition plugins

Acquisition plugins get missing and upcoming videos for you. An **Acquire** action
appears on:

- a missing or upcoming episode: its card's right-click menu, and the **Acquire**
  button on its page;
- a season with missing or upcoming episodes: its card's right-click menu, and the
  **Acquire season** button while browsing it;
- a film added with **Add title** that has no file yet: its card's menu and its page.

Watchroom only offers plugins that support the title's content type: `anime`,
`show` or `movie`. Interactive plugins open a picker for provider titles, quality,
filters and release selection before downloading. Command-only plugins run
directly, with a provider menu when several are available. The plugin downloads
the video or hands it to a downloader. When the
files reach a library folder, the next scan adds them to the library and removes
the placeholders.

## Installing

SubsPlease, EZTV, TokyoToshokan and TheRARBG are available as Sparrow provider
ports in [BlackPearlPlugins](https://github.com/Svovoniks/BlackPearlPlugins). Run
`./install.ps1` from a checkout of that repository, then Reload. Its README covers
provider setup and downloader settings.

Each plugin is a folder in the plugins folder:

```
%LOCALAPPDATA%\Watchroom\plugins\<plugin folder>\plugin.json
```

**Folders > Acquisition plugins** lists the loaded plugins and any that failed to
load. **Open plugins folder** opens the folder, and **Reload** reads it again after
you change it. The `WATCHROOM_PLUGINS` environment variable sets a different
plugins folder.

## plugin.json

```json
{
  "id": "my-downloader",
  "name": "My downloader",
  "description": "Sends releases to qBittorrent",
  "contentTypes": ["anime", "show", "movie"],
  "command": "acquire.ps1",
  "arguments": [],
  "timeoutSeconds": 120
}
```

| Field | Meaning |
| --- | --- |
| `id` | Optional. Defaults to the folder name. |
| `name` | Optional. Shown in menus and messages. Defaults to the folder name. |
| `description` | Optional. Shown in Settings and as the menu tooltip. |
| `contentTypes` | Required. Any of `anime`, `show` (or `shows`, `tv`, `series`) and `movie` (or `movies`, `film`). |
| `command` | Required. A file in the plugin folder, an absolute path or a program on `PATH`. `.ps1` scripts run with Windows PowerShell (`-NoProfile -ExecutionPolicy Bypass`). `.cmd`, `.bat` and `.exe` files run directly. |
| `arguments` | Optional. `{request}` is replaced with the path of the request file. |
| `timeoutSeconds` | Optional, 5–3600, default 120. Watchroom stops the plugin after this time. Long downloads should be handed to a downloader instead of waited for. |
| `interactive` | Optional, default false. Opens Watchroom's provider/title/release picker. The plugin must support the interactive actions below. |
| `settingsSchema` | Optional path relative to the plugin folder. A JSON array of settings fields; Watchroom displays a Settings button and preserves other fields when saving `settings.json`. |

## Interactive plugins

Interactive requests use the same JSON process protocol. Additional fields are:

```json
{
  "action": "search",
  "options": {
    "query": "Provider title",
    "sourceUrl": null,
    "resolution": 1080,
    "filter": "",
    "episodeOffset": null,
    "providerSeason": null
  },
  "selectedReleases": null
}
```

`search` and `releases` must be read-only. `search` returns catalog choices with
`stage: "shows"`, or release choices with `stage: "releases"` for a provider that
searches releases directly. After choosing a catalog title, Watchroom sends
`releases` with its title as `options.query` and its ID as `options.sourceUrl`.
The plugin's final output line can be:

```json
{"success":true,"stage":"releases","message":"Choose releases","choices":[{"id":"release-id","title":"Show S01E02 1080p","detail":"Requested: S01E02 | 1080p","matches":true,"episode":"S01E02","resolution":1080,"suggestedFilter":"Show <<episode_number>> 1080p"}]}
```

For releases, `matches` indicates whether the release satisfies the requested
missing episodes, quality, filter and numbering. `episode` groups alternate
versions so Watchroom can select one release per episode. IDs are opaque to the
host; the provider must revalidate them. `suggestedFilter` is an optional editable
Sparrow-style literal template. Catalog choices need only `id`, `title`, `detail`.

Confirmation sends `action: "acquire"`, the chosen options and an explicit
`selectedReleases` ID array. Download only those IDs. Reject empty, stale or
incompatible selections before handing anything to a downloader. The original
episode request still defines what is missing and its destination. Legacy
requests omit `action`, `options` and `selectedReleases` (or send null); existing
command-only plugins retain their direct acquisition behavior.

Watchroom saves opted-in choices by plugin, title, year, content type and season.
It cancels pending searches on edits or close and ignores stale responses.

### Settings form

A `settingsSchema` file contains fields such as:

```json
[
  { "key": "downloader", "label": "Download with", "type": "choice", "choices": ["magnet", "qbittorrent"], "default": "magnet" },
  { "key": "qbittorrent.password", "label": "Web UI password", "type": "password" }
]
```

Supported types are `text` (default), `password`, `choice` and `number` (integer).
Optional `help` adds field guidance; dotted keys address nested JSON properties.
Passwords are masked in the form and stored in the plugin's local `settings.json`.
Saving validates numeric fields and atomically replaces the settings file while
preserving keys absent from the schema, including existing title bindings.

## The request

The plugin runs with its own folder as the working directory. The request is JSON
on **standard input**. It is also saved to a temporary file, whose path is in
`WATCHROOM_REQUEST_FILE`. Watchroom deletes that file when the plugin exits.
`WATCHROOM_PLUGIN_DIR` holds the plugin's folder.

```json
{
  "version": 1,
  "scope": "episode",
  "contentType": "anime",
  "name": "Frieren S01E05",
  "title": "Frieren",
  "originalTitle": "Sousou no Frieren",
  "year": 2023,
  "targetDirectory": "D:\\Anime\\Frieren (2023)\\Season 01",
  "targetExists": true,
  "showDirectory": "D:\\Anime\\Frieren (2023)",
  "seasonDirectory": "D:\\Anime\\Frieren (2023)\\Season 01",
  "season": 1,
  "episodes": [
    { "season": 1, "episode": 5, "title": "Phantoms of the Dead", "airDate": "2023-10-06", "upcoming": false, "absoluteEpisode": null }
  ],
  "providerIds": { "tmdb": "209867", "tvdb": "424536" },
  "libraries": [
    { "path": "D:\\Anime", "kind": "anime" },
    { "path": "D:\\Shows", "kind": "show" },
    { "path": "D:\\Movies", "kind": "movie" },
    { "path": "E:\\Downloads", "kind": "mixed" }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `scope` | `episode`, `season` or `movie`. |
| `contentType` | `anime`, `show` or `movie`. This is the type the plugin was chosen for. |
| `name` | The video to get: `Show S01E05` for an episode, `Show S01` for a season, `Film (2021)` for a film. Characters that aren't allowed in file names are removed. |
| `title`, `originalTitle`, `year` | The show or film, as the library knows it. |
| `targetDirectory` | Where the files belong. For an episode, this is the season's folder. For a season, it's the show's folder. For a film, it's the library folder for films of its kind. |
| `targetExists` | `false` when the folder doesn't exist yet. The plugin should create it. |
| `showDirectory` | The show's folder. Null for films. |
| `seasonDirectory` | The season's folder, if there is one. For a season request it is null when no file of that season exists yet. |
| `season` | The season number. Null for films. |
| `episodes` | For an episode, that episode. For a season, its missing and upcoming episodes. Empty for films. `upcoming` is true when the episode hasn't aired yet. |
| `providerIds` | IDs of the title at metadata providers, such as `tmdb`, `tvdb` and `imdb`, when known. |
| `libraries` | Every library folder, with `kind` set to `anime`, `show`, `movie` or `mixed`. |

### Where folders come from

- A show's folder is the folder its files are in, even when they are spread over
  season folders.
- When an episode's season has no files yet, its folder is `Season NN` inside the
  show's folder. Season 0 uses `Specials`. If the show keeps every episode directly
  in its own folder, the episode goes there instead.
- A show with no files at all, added with **Add title**, gets a folder named
  `Title (Year)` in a library folder of its kind. Watchroom looks for a folder
  marked Show (Anime for anime) first and a Mixed folder next. An anime film looks
  for Anime, then Movie, then Mixed.
- If there are no library folders, Acquire isn't offered.

## The result

Exit code `0` means success. A non-zero exit code means failure.

The last line the plugin prints may be JSON, which then decides the result:

```json
{"success": true, "message": "Sent Frieren S01E05 to qBittorrent"}
```

`success` can be replaced by `status`. The values `ok`, `success`, `queued` and
`started` count as success. Watchroom shows `message` in a notification. Without a
JSON line, it shows the last line of output on success and the last line of error
output on failure.

After a successful run, Watchroom scans the library again a few seconds later.
Files that arrive later are picked up when the folder watcher notices them, or by
the next scan.

## Example

A PowerShell plugin that writes each request to a log and reports success.
Replace the logging with a call to your downloader:

`plugin.json`

```json
{ "name": "Request logger", "contentTypes": ["anime", "show", "movie"], "command": "acquire.ps1" }
```

`acquire.ps1`

```powershell
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
$line = "{0:u} {1} {2} -> {3}" -f (Get-Date), $request.scope, $request.name, $request.targetDirectory
Add-Content -Path (Join-Path $env:WATCHROOM_PLUGIN_DIR 'requests.log') -Value $line
if (-not $request.targetExists) { New-Item -ItemType Directory -Force -Path $request.targetDirectory | Out-Null }
@{ success = $true; message = "Logged $($request.name)" } | ConvertTo-Json -Compress
```
