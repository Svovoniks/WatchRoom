using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

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
    public Func<Task<string?>>? RemoteArtworkLoader { get; set; }
    public bool IsGuestHovered { get; private set; }
    public void SetGuestHovered(bool value) { if (IsGuestHovered != value) { IsGuestHovered = value; Changed(nameof(IsGuestHovered)); } }
    // Picked for an action on several cards at once; see CardSelection.
    public bool IsSelected { get; private set; }
    public void SetSelected(bool value) { if (IsSelected != value) { IsSelected = value; Changed(nameof(IsSelected)); } }
    private (long Length, long Modified) posterVersion;
    public static (long Length, long Modified) Version(string? path)
    {
        if (path is null) return default;
        try { var file = new FileInfo(path); return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }
    public LibraryCard Card { get; private set; }
    public string DisplayTitle => Card.DisplayTitle;
    public string Caption => Card.Caption;
    public bool IsEpisode => Card.Level == "episode";
    public bool IsMovie => Card.Level == "movie";
    // Album covers are square, and a track card shows its album's cover whole.
    public bool IsMusic => Card.Media.IsMusic;
    public bool IsAlbum => IsMusic && Card.Level == "series";
    public bool HasPlaybackProgress => !IsRemote && PlaybackLabel.Length > 0;
    public bool IsRemote { get; private set; }
    public string? HomeSection { get; set; }
    public long HomePosition { get; set; }
    // Home shows a show's name on its next episode; the description lines are set by Home.
    public string HomeTitle => IsEpisode ? Card.Media.DisplayTitle : Card.DisplayTitle;
    public string HomeDetail { get; private set; } = "";
    public string HomeTag { get; private set; } = "";
    public Visibility HomeTagVisibility => HomeTag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public void SetHomeText(string detail, string tag = "")
    {
        if (HomeDetail != detail) { HomeDetail = detail; Changed(nameof(HomeDetail)); }
        if (HomeTag != tag) { HomeTag = tag; Changed(nameof(HomeTag)); Changed(nameof(HomeTagVisibility)); }
    }
    public string EpisodeMeta => IsMusic ? Card.Caption : (Card.Media.IsBonus ? Card.Media.BonusKind is null ? "Extra" : Card.Media.BonusLabel : (Card.Media.Season is { } season ? $"S{season:00} · " : "") +
        (Card.Media.Episode is { } episode ? $"E{episode:00}" + (Card.Media.EpisodeEnd > episode ? $"–{Card.Media.EpisodeEnd:00}" : "") : "Episode unknown")) +
        (Card.Media.RuntimeMinutes is > 0 ? $" · {Card.Media.RuntimeMinutes} min" : "") +
        (Card.Version is { Length: > 0 } version ? $" · {version} · {Card.Versions} versions" : "");
    // Shown in red beside the episode details and as a badge on the artwork.
    public string EpisodeStatus => IsRemote ? "" : Card.Status ?? (Card.Media.IsVirtual ? DateOnly.TryParse(Card.Media.AirDate, out var date) && date > DateOnly.FromDateTime(DateTime.UtcNow) ? "Upcoming" : "Missing" : Card.Media.Available ? "" : "Unavailable");
    public string EpisodeStatusSuffix => EpisodeStatus.Length > 0 ? " · " + EpisodeStatus : "";
    // Season cards carry the badge too when every episode of the season is a placeholder.
    public bool HasEpisodeStatus => (IsEpisode || Card.Status is not null) && EpisodeStatus.Length > 0;
    // A show or film with no file at all, added before its files were here: dimmed, with a dashed edge.
    public bool HasNoFiles => !IsRemote && Card.Level is "series" or "movie" && Card.Media.IsVirtual;
    // Episodes and seasons that are only placeholders dim; a show with some files keeps its artwork bright.
    public bool DimArtwork => HasEpisodeStatus && Card.Level is "episode" or "season";
    public bool IsUpcoming => EpisodeStatus == "Upcoming";
    public double Progress { get; private set; }
    public bool IsWatched { get; private set; }
    public string PlaybackLabel { get; private set; } = "";
    public BitmapSource? Poster { get; private set; }
    // Landscape stills fill the episode frame; portrait fallback posters fit inside it uncropped.
    public bool PosterFills => Poster is { PixelWidth: > 0 } image && image.PixelWidth >= image.PixelHeight;
    private void SetPoster(BitmapSource? image) { Poster = image; Changed(nameof(Poster)); Changed(nameof(PosterFills)); }
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
        HomeSection = entry.HomeSection; HomePosition = entry.PositionMs;
        SetPlayback(entry.PositionMs, false);
        if (entry.HomeSection == "continue" && entry.PositionMs < 10000) SetPlaybackLabel("Next episode");
        remoteThumbnail = entry.Thumbnail is { Length: > 0 and <= 5400 } data ? data : null;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    // Filtering passes a version read off the UI thread to avoid a file stat per card.
    public void Update(LibraryCard card, (long Length, long Modified)? posterFile = null)
    {
        var previous = Card; Card = card;
        if (previous != card) Changed(nameof(Card));
        if (previous.DisplayTitle != card.DisplayTitle) Changed(nameof(DisplayTitle));
        if (previous.Media.DisplayTitle != card.Media.DisplayTitle || previous.DisplayTitle != card.DisplayTitle) Changed(nameof(HomeTitle));
        if (previous.Caption != card.Caption) Changed(nameof(Caption));
        if (previous.Media.Season != card.Media.Season || previous.Media.Episode != card.Media.Episode || previous.Media.EpisodeEnd != card.Media.EpisodeEnd ||
            previous.Media.RuntimeMinutes != card.Media.RuntimeMinutes || previous.Media.IsVirtual != card.Media.IsVirtual || previous.Media.Available != card.Media.Available || previous.Status != card.Status)
        {
            Changed(nameof(EpisodeMeta)); Changed(nameof(EpisodeStatus)); Changed(nameof(EpisodeStatusSuffix)); Changed(nameof(HasEpisodeStatus));
            Changed(nameof(HasNoFiles)); Changed(nameof(DimArtwork)); Changed(nameof(IsUpcoming));
        }
        var version = posterFile ?? Version(card.Poster);
        var changed = previous.Poster != card.Poster || posterVersion != version;
        if (changed) { imageGeneration++; requestedPath = null; }
        posterVersion = version;
        if (changed) Reload();
    }
    public void SetPlayback(long positionMs, bool watched)
    {
        if (IsWatched != watched) { IsWatched = watched; Changed(nameof(IsWatched)); }
        var progress = watched ? 100 : Card.Media.RuntimeMinutes is > 0 ? Math.Clamp(positionMs / (Card.Media.RuntimeMinutes.Value * 600d), 0, 100) : 0;
        var label = watched ? "Watched" : positionMs >= 10000 ? $"Resume · {(int)(positionMs / 60000)}:{positionMs / 1000 % 60:00}" : "";
        if (Progress != progress) { Progress = progress; Changed(nameof(Progress)); }
        if (PlaybackLabel != label) { PlaybackLabel = label; Changed(nameof(PlaybackLabel)); Changed(nameof(HasPlaybackProgress)); }
    }
    public void SetPlaybackLabel(string label) { if (PlaybackLabel == label) return; PlaybackLabel = label; Changed(nameof(PlaybackLabel)); Changed(nameof(HasPlaybackProgress)); }
    // A decoded poster takes about half a megabyte, and as much again on the graphics card while it is drawn, so a
    // card holds its artwork only while an image on screen shows it (see PosterLease) and decodes it no larger than the
    // frame it is drawn in. Cards nothing has leased, such as queue rows, keep their artwork as long as they live.
    public static double PixelScale { get; set; } = 1;
    private Size? frame;
    private bool wholeFrame;
    private double requestedScale;
    private int shown, hides;
    private bool leased, asked;
    // The area the artwork fills, in device-independent units, or fits inside when "whole".
    public void ShowIn(double width, double height, bool whole = false)
    {
        var area = new Size(width, height);
        if (frame == area && wholeFrame == whole) return;
        frame = area; wholeFrame = whole;
        if (IsRemote || requestedPath is null) return;
        requestedPath = null; Reload();
    }
    // The library grid's artwork frames: see the card template in MainWindow.xaml.
    public void ShowInGrid() => ShowIn(IsEpisode ? 288 : 168, IsEpisode ? 162 : IsAlbum ? 168 : 248);
    // Loads again after the artwork, its frame or the display scale changed. A card that is off screen loads when it is
    // shown, and one whose artwork nothing has asked for yet, such as a grid card never scrolled to, waits to be asked.
    public void Reload() { if (shown > 0 || !leased && asked) _ = LoadPosterAsync(); }
    internal void Shown() { leased = true; hides++; shown++; _ = LoadPosterAsync(); }
    internal void Hidden() { if (--shown == 0) ReleaseLater(); }
    // For artwork loaded while the card was off screen, as when its details page borrows it.
    internal void ReleaseIfHidden() { if (leased && shown == 0) ReleaseLater(); }
    // Artwork that left the screen is kept for a short while, so going back a page or scrolling back shows it at once.
    private const int HiddenLimit = 96;
    internal static long HiddenForMs = 30000;
    private static readonly Queue<(LibraryCardView View, int Hide, long At)> hidden = new();
    private static System.Windows.Threading.DispatcherTimer? sweep;
    private void ReleaseLater()
    {
        hidden.Enqueue((this, ++hides, Environment.TickCount64));
        sweep ??= new(TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Background, (_, _) => Sweep(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        sweep.Start(); Sweep();
    }
    internal static void Sweep()
    {
        var now = Environment.TickCount64;
        while (hidden.Count > 0 && (hidden.Count > HiddenLimit || now - hidden.Peek().At >= HiddenForMs))
        {
            var (view, hide, _) = hidden.Dequeue();
            // Shown again since, or hidden again later: a newer entry decides.
            if (view.shown > 0 || view.hides != hide) continue;
            view.imageGeneration++; view.requestedPath = null;
            if (view.Poster is not null) { view.SetPoster(null); released++; }
        }
        if (hidden.Count == 0) sweep?.Stop();
        // A bitmap's pixels live outside the managed heap and are freed only once the collector finds its small managed
        // object, which an idle app never prompts it to do. So ask, once the screen has settled or enough has piled up.
        if (released > 0 && (hidden.Count == 0 || released >= 200)) { released = 0; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false); }
    }
    private static int released;
    public Task LoadPosterAsync()
    {
        asked = true;
        if (IsRemote)
        {
            if (requestedPath is not null || remoteThumbnail is null && RemoteArtworkLoader is null) return loading;
            requestedPath = "remote";
            return loading = LoadEnhancedPosterAsync();
        }
        if (requestedPath == Card.Poster && Card.Poster is not null && requestedScale == PixelScale) return loading;
        return loading = LoadPosterCoreAsync();
    }
    private async Task LoadEnhancedPosterAsync()
    {
        if (remoteThumbnail is not null) await LoadRemotePosterAsync(remoteThumbnail);
        if (RemoteArtworkLoader is { } load && await load() is { Length: > 0 } data) await LoadRemotePosterAsync(data, 600);
    }
    private async Task LoadRemotePosterAsync(string data, int width = 160)
    {
        var image = await Task.Run(async () =>
        {
            await imageWorkers.WaitAsync();
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(data));
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                return (BitmapSource?)bitmap;
            }
            catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or ArgumentException) { return null; }
            finally { imageWorkers.Release(); }
        });
        // Released while it decoded: the card left the screen.
        if (image is not null && requestedPath is not null) SetPoster(image);
    }
    private async Task LoadPosterCoreAsync()
    {
        if (Card.Poster is not { } path) { if (Poster is not null) SetPoster(null); return; }
        var scale = PixelScale;
        if (requestedPath == path && requestedScale == scale) return;
        requestedPath = path; requestedScale = scale;
        var generation = ++imageGeneration; var version = posterVersion;
        var episode = IsEpisode; var area = frame; var whole = wholeFrame;
        var cacheKey = $"{path}:{version.Length}:{version.Modified}:{episode}:{area}:{whole}:{scale}";
        // Artwork another card still holds, or that has not been collected since this one let go of it, shows at once.
        lock (images) if (images.TryGetValue(cacheKey, out var kept) && kept.TryGetTarget(out var ready))
            { if (!ReferenceEquals(Poster, ready)) SetPoster(ready); return; }
        var image = await Task.Run(async () =>
        {
            await imageWorkers.WaitAsync();
            try
            {
                lock (images) if (images.TryGetValue(cacheKey, out var cached) && cached.TryGetTarget(out var found)) return found;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (area is { Width: > 0, Height: > 0 } box)
                {
                    // The file's own size says which side of the frame the artwork reaches first.
                    var source = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    double width = source.PixelWidth, height = source.PixelHeight; stream.Position = 0;
                    double across = box.Width * scale / width, down = box.Height * scale / height;
                    // Never larger than the file, nor than artwork without a frame is decoded.
                    var ratio = Math.Min(Math.Min(whole ? Math.Min(across, down) : Math.Max(across, down), episode ? 640 / width : 400 / height), 1);
                    bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Ceiling(width * ratio));
                }
                else if (episode) bitmap.DecodePixelWidth = 640; else bitmap.DecodePixelHeight = 400;
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
        if (image is not null && !ReferenceEquals(Poster, image)) SetPoster(image);
    }
}
