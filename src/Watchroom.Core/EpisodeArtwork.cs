using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

/// <summary>Lazy artwork upgrades for a visited season; never scans the entire library.</summary>
public sealed class EpisodeArtwork
{
    private readonly SemaphoreSlim workers = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<MetadataSeason?>>> seasons = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> mazeImages = new();

    public async Task<MediaItem> ImproveAsync(MediaItem item, string directory, string? token, string? ffmpeg, CancellationToken ct, MetadataOptions? options = null)
    {
        options ??= new();
        if (item.Series is null || item.IsVirtual || item.MetadataLocked || LibraryIdentity.Locked(item, "EpisodePoster")) return item;
        await workers.WaitAsync(ct);
        try
        {
            // The exact matched episode URL also handles DVD/absolute numbering without guessing.
            var source = Regex.Match(item.EpisodeSource ?? "", @"^https://www\.themoviedb\.org/tv/(\d+)/season/(\d+)/episode/(\d+)$");
            if (source.Success && options.ProviderOrder.Contains("tmdb") && !MetadataTitles.IsUnverifiedCompilation(item) && !string.IsNullOrWhiteSpace(token) &&
                (!ArtworkCache.IsUsable(item.EpisodePoster) || item.EpisodePosterQualityPath != item.EpisodePoster))
            {
                try
                {
                    using var metadata = new MetadataClient(token, options: options);
                    var show = int.Parse(source.Groups[1].Value); var season = int.Parse(source.Groups[2].Value); var episode = int.Parse(source.Groups[3].Value);
                    var details = await seasons.GetOrAdd($"{LibraryIdentity.Hash(token)}:{show}:{season}", _ => new(() => metadata.SeasonAsync(show, season, ct))).Value;
                    if (details?.Episodes.FirstOrDefault(x => x.Number == episode)?.Image is { } image)
                    {
                        var path = await metadata.CacheImageAsync(image, $"tv-{show}-s{season}-e{episode}", Path.Combine(directory, "posters"), ct, imageSize: "w780");
                        if (ArtworkCache.IsUsable(path)) return item with { EpisodePoster = path, EpisodePosterQualityPath = path };
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }
            }
            var maze = Regex.Match(item.EpisodeSource ?? "", @"^https://www\.tvmaze\.com/shows/(\d+)(?:/.*)?$");
            if (maze.Success && options.ProviderOrder.Contains("tvmaze") && item.NumberingOrder == "aired" && item.Season is { } mazeSeason && item.Episode is { } mazeEpisode &&
                !MetadataTitles.IsUnverifiedCompilation(item) && (!ArtworkCache.IsUsable(item.EpisodePoster) || item.EpisodePosterQualityPath != item.EpisodePoster))
            {
                try
                {
                    var url = await mazeImages.GetOrAdd($"{maze.Groups[1].Value}:{mazeSeason}:{mazeEpisode}", _ => new(() => MazeImage(maze.Groups[1].Value, mazeSeason, mazeEpisode, ct))).Value;
                    if (url is not null)
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                        var path = await AutomaticArtwork.Download(http, url, directory, ct);
                        if (ArtworkCache.IsUsable(path)) return item with { EpisodePoster = path, EpisodePosterQualityPath = path };
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }
            }
            if (ArtworkCache.IsUsable(item.EpisodePoster) || ArtworkCache.IsUsable(item.GeneratedEpisodePoster) || !item.Available || !File.Exists(item.Path)) return item;
            var thumbnail = await GenerateAsync(item, directory, ffmpeg, ct);
            return thumbnail is null ? item : item with { GeneratedEpisodePoster = thumbnail };
        }
        finally { workers.Release(); }
    }

    private static async Task<string?> MazeImage(string show, int season, int episode, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var json = System.Text.Json.JsonDocument.Parse(await MetadataHttp.GetString(http, $"https://api.tvmaze.com/shows/{show}/episodebynumber?season={season}&number={episode}", ct));
        return json.RootElement.TryGetProperty("image", out var image) && image.ValueKind == System.Text.Json.JsonValueKind.Object &&
            image.TryGetProperty("original", out var original) ? original.GetString() : null;
    }

    public static async Task<string?> GenerateAsync(MediaItem item, string directory, string? ffmpeg, CancellationToken ct)
    {
        var file = new FileInfo(item.Path);
        if (!file.Exists) return null;
        var cache = Path.Combine(directory, "episode-thumbnails"); Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, LibraryIdentity.Hash($"{item.Path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}") + ".jpg");
        if (ArtworkCache.IsUsable(path)) return path;
        // Skip opening credits where possible. A second early seek handles short/unknown videos.
        foreach (var seconds in new[] { item.RuntimeMinutes is > 0 ? Math.Min(600, item.RuntimeMinutes.Value * 15d) : 180, 1d })
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".jpg";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = new Process { StartInfo = new ProcessStartInfo(string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", item.Path, "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "3", "-y", temporary }) process.StartInfo.ArgumentList.Add(arg);
            try
            {
                process.Start();
                var errors = process.StandardError.ReadToEndAsync(timeout.Token); var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(errors, output);
                if (process.ExitCode == 0 && ArtworkCache.IsUsable(temporary)) { File.Move(temporary, path, true); return path; }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                if (ex is System.ComponentModel.Win32Exception) return null;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return null;
    }
}
