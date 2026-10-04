using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Media.Imaging;
using Watchroom.Core;

namespace Watchroom.Desktop;

public sealed class LibraryCardCollection : ObservableCollection<LibraryCardView>
{
    public void AddBatch(IEnumerable<LibraryCardView> cards)
    {
        foreach (var card in cards) Items.Add(card);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class LibraryCardView : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim imageWorkers = new(2);
    private static readonly Dictionary<string, WeakReference<BitmapSource>> images = new(StringComparer.OrdinalIgnoreCase);
    private int imageGeneration;
    private string? requestedPath;
    private Task loading = Task.CompletedTask;
    private string? remoteThumbnail;
    private (long Length, long Modified) posterVersion;
    private static (long Length, long Modified) Version(string? path)
    {
        if (path is null) return default;
        try { var file = new FileInfo(path); return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }
    public LibraryCard Card { get; private set; }
    public string DisplayTitle => Card.DisplayTitle;
    public string Caption => Card.Caption;
    public bool IsEpisode => Card.Level == "episode";
    public bool IsRemote { get; private set; }
    public string EpisodeMeta => (Card.Media.Season is { } season ? $"S{season:00} · " : "") +
        (Card.Media.Episode is { } episode ? $"E{episode:00}" + (Card.Media.EpisodeEnd > episode ? $"–{Card.Media.EpisodeEnd:00}" : "") : "Episode unknown") +
        (Card.Media.RuntimeMinutes is > 0 ? $" · {Card.Media.RuntimeMinutes} min" : "") + (Card.Media.IsVirtual ? " · Missing" : Card.Media.Available ? "" : " · Unavailable");
    public double Progress { get; private set; }
    public bool IsWatched { get; private set; }
    public string PlaybackLabel { get; private set; } = "";
    public BitmapSource? Poster { get; private set; }
    public string Key => Identity(Card);
    public static string Identity(LibraryCard card) => card.Level is "series" or "season"
        ? $"{card.Level}:{LibraryIdentity.ShowKey(card.Media)}:{card.Season}"
        : card.Level + ":" + card.Media.Id;
    public LibraryCardView(LibraryCard card) { Card = card; posterVersion = Version(card.Poster); }
    public static LibraryCardView FromSharedEntry(SharedLibraryEntry entry) => new(entry);
    private LibraryCardView(SharedLibraryEntry entry) : this(new LibraryCard(entry.Title, entry.Caption, null,
        new MediaItem(entry.Id, "", entry.Title, entry.Kind, Season: entry.Season, Episode: entry.Episode,
            Overview: entry.Overview, RuntimeMinutes: entry.RuntimeMinutes), entry.Level, entry.Season))
    {
        IsRemote = true;
        remoteThumbnail = entry.Thumbnail is { Length: > 0 and <= 5400 } data ? data : null;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    public void Update(LibraryCard card)
    {
        var previous = Card; Card = card;
        if (previous != card) Changed(nameof(Card));
        if (previous.DisplayTitle != card.DisplayTitle) Changed(nameof(DisplayTitle));
        if (previous.Caption != card.Caption) Changed(nameof(Caption));
        if (previous.Media.Season != card.Media.Season || previous.Media.Episode != card.Media.Episode || previous.Media.EpisodeEnd != card.Media.EpisodeEnd ||
            previous.Media.RuntimeMinutes != card.Media.RuntimeMinutes || previous.Media.IsVirtual != card.Media.IsVirtual || previous.Media.Available != card.Media.Available) Changed(nameof(EpisodeMeta));
        var version = Version(card.Poster);
        var changed = previous.Poster != card.Poster || posterVersion != version;
        if (changed) { imageGeneration++; requestedPath = null; }
        posterVersion = version;
        if (changed) _ = LoadPosterAsync();
    }
    public void SetPlayback(long positionMs, bool watched)
    {
        if (IsWatched != watched) { IsWatched = watched; Changed(nameof(IsWatched)); }
        var progress = watched ? 100 : Card.Media.RuntimeMinutes is > 0 ? Math.Clamp(positionMs / (Card.Media.RuntimeMinutes.Value * 600d), 0, 100) : 0;
        var label = watched ? "Watched" : positionMs >= 10000 ? $"Resume · {(int)(positionMs / 60000)}:{positionMs / 1000 % 60:00}" : "";
        if (Progress != progress) { Progress = progress; Changed(nameof(Progress)); }
        if (PlaybackLabel != label) { PlaybackLabel = label; Changed(nameof(PlaybackLabel)); }
    }
    public Task LoadPosterAsync()
    {
        if (IsRemote)
        {
            if (requestedPath is not null || remoteThumbnail is null) return loading;
            requestedPath = "remote";
            return loading = LoadRemotePosterAsync(remoteThumbnail);
        }
        if (requestedPath == Card.Poster && Card.Poster is not null) return loading;
        return loading = LoadPosterCoreAsync();
    }
    private async Task LoadRemotePosterAsync(string data)
    {
        var image = await Task.Run(async () =>
        {
            await imageWorkers.WaitAsync();
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(data));
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 160; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                return (BitmapSource?)bitmap;
            }
            catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or ArgumentException) { return null; }
            finally { imageWorkers.Release(); }
        });
        if (image is not null) { Poster = image; Changed(nameof(Poster)); }
    }
    private async Task LoadPosterCoreAsync()
    {
        if (Card.Poster is not { } path) { if (Poster is not null) { Poster = null; Changed(nameof(Poster)); } return; }
        if (requestedPath == path) return;
        requestedPath = path;
        var generation = ++imageGeneration; var version = posterVersion;
        var episode = IsEpisode;
        var cacheKey = $"{path}:{version.Length}:{version.Modified}:{episode}";
        var image = await Task.Run(async () =>
        {
            await imageWorkers.WaitAsync();
            try
            {
                lock (images) if (images.TryGetValue(cacheKey, out var cached) && cached.TryGetTarget(out var found)) return found;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (episode) bitmap.DecodePixelWidth = 640; else bitmap.DecodePixelHeight = 400;
                bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                lock (images)
                {
                    if (images.Count > 512) foreach (var key in images.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray()) images.Remove(key);
                    images[cacheKey] = new(bitmap);
                }
                return (BitmapSource?)bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException) { return null; }
            finally { imageWorkers.Release(); }
        });
        if (generation != imageGeneration || path != Card.Poster) return;
        // A failed refresh keeps the image already on screen; successful replacements
        // publish once, after decoding, without a null/placeholder frame in between.
        if (image is not null && !ReferenceEquals(Poster, image)) { Poster = image; Changed(nameof(Poster)); }
    }
}
