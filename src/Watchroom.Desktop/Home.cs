using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private int homeSeed = Random.Shared.Next();
    private LibraryCardView? homeMystery;
    private long homeGeneration;
    private bool homeShowsGuestLibrary;
    private async void RefreshHome()
    {
        if (HomeContinue is null || library is null || currentPage != "Home") return;
        homeShowsGuestLibrary = GuestLibrary;
        System.Windows.Automation.AutomationProperties.SetName(HomePage, GuestLibrary ? "Home · " + HostLibraryName : "Home");
        if (GuestLibrary) { RenderGuestHome(); return; }
        if (!libraryLoaded && items.Count == 0)
        {
            HomeStatus.Text = "Loading your library…";
            RenderHome([]); return;
        }
        var generation = ++homeGeneration;
        var source = items.ToArray(); var seed = homeSeed;
        List<HomeLibraryCard> entries;
        try { entries = await Task.Run(() => LibraryHome.Browse(source, library.PlaybackSettings(), seed)); }
        catch (Exception ex)
        {
            if (generation == homeGeneration && currentPage == "Home" && !closing) HomeStatus.Text = "Could not load Home: " + ex.Message;
            return;
        }
        if (generation != homeGeneration || currentPage != "Home" || GuestLibrary || closing) return;
        var existing = HomeContinue.Items.Cast<LibraryCardView>().Concat(HomeSuggestions.Items.Cast<LibraryCardView>())
            .Concat(homeMystery is null ? [] : new[] { homeMystery }).Where(view => !view.IsRemote)
            .ToDictionary(view => (view.HomeSection, view.Key));
        var views = entries.Select(entry =>
        {
            var key = (entry.Section, LibraryCardView.Identity(entry.Card));
            var view = existing.GetValueOrDefault(key) ?? new LibraryCardView(entry.Card) { HomeSection = entry.Section };
            view.Update(entry.Card);
            view.SetPlayback(entry.PositionMs, false);
            if (entry.Section == "continue" && entry.PositionMs < 10000) view.SetPlaybackLabel("Next episode");
            return view;
        }).ToArray();
        HomeStatus.Text = items.Count == 0 ? "Add a folder to find something to watch." : "";
        RenderHome(views);
    }
    private void RenderHome(IEnumerable<LibraryCardView> source)
    {
        var views = source.ToArray();
        var continuing = views.Where(view => view.HomeSection == "continue").ToArray();
        var suggestions = views.Where(view => view.HomeSection == "suggestions").ToArray();
        if (HomeContinue.ItemsSource is not LibraryCardView[] previousContinue || !previousContinue.SequenceEqual(continuing)) HomeContinue.ItemsSource = continuing;
        if (HomeSuggestions.ItemsSource is not LibraryCardView[] previousSuggestions || !previousSuggestions.SequenceEqual(suggestions)) HomeSuggestions.ItemsSource = suggestions;
        homeMystery = views.FirstOrDefault(view => view.HomeSection == "mystery");
        HomeMystery.IsEnabled = homeMystery is not null;
        HomeContinueEmpty.Visibility = HomeContinue.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeSuggestionsEmpty.Visibility = HomeSuggestions.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RenderGuestHome()
    {
        ++homeGeneration;
        homeShowsGuestLibrary = true;
        System.Windows.Automation.AutomationProperties.SetName(HomePage, "Home · " + HostLibraryName);
        HomeStatus.Text = GuestLibraryStatus.Text == HostLibraryName ? "" : GuestLibraryStatus.Text;
        RenderHome(OwnsLibrary && guestBrowse.Home ? guestLibraryCards : []);
    }
    private void RefreshHomePicks(object sender, RoutedEventArgs e)
    {
        homeSeed = Random.Shared.Next();
        if (GuestLibrary)
        {
            guestBrowse = new(Home: true, Seed: homeSeed);
            guestLibraryNotice = null; guestCurrentBrowse = null; guestLibraryCards.Clear();
            RequestHostLibrary(); RenderGuestHome();
        }
        else RefreshHome();
    }
    private async void HomeArtworkLoaded(object sender, RoutedEventArgs e)
    {
        if (((Image)sender).DataContext is LibraryCardView view) await view.LoadPosterAsync();
    }
    private void OpenHomeCard(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LibraryCardView view }) OpenHomeTitle(view);
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
    private void PickMystery(object sender, RoutedEventArgs e)
    {
        if (homeMystery is { } view) OpenHomeTitle(view);
    }
}
