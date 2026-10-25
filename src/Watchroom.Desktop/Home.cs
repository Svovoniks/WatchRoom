using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Watchroom.Core;

namespace Watchroom.Desktop;

// One category of Home recommendations: a spotlight title followed by poster tiles.
public sealed class HomeRowView(string kind, string label, int category) : INotifyPropertyChanged
{
    public string Kind { get; } = kind;
    public string Label { get; } = label;
    public int Category { get; } = category;
    public string OpenLabel => $"Open {Label} in library";
    public string ReshuffleLabel => $"New {Label} suggestions";
    public LibraryCardView? Spotlight { get; private set; }
    public LibraryCardView[] Tiles { get; private set; } = [];
    public string CountText { get; private set; } = "";
    public Visibility Visibility => Spotlight is null ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    // Keep the existing tile array when its members are unchanged so loaded posters stay put.
    public void Update(LibraryCardView? spotlight, LibraryCardView[] tiles, string count)
    {
        if (!ReferenceEquals(Spotlight, spotlight)) { Spotlight = spotlight; Changed(nameof(Spotlight)); Changed(nameof(Visibility)); }
        if (!Tiles.SequenceEqual(tiles)) { Tiles = tiles; Changed(nameof(Tiles)); }
        if (CountText != count) { CountText = count; Changed(nameof(CountText)); }
    }
}

public partial class MainWindow
{
    private int homeSeed = Random.Shared.Next();
    private readonly Dictionary<string, int> homeRowSeeds = [];
    private LibraryCardView? homeHero, homeMystery, homeMysteryFallback;
    private LibraryCardView[] homeContinueAll = [];
    private HomeTitle[] homeUnseen = [];
    private Dictionary<string, HomeTitle> homeFacts = [];
    private bool homeStripExpanded;
    private string? mysteryKind;
    private MysteryLength mysteryLength;
    private int mysteryDraw = Random.Shared.Next();
    private HomeRowView[]? homeRows;
    private long homeGeneration;
    private bool homeShowsGuestLibrary;
    private HomeRowView[] HomeRowViews => homeRows ??= [new("Anime", "Anime", 3), new("Show", "Shows", 2), new("Movie", "Movies", 1)];
    private async void RefreshHome()
    {
        if (HomeContinue is null || library is null || currentPage != "Home") return;
        homeShowsGuestLibrary = GuestLibrary;
        System.Windows.Automation.AutomationProperties.SetName(HomePage, GuestLibrary ? "Home · " + HostLibraryName : "Home");
        if (GuestLibrary) { RenderGuestHome(); return; }
        if (!libraryLoaded && items.Count == 0)
        {
            HomeStatus.Text = "Loading your library…";
            homeUnseen = []; RenderHome([]); return;
        }
        var generation = ++homeGeneration;
        var source = items.ToArray(); var seed = homeSeed; var rowSeeds = new Dictionary<string, int>(homeRowSeeds);
        HomeOverview overview;
        try { overview = await Task.Run(() => LibraryHome.Overview(source, library.PlaybackSettings(), seed, rowSeeds)); }
        catch (Exception ex)
        {
            if (generation == homeGeneration && currentPage == "Home" && !closing) HomeStatus.Text = "Could not load Home: " + ex.Message;
            return;
        }
        if (generation != homeGeneration || currentPage != "Home" || GuestLibrary || closing) return;
        var existing = HomeViews().Where(view => !view.IsRemote).GroupBy(view => (view.HomeSection, view.Key))
            .ToDictionary(group => group.Key, group => group.First());
        var views = overview.Entries.Select(entry =>
        {
            var key = (entry.Section, LibraryCardView.Identity(entry.Card));
            var view = existing.GetValueOrDefault(key) ?? new LibraryCardView(entry.Card) { HomeSection = entry.Section };
            view.Update(entry.Card);
            view.HomePosition = entry.PositionMs;
            view.SetPlayback(entry.PositionMs, false);
            if (entry.Section == "continue" && entry.PositionMs < 10000) view.SetPlaybackLabel("Next episode");
            return view;
        }).ToArray();
        homeUnseen = overview.Unseen;
        homeFacts = overview.Unseen.GroupBy(title => LibraryCardView.Identity(title.Card)).ToDictionary(group => group.Key, group => group.First());
        HomeStatus.Text = items.Count == 0 ? "Add a folder to find something to watch." : "";
        RenderHome(views);
    }
    private IEnumerable<LibraryCardView> HomeViews() =>
        new[] { homeHero, homeMystery, homeMysteryFallback }.OfType<LibraryCardView>().Concat(homeContinueAll)
            .Concat(HomeRowViews.SelectMany(row => row.Tiles.Prepend(row.Spotlight).OfType<LibraryCardView>()));
    private void RenderHome(IEnumerable<LibraryCardView> source)
    {
        var views = source.ToArray();
        var continuing = views.Where(view => view.HomeSection == "continue").ToArray();
        foreach (var view in continuing) view.SetHomeText(ContinueDetail(view));
        homeHero = continuing.FirstOrDefault();
        HomeHero.DataContext = homeHero;
        HomeHero.Visibility = homeHero is null ? Visibility.Collapsed : Visibility.Visible;
        HomeIntro.Visibility = homeHero is null ? Visibility.Visible : Visibility.Collapsed;
        if (homeHero is not null)
        {
            HomeHeroKicker.Text = "Continue watching · " + homeHero.HomeDetail;
            HomeHeroTitle.Text = homeHero.HomeTitle;
            HomeHeroOverview.Text = homeHero.Card.Media.DetailOverview ?? "";
            HomeHeroOverview.Visibility = HomeHeroOverview.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeHeroResume.Content = homeHero.HomePosition >= 10000 ? "Resume" : homeHero.IsEpisode ? "Play next episode" : "Play";
            System.Windows.Automation.AutomationProperties.SetName(HomeHeroResume, HomeHeroResume.Content + " " + homeHero.HomeTitle);
            _ = homeHero.LoadPosterAsync();
        }
        homeContinueAll = continuing.Skip(1).ToArray();
        RenderHomeStrip();
        var suggestions = views.Where(view => view.HomeSection == "suggestions").ToArray();
        foreach (var view in suggestions) view.SetHomeText(TitleFacts(view), view.Card.Media.Genres?.FirstOrDefault(genre => !string.IsNullOrWhiteSpace(genre)) ?? "");
        if (HomeRows.ItemsSource is null) HomeRows.ItemsSource = HomeRowViews;
        foreach (var row in HomeRowViews)
        {
            var members = suggestions.Where(view => view.Card.Media.Kind == row.Kind).ToArray();
            var unseen = homeUnseen.Count(title => title.Card.Media.Kind == row.Kind);
            row.Update(members.FirstOrDefault(), members.Skip(1).ToArray(), GuestLibrary || unseen == 0 ? "" : $"{unseen} not started");
            // The spotlight card is reused when its title changes, so its image's Loaded event does not fire again.
            if (row.Spotlight is { } spotlight) _ = spotlight.LoadPosterAsync();
        }
        HomeSuggestionsEmpty.Visibility = suggestions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        homeMysteryFallback = views.FirstOrDefault(view => view.HomeSection == "mystery");
        RenderMystery(redraw: false);
    }
    private void RenderGuestHome()
    {
        ++homeGeneration;
        homeShowsGuestLibrary = true;
        homeUnseen = []; homeFacts = [];
        System.Windows.Automation.AutomationProperties.SetName(HomePage, "Home · " + HostLibraryName);
        HomeStatus.Text = GuestLibraryStatus.Text == HostLibraryName ? "" : GuestLibraryStatus.Text;
        RenderHome(OwnsLibrary && guestBrowse.Home ? guestLibraryCards : []);
    }
    private static string Duration(int minutes) => minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} m" : $"{minutes} min";
    private static string ContinueDetail(LibraryCardView view)
    {
        var media = view.Card.Media;
        var episode = view.IsEpisode && media.Episode is { } number ? (media.Season is { } season ? $"S{season} E{number}" : $"Episode {number}") : null;
        var remaining = media.RuntimeMinutes is > 0 and var runtime && view.HomePosition >= 10000
            ? (int)Math.Ceiling(Math.Max(0, runtime * 60000L - view.HomePosition) / 60000d) : (int?)null;
        var state = view.HomePosition < 10000 ? episode is null ? "Not started" : "Up next" : remaining is { } left ? Duration(Math.Max(1, left)) + " left" : "In progress";
        return episode is null ? state : episode + " · " + state;
    }
    private string TitleFacts(LibraryCardView view)
    {
        var media = view.Card.Media; var parts = new List<string>();
        if (view.Card.Level is "series" or "season")
            parts.Add(homeFacts.GetValueOrDefault(view.Key) is { Episodes: > 0 } facts ? facts.Episodes == 1 ? "1 episode" : $"{facts.Episodes} episodes" : media.Kind == "Anime" ? "Anime" : "Series");
        else if (media.RuntimeMinutes is > 0 and var runtime) parts.Add(Duration(runtime));
        if (media.Year is { } year) parts.Add(year.ToString());
        return string.Join(" · ", parts);
    }
    private void RenderHomeStrip()
    {
        var columns = HomeStrip.Tag as int? ?? 4;
        var shown = homeStripExpanded ? homeContinueAll : homeContinueAll.Take(columns).ToArray();
        if (HomeContinue.ItemsSource is not LibraryCardView[] previous || !previous.SequenceEqual(shown)) HomeContinue.ItemsSource = shown;
        HomeStrip.Visibility = homeContinueAll.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeStripCount.Text = homeContinueAll.Length == 1 ? "1 title" : $"{homeContinueAll.Length} titles";
        HomeStripMore.Visibility = homeContinueAll.Length > columns ? Visibility.Visible : Visibility.Collapsed;
        HomeStripMore.Content = homeStripExpanded ? "Show less" : $"All {homeContinueAll.Length}";
    }
    private void HomeStripResized(object sender, SizeChangedEventArgs e)
    {
        var columns = Math.Clamp((int)((e.NewSize.Width - 150) / 210), 1, 4);
        if (HomeStrip.Tag as int? == columns) return;
        HomeStrip.Tag = columns; RenderHomeStrip();
    }
    private double homeStripCollapsedHeight;
    // The bar grows to its new height while the extra titles fade in, and shrinks before they leave.
    private void ToggleHomeStrip(object sender, RoutedEventArgs e)
    {
        var from = HomeStrip.ActualHeight;
        homeStripExpanded = !homeStripExpanded;
        HomeStrip.BeginAnimation(HeightProperty, null);
        if (!SystemParameters.ClientAreaAnimation || !HomeStrip.IsLoaded) { RenderHomeStrip(); return; }
        HomeStrip.ClipToBounds = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        if (homeStripExpanded)
        {
            homeStripCollapsedHeight = from;
            RenderHomeStrip(); HomeStrip.UpdateLayout();
            var grow = new DoubleAnimation(from, HomeStrip.ActualHeight, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };
            grow.Completed += (_, _) => { if (homeStripExpanded) HomeStrip.BeginAnimation(HeightProperty, null); };
            HomeStrip.BeginAnimation(HeightProperty, grow);
        }
        else
        {
            HomeStripMore.Content = $"All {homeContinueAll.Length}";
            var shrink = new DoubleAnimation(from, homeStripCollapsedHeight > 0 ? homeStripCollapsedHeight : from, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
            shrink.Completed += (_, _) => { if (homeStripExpanded) return; RenderHomeStrip(); HomeStrip.BeginAnimation(HeightProperty, null); };
            HomeStrip.BeginAnimation(HeightProperty, shrink);
        }
    }
    private Action? mysteryReveal;
    private void RenderMystery(bool redraw, bool animate = false)
    {
        var local = !GuestLibrary && homeUnseen.Length > 0;
        HomeMysteryFilters.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        LibraryCardView? pick = homeMysteryFallback; var note = "";
        if (local)
        {
            var current = homeMystery is { HomeSection: "draw" } shown ? LibraryIdentity.ShowKey(shown.Card.Media) : null;
            var title = redraw ? null : homeUnseen.FirstOrDefault(title => LibraryIdentity.ShowKey(title.Card.Media) == current && LibraryHome.Matches(title, mysteryKind, mysteryLength));
            title ??= LibraryHome.Draw(homeUnseen, mysteryKind, mysteryLength, mysteryDraw, redraw ? current : null);
            pick = title is null ? null : homeMystery is { HomeSection: "draw" } same && same.Key == LibraryCardView.Identity(title.Card) ? same : new LibraryCardView(title.Card) { HomeSection = "draw" };
            if (title is not null) { pick!.Update(title.Card); note = "Not started yet"; }
            HomeMysteryDraw.IsEnabled = homeUnseen.Count(candidate => LibraryHome.Matches(candidate, mysteryKind, mysteryLength)) > 1;
        }
        else HomeMysteryDraw.IsEnabled = pick is not null;
        var changed = !ReferenceEquals(homeMystery, pick);
        homeMystery = pick;
        HomeMysteryPlay.DataContext = pick;
        HomeMysteryPlay.IsEnabled = pick is not null;
        var heading = pick?.DisplayTitle ?? (local ? "Nothing matches" : "");
        var meta = pick is null ? local ? "Try another type or length." : "" :
            string.Join(" · ", new[] { pick.Card.Media.Kind, TitleFacts(pick), note }.Where(part => part.Length > 0));
        if (pick is not null) _ = pick.LoadPosterAsync();
        void Reveal()
        {
            HomeMysteryCard.DataContext = pick;
            HomeMysteryCard.Visibility = pick is null ? Visibility.Hidden : Visibility.Visible;
            HomeMysteryCard.IsEnabled = pick is not null;
            HomeMysteryTitle.Text = heading; HomeMysteryMeta.Text = meta;
        }
        if (animate && changed) FlipMysteryCard(Reveal); else { mysteryReveal = null; Reveal(); }
    }
    // Turns the drawn card over: it folds to its edge, swaps title, and opens again.
    private void FlipMysteryCard(Action reveal)
    {
        if (!SystemParameters.ClientAreaAnimation || !HomeMysteryPanel.IsVisible) { mysteryReveal = null; reveal(); return; }
        if (HomeMysteryCard.RenderTransform is not ScaleTransform scale)
        {
            HomeMysteryCard.RenderTransformOrigin = new(0.5, 0.5);
            HomeMysteryCard.RenderTransform = scale = new ScaleTransform();
        }
        var flipping = mysteryReveal is not null;
        mysteryReveal = reveal;
        if (flipping) return;
        var text = new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(130));
        HomeMysteryTitle.BeginAnimation(OpacityProperty, text); HomeMysteryMeta.BeginAnimation(OpacityProperty, text);
        FanDeck(gather: true);
        var fold = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fold.Completed += (_, _) =>
        {
            var show = mysteryReveal; mysteryReveal = null; show?.Invoke();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
                { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
            FanDeck(gather: false);
            var restore = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220));
            HomeMysteryTitle.BeginAnimation(OpacityProperty, restore); HomeMysteryMeta.BeginAnimation(OpacityProperty, restore);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, fold);
    }
    private void FanDeck(bool gather)
    {
        IEasingFunction ease = gather ? new QuadraticEase { EasingMode = EasingMode.EaseIn } : new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(gather ? 170 : 420);
        void Move(Animatable target, DependencyProperty property, double to) =>
            target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = ease });
        Move(MysteryLeftTilt, RotateTransform.AngleProperty, gather ? -3 : -11);
        Move(MysteryRightTilt, RotateTransform.AngleProperty, gather ? 3 : 11);
        Move(MysteryLeftShift, TranslateTransform.XProperty, gather ? -22 : -78);
        Move(MysteryRightShift, TranslateTransform.XProperty, gather ? 22 : 78);
        Move(MysteryLeftShift, TranslateTransform.YProperty, gather ? 2 : 8);
        Move(MysteryRightShift, TranslateTransform.YProperty, gather ? 2 : 8);
    }
    private void MysteryFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } button || HomeMysteryCard is null) return;
        if (button.GroupName == "MysteryKind") mysteryKind = tag.Length == 0 ? null : tag;
        else mysteryLength = Enum.Parse<MysteryLength>(tag);
        RenderMystery(redraw: false, animate: true);
    }
    private void DrawMystery(object sender, RoutedEventArgs e)
    {
        if (GuestLibrary || homeUnseen.Length == 0) { RefreshHomePicks(sender, e); return; }
        mysteryDraw = Random.Shared.Next(); RenderMystery(redraw: true, animate: true);
    }
    private void RefreshHomePicks(object sender, RoutedEventArgs e)
    {
        homeSeed = Random.Shared.Next(); homeRowSeeds.Clear();
        if (GuestLibrary)
        {
            guestBrowse = new(Home: true, Seed: homeSeed);
            guestLibraryNotice = null; guestCurrentBrowse = null; guestLibraryCards.Clear();
            RequestHostLibrary(); RenderGuestHome();
        }
        else RefreshHome();
    }
    private void ReshuffleHomeRow(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HomeRowView row }) return;
        if (GuestLibrary) { RefreshHomePicks(sender, e); return; }
        homeRowSeeds[row.Kind] = Random.Shared.Next(); RefreshHome();
    }
    private void OpenHomeCategory(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HomeRowView row }) NavigateLibrary(row.Category);
    }
    private async void HomeArtworkLoaded(object sender, RoutedEventArgs e)
    {
        if (((Image)sender).DataContext is LibraryCardView view) await view.LoadPosterAsync();
    }
    // Artwork that arrives after its card is on screen fades in; artwork present at creation rides the card entrance.
    private void HomeArtworkUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is not Image { IsLoaded: true, Source: not null } image || e.Property != Image.SourceProperty || !SystemParameters.ClientAreaAnimation) return;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    // New cards drift in, staggered along their row.
    private void HomeItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement item) return;
        var container = VisualTreeHelper.GetParent(item) as DependencyObject;
        var owner = container is null ? null : ItemsControl.ItemsControlFromItemContainer(container);
        var index = owner?.ItemContainerGenerator.IndexFromContainer(container!) ?? -1;
        var pill = owner == HomeContinue;
        Entrance(item, TimeSpan.FromMilliseconds(Math.Clamp(index + (pill ? 0 : 1), 0, 9) * 45), pill ? 0 : 22, pill ? 8 : 0);
    }
    private void HomeSpotlightChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsLoaded: true } card && e.NewValue is not null) Entrance(card, TimeSpan.Zero, 22, 0);
    }
    private static void Entrance(FrameworkElement item, TimeSpan delay, double fromX, double fromY)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(420);
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimationUsingKeyFrames Track(double from, double to) => new()
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)), new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)),
                new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration), ease) }
        };
        if (item.RenderTransform is not TranslateTransform shift) item.RenderTransform = shift = new TranslateTransform();
        var fade = Track(0, 1);
        fade.Completed += (_, _) => { item.BeginAnimation(OpacityProperty, null); shift.BeginAnimation(TranslateTransform.XProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null); };
        item.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.XProperty, Track(fromX, 0));
        shift.BeginAnimation(TranslateTransform.YProperty, Track(fromY, 0));
    }
    private void OpenHomeCard(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LibraryCardView view }) OpenHomeTitle(view);
    }
    // Plays straight from Home: a series starts at its first episode. Guests go through
    // the room's details page, which asks the host to start playback.
    private void PlayHomeCard(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: LibraryCardView view }) return;
        var item = GuestLibrary ? null : view.Card.Level is "series" or "season" ? FirstEpisode(view.Card.Media) : view.Card.Media;
        if (item is null) { OpenHomeTitle(view); return; }
        Select(item); PlayLocal(sender, e);
    }
    private MediaItem? FirstEpisode(MediaItem show)
    {
        var key = LibraryIdentity.ShowKey(show);
        return LibraryCatalog.PreferredFiles(items.Where(item => item.Available && !item.IsExtra && !item.IsVirtual && LibraryIdentity.ShowKey(item) == key), PreferredVersion(key))
            .OrderBy(item => item.Season is null or 0 ? int.MaxValue : item.Season).ThenBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Id).FirstOrDefault();
    }
    private void OpenHomeTitle(LibraryCardView view)
    {
        var card = view.Card;
        if (GuestLibrary)
        {
            if (!OwnsLibrary) return;
            if (card.Level == "series")
            {
                guestParentTitle = card.DisplayTitle;
                guestBrowse = new(Category: card.Media.Kind == "Anime" ? 3 : 2, ParentId: card.Media.Id);
                SearchBox.Clear(); ShowPage("Library");
            }
            else
            {
                guestBrowse = new(Category: card.Media.Kind == "Anime" ? 3 : card.Media.Kind == "Show" ? 2 : 1);
                OpenGuestVideo(card, view.HomeSection == "continue");
            }
        }
        else if (card.Level == "series") NavigateLibrary(CategoryFor(card.Media), LibraryIdentity.ShowKey(card.Media), card.Media.Kind);
        else Select(card.Media);
    }
}
