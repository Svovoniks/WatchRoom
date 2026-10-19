using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using Watchroom.Core;
using Watchroom.Desktop;

// The end-of-episode action follows the playing video's current metadata, and
// a loaded queue continues only the video it started.
static class EpisodeEndChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var directory = Path.Combine(Path.GetTempPath(), "wr-episodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new[] { "Show S01E01.avi", "Show S01E02.avi", "Queue movie.avi", "Queue movie two.avi" }.Select(name => Path.Combine(directory, name)).ToArray();
        foreach (var path in paths) VideoFixture.Write(path, 20);
        var first = new MediaItem("episode-end-1", paths[0], "Fixture Show", "Show", Series: "Fixture Show", Season: 1, Episode: 1);
        var second = new MediaItem("episode-end-2", paths[1], "Fixture Show", "Show", Series: "Fixture Show", Season: 1, Episode: 2);
        var movie = new MediaItem("episode-end-movie", paths[2], "Queue movie", "Movie");
        var movieTwo = new MediaItem("episode-end-movie-two", paths[3], "Queue movie two", "Movie");
        var items = Field<List<MediaItem>>("items"); var originalItems = items;
        var queue = Field<List<MediaItem>>("queue"); var previousQueue = queue.ToArray(); var previousPosition = Field<int>("queuePosition");
        var button = (Button)window.FindName("EpisodeEndButton");
        try
        {
            var library = items.Concat([first, second, movie, movieTwo]).ToList(); Set("items", library);
            queue.Clear(); queue.AddRange([movie, movieTwo]); Set("queuePosition", -1);
            await (Task)Call("PlayLocalItem", first)!;
            Call("UpdateSessionControls");
            check(button.Visibility == Visibility.Visible && (string)button.Content == "Next episode →",
                "an episode started from the library shows Next episode while a queue is loaded");
            check(!(bool)Call("ContinuesQueue", first)!, "a loaded queue does not replace the next episode of a library video");

            // Metadata matching assigns a show identity after playback started.
            var matched = library.Select(item => !item.Id.StartsWith("episode-end-") || item.Series is null ? item : item with { ShowId = "show-matched", Series = "Fixture Show (2026)" }).ToList();
            Set("items", matched); Call("RefreshPlayingItem"); Call("UpdateSessionControls");
            check(Field<MediaItem>("playingItem").ShowId == "show-matched" && (string)button.Content == "Next episode →",
                "the playing episode follows library metadata updates and keeps its next episode");

            Set("queuePosition", 0); queue[0] = first;
            check((bool)Call("ContinuesQueue", first)!, "a video started from the queue continues the queue");
            Set("queuePosition", 1); queue[1] = first;
            check(!(bool)Call("ContinuesQueue", first)!, "at the end of the queue an episode offers its next episode instead");

            var library2 = Field<LibraryStore>("library");
            library2.Setting("position:" + movie.Id, "65000");
            var movieCard = new LibraryCardView(new("Queue movie", "", null, movie, "movie"));
            Call("UpdateCardPlayback", movieCard);
            check(movieCard.HasPlaybackProgress && movieCard.PlaybackLabel == "Resume · 1:05",
                "movie cards keep their resume overlay when playback progress refreshes");
            library2.Setting("position:" + movie.Id, "0");

            // The next episode is offered in the player with a countdown the user can cancel.
            var card = (Border)window.FindName("UpNextCard");
            var offer = (Task<bool>)Call("OfferNextEpisode", second, (Func<bool>)(() => false))!;
            check(card.Visibility == Visibility.Visible && ((TextBlock)window.FindName("UpNextCountdown")).Text == "Playing in 15s" && !offer.IsCompleted,
                "the next episode is offered in the player with a 15 second countdown");
            check(card.Parent is StackPanel stack && stack.Children.IndexOf(card) < stack.Children.IndexOf((UIElement)window.FindName("PlaybackControls")),
                "the up-next card stacks above the player controls instead of under them");
            Call("EndUpNext", false);
            check(card.Visibility == Visibility.Collapsed && offer.IsCompleted && !offer.Result, "cancelling the countdown hides the card and keeps the episode");
            offer = (Task<bool>)Call("OfferNextEpisode", second, (Func<bool>)(() => false))!;
            Call("RequestPlayback", true);
            check(offer.IsCompleted && offer.Result && card.Visibility == Visibility.Collapsed,
                "pressing Play during the countdown continues to the next episode instead of replaying");
            offer = (Task<bool>)Call("OfferNextEpisode", second, (Func<bool>)(() => false))!;
            Call("TogglePlayback", window, new RoutedEventArgs());
            await Task.Delay(400);
            check(!offer.IsCompleted && card.Visibility == Visibility.Visible && ((Border)window.FindName("UpNextProgress")).Visibility == Visibility.Collapsed &&
                ((TextBlock)window.FindName("UpNextCountdown")).Text == "Autoplay paused",
                "pausing during the countdown stops it and removes the card's time bar");
            Call("TogglePlayback", window, new RoutedEventArgs());
            check(!offer.IsCompleted && card.Visibility == Visibility.Visible && ((Border)window.FindName("UpNextProgress")).Visibility == Visibility.Visible &&
                ((TextBlock)window.FindName("UpNextCountdown")).Text == "Playing in 15s", "pressing pause again restarts the full countdown and its time bar");
            Call("TogglePlayback", window, new RoutedEventArgs());
            Call("UpNextPlayClicked", window, new RoutedEventArgs(Button.ClickEvent));
            check(offer.IsCompleted && offer.Result && card.Visibility == Visibility.Collapsed, "Play now after pausing the countdown continues to the next episode");
            offer = (Task<bool>)Call("OfferNextEpisode", second, (Func<bool>)(() => false))!;
            check(((Border)window.FindName("UpNextProgress")).Visibility == Visibility.Visible, "a new countdown shows its time bar again");
            Call("EndUpNext", false);
            var stale = false;
            offer = (Task<bool>)Call("OfferNextEpisode", second, (Func<bool>)(() => stale))!;
            stale = true;
            check(await Task.WhenAny(offer, Task.Delay(2000)) == offer && !offer.Result && card.Visibility == Visibility.Collapsed,
                "restarting or replacing the video cancels the countdown");

            // The details page marks an episode watched and back.
            library2.Setting("watched:" + second.Id, "false"); Field<Dictionary<string, bool>>("watchedState").Remove(second.Id);
            library2.Setting("position:" + second.Id, "65000");
            Call("Select", second);
            var watchedButton = (Button)window.FindName("DetailWatched"); var watchedText = (TextBlock)window.FindName("DetailWatchedText");
            check(watchedButton.Visibility == Visibility.Visible && watchedText.Text == "Mark as watched" &&
                ((Button)window.FindName("RestartButton")).Visibility == Visibility.Visible, "an unwatched episode offers Mark as watched on its details page");
            watchedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(library2.Setting("watched:" + second.Id) == "true" && library2.Setting("position:" + second.Id) == "0" && watchedText.Text == "Watched" &&
                (string)((Button)window.FindName("DetailPlay")).Content == "Play locally", "marking an episode watched from details clears its resume point");
            watchedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(library2.Setting("watched:" + second.Id) == "false" && watchedText.Text == "Mark as watched", "the details page can mark an episode unwatched again");

            // Without a queue, the queue menu lists the playing show's episodes.
            var queueList = (ListBox)window.FindName("QueueList");
            queue.Clear(); Set("queuePosition", -1); Call("PublishQueue");
            var listed = queueList.Items.Cast<object>().Select(entry => (MediaItem)entry.GetType().GetProperty("Media")!.GetValue(entry)!).ToArray();
            var firstIndex = Array.FindIndex(listed, item => item.Id == first.Id);
            check(listed.Select(item => item.Id).Where(id => id.StartsWith("episode-end-")).SequenceEqual([first.Id, second.Id]) && listed[firstIndex + 1].Id == second.Id && queueList.SelectedIndex == firstIndex &&
                ((TextBlock)window.FindName("QueuePillText")).Text == "Episodes" &&
                ((Button)window.FindName("RemoveQueueButton")).Visibility == Visibility.Collapsed &&
                ((Button)window.FindName("QueueEarlier")).Visibility == Visibility.Collapsed &&
                ((Button)window.FindName("AddEpisodeToQueueButton")).Visibility == Visibility.Visible &&
                ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("QueueModeOff")).IsChecked == true && ((TextBlock)window.FindName("QueueModeOffText")).Text == "Episodes" &&
                ((Button)window.FindName("PlayPlayerQueueButton")).Visibility == Visibility.Collapsed,
                "without a queue, the queue menu lists the show's episodes with the playing one selected");
            var listSource = queueList.ItemsSource;
            Set("items", Field<List<MediaItem>>("items").ToList()); Call("UpdateSessionControls");
            check(ReferenceEquals(listSource, queueList.ItemsSource), "a library refresh with the same episodes keeps the list instead of rebuilding it");
            var nextButton = (Button)window.FindName("NextButton"); var previousButton = (Button)window.FindName("PreviousButton");
            check(nextButton.Visibility == Visibility.Visible && nextButton.IsEnabled && (string)nextButton.ToolTip == "Next episode (N)",
                "previous and next stay in the player bar and step through the show's episodes");
            await (Task)Call("PlayListedEpisode", listed[firstIndex + 1])!;
            check(Field<MediaItem>("playingItem").Id == second.Id && queueList.SelectedIndex == firstIndex + 1 && queue.Count == 0,
                "playing an episode from the list follows the selection without creating a queue");
            check(!nextButton.IsEnabled && previousButton.IsEnabled, "next is disabled on the last episode");
            await (Task)Call("PlayQueueStep", -1)!;
            check(Field<MediaItem>("playingItem").Id == first.Id && queueList.SelectedIndex == firstIndex, "previous plays the episode before");
            queue.AddRange([movie, movieTwo]); Call("PublishQueue");
            check(queueList.Items.Cast<object>().SequenceEqual([movie, movieTwo]) && ((TextBlock)window.FindName("QueuePillText")).Text == "Queue" && ((TextBlock)window.FindName("QueuePillDetail")).Text == "2 videos" &&
                ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("QueueModeOn")).IsChecked == true &&
                ((Button)window.FindName("RemoveQueueButton")).Visibility == Visibility.Visible && (string)nextButton.ToolTip == "Next in queue (N)",
                "a loaded queue replaces the episode list");
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            Set("items", originalItems);
            queue.Clear(); queue.AddRange(previousQueue); Set("queuePosition", previousPosition);
            foreach (var path in paths) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
