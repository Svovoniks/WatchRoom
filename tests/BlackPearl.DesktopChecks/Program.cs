using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using BlackPearl.Core;
using BlackPearl.Desktop;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (HiddenDesktop.Relaunch(out var hiddenExitCode)) { Environment.ExitCode = hiddenExitCode; return; }
        // Voice runs on simulated devices so the checks never open the microphone; --voice-devices checks the real ones.
        if (args.FirstOrDefault() != "--voice-devices") Environment.SetEnvironmentVariable("BLACKPEARL_SIMULATED_AUDIO", "1");
        // Settings > Plugins checks an empty local catalog, so opening the tab never reaches GitHub.
        var emptyCatalog = Path.Combine(Path.GetTempPath(), "blackpearl-empty-catalog"); Directory.CreateDirectory(emptyCatalog);
        Environment.SetEnvironmentVariable("BLACKPEARL_PLUGIN_CATALOG", emptyCatalog);
        // Auto-download never registers a task, link or notification identity with Windows during the checks; the probe does, on its own scratch library.
        // Its desktop icon goes to a scratch folder, never the real desktop.
        var desktop = Path.Combine(Path.GetTempPath(), "blackpearl-check-desktop-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(desktop);
        Environment.SetEnvironmentVariable("BLACKPEARL_DESKTOP", desktop);
        if (args.FirstOrDefault() == "--follow-system-probe") { FollowSystemProbe.Run(args[1]); return; }
        Environment.SetEnvironmentVariable("BLACKPEARL_FOLLOW_SYSTEM", "off");
        if (args.FirstOrDefault() == "--video-frames")
        {
            VideoFrameChecks.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine("artifacts", "video-frames-" + Guid.NewGuid().ToString("N")))).GetAwaiter().GetResult(); return;
        }
        if (args.FirstOrDefault() == "--language-probe") { LanguageProbe.Run(args.Skip(1)).GetAwaiter().GetResult(); return; }
        if (args.FirstOrDefault() == "--metadata-probe") { MetadataProbe.Run(args[1], args[2]).GetAwaiter().GetResult(); return; }
        if (args.FirstOrDefault() == "--remaining-audit") { MetadataProbe.Audit(args[1], args[2], args.Contains("--apply")).GetAwaiter().GetResult(); return; }
        if (args.FirstOrDefault() == "--render-screens")
        {
            // Uses the library in BLACKPEARL_DATA; point it at a copy, never at a live profile.
            if (Environment.GetEnvironmentVariable("BLACKPEARL_DATA") is null) throw new InvalidOperationException("Set BLACKPEARL_DATA to a library copy.");
            Environment.SetEnvironmentVariable("BLACKPEARL_DISABLE_UPDATES", "1");
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; // deterministic captures; App.OnStartup uses this only as a fallback
            new LibraryStore(Environment.GetEnvironmentVariable("BLACKPEARL_DATA")!).Setting("artwork", "false");
            var renderApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = LoadAppResources() };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var renderWindow = new MainWindow { Left = -20000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            renderWindow.Loaded += async (_, _) =>
            {
                try { await ScreenRender.Run(renderWindow, args[1], args.Length > 3 ? int.Parse(args[2]) : 1280, args.Length > 3 ? int.Parse(args[3]) : 810); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { renderWindow.Close(); renderApp.Shutdown(); }
            };
            renderWindow.Show(); renderApp.Run(); return;
        }
        var directory = Path.GetFullPath(Path.Combine("artifacts/library-responsiveness-check", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("BLACKPEARL_DATA", directory);
        Environment.SetEnvironmentVariable("BLACKPEARL_DISABLE_UPDATES", "1");
        var store = new LibraryStore(directory); store.Setting("artwork", "false");
        store.Setting("folders", Wire.Serialize(new[] { new LibraryFolder(directory) }));
        var entries = Enumerable.Range(0, 750).Select(index => new MediaItem("item-" + index, Path.Combine(directory, "movie-" + index + ".mp4"), $"Movie {index:0000}", "Movie", ProviderIds: new() { ["imdb"] = "tt" + index })).ToArray();
        foreach (var entry in entries) store.Save(entry);
        var startupQueue = new SavedQueue("startup-queue", "Startup queue", [entries[0].Id, entries[^1].Id]);
        store.Setting("queues", Wire.Serialize(new[] { startupQueue })); store.Setting("activeQueue", startupQueue.Id);
        store.Setting("queue", Wire.Serialize(startupQueue.MediaIds)); store.Setting("queuePosition", "1");
        store.Setting("watched:" + entries[3].Id, "true");
        var app = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources = LoadAppResources();
        if (args.FirstOrDefault() == "--metadata-picker")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(async () =>
            {
                try { await MetadataPickerChecks.Run(directory); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { app.Shutdown(); }
            });
            app.Run(); return;
        }
        if (args.FirstOrDefault() == "--acquisition-layout")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var layoutWindow = new MainWindow { Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            layoutWindow.Loaded += (_, _) =>
            {
                var count = 0;
                try { AcquisitionLayoutChecks.Run(layoutWindow, directory, (condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }); Console.WriteLine($"{count} acquisition layout checks passed."); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { layoutWindow.Close(); app.Shutdown(); }
            };
            layoutWindow.Show(); app.Run(); return;
        }
        if (args.FirstOrDefault() == "--voice-ui")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var voiceWindow = new MainWindow { Left = -20000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            voiceWindow.Loaded += async (_, _) =>
            {
                var count = 0;
                try { await VoiceUiChecks.Run(voiceWindow, store, directory, (condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }); Console.WriteLine($"{count} voice UI checks passed."); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { voiceWindow.Close(); app.Shutdown(); }
            };
            voiceWindow.Show(); app.Run(); return;
        }
        if (args.FirstOrDefault() == "--guest-recovery")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var recoveryWindow = new MainWindow { Left = -20000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            recoveryWindow.Loaded += async (_, _) =>
            {
                try { await GuestRecoveryChecks.Run(recoveryWindow, directory); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { recoveryWindow.Close(); app.Shutdown(); }
            };
            recoveryWindow.Show(); app.Run(); return;
        }
        if (args.FirstOrDefault() == "--ui-findings")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiFindingsChecks.Run(); app.Shutdown(); return;
        }
        if (args.FirstOrDefault() == "--ui-experience")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiExperienceChecks.Run(); app.Shutdown(); return;
        }
        if (args.FirstOrDefault() == "--player-layout")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            PlayerLayoutChecks.Run(); app.Shutdown(); return;
        }
        if (args.FirstOrDefault() == "--room-playback-status")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RoomPlaybackStatusChecks.Run(args.Contains("--screenshots")); app.Shutdown(); return;
        }
        if (args.FirstOrDefault() == "--room-tracks")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(async () =>
            {
                try { await RoomTrackChecks.Run(args[1], directory); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { app.Shutdown(); }
            });
            app.Run(); return;
        }
        if (args.FirstOrDefault() == "--language-detection")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(async () =>
            {
                try { await LanguageDetectionChecks.Run(directory); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { app.Shutdown(); }
            });
            app.Run(); return;
        }
        if (args.FirstOrDefault() == "--track-preferences")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            TrackPreferenceChecks.Run(args[1]); app.Shutdown(); return;
        }
        if (args.FirstOrDefault() == "--guest-library-layout")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            GuestLibraryLayoutChecks.Run(); app.Shutdown(); return;
        }
        if (args.FirstOrDefault() == "--host-library-browse")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            HostLibraryBrowseChecks.Run(directory); app.Shutdown(); return;
        }
        if (args.FirstOrDefault() == "--voice-devices")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(async () =>
            {
                var count = 0;
                try { await VoiceDeviceChecks.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }); Console.WriteLine($"{count} voice device checks passed."); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { app.Shutdown(); }
            });
            app.Run(); return;
        }
        if (args.FirstOrDefault() == "--voice-probe") { VoiceProbe.Run(args[1], args.ElementAtOrDefault(2)); return; }
        if (args.FirstOrDefault() == "--voice-processing")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(async () =>
            {
                var count = 0;
                try { await VoiceProcessingChecks.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }); Console.WriteLine($"{count} voice processing checks passed."); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { app.Shutdown(); }
            });
            app.Run(); return;
        }
        if (args.FirstOrDefault() == "--home")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(async () =>
            {
                try { await HomeChecks.Run(directory); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { app.Shutdown(); }
            });
            app.Run(); return;
        }
        var startupGaps = new List<double>(); var startupClock = Stopwatch.StartNew(); var startupPrior = startupClock.Elapsed.TotalMilliseconds;
        var startupHeartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        startupHeartbeat.Tick += (_, _) => { var now = startupClock.Elapsed.TotalMilliseconds; startupGaps.Add(now - startupPrior); startupPrior = now; };
        var window = new MainWindow { Left = -20000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        try
        {
            Check(MetadataCredential.Load(directory) == "", "new profile has no saved token");
            MetadataCredential.Save(directory, "fixture-token-one");
            Check(MetadataCredential.Load(directory) == "fixture-token-one", "credential vault restores a saved token");
            MetadataCredential.Save(directory, "fixture-token-two");
            Check(MetadataCredential.Load(directory) == "fixture-token-two", "credential vault replaces a saved token");
            MetadataCredential.Save(directory, "");
            Check(MetadataCredential.Load(directory) == "", "clearing the token removes the saved credential");
        }
        finally { MetadataCredential.Save(directory, ""); }
        window.Loaded += async (_, _) =>
        {
            try
            {
                startupPrior = startupClock.Elapsed.TotalMilliseconds; startupHeartbeat.Start();
                typeof(MainWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Library"]);
                await Task.Delay(2500);
                await CheckScrolling(Check);
                var field = typeof(MainWindow).GetField("libraryCards", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var cards = (ObservableCollection<LibraryCardView>)field.GetValue(window)!;
                Check(cards.Count == entries.Length, "large library is displayed");
                Check(cards.Single(card => card.Card.Media.Id == entries[3].Id).IsWatched, "background startup restores persisted watched flags");
                var restoredQueue = (List<MediaItem>)typeof(MainWindow).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Check(restoredQueue.Select(item => item.Id).SequenceEqual(startupQueue.MediaIds) && store.Setting("queuePosition") == "1",
                    "background startup restores queue order and cursor before persisting them");
                startupHeartbeat.Stop();
                Console.WriteLine($"Startup max input gap: {startupGaps.DefaultIfEmpty().Max():F1} ms");
                Check(startupGaps.Count > 20 && startupGaps.Max() < 350, "startup library loading keeps processing input");
                var grid = (System.Windows.Controls.ItemsControl)window.FindName("PosterGrid");
                int Realized() => Enumerable.Range(0, cards.Count).Count(index => grid.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Check(Realized() < 50, "large library realizes only visible rows");
                var libraryScroll = (System.Windows.Controls.ScrollViewer)window.FindName("LibraryScroll");
                libraryScroll.ScrollToBottom(); await Task.Delay(150);
                Console.WriteLine($"Library viewport {libraryScroll.ViewportHeight:F0}, extent {libraryScroll.ExtentHeight:F0}, offset {libraryScroll.VerticalOffset:F0}, realized {Realized()}");
                Check(grid.ItemContainerGenerator.ContainerFromIndex(cards.Count - 1) is not null && Realized() < 50,
                    "scrolling reaches the final card without realizing the entire library");
                libraryScroll.ScrollToTop(); await Task.Delay(150);
                var originalWidth = window.Width; window.Width = 900; await Task.Delay(150);
                libraryScroll.ScrollToBottom(); await Task.Delay(150);
                Check(grid.ItemContainerGenerator.ContainerFromIndex(cards.Count - 1) is not null && Realized() < 50,
                    "resizing recalculates library rows and keeps the final card reachable");
                window.Width = originalWidth; libraryScroll.ScrollToTop(); await Task.Delay(150);
                await LibraryScrollingChecks.Run(grid, libraryScroll, Check);
                // A click focuses a card; focusing a half-visible card must not scroll the grid.
                libraryScroll.ScrollToVerticalOffset(165); await Task.Delay(150);
                var halfVisible = Enumerable.Range(0, grid.Items.Count).Select(index => grid.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement)
                    .Where(container => container is not null).Select(container =>
                    {
                        var top = container!.TransformToAncestor(libraryScroll).Transform(new Point()).Y;
                        return (container, top);
                    }).First(pair => pair.top < libraryScroll.ViewportHeight && pair.top + 312 > libraryScroll.ViewportHeight).container;
                var requests = new List<string>();
                void Trace(object sender, RequestBringIntoViewEventArgs e) => requests.Add(e.TargetObject?.GetType().Name + (e.Handled ? " handled" : ""));
                window.AddHandler(FrameworkElement.RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)Trace, true);
                var cardButton = LibraryScrollingChecks.Descendants(halfVisible).OfType<System.Windows.Controls.Button>().First(button => button.Tag is LibraryCard);
                var before = libraryScroll.VerticalOffset;
                cardButton.Focus(); await Task.Delay(150);
                window.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)Trace);
                Console.WriteLine($"Focus requests: {string.Join(", ", requests)}; offset {before} -> {libraryScroll.VerticalOffset}");
                Check(Math.Abs(libraryScroll.VerticalOffset - before) < .5, "focusing a half-visible library card does not scroll the grid");
                var tick = LibraryScrollingChecks.Descendants(halfVisible).OfType<System.Windows.Controls.Button>().First(button => button.Tag is not LibraryCard);
                tick.Focus(); await Task.Delay(150);
                Check(Math.Abs(libraryScroll.VerticalOffset - before) < .5, "focusing the watched tick on a half-visible card does not scroll the grid");
                System.Windows.Input.Keyboard.ClearFocus(); libraryScroll.ScrollToTop(); await Task.Delay(150);
                var previous = cards.ToArray();
                await CheckSearchLayout(window, Check);
                var tracker = new LibraryCardView(new("Example", "Movie", null, entries[0], "movie"));
                var properties = new List<string>(); tracker.PropertyChanged += (_, e) => properties.Add(e.PropertyName!);
                tracker.Update(tracker.Card with { Media = entries[0] with { Overview = "New metadata" } });
                Check(properties.SequenceEqual(["Card"]), "metadata-only update keeps poster and displayed text bindings intact");
                var posterPath = Path.Combine(directory, "thumbnail.png");
                await File.WriteAllBytesAsync(posterPath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII="));
                tracker.Update(tracker.Card with { Poster = posterPath });
                await tracker.LoadPosterAsync();
                Check(tracker.Poster?.IsFrozen == true, "background thumbnail decode returns a frozen WPF image");
                var frozen = tracker.Poster; await tracker.LoadPosterAsync();
                Check(ReferenceEquals(frozen, tracker.Poster), "unchanged image is reused");
                var dead = new LibraryCardView(tracker.Card with { Poster = Path.Combine(directory, "missing.png") });
                await dead.LoadPosterAsync(); await dead.LoadPosterAsync();
                Check(dead.Poster is null, "missing poster keeps fallback without reloading forever");
                await File.WriteAllBytesAsync(dead.Card.Poster!, await File.ReadAllBytesAsync(posterPath));
                dead.Update(dead.Card); await dead.LoadPosterAsync();
                Check(dead.Poster?.IsFrozen == true, "poster created at the same path retries after an earlier load failure");
                File.SetLastWriteTimeUtc(posterPath, File.GetLastWriteTimeUtc(posterPath).AddSeconds(2));
                tracker.Update(tracker.Card); await tracker.LoadPosterAsync();
                Check(tracker.Poster is not null && !ReferenceEquals(frozen, tracker.Poster), "replaced image at the same path invalidates the WPF thumbnail cache");
                var retained = tracker.Poster;
                var replacementPath = Path.Combine(directory, "replacement.png");
                await File.WriteAllBytesAsync(replacementPath, await File.ReadAllBytesAsync(posterPath));
                var blankFrames = 0;
                tracker.PropertyChanged += (_, e) => { if (e.PropertyName == "Poster" && tracker.Poster is null) blankFrames++; };
                tracker.Update(tracker.Card with { Poster = replacementPath });
                Check(ReferenceEquals(retained, tracker.Poster), "artwork refresh retains old image while replacement decodes");
                await tracker.LoadPosterAsync();
                Check(blankFrames == 0 && tracker.Poster is not null, "successful artwork replacement never publishes a blank frame");
                retained = tracker.Poster;
                tracker.Update(tracker.Card with { Poster = Path.Combine(directory, "failed-replacement.png") }); await tracker.LoadPosterAsync();
                Check(ReferenceEquals(retained, tracker.Poster), "failed artwork replacement keeps the previous image");
                await PosterLeaseChecks.Run(directory, entries[0], Check);
                var episodeView = new LibraryCardView(new("Episode 1", "Example", posterPath, entries[0] with { Series = "Example", Season = 1, Episode = 1, RuntimeMinutes = 20 }, "episode"));
                episodeView.SetPlayback(600000, false);
                Check(episodeView.Progress == 50 && episodeView.PlaybackLabel == "Resume · 10:00", "episode playback progress uses runtime and saved resume point");
                episodeView.SetPlayback(0, true);
                Check(episodeView.Progress == 100 && episodeView.PlaybackLabel == "Watched", "completed episode retains watched progress");
                Check(episodeView.IsWatched, "completed episodes expose the watched checkmark state");
                episodeView.SetPlayback(0, false);
                Check(!episodeView.IsWatched, "restarting an episode clears its watched checkmark state");
                var movieView = new LibraryCardView(new("Movie", "", null, entries[0], "movie"));
                movieView.SetPlayback(0, true);
                Check(movieView.IsWatched, "watched checkmark state is available for movies as well as episodes");
                var episodeButton = new System.Windows.Controls.Button { Style = (Style)app.Resources["PosterCard"], DataContext = episodeView };
                episodeButton.Measure(new Size(600, 600)); await Dispatcher.Yield(DispatcherPriority.DataBind);
                Check(episodeButton.Width == 300 && episodeButton.Height == 278, "episode cards use landscape layout without changing movie cards");

                var gaps = new List<double>(); var clock = Stopwatch.StartNew(); var prior = clock.Elapsed.TotalMilliseconds;
                var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
                heartbeat.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; gaps.Add(now - prior); prior = now; };
                heartbeat.Start();
                var activeStore = (LibraryStore)typeof(MainWindow).GetField("library", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                await Task.Run(() => { foreach (var entry in entries) activeStore.Save(entry with { Overview = "Updated during scan" }); });
                await Task.Delay(1500); heartbeat.Stop();
                Check(cards.Count == previous.Length && cards.Zip(previous).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    "scan updates preserve all existing card instances");
                Check(cards.All(card => card.Card.Media.Overview == "Updated during scan"), "incremental metadata reaches every card");
                Check(grid.IsEnabled, "background refresh leaves the library interactive");
                typeof(MainWindow).GetMethod("FilterLibrary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [false, false]);
                Check(grid.IsEnabled, "full refresh never dims or disables the poster grid");
                Console.WriteLine($"Observed {gaps.Count} input heartbeats; maximum gap {(gaps.Count == 0 ? 0 : gaps.Max()):F1} ms.");
                Check(gaps.Count > 50 && gaps.Max() < 250, "dispatcher input continues during 750 scan updates");
                await CheckLibraryNavigation(window, activeStore, cards, directory, posterPath, Check);
                CheckRoomSettings(window, activeStore, Check);
                CheckQueueEditing(window, activeStore, Check);
                StreamingBufferChecks.Run(window, activeStore, directory, Check);
                PreferenceNavigationChecks.Run(window, activeStore, Check);
                await QueuePlaybackChecks.Run(window, activeStore, directory, Check);
                await EpisodeEndChecks.Run(window, Check);
                await MusicPlaybackChecks.Run(window, Check);
                await LoopChecks.Run(window, Check);
                await OpenFileChecks.Run(window, Check);
                RenameChecks.Run(Check);
                await PictureInPictureChecks.Run(window, Check);
                await EndedSeekChecks.Run(window, Check);
                await LocalAviAudioChecks.Run(window, Check);
                await CheckBackNavigation(window, Check);
                PlayerInteractionChecks.Run(window, Check);
                UnavailableLibraryChecks.Run(window, activeStore, directory, Check);
                UnmatchedLibraryChecks.Run(window, activeStore, directory, Check);
                CardSelectionChecks.Run(window, activeStore, directory, Check);
                WantedLibraryChecks.Run(window, activeStore, directory, Check);
                AcquisitionChecks.Run(window, activeStore, directory, Check);
                FollowingChecks.Run(window, activeStore, directory, Check);
                await PlayerShutdownChecks.Run(window, Check);
                await RoomRecoveryChecks.Run(window, directory, Check);
                await GuestRecoveryChecks.Run(window, directory, Check);
                await VoiceUiChecks.Run(window, activeStore, directory, Check);
                Console.WriteLine($"Input heartbeat: maximum {gaps.Max():F1} ms; p95 {gaps.Order().ElementAt((int)(gaps.Count * .95)):F1} ms");
                Console.WriteLine($"{checks} desktop responsiveness checks passed.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run(window);
    }

    // App.xaml's resources without its compiled code-behind, so checks run against the same styles as the app.
    private static ResourceDictionary LoadAppResources()
    {
        var resourceXml = System.Xml.Linq.XDocument.Load("src/BlackPearl.Desktop/App.xaml");
        var resourceRoot = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"), resourceXml.Root!.Elements().Single().Elements());
        resourceRoot.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        resourceRoot.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:BlackPearl.Desktop;assembly=BlackPearl");
        foreach (var element in resourceRoot.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:BlackPearl.Desktop"))
            element.Name = System.Xml.Linq.XName.Get(element.Name.LocalName, "clr-namespace:BlackPearl.Desktop;assembly=BlackPearl");
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resourceRoot.ToString());
    }
    private static async Task CheckLibraryNavigation(MainWindow window, LibraryStore store, ObservableCollection<LibraryCardView> cards,
        string directory, string poster, Action<bool, string> check)
    {
        var show = new MediaItem("navigation-show", Path.Combine(directory, "Show S01E01.mkv"), "Navigation Show", "Show", Series: "Navigation Show", Season: 1, Episode: 1, EpisodeTitle: "The Beginning", ProviderIds: new() { ["imdb"] = "tt-navigation-show" });
        var special = show with { Id = "navigation-special", Path = Path.Combine(directory, "Show S00E01.mkv"), Season = 0 };
        var unknown = show with { Id = "navigation-unknown", Path = Path.Combine(directory, "Unknown episode.mkv"), Season = null };
        var anime = show with { Id = "navigation-anime", Path = Path.Combine(directory, "Anime S01E01.mkv"), Kind = "Anime", Series = "Navigation Anime", Title = "Navigation Anime", ProviderIds = new() { ["imdb"] = "tt-navigation-anime" } };
        var animeMovie = new MediaItem("navigation-anime-film", Path.Combine(directory, "Anime film.mkv"), "Navigation Anime Film", "Anime", Poster: poster, Available: false);
        foreach (var item in new[] { show, special, unknown, anime, animeMovie }) store.Save(item);
        await Task.Delay(600);
        void Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
        void Navigate(int category, string? series = null, string? kind = null, int? season = null) => Call("NavigateLibrary", category, series, kind, season);
        async Task Wait(Func<bool> ready)
        {
            var until = Stopwatch.StartNew();
            while (!ready() && until.ElapsedMilliseconds < 4000) await Task.Delay(25);
            check(ready(), "navigation finishes with the expected library cards");
        }
        LibraryBreadcrumb[] Crumbs(bool details = false) => ((System.Windows.Controls.ItemsControl)window.FindName(details ? "DetailBreadcrumbs" : "LibraryBreadcrumbs")).Items.Cast<LibraryBreadcrumb>().ToArray();
        void ClickCrumb(LibraryBreadcrumb crumb) => Call("BreadcrumbClick", window,
            new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, new System.Windows.Controls.Button { DataContext = crumb }));
        string Status(string name) => System.Windows.Automation.AutomationProperties.GetItemStatus((DependencyObject)window.FindName(name));
        Navigate(3); await Wait(() => cards.Count == 1 && cards.All(x => x.Card.Media.Kind == "Anime"));
        var categoryButton = (System.Windows.Controls.Button)window.FindName("ShowsLibraryNav");
        categoryButton.Focus(); Call("LibraryCategoryClick", categoryButton, new RoutedEventArgs());
        await Wait(() => cards.Count == 1 && cards[0].Card.Level == "series");
        check(!((System.Windows.Controls.TextBox)window.FindName("SearchBox")).IsKeyboardFocusWithin, "category navigation does not move the cursor into search");
        Navigate(3); await Wait(() => cards.Count == 1 && cards.All(x => x.Card.Media.Kind == "Anime"));
        check(Crumbs().Length == 0 && Status("AnimeLibraryNav") == "Selected", "Anime category excludes unavailable titles and is marked in the category bar rather than a trail");
        var status = (System.Windows.Controls.ComboBox)window.FindName("LibraryStatusFilter");
        Navigate(4); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == animeMovie.Id);
        check(Status("UnavailableLibraryNav") == "Selected" && cards.All(x => !x.Card.Media.Available), "unavailable videos appear only in their separate library tab");
        // Search's type of title and status filters combine.
        var kinds = ((System.Windows.Controls.Panel)window.FindName("SearchKinds")).Children.OfType<System.Windows.Controls.RadioButton>().ToArray();
        Call("OpenSearch", false); kinds.Single(x => (string)x.Tag == "3").IsChecked = true;
        status.SelectedIndex = 1; await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == anime.Id);
        check(cards[0].Card.Poster is null, "artwork status combines with the type of title in Search");
        check(!status.Items.OfType<System.Windows.Controls.ComboBoxItem>().Any(item => Equals(item.Content, "Unavailable")),
            "availability is a separate tab rather than a status filter");
        status.SelectedIndex = 0; kinds[0].IsChecked = true;
        Navigate(2, LibraryIdentity.ShowKey(show), "Show"); await Wait(() => cards.Count == 3 && cards.All(x => x.Card.Level == "season"));
        check(Crumbs().Select(x => x.Label).SequenceEqual(["Shows", "Navigation Show"]) && Crumbs()[0].Separator == "", "show breadcrumb points to the season grid and starts at its category");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show", 0); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == special.Id);
        check(Crumbs().Last().Label == "Specials", "specials have an explicit clickable breadcrumb");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show", -1); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == unknown.Id);
        check(Crumbs().Last().Label == "Season unknown", "unknown seasons preserve their navigation identity");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show", 1); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == show.Id);
        Call("Select", show);
        check(Crumbs(true).Select(x => x.Label).SequenceEqual(["Shows", "Navigation Show", "Season 1", "Episode 1 · The Beginning"]), "episode details display the complete hierarchy from their category");
        window.UpdateLayout(); var clipped = BorderAudit.Find((System.Windows.Media.Visual)window.Content);
        check(clipped.Count == 0, "details breadcrumbs and disclosure outlines are not clipped" + string.Concat(clipped.Select(x => Environment.NewLine + "  " + x)));
        ClickCrumb(Crumbs(true).Single(x => x.Level == "season"));
        await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == show.Id);
        check(((FrameworkElement)window.FindName("LibraryPage")).Visibility == Visibility.Visible && Crumbs().Last().Level == "season", "season breadcrumb returns from episode details to its grid");
        ClickCrumb(Crumbs().Single(x => x.Level == "show")); await Wait(() => cards.Count == 3 && cards.All(x => x.Card.Level == "season"));
        Call("LibraryNavClick", window, new RoutedEventArgs()); await Wait(() => cards.Count == 1 && cards[0].Card.Level == "series");
        check(Crumbs().Length == 0, "Library, clicked on the library, returns to the top of the category");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show"); await Wait(() => cards.Count == 3 && cards.All(x => x.Card.Level == "season"));
        ClickCrumb(Crumbs().Single(x => x.Label == "Shows")); await Wait(() => cards.Count == 1 && cards[0].Card.Level == "series");
        var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        Call("OpenSearch", false); search.Text = "no matching title"; await Wait(() => cards.Count == 0);
        check(((FrameworkElement)window.FindName("NoResults")).Visibility == Visibility.Visible, "a search without matches says so");
        Call("LibraryCategoryClick", window.FindName("AllLibraryNav"), new RoutedEventArgs()); await Wait(() => cards.Count == 752);
        check(Crumbs().Length == 0 && Status("AllLibraryNav") == "Selected", "the All category lists every title, whatever Search holds");
        search.Clear();
        await CheckCategoryBar(window, check);
        await CheckThreadSelectors(window, check);
        CheckResourceLookups(window, check);
        await CheckShortRail(window, check);
        await CheckCollapsedRail(window, check);
        Navigate(1); await Wait(() => cards.Count == 750 && cards.All(x => x.Card.Media.Kind == "Movie"));
        Call("Select", animeMovie);
        check(Crumbs(true).Select(x => x.Label).SequenceEqual(["Unavailable", "Navigation Anime Film"]), "unavailable film details link back to the unavailable tab");
        ClickCrumb(Crumbs(true).First()); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == animeMovie.Id);
        Navigate(0); await Wait(() => cards.Count == 752);
    }

    // A window too short for the sidebar scrolls its navigation; Settings stays in view and the buttons keep their width.
    private static async Task CheckShortRail(MainWindow window, Action<bool, string> check)
    {
        var rail = (FrameworkElement)window.FindName("Rail");
        var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("RailScroll");
        var home = (FrameworkElement)window.FindName("HomeNav");
        var settings = (FrameworkElement)window.FindName("SettingsNav");
        var (height, minimum, state) = (window.Height, window.MinHeight, window.WindowState);
        window.WindowState = WindowState.Normal; window.Height = 2000;
        await Dispatcher.Yield(DispatcherPriority.Render); window.UpdateLayout();
        var width = home.ActualWidth;
        // Well below the minimum height, so the navigation overflows whichever entries are listed.
        window.MinHeight = 280; window.Height = 280; scroll.ScrollToBottom();
        await Dispatcher.Yield(DispatcherPriority.Render); window.UpdateLayout();
        var bottom = settings.TranslatePoint(new Point(0, settings.ActualHeight), rail).Y;
        check(scroll.ScrollableHeight > 0 && settings.IsVisible && bottom <= rail.ActualHeight,
            $"a short window scrolls the sidebar and keeps Settings in view (scrollable {scroll.ScrollableHeight:0.#}, Settings bottom {bottom:0.#} of {rail.ActualHeight:0.#})");
        check(home.ActualWidth == width, $"sidebar buttons keep their width when its scroll bar appears ({width:0.#} -> {home.ActualWidth:0.#})");
        scroll.ScrollToTop(); window.MinHeight = minimum; window.Height = height; window.WindowState = state;
        await Dispatcher.Yield(DispatcherPriority.Render); window.UpdateLayout();
    }

    // A thread selector shows its choice by lighting the thread under it: centred on the chosen word, covering it, on that row's
    // thread. Returns the choice it found and a description of anything wrong, or "".
    internal static (FrameworkElement? Chosen, string Problem) ThreadLit(ThreadBar bar)
    {
        var choices = ((System.Windows.Controls.Panel)bar.Content).Children.OfType<FrameworkElement>().Where(choice => choice.IsVisible).ToList();
        var chosen = choices.FirstOrDefault(choice => choice is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true } || System.Windows.Automation.AutomationProperties.GetItemStatus(choice) == "Selected");
        var light = bar.Light;
        if (chosen is null || light is null) return (chosen, " (nothing chosen, or no light)");
        var padding = ((System.Windows.Controls.Control)chosen).Padding;
        var word = chosen.TranslatePoint(new Point(padding.Left, 0), bar).X; var wordWidth = chosen.ActualWidth - padding.Left - padding.Right;
        var at = light.TranslatePoint(new Point(0, light.ActualHeight / 2), bar);
        var below = chosen.TranslatePoint(new Point(0, chosen.ActualHeight), bar).Y;
        var thread = bar.Threads.Select(line => line.TranslatePoint(new Point(0, 0.5), bar).Y).Where(y => y >= below).DefaultIfEmpty(double.NaN).Min();
        var fine = light.IsVisible && Math.Abs(at.X + light.ActualWidth / 2 - (word + wordWidth / 2)) < 1 && at.X <= word && at.X + light.ActualWidth >= word + wordWidth && Math.Abs(at.Y - thread) < 0.5;
        return (chosen, fine ? "" : $" (light {(light.IsVisible ? "at" : "hidden at")} {at.X:0.#},{at.Y:0.#}, {light.ActualWidth:0.#} wide; word at {word:0.#}, {wordWidth:0.#} wide; its thread at {thread:0.#})");
    }
    // Every style or brush the code looks up by name exists. Such a lookup fails silently, so a style removed or renamed in XAML
    // would leave a control the code builds unstyled, as the Downloads choice once was.
    private static void CheckResourceLookups(MainWindow window, Action<bool, string> check)
    {
        var keys = new HashSet<object>();
        void Add(ResourceDictionary dictionary) { foreach (var key in dictionary.Keys) keys.Add(key); foreach (var merged in dictionary.MergedDictionaries) Add(merged); }
        void Walk(object node)
        {
            if (node is FrameworkElement element) Add(element.Resources);
            if (node is DependencyObject parent) foreach (var child in LogicalTreeHelper.GetChildren(parent)) Walk(child);
        }
        Add(Application.Current.Resources); Walk(window);
        var used = Directory.EnumerateFiles("src/BlackPearl.Desktop", "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"(?:SetResourceReference\([A-Za-z.]*Property, |FindResource\(|TryFindResource\()""([^""]+)""").Select(match => match.Groups[1].Value))
            .Distinct().ToList();
        var missing = used.Where(key => !keys.Contains(key)).ToList();
        check(used.Count > 10 && missing.Count == 0, $"every resource the code looks up by name exists ({used.Count} looked up{(missing.Count > 0 ? "; missing: " + string.Join(", ", missing) : "")})");
    }
    // Every pick-one selector is the thread selector: Settings' sections, the plugin view and Search's type of title light the
    // thread under their choice and move the light when it changes. Settings' sections wrap in a narrow window, each row on its
    // own thread.
    private static async Task CheckThreadSelectors(MainWindow window, Action<bool, string> check)
    {
        void Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).First(m => m.Name == name && m.GetParameters().Length == arguments.Length).Invoke(window, arguments);
        // Long enough for a glide to finish after a section's page has laid itself out.
        async Task Settle() { await Task.Delay(900); await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        ThreadBar BarOf(string name) { DependencyObject? at = (DependencyObject)window.FindName(name); while (at is not null and not ThreadBar) at = LogicalTreeHelper.GetParent(at); return (ThreadBar)at!; }
        void Lit(ThreadBar bar, string what, string expected)
        {
            var (chosen, problem) = ThreadLit(bar);
            var label = chosen is System.Windows.Controls.ContentControl { Content: var content } ? content?.ToString() : null;
            check(problem == "" && label?.StartsWith(expected) == true, $"{what}: the thread is lit under {expected} (under {label ?? "nothing"}){problem}");
        }
        Call("LoadPreferences"); Call("ShowPage", "Settings"); Call("ShowPreferenceSection", "Playback"); await Settle();
        var sections = BarOf("PreferenceSections");
        Lit(sections, "Settings", "Playback");
        Call("ShowPreferenceSection", "Plugins"); await Settle();
        Lit(sections, "Settings", "Plugins");
        var plugins = BarOf("InstalledPluginsView");
        ((System.Windows.Controls.RadioButton)window.FindName("AvailablePluginsView")).IsChecked = true; await Settle();
        Lit(plugins, "The plugin view", "Available");
        ((System.Windows.Controls.RadioButton)window.FindName("InstalledPluginsView")).IsChecked = true; await Settle();
        Lit(plugins, "The plugin view", "Installed");
        // A narrow window wraps the sections: each row has its own thread, and the light sits on the chosen one's row.
        var wide = window.Width; window.Width = window.MinWidth; Call("ShowPreferenceSection", "Updates"); await Settle();
        var rows = ((System.Windows.Controls.Panel)sections.Content).Children.OfType<FrameworkElement>().Select(tab => Math.Round(tab.TranslatePoint(new Point(0, 0), sections).Y)).Distinct().Count();
        check(rows > 1 && sections.Threads.Count == rows, $"wrapped sections have a thread for each row ({rows} rows, {sections.Threads.Count} threads)");
        Lit(sections, "Wrapped Settings", "Updates");
        window.Width = wide; await Settle();
        Call("ShowPage", "Library"); Call("OpenSearch", false); await Settle();
        var kinds = BarOf("SearchKinds");
        var shows = ((System.Windows.Controls.Panel)window.FindName("SearchKinds")).Children.OfType<System.Windows.Controls.RadioButton>().Single(kind => (string)kind.Tag == "2");
        var all = ((System.Windows.Controls.Panel)window.FindName("SearchKinds")).Children.OfType<System.Windows.Controls.RadioButton>().Single(kind => (string)kind.Tag == "0");
        all.IsChecked = true; await Settle();
        Lit(kinds, "Search", "All videos");
        shows.IsChecked = true; await Settle();
        Lit(kinds, "Search", "Shows");
        all.IsChecked = true; await Settle();
        Call("NavigateLibrary", 0, null, null, null); await Settle();
    }
    // The library's categories are words on a thread at the top of the Library page; the stretch of thread under the current one
    // is lit and glides to the next. The pearl stays on the sidebar alone.
    private static async Task CheckCategoryBar(MainWindow window, Action<bool, string> check)
    {
        void Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
        var categories = (ThreadBar)window.FindName("LibraryCategoryBar");
        var glow = categories.Light!;
        async Task Settle() { await Task.Delay(400); await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        string Under(string name) => ((FrameworkElement)window.FindName(name)) is var chosen && ThreadLit(categories) is var lit && lit.Chosen == chosen ? lit.Problem : $" (lit under {lit.Chosen?.Name ?? "nothing"} rather than {name})";
        int Pearls() => Descendants(window).OfType<System.Windows.Controls.ContentControl>().Count(element => element.IsVisible && element.Style == window.FindResource("Pearl"));
        static IEnumerable<DependencyObject> Descendants(DependencyObject node) => Enumerable.Range(0, System.Windows.Media.VisualTreeHelper.GetChildrenCount(node))
            .Select(i => System.Windows.Media.VisualTreeHelper.GetChild(node, i)).SelectMany(child => Descendants(child).Prepend(child));
        Call("NavigateLibrary", 0, null, null, null); await Settle();
        check(Pearls() == 1, $"the Library page shows one pearl, on the sidebar ({Pearls()} shown)");
        check(categories.IsVisible && categories.TranslatePoint(new Point(0, 0), (FrameworkElement)window.FindName("LibraryPage")).Y < 1, "the categories sit at the top of the Library page");
        // The poster grid reaches 6 DIP past the page edge for its cards' padding, so the artwork itself lines up with the page edge.
        var first = ((FrameworkElement)window.FindName("AllLibraryNav")).TranslatePoint(new Point(0, 0), (FrameworkElement)window.FindName("LibraryPage")).X;
        check(Math.Abs(first) < 0.5, $"the first category starts at the page edge, in line with the artwork below ({first:0.#})");
        // A category's count is part of its label: the word's size and ink, so it reads as one label and brightens with it.
        foreach (var name in new[] { "MissingLibraryCount", "UnmatchedLibraryCount" })
        {
            var number = (System.Windows.Documents.Run)window.FindName(name); var label = (System.Windows.Controls.TextBlock)number.Parent;
            check(number.FontSize == label.FontSize && number.FontFamily.Equals(label.FontFamily) && Equals(number.Foreground, label.Foreground),
                $"{name} is part of its label, in the word's size and ink ({number.FontSize} against {label.FontSize})");
        }
        var under = Under("AllLibraryNav");
        check(under == "", "the thread is lit under All" + under);
        // The light glides along the thread, always forward, rather than jumping, and stays on the thread as it goes.
        var rolled = new List<double>(); var onThread = true;
        Call("LibraryCategoryClick", window.FindName("MoviesLibraryNav"), new RoutedEventArgs());
        for (var until = DateTime.UtcNow.AddMilliseconds(500); DateTime.UtcNow < until;)
        {
            await Task.Delay(12); window.UpdateLayout();
            var at = glow.TranslatePoint(new Point(glow.ActualWidth / 2, glow.ActualHeight / 2), categories);
            rolled.Add(at.X); onThread &= Math.Abs(at.Y - categories.Threads[0].TranslatePoint(new Point(0, 0.5), categories).Y) < 0.5;
        }
        var end = rolled[^1];
        if (SystemParameters.ClientAreaAnimation)
            check(rolled.Any(x => x > 30 && x < end - 10) && rolled.Zip(rolled.Skip(1)).All(pair => pair.Second >= pair.First - 0.01) && onThread,
                $"the light glides along the thread to the category chosen ({string.Join(", ", rolled.Distinct().Take(10).Select(x => x.ToString("0")))})");
        await Settle();
        under = Under("MoviesLibraryNav");
        check(under == "", "the light settles under the category chosen" + under);
        Call("OpenSearch", false); await Settle();
        check(!categories.IsVisible, "Search hides the categories");
        Call("LibraryNavClick", window, new RoutedEventArgs()); await Settle();
        under = Under("AllLibraryNav");
        check(categories.IsVisible && under == "", "Library leaves Search for All, and the light is placed again when the categories return" + under);
    }

    // The sidebar folds into a strip of icons, with each icon named in a tooltip, and unfolds again.
    private static async Task CheckCollapsedRail(MainWindow window, Action<bool, string> check)
    {
        void Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
        var rail = (FrameworkElement)window.FindName("Rail");
        var home = (System.Windows.Controls.Button)window.FindName("HomeNav");
        var label = ((System.Windows.Controls.Panel)home.Content).Children[1];
        var icon = (FrameworkElement)((System.Windows.Controls.Panel)home.Content).Children[0];
        var content = (FrameworkElement)window.FindName("ContentArea");
        var expanded = content.ActualWidth;
        var logo = (FrameworkElement)window.FindName("RailLogo");
        var toggle = (FrameworkElement)window.FindName("RailToggle");
        var toggleAt = toggle.TranslatePoint(new Point(0, 0), rail); var logoAt = logo.TranslatePoint(new Point(0, 0), rail);
        check(logoAt.X >= toggleAt.X + toggle.ActualWidth && Math.Abs(logoAt.Y + logo.ActualHeight / 2 - (toggleAt.Y + toggle.ActualHeight / 2)) < 1,
            $"an open sidebar shows the logo beside its toggle, on one row (logo {logoAt}, toggle {toggleAt})");
        // The pearl on the sidebar's edge marks the current page, and rolls to the next one.
        var pearl = (FrameworkElement)window.FindName("RailPearl");
        string Pearled(string entry)
        {
            var target = (FrameworkElement)window.FindName(entry);
            var centre = pearl.TranslatePoint(new Point(pearl.ActualWidth / 2, pearl.ActualHeight / 2), rail);
            var middle = target.TranslatePoint(new Point(0, target.ActualHeight / 2), rail).Y;
            return pearl.IsVisible && Math.Abs(centre.X - (rail.ActualWidth - 0.5)) < 0.5 && Math.Abs(centre.Y - middle) < 1 ? ""
                : $" (pearl {(pearl.IsVisible ? "shown" : "hidden")} at {centre.X:0.#},{centre.Y:0.#}; edge {rail.ActualWidth - 0.5:0.#}, entry middle {middle:0.#})";
        }
        var page = (string)typeof(MainWindow).GetField("currentPage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Call("ShowPage", "Home"); await Task.Delay(450); window.UpdateLayout();
        var placed = Pearled("HomeNav");
        check(placed == "", "the pearl sits on the sidebar's edge beside the current page" + placed);
        // The roll starts once the new page has laid itself out, which takes longer on a busy machine, so the check waits for it to land.
        Call("ShowPage", "Queues");
        for (var until = DateTime.UtcNow.AddMilliseconds(1500); DateTime.UtcNow < until && Pearled("QueuesNav") != "";) { await Task.Delay(50); window.UpdateLayout(); }
        placed = Pearled("QueuesNav");
        check(placed == "", "the pearl rolls to the page opened" + placed);
        check(Math.Abs(toggleAt.X - toggleAt.Y) < 0.5, $"the toggle sits as far from the sidebar's top as from its side ({toggleAt})");
        // Folding glides: the sidebar narrows and the entries move down for the logo a step at a time, while the toggle stays put.
        // A sidebar icon is cut off when its glyph runs past the room its button gives its content.
        var cut = new List<string>();
        IEnumerable<string> Cut() => RailEntries().Where(entry => entry.IsVisible).Select(entry =>
        {
            var presenter = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(System.Windows.Media.VisualTreeHelper.GetChild(entry, 0), 2);
            var glyph = (System.Windows.Controls.TextBlock)((System.Windows.Controls.Panel)((System.Windows.Controls.Button)entry).Content).Children[0];
            var room = presenter.TranslatePoint(new Point(presenter.ActualWidth, 0), rail).X;
            var end = glyph.TranslatePoint(new Point(glyph.FontSize, 0), rail).X;
            return end > room + 0.01 ? $"{entry.Name} at {rail.ActualWidth:0.#} wide: glyph ends {end:0.#}, room ends {room:0.#}" : null;
        }).OfType<string>();
        async Task<(List<double> Widths, List<double> Tops, bool Still)> Fold(bool collapsed)
        {
            var widths = new List<double>(); var tops = new List<double>(); var still = true;
            Call("SetRailCollapsed", collapsed);
            for (var until = DateTime.UtcNow.AddMilliseconds(700); DateTime.UtcNow < until;)
            {
                await Task.Delay(12); window.UpdateLayout();
                widths.Add(rail.ActualWidth); tops.Add(home.TranslatePoint(new Point(0, 0), rail).Y);
                still &= toggle.TranslatePoint(new Point(0, 0), rail) == toggleAt;
                cut.AddRange(Cut());
            }
            return (widths, tops, still);
        }
        static bool Steady(List<double> values, int direction) => values.Zip(values.Skip(1)).All(pair => (pair.Second - pair.First) * direction >= -0.01);
        var folding = await Fold(true);
        if (SystemParameters.ClientAreaAnimation)
            check(folding.Widths.Any(width => width > 64 && width < 212) && Steady(folding.Widths, -1) && Steady(folding.Tops, 1),
                $"folding narrows the sidebar and moves its entries smoothly ({string.Join(", ", folding.Widths.Distinct().Take(12).Select(width => width.ToString("0")))})");
        check(folding.Still, "the toggle stays in its spot while the sidebar folds");
        placed = Pearled("QueuesNav");
        check(placed == "", "the pearl stays on the edge of a folded sidebar" + placed);
        Call("ShowPage", "Home"); await Task.Delay(450); window.UpdateLayout();
        // Icon glyphs are an em square drawn from the left of their box.
        var glyphMiddle = icon.TranslatePoint(new Point(((System.Windows.Controls.TextBlock)icon).FontSize / 2, 0), rail).X;
        var logoMiddle = logo.TranslatePoint(new Point(logo.ActualWidth / 2, 0), rail).X;
        check(rail.ActualWidth == 64 && !label.IsVisible && !((FrameworkElement)window.FindName("RailBrandName")).IsVisible,
            $"a collapsed sidebar is a strip of icons ({rail.ActualWidth:0.#} wide)");
        check(logo.IsVisible && Math.Abs(logoMiddle - glyphMiddle) < 1,
            $"a collapsed sidebar keeps the logo, centred over its icons (logo middle {logoMiddle:0.#}, icon middle {glyphMiddle:0.#})");
        var foldedLogo = logo.TranslatePoint(new Point(0, 0), rail);
        check(toggle.IsVisible && toggle.TranslatePoint(new Point(0, 0), rail) == toggleAt && foldedLogo.Y >= toggleAt.Y + toggle.ActualHeight,
            $"folding leaves the toggle in its spot and drops the logo under it (toggle {toggle.TranslatePoint(new Point(0, 0), rail)}, was {toggleAt}; logo {foldedLogo})");
        check(Math.Abs(glyphMiddle - (rail.ActualWidth - 1) / 2) < 1, $"a collapsed sidebar centres its icons in the strip ({glyphMiddle:0.#} in {rail.ActualWidth - 1:0.#})");
        var toggleMiddle = toggle.TranslatePoint(new Point(toggle.ActualWidth / 2, 0), rail).X;
        check(Math.Abs(toggleMiddle - glyphMiddle) < 1, $"the toggle lines up with the icons ({toggleMiddle:0.#}, {glyphMiddle:0.#})");
        check(Equals(home.ToolTip, "Home") && content.ActualWidth > expanded, "collapsed icons name themselves and the page gains the width");
        // The fold ends by taking the faded words out of the layout; the icons must not move when it does.
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(rail).PixelsPerDip;
        (int Width, byte[] Pixels) Shoot()
        {
            window.UpdateLayout();
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Round(rail.ActualWidth * scale), (int)Math.Round(rail.ActualHeight * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(rail); var pixels = new byte[shot.PixelWidth * shot.PixelHeight * 4]; shot.CopyPixels(pixels, shot.PixelWidth * 4, 0);
            return (shot.PixelWidth, pixels);
        }
        string Ink((int Width, byte[] Pixels) shot, FrameworkElement entry)
        {
            var at = entry.TranslatePoint(new Point(0, 0), rail);
            int left = (int)(at.X * scale), top = (int)(at.Y * scale), right = (int)((at.X + entry.ActualWidth) * scale), bottom = (int)((at.Y + entry.ActualHeight) * scale);
            int Pixel(int x, int y) => BitConverter.ToInt32(shot.Pixels, (y * shot.Width + x) * 4);
            var ground = Pixel(left + 1, top + 1); int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (var y = top; y < bottom; y++) for (var x = left; x < right; x++)
                if (Pixel(x, y) != ground) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            return $"{x0},{y0}-{x1},{y1}";
        }
        Call("ShowRailText", true); var before = Shoot();
        Call("ShowRailText", false); var after = Shoot();
        var moved = RailEntries().Where(entry => entry.IsVisible && Ink(before, entry) != Ink(after, entry)).Select(entry => $"{entry.Name} {Ink(before, entry)} -> {Ink(after, entry)}").ToList();
        check(moved.Count == 0, "the icons stay put as the folded sidebar takes out its words" + (moved.Count > 0 ? $" (moved: {string.Join("; ", moved)})" : ""));
        IEnumerable<FrameworkElement> RailEntries() => new[] { "HomeNav", "SearchNav", "LibraryNav", "QueuesNav", "RoomNav", "SettingsNav" }.Select(name => (FrameworkElement)window.FindName(name));
        var unfolding = await Fold(false);
        if (SystemParameters.ClientAreaAnimation)
            check(unfolding.Widths.Any(width => width > 64 && width < 212) && Steady(unfolding.Widths, 1) && Steady(unfolding.Tops, -1),
                "unfolding widens the sidebar and moves its entries back smoothly");
        check(unfolding.Still, "the toggle stays in its spot while the sidebar unfolds");
        check(cut.Count == 0, "no sidebar icon is cut off, folded, open or in between" + (cut.Count > 0 ? $" ({string.Join("; ", cut.Distinct().Take(4))})" : ""));
        check(rail.ActualWidth == 212 && label.IsVisible && label.Opacity == 1 && home.ToolTip is null && content.ActualWidth == expanded, "the sidebar expands back to its labels");
        check(toggle.TranslatePoint(new Point(0, 0), rail) == toggleAt && logo.TranslatePoint(new Point(0, 0), rail) == logoAt, "unfolding leaves the toggle in its spot and puts the logo back beside it");
        Call("ShowPage", page); await Dispatcher.Yield(DispatcherPriority.Render);
    }

    // The item count rolls to a new number: only the digits that differ move, and it settles exactly where plain text would be.
    private static async Task CheckRollingCount(MainWindow window, RollingText count, Action<bool, string> check)
    {
        var original = count.Text;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(count).PixelsPerDip;
        IEnumerable<System.Windows.Controls.TextBlock> Blocks(DependencyObject node) => Enumerable.Range(0, System.Windows.Media.VisualTreeHelper.GetChildrenCount(node))
            .Select(i => System.Windows.Media.VisualTreeHelper.GetChild(node, i)).SelectMany(child => child is System.Windows.Controls.TextBlock block ? [block] : Blocks(child));
        string Shown() => string.Concat(Blocks(count).Where(block => block.IsVisible).Select(block => block.Text));
        // Rendering an element directly draws it at its offset in the window, off a bitmap its own size, and a brush of it
        // stretches whatever it draws, including pieces rolling out above it. So the whole window is drawn and the count's box
        // cut out of it.
        byte[] Shot()
        {
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            var full = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            full.Render(root);
            var at = count.TranslatePoint(new Point(0, 0), root);
            var box = new Int32Rect((int)Math.Round(at.X * scale), (int)Math.Round(at.Y * scale), (int)Math.Ceiling(count.ActualWidth * scale), (int)Math.Ceiling(count.ActualHeight * scale));
            var pixels = new byte[box.Width * box.Height * 4]; full.CopyPixels(box, pixels, box.Width * 4, 0);
            check(pixels.Any(value => value != 0), "the count's picture for comparison is not blank");
            return pixels;
        }
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        IEnumerable<IGrouping<DependencyObject, System.Windows.Controls.TextBlock>> Pieces() => Blocks(count).Where(block => block.IsVisible).GroupBy(block => System.Windows.Media.VisualTreeHelper.GetParent(block));
        double Shade() => ((UIElement)count.Child).Opacity;
        // Rolls to a new count, holds the pieces where the roll ends, and compares them with how they look once they settle.
        async Task<(int Rolling, int Still, bool Seamless, string Shown)> Roll(string from, string to)
        {
            count.Text = from; await Task.Delay(900);
            // Until the clock first ticks, a roll that has just begun shows its starting point, which must be what was there.
            var before = Shot(); count.Text = to; var begun = Shot();
            check(begun.SequenceEqual(before), $"starting a roll from \"{from}\" to \"{to}\" redraws nothing until the digits move");
            var pieces = Pieces().ToList();
            var rolling = pieces.Count(piece => piece.Count() > 1); var still = pieces.Count(piece => piece.Count() == 1);
            ((DispatcherTimer?)typeof(RollingText).GetField("settle", flags)!.GetValue(count))?.Stop();
            await Task.Delay(800); var ended = Shot();
            typeof(RollingText).GetMethod("Settle", flags)!.Invoke(count, null); var settled = Shot();
            return (rolling, still, ended.SequenceEqual(settled), Shown());
        }
        if (SystemParameters.ClientAreaAnimation)
        {
            var roll = await Roll("218 items", "242 items");
            check(roll.Rolling == 2 && roll.Still == 2, $"the count rolls only the digits that change (218 to 242: {roll.Rolling} rolling, {roll.Still} still)");
            foreach (var (from, to) in new[] { ("218 items", "242 items"), ("242 items", "1 item"), ("9 items", "10 items") })
            {
                roll = await Roll(from, to);
                check(roll.Seamless && roll.Shown == to, $"rolling from \"{from}\" to \"{to}\" settles without a flicker or jump when the roll ends (shows \"{roll.Shown}\")");
            }
        }
        // While a new count is on its way the old one dims and stays, instead of a status flashing between the two.
        count.Text = "42 items"; await Task.Delay(900);
        count.ShowPending("Searching…"); await Task.Delay(400);
        check(count.Text == "42 items" && Shade() < 0.6, $"a pending count dims and keeps its number (shows \"{count.Text}\" at {Shade():0.##})");
        count.Text = "57 items"; await Task.Delay(900);
        check(Shade() == 1 && Shown() == "57 items", "the new count rolls in at full strength");
        // A search that finds as many titles as before brings the count back to full strength too.
        count.ShowPending("Searching…"); await Task.Delay(400); count.Text = "57 items"; await Task.Delay(400);
        check(Shade() == 1 && Shown() == "57 items", $"a count that comes back unchanged is no longer dimmed (at {Shade():0.##})");
        count.Text = "Loading library…"; count.ShowPending("Searching…");
        check(count.Text == "Searching…", "with no count yet, the status shows");
        count.Text = original;
    }

    // Categories show their location and actions in one toolbar row; Search shows its field and filters there instead.
    private static async Task CheckSearchLayout(MainWindow window, Action<bool, string> check)
    {
        void Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
        var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        var scan = (System.Windows.Controls.Button)window.FindName("RescanButton");
        var status = (System.Windows.Controls.ComboBox)window.FindName("LibraryStatusFilter");
        var frame = (FrameworkElement)window.FindName("SearchFrame");
        var header = (FrameworkElement)window.FindName("SearchHeader");
        var crumbs = (FrameworkElement)window.FindName("LibraryBreadcrumbs");
        var toolbar = (FrameworkElement)window.FindName("LibraryToolbar");
        var grid = (FrameworkElement)window.FindName("PosterGrid");
        var content = (FrameworkElement)window.FindName("ContentArea");
        Call("NavigateLibrary", 0, null, null, null); await Task.Delay(100); window.UpdateLayout();
        check(header.Visibility == Visibility.Collapsed && crumbs.Visibility == Visibility.Visible && scan.Visibility == Visibility.Visible,
            "library categories show their location and actions rather than a search field");
        var height = toolbar.ActualHeight;
        check(height >= 40 && scan.ActualHeight <= height, "the library toolbar keeps one usable height for its location and actions");
        var original = scan.Content; scan.Content = "Cancel scan"; window.UpdateLayout();
        check(toolbar.ActualHeight == height, "scan button label changes keep the toolbar height");
        scan.Content = original; window.UpdateLayout();
        // The categories share their row with the count and actions, and a narrow window moves the actions under them rather than overlapping.
        var categories = (FrameworkElement)window.FindName("LibraryCategoryBar");
        // The actions line up with the category words; the thread runs under them.
        var words = (FrameworkElement)window.FindName("AllLibraryNav");
        var add = (FrameworkElement)window.FindName("AddTitleButton");
        var count = (FrameworkElement)window.FindName("ResultCount");
        Rect Box(FrameworkElement element) => new(element.TranslatePoint(new Point(0, 0), toolbar), new Size(element.ActualWidth, element.ActualHeight));
        string Crowded()
        {
            var parts = new[] { categories, count, scan, add }.Where(element => element.IsVisible).ToList();
            var problems = parts.SelectMany((a, i) => parts.Skip(i + 1).Where(b => Rect.Intersect(Box(a), Box(b)) is { IsEmpty: false, Width: > 0.5, Height: > 0.5 }).Select(b => $"{a.Name} overlaps {b.Name}"))
                .Concat(parts.Where(element => Box(element).Right > toolbar.ActualWidth + 0.5).Select(element => $"{element.Name} runs past the toolbar"))
                // A bar given less room than its segments need shrinks to that room and cuts its last segments off.
                .Concat(SegmentsEnd() > categories.ActualWidth + 0.5 ? [$"the categories are cut off (segments end at {SegmentsEnd():0.#} in a bar {categories.ActualWidth:0.#} wide)"] : Array.Empty<string>());
            return string.Join("; ", problems);
        }
        double SegmentsEnd()
        {
            var last = ((System.Windows.Controls.Panel)window.FindName("LibraryCategories")).Children.OfType<FrameworkElement>().LastOrDefault(segment => segment.IsVisible);
            return !categories.IsVisible || last is null ? 0 : last.TranslatePoint(new Point(last.ActualWidth, 0), categories).X;
        }
        // Box middles agree to within the nudges that put the text on one baseline, which is checked exactly further on.
        double Middle(FrameworkElement element) => Box(element).Top + element.ActualHeight / 2;
        check(categories.IsVisible && Math.Abs(Middle(words) - Middle(scan)) < 1.5 && Crowded() == "",
            $"the library's categories, count and actions share one row ({Box(categories)}, {Box(scan)}{(Crowded() is { Length: > 0 } c ? "; " + c : "")})");
        var wide = window.Width; window.Width = window.MinWidth; await Task.Delay(150); window.UpdateLayout();
        check(Crowded() == "" && (Math.Abs(Middle(words) - Middle(scan)) < 1.5 || Box(scan).Top >= Box(categories).Bottom),
            $"a narrow window keeps the categories and actions apart ({toolbar.ActualWidth:0} wide{(Crowded() is { Length: > 0 } n ? ": " + n : "")})");
        // Too narrow for the categories and actions side by side: the actions take a row of their own and the categories keep their whole width.
        toolbar.Width = SegmentsEnd() + 140; await Task.Delay(150); window.UpdateLayout();
        check(Crowded() == "" && Box(scan).Top >= Box(categories).Bottom,
            $"a toolbar too narrow for one row puts the actions under the categories, uncut ({toolbar.ActualWidth:0} wide{(Crowded() is { Length: > 0 } w ? ": " + w : "")})");
        // A little short of room: Add title shortens to its plus and everything stays on one row.
        var addWidths = ((double Full, double Compact))typeof(MainWindow).GetField("addTitleWidths", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        check(addWidths.Compact < addWidths.Full - 30, $"Add title is much narrower as only its plus ({addWidths.Compact:0.#} against {addWidths.Full:0.#})");
        toolbar.Width = SegmentsEnd() + 16 + count.DesiredSize.Width + scan.DesiredSize.Width + addWidths.Compact + 2; await Task.Delay(150); window.UpdateLayout();
        check(Crowded() == "" && Math.Abs(Middle(words) - Middle(scan)) < 1.5 && ((System.Windows.Controls.Button)add).Content is not string,
            $"a toolbar a little short of room shortens Add title to its plus and keeps one row ({toolbar.ActualWidth:0} wide{(Crowded() is { Length: > 0 } s ? ": " + s : "")})");
        toolbar.Width = double.NaN;
        window.Width = wide; await Task.Delay(150); window.UpdateLayout();
        check(Math.Abs(Middle(words) - Middle(scan)) < 1.5 && toolbar.ActualHeight == height, "widening the window brings the actions back beside the categories");
        await CheckRollingCount(window, (RollingText)count, check);
        // The words, the count and the buttons share one baseline, measured as where the ink of letters without descenders ends.
        ((RollingText)count).Text = "218 items"; await Task.Delay(900);
        {
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(toolbar).PixelsPerDip;
            var root = (FrameworkElement)window.Content; window.UpdateLayout();
            var full = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            full.Render(root);
            var stride = full.PixelWidth * 4; var pixels = new byte[stride * full.PixelHeight]; full.CopyPixels(pixels, stride, 0);
            // The last row where the element's right-hand part (from `from`, a fraction of its width) has ink unlike the canvas.
            int InkBottom(FrameworkElement element, double from)
            {
                var at = element.TranslatePoint(new Point(0, 0), root);
                int x0 = (int)((at.X + element.ActualWidth * from) * scale), x1 = (int)((at.X + element.ActualWidth) * scale) - 1;
                int y0 = (int)(at.Y * scale), y1 = (int)((at.Y + element.ActualHeight) * scale) - 1;
                var ground = pixels.AsSpan((y0 * stride) + (x0 * 4), 4).ToArray(); var last = -1;
                for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
                {
                    var i = y * stride + x * 4;
                    if (Math.Abs(pixels[i] - ground[0]) + Math.Abs(pixels[i + 1] - ground[1]) + Math.Abs(pixels[i + 2] - ground[2]) > 90) { last = y; break; }
                }
                return last;
            }
            // "items" is the right part of the count; "218" has no descending figures either.
            int lineWord = InkBottom((FrameworkElement)window.FindName("AnimeLibraryNav"), 0), lineCount = InkBottom(count, 0.55), lineScan = InkBottom(scan, 0);
            check(Math.Abs(lineCount - lineWord) <= 1 && Math.Abs(lineScan - lineWord) <= 1,
                $"the categories, the count and the buttons share one baseline (words {lineWord}, count {lineCount}, Rescan {lineScan} at {scale:0.##}x)");
        }
        Call("OpenSearch", false); await Task.Delay(100); window.UpdateLayout();
        check(header.Visibility == Visibility.Visible && crumbs.Visibility == Visibility.Collapsed && scan.Visibility == Visibility.Collapsed,
            "Search shows its field and filters in place of the category's location and actions");
        check(System.Windows.Automation.AutomationProperties.GetItemStatus((DependencyObject)window.FindName("SearchNav")) == "Selected" &&
            System.Windows.Automation.AutomationProperties.GetItemStatus((DependencyObject)window.FindName("LibraryNav")) == "" &&
            System.Windows.Automation.AutomationProperties.GetItemStatus((DependencyObject)window.FindName("AllLibraryNav")) == "",
            "the sidebar marks Search, and no library category, as current");
        var location = grid.TranslatePoint(new Point(0, 0), content);
        var width = frame.ActualWidth;
        search.Focus(); await Dispatcher.Yield(DispatcherPriority.Input); window.UpdateLayout();
        check(grid.TranslatePoint(new Point(0, 0), content) == location, "focusing search does not move the poster grid");
        search.Text = "Movie"; await Task.Delay(300); window.UpdateLayout();
        check(frame.ActualWidth == width && grid.TranslatePoint(new Point(0, 0), content) == location, "search text and clear button do not resize the field or move the grid");
        search.Clear(); await Task.Delay(300);
        var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("LibraryScroll");
        // The slim scroll bar sits in the page's margin beside the grid, so showing it never changes the grid's width.
        var presenter = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        var bar = (FrameworkElement)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        var shown = scroll.VerticalScrollBarVisibility;
        scroll.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden; window.UpdateLayout(); var hiddenWidth = presenter.ActualWidth;
        scroll.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Visible; window.UpdateLayout(); var visibleWidth = presenter.ActualWidth;
        var barLeft = bar.TranslatePoint(new Point(0, 0), grid).X;
        scroll.VerticalScrollBarVisibility = shown; window.UpdateLayout();
        check(shown == System.Windows.Controls.ScrollBarVisibility.Auto && hiddenWidth == visibleWidth && barLeft >= grid.ActualWidth && bar.ActualWidth <= 10,
            $"the poster grid has a slim scroll bar beside it that never changes its width (grid {grid.ActualWidth:0}, bar at {barLeft:0}, content {hiddenWidth:0} / {visibleWidth:0})");
        var menus = new[] { "SearchAudio", "SearchSubtitles", "SearchGenre", "SearchDecade", "SearchLengthFilter", "SearchRating", "LibraryStatusFilter" }
            .Select(name => (System.Windows.Controls.ComboBox)window.FindName(name)).ToArray();
        check(frame.ActualHeight >= 40 && menus.All(menu => menu.ActualHeight == status.ActualHeight && menu.ActualHeight >= 32),
            "filter menus share one usable height under a full-height search field");
        check(menus[0].Items.Count >= 1 && ((SearchChoice)menus[0].Items[0]).Code == "" && menus[1].Items.OfType<SearchChoice>().Any(x => x.Code == LibrarySearchFilters.NoSubtitles),
            "language menus start with Any, and subtitles offer No subtitles");
        // An open menu takes the next click to close itself; a click on another pill or type of title still acts.
        var length = menus[4]; var rating = menus[5];
        var kinds = ((System.Windows.Controls.Panel)window.FindName("SearchKinds")).Children.OfType<System.Windows.Controls.RadioButton>().ToArray();
        Point Center(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), header);
        length.IsDropDownOpen = true;
        Call("ClickThrough", length, Center(rating)); await Task.Delay(100);
        check(rating.IsDropDownOpen, "clicking another filter while a menu is open opens that filter");
        rating.IsDropDownOpen = false; length.IsDropDownOpen = true;
        Call("ClickThrough", length, Center(kinds[1])); await Task.Delay(100);
        check(kinds[1].IsChecked == true, "clicking a type of title while a menu is open picks it");
        length.IsDropDownOpen = false; kinds[0].IsChecked = true;
        rating.SelectedIndex = 2; window.UpdateLayout();
        var clear = LibraryScrollingChecks.Descendants(rating).OfType<System.Windows.Controls.Button>().Single(button => button.Command is not null);
        length.IsDropDownOpen = true;
        Call("ClickThrough", length, Center(clear)); await Task.Delay(100);
        check(rating.SelectedIndex == 0 && !rating.IsDropDownOpen, "clicking a filter's clear button while another menu is open clears it");
        length.IsDropDownOpen = false; await Task.Delay(400);
        // A filter menu grows out of its pill like the player's menus, and folds back into it before it closes.
        var popup = (PlayerMenuPopup)status.Template.FindName("PART_Popup", status);
        void Snapshot(string name)
        {
            if (popup.Child is not FrameworkElement child || child.ActualWidth < 1) return;
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)child.ActualWidth, (int)child.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(child);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            Directory.CreateDirectory("artifacts/ui-review/screens");
            using var output = File.Create($"artifacts/ui-review/screens/{name}.png"); encoder.Save(output);
        }
        status.IsDropDownOpen = true; await Task.Delay(120); Snapshot("filter-menu-growing");
        await Task.Delay(600); Snapshot("filter-menu-open");
        check(popup.IsOpen && popup.Child?.Clip is null, "a filter menu opens fully, with nothing left clipping it");
        status.IsDropDownOpen = false;
        var folding = popup.IsOpen;
        await Task.Delay(500);
        check(!popup.IsOpen && (folding || !SystemParameters.ClientAreaAnimation), "a filter menu folds back into its pill before it closes");
        status.Focus(); window.UpdateLayout();
    }

    private static async Task CheckBackNavigation(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        var filter = (System.Windows.Controls.ComboBox)window.FindName("LibraryStatusFilter");
        var anime = ((System.Windows.Controls.Panel)window.FindName("SearchKinds")).Children.OfType<System.Windows.Controls.RadioButton>().Single(x => (string)x.Tag == "3");
        bool Searching() => (bool)Field("librarySearch")!;
        Call("OpenSearch", false); search.Text = "Navigation"; filter.SelectedIndex = 1; anime.IsChecked = true;
        Call("NavigateLibrary", 4, null!, null!, null!);
        check(!Searching() && (int)Field("libraryCategory")! == 4, "a library category leaves Search");
        Call("BrowseHistory", true);
        check(Searching() && search.Text == "Navigation" && filter.SelectedIndex == 1 && anime.IsChecked == true, "Back from a category returns to Search with its text and filters");
        Call("BrowseHistory", false);
        check(!Searching() && (int)Field("libraryCategory")! == 4, "Forward returns to the category");
        Call("BrowseHistory", true);
        Call("ShowPage", "Settings");
        Call("BrowseHistory", true); await Task.Delay(150);
        check(Field("currentPage") as string == "Library" && Searching() && search.Text == "Navigation" && filter.SelectedIndex == 1, "back restores Search, its text and its filters");
        Call("BrowseHistory", false);
        check(Field("currentPage") as string == "Settings", "forward restores the page left by Back");
        Call("BrowseHistory", true); Call("ShowPage", "Queues");
        check(!(bool)Call("BrowseHistory", false)!, "a new navigation clears forward history");
        var mouse = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.XButton1) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent };
        Call("NavigationMouseUp", window, mouse);
        check(mouse.Handled && Field("currentPage") as string == "Library", "mouse back button uses the same browsing history");
        var media = ((IEnumerable<MediaItem>)Field("items")!).First(x => x.Series is not null);
        Call("NavigateLibrary", 2, LibraryIdentity.ShowKey(media), media.Kind, 1);
        Call("Select", media); Call("BrowseHistory", true);
        check(Field("currentPage") as string == "Library" && (int?)Field("browseSeason") == 1, "Back from an episode returns to its previous season view");
        check((bool)Call("GestureControl", search)!, "swipe navigation ignores text entry controls");
        Call("ShowPage", "Settings");
        var hook = typeof(MainWindow).GetMethod("NavigationWindowMessage", flags)!;
        void Horizontal(int delta)
        {
            hook.Invoke(window, new object[] { IntPtr.Zero, 0x020e, new IntPtr((long)(ushort)(short)delta << 16), IntPtr.Zero, false });
        }
        Horizontal(-120); Horizontal(-120);
        check(Field("currentPage") as string == "Settings", "small horizontal movements do not accidentally navigate");
        Horizontal(-120); Horizontal(-360);
        check(Field("currentPage") as string == "Library", "horizontal swipe goes Back once and ignores its inertia");
        Call("OpenSearch", false); Call("ShowPage", "Home"); Call("LibraryNavClick", window, new RoutedEventArgs());
        check(Field("currentPage") as string == "Library" && !Searching() &&
            System.Windows.Automation.AutomationProperties.GetItemStatus((DependencyObject)window.FindName("LibraryNav")) == "Selected",
            "the Library entry opens the library, not Search, after Search was used from another page");
        Call("ShowPage", "Home"); Call("OpenSearch", false); Call("BrowseHistory", true);
        check(Field("currentPage") as string == "Home" && !Searching(), "Back from Search to Home leaves Search behind");
        Call("NavigateLibrary", 0, null!, null!, null!);
    }

    private static void CheckQueueEditing(MainWindow window, LibraryStore store, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        void Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        var saved = (List<SavedQueue>)Field("savedQueues");
        var media = ((IEnumerable<MediaItem>)Field("items")).Take(2).ToArray();
        var fixture = new SavedQueue("queue-edit-fixture", "Test queue", [media[0].Id, "missing-media", media[0].Id, media[1].Id]);
        saved.Add(fixture); Call("SelectPlaybackQueue", fixture); Call("OpenQueue", fixture.Id); Set("queuePosition", 1);
        var list = (System.Windows.Controls.ListBox)window.FindName("SavedQueueItems");
        check(list.Items.Count == 4, "queue cards retain missing entries and duplicate occurrences");
        Call("MoveSavedEntry", fixture, 2, 0);
        var reordered = saved.Single(x => x.Id == fixture.Id);
        check(reordered.MediaIds.SequenceEqual([media[0].Id, media[0].Id, "missing-media", media[1].Id]), "drag reorder moves one duplicate occurrence to the requested position");
        check((int)Field("queuePosition") == 0, "reordering keeps the exact playing duplicate as the active entry");
        Call("ApplyQueueOrder", fixture, new[] { 0 });
        check(saved.Single(x => x.Id == fixture.Id).MediaIds.Length == 4, "stale queue edits cannot overwrite newer ordering");
        var button = new System.Windows.Controls.Button { Tag = list.Items[2] };
        Call("RemoveSavedItem", button, new RoutedEventArgs());
        var afterRemoval = saved.Single(x => x.Id == fixture.Id);
        check(afterRemoval.MediaIds.SequenceEqual([media[0].Id, media[0].Id, media[1].Id]), "card remove targets a missing entry without removing another video");
        check((int)Field("queuePosition") == 0, "removing another card preserves the playback position");
        button.Tag = list.Items[0]; Call("RemoveSavedItem", button, new RoutedEventArgs());
        check((int)Field("queuePosition") == -1, "removing the current entry leaves the following duplicate next to play");
        var persisted = Wire.Read<SavedQueue[]>(store.Setting("queues")!).Single(x => x.Id == fixture.Id);
        check(persisted.MediaIds.SequenceEqual([media[0].Id, media[1].Id]), "queue reordering and per-card deletion persist immediately");
        Call("ShowPage", "Queues"); window.UpdateLayout();
        check(list.ActualHeight > 100 && list.AllowDrop, "redesigned queue keeps a usable drag-and-drop list");
        Call("ShowAllQueues");
        check(((FrameworkElement)window.FindName("QueueGalleryView")).Visibility == Visibility.Visible, "the queue page returns to the gallery of queues");
        Call("BrowseHistory", true);
        check(Field("openQueueId") as string == fixture.Id && ((FrameworkElement)window.FindName("QueueDetailView")).Visibility == Visibility.Visible,
            "Back from the gallery reopens the queue that was open");
        var picker = (System.Windows.Controls.ComboBox)window.FindName("PlayerQueuePicker");
        check(picker.Items.Cast<SavedQueue>().All(item => item.Name != "No queue"), "the player queue picker lists only queues; the switch turns the queue off");
        Call("QueueModeOffClicked", window, new RoutedEventArgs());
        check(((List<MediaItem>)Field("queue")).Count == 0 && (int)Field("queuePosition") == -1,
            "No queue disables active queue playback and clears its cursor");
        check(saved.Single(item => item.Id == fixture.Id).MediaIds.SequenceEqual([media[0].Id, media[1].Id]),
            "No queue preserves saved queue contents");
        check(store.Setting("activeQueue") == "" && Wire.Read<string[]>(store.Setting("queue")!).Length == 0,
            "No queue persists independently of saved queues");
        var lastQueue = store.Setting("lastQueue"); store.Setting("lastQueue", "");
        void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
        var queueMenu = (PlayerMenuPopup)window.FindName("PlayerQueueMenu");
        Call("ShowPage", "Room"); Call("ShowPlayerControls");
        for (var wait = 0; wait < 200 && !queueMenu.IsLoaded; wait++) { Drain(); Thread.Sleep(10); }
        queueMenu.IsOpen = true; Drain();
        var sizer = (MenuSizer)queueMenu.Child; var menuCard = (FrameworkElement)sizer.Child;
        for (var wait = 0; wait < 200 && menuCard.ActualHeight == 0; wait++) { Drain(); Thread.Sleep(10); }
        var episodesHeight = menuCard.ActualHeight;
        Call("QueueModeOnClicked", window, new RoutedEventArgs());
        Drain(); // Anything queued to open the picker has run by now.
        var resizeStarted = sizer.IsResizing;
        for (var wait = 0; wait < 50 && !resizeStarted; wait++) { Drain(); Thread.Sleep(10); resizeStarted = sizer.IsResizing; }
        for (var wait = 0; wait < 200 && sizer.IsResizing; wait++) { Drain(); Thread.Sleep(10); }
        menuCard.Measure(new Size(menuCard.ActualWidth + menuCard.Margin.Left + menuCard.Margin.Right, double.PositiveInfinity));
        check((resizeStarted || !SystemParameters.ClientAreaAnimation) && !sizer.IsResizing && double.IsNaN(menuCard.Height) &&
            Math.Abs(menuCard.ActualHeight - (menuCard.DesiredSize.Height - menuCard.Margin.Top - menuCard.Margin.Bottom)) < 1 && sizer.ActualHeight >= menuCard.DesiredSize.Height - 1,
            $"switching modes eases the queue menu from {episodesHeight:F0} to its new height inside a popup that holds it");
        check(queueMenu.IsOpen && !picker.IsDropDownOpen && ((FrameworkElement)window.FindName("QueuePickerRow")).Visibility == Visibility.Visible,
            "switching to queue mode shows the queue picker without opening it");
        queueMenu.CloseNow(); Call("ShowPage", "Queues");
        Call("QueueModeOffClicked", window, new RoutedEventArgs()); store.Setting("lastQueue", lastQueue ?? "");
        var card = new LibraryCardView(new(media[0].Title, "", null, media[0], "movie"));
        var cards = (ObservableCollection<LibraryCardView>)Field("libraryCards"); cards.Add(card);
        var watchedButton = new System.Windows.Controls.Button { DataContext = card };
        var watchedClick = new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent);
        Call("ToggleCardWatched", watchedButton, watchedClick);
        check(card.IsWatched && store.Setting("watched:" + media[0].Id) == "true" && watchedClick.Handled,
            "card action marks watched immediately without opening the video's details");
        Call("ToggleCardWatched", watchedButton, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        check(!card.IsWatched && store.Setting("watched:" + media[0].Id) == "false",
            "card action clears watched state immediately");
        cards.Remove(card);
        var episode = ((IEnumerable<MediaItem>)Field("items")).First(item => item.Series is not null && item.Season == 1);
        var seasonCard = new LibraryCardView(new("Season 1", "", null, episode, "season", 1)); cards.Add(seasonCard);
        watchedButton.DataContext = seasonCard; Call("ToggleCardWatched", watchedButton, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var showEpisodes = ((IEnumerable<MediaItem>)Field("items")).Where(item => LibraryIdentity.SameShow(item, episode)).ToArray();
        check(seasonCard.IsWatched && showEpisodes.Where(item => item.Season == 1).All(item => store.Setting("watched:" + item.Id) == "true") &&
            showEpisodes.Where(item => item.Season != 1).All(item => store.Setting("watched:" + item.Id) != "true"),
            "season checkmark toggles only that season's episodes");
        var showCard = new LibraryCardView(new(episode.Series!, "", null, episode, "series")); cards.Add(showCard);
        watchedButton.DataContext = showCard; Call("ToggleCardWatched", watchedButton, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        check(showCard.IsWatched && showEpisodes.All(item => store.Setting("watched:" + item.Id) == "true"),
            "show checkmark marks every season watched");
        Call("ToggleCardWatched", watchedButton, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        check(!showCard.IsWatched && !seasonCard.IsWatched && showEpisodes.All(item => store.Setting("watched:" + item.Id) == "false"),
            "show checkmark clears all watched episode and season states");
        cards.Remove(seasonCard); cards.Remove(showCard);
    }

    private static void CheckRoomSettings(MainWindow window, LibraryStore store, Action<bool, string> Check)
    {
        var rooms = (ObservableCollection<SavedRoom>)typeof(MainWindow).GetField("savedRooms", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var first = new SavedRoom("room-one", "Movie night", "https://room.test", new string('A', 24), new string('1', 64));
        var second = new SavedRoom("room-two", "Anime night", first.Server, new string('B', 24), new string('2', 64));
        var guest = new SavedRoom("room-guest", "Friend's room", first.Server, new string('C', 24));
        rooms.Add(first); rooms.Add(second); rooms.Add(guest);
        var list = (System.Windows.Controls.ListBox)window.FindName("SavedRoomsList");
        var controls = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("SavedRoomSharedControls");
        list.SelectedItem = first;
        // With nothing watched yet, the hero still shows the selected room with a placeholder instead of hiding.
        string HeroText(string name) => ((System.Windows.Controls.TextBlock)window.FindName(name)).Text;
        FrameworkElement HeroPart(string name) => (FrameworkElement)window.FindName(name);
        Check(HeroPart("RoomHero").Visibility == Visibility.Visible && HeroText("RoomHeroLiveText") == "Closed" && HeroText("RoomHeroTitle") == "Nothing watched here yet" &&
            ((System.Windows.Controls.Button)window.FindName("RoomHeroResume")).Content as string == "Open room" &&
            ((System.Windows.Controls.Button)window.FindName("RoomHeroSecondary")).Content as string == "Copy invite" && HeroPart("RoomHeroSecondary").Visibility == Visibility.Visible &&
            HeroPart("RoomHeroBadge").Visibility == Visibility.Visible && HeroText("RoomHeroBadgeText") == "M", "an unused host room shows a closed hero with Open room, Copy invite and its initial");
        list.SelectedItem = guest;
        Check(HeroText("RoomHeroLiveText") == "Offline" && ((System.Windows.Controls.Button)window.FindName("RoomHeroResume")).Content as string == "Connect" &&
            HeroPart("RoomHeroSecondary").Visibility == Visibility.Collapsed && HeroText("RoomHeroBadgeText") == "F", "an unused guest room shows an offline hero with Connect");
        Check(HeroPart("SavedRoomHostControls").Visibility == Visibility.Collapsed && System.Windows.Controls.Grid.GetRow(HeroPart("SavedRoomActions")) == 0 &&
            HeroText("SavedRoomGuestHeading") == "People" && HeroText("SavedRoomSessionTitle") == "Nothing watched here yet", "a guest room drops the permissions tile and lists people instead of guests");
        list.SelectedItem = first;
        Check(HeroText("SavedRoomCode") == "AAAAAA AAAAAA AAAAAA AAAAAA" && HeroPart("SavedRoomHostControls").Visibility == Visibility.Visible &&
            System.Windows.Controls.Grid.GetRow(HeroPart("SavedRoomActions")) == 2 && HeroText("SavedRoomGuestHeading") == "Guests", "a host room shows its grouped code and the permissions tile");
        string Initials(string text, int count) => (string)typeof(MainWindow).GetMethod("Initials", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text, count])!;
        Check(Initials("Spy x Family · S1E4", 2) == "SF" && Initials("The Office", 2) == "O" && Initials("holiday_video_2019 mkv", 2) == "HV" && Initials("!!!", 2) == "?",
            "title cards use the leading words' initials");
        int Hue(string title) => (int)typeof(MainWindow).GetMethod("TitleHue", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [title])!;
        Check(Hue("Dune") == 292 && Hue("Arrival") == 92, "a title keeps the same card colour across sessions");
        list.SelectedItem = first;
        Check(controls.IsEnabled && controls.IsChecked == false, "saved host room exposes its playback-control setting");
        controls.IsChecked = true;
        var browsing = (System.Windows.Controls.CheckBox)window.FindName("SavedRoomLibraryBrowsing");
        Check(browsing.IsEnabled && browsing.IsChecked == false, "library permission is integrated into host room settings and defaults off");
        browsing.IsChecked = true;
        Check(store.Setting("room:" + first.Server + ":" + first.Code + ":libraryBrowsing") == "true", "library permission persists per room while offline");
        typeof(MainWindow).GetMethod("SavedRoomControlsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [controls, new RoutedEventArgs()]);
        Check(store.Setting("room:" + first.Server + ":" + first.Code + ":sharedControls") == "true", "offline room setting saves immediately");
        list.SelectedItem = second;
        Check(browsing.IsChecked == false, "library permission is isolated between saved rooms");
        Check(controls.IsChecked == false, "each room has an independent shared-controls setting");
        list.SelectedItem = first;
        Check(controls.IsChecked == true, "returning to a saved room restores its control setting");
        list.SelectedItem = guest;
        Check(!browsing.IsEnabled, "guest cannot change host library permission");
        Check(!controls.IsEnabled, "guests cannot edit room settings");
        var rename = (System.Windows.Controls.Button)window.FindName("RenameSavedRoomButton");
        Check(!rename.IsEnabled, "guest room name is read only");
        typeof(MainWindow).GetMethod("RenameHostRoom", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [guest, "Guest override"]);
        Check(rooms.Single(x => x.Id == guest.Id).Name == guest.Name, "guest cannot rename a room through its handler");
        list.SelectedItem = first;
        Check(rename.IsEnabled, "host can rename their saved room offline");
        typeof(MainWindow).GetMethod("RenameHostRoom", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [first, "New movie night"]);
        Check(rooms.Single(x => x.Id == first.Id).Name == "New movie night" && store.Setting("room:" + first.Server + ":" + first.Code + ":name") == "New movie night", "host rename persists for reconnection");
        var roomField = typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previousRoom = roomField.GetValue(window);
        var waitingClient = new RoomClient();
        var waitingSnapshot = new RoomSnapshot([new("host", "Host", true, true), new("guest", "Guest", false, false)], null, null, false, [], Name: "Host's shared name");
        typeof(RoomClient).GetProperty(nameof(RoomClient.ServerAddress))!.SetValue(waitingClient, guest.Server);
        typeof(RoomClient).GetProperty(nameof(RoomClient.Identity))!.SetValue(waitingClient, new Welcome(guest.Code, "guest", false, [], false));
        typeof(RoomClient).GetProperty(nameof(RoomClient.Snapshot))!.SetValue(waitingClient, waitingSnapshot);
        roomField.SetValue(window, waitingClient);
        try
        {
            list.SelectedItem = guest;
            typeof(MainWindow).GetMethod("SyncCurrentRoomName", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [waitingSnapshot]);
            Check(((System.Windows.Controls.TextBlock)window.FindName("SavedRoomHeading")).Text == waitingSnapshot.Name &&
                rooms.Single(x => x.Id == guest.Id).Name == waitingSnapshot.Name && !rename.IsEnabled,
                "waiting guest replaces its room-code label with the host name and cannot rename it");
            Check(Wire.Read<SavedRoom[]>(store.Setting("rooms")!).Single(x => x.Id == guest.Id).Name == waitingSnapshot.Name,
                "waiting guest's canonical room name is saved for the next app launch");
            void RefreshLibraryUi() => typeof(MainWindow).GetMethod("RefreshSharedLibrary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, []);
            RefreshLibraryUi();
            Check(((FrameworkElement)window.FindName("GuestLibraryStatus")).Visibility == Visibility.Visible &&
                ((FrameworkElement)window.FindName("LocalLibraryContent")).Visibility == Visibility.Visible,
                "guest library shares the regular library layout within the Library tab");
            Check(!(bool)typeof(MainWindow).GetField("guestBrowsePending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!, "waiting guest cannot start browsing before direct admission");
            ((TaskCompletionSource)typeof(RoomClient).GetField("directConnected", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(waitingClient)!).TrySetResult();
            var busy = waitingSnapshot with { LibraryBrowsing = true, LibraryActivity = new("other", "Alice", "Example", "pilot", Wire.Now) };
            typeof(RoomClient).GetProperty(nameof(RoomClient.Snapshot))!.SetValue(waitingClient, busy);
            RefreshLibraryUi();
            Check(!(bool)typeof(MainWindow).GetField("guestBrowsePending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! &&
                ((System.Windows.Controls.TextBlock)window.FindName("GuestLibraryStatus")).Text.Contains("Alice"), "busy library identifies the current guest and locks the browse action");
            typeof(RoomClient).GetProperty(nameof(RoomClient.Snapshot))!.SetValue(waitingClient, busy with { LibraryActivity = null });
            RefreshLibraryUi();
            Check(!((System.Windows.Controls.TextBlock)window.FindName("GuestLibraryStatus")).Text.Contains("Alice"), "released browsing slot becomes available without reconnecting");
            var pageField = typeof(MainWindow).GetField("currentPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var previousPage = pageField.GetValue(window);
            typeof(MainWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Library"]);
            typeof(RoomClient).GetProperty(nameof(RoomClient.Snapshot))!.SetValue(waitingClient, busy with { LibraryActivity = new("guest", "Guest", "All titles", "", Wire.Now) });
            var requestNumber = (long)typeof(MainWindow).GetField("guestBrowseRequest", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var catalog = new LibraryBrowsePage([
                new("1", "The Grand Budapest Hotel", "Movie · 2014", "movie", null, "A concierge and his lobby boy become unlikely friends during a journey across a changing Europe."),
                new("2", "Cowboy Bebop", "1 season · 26 episodes", "series", null, "A crew of bounty hunters travels through the solar system."),
                new("3", "Severance", "2 seasons · 19 episodes", "series", null, "Office workers discover the consequences of separating their memories.")], 3, 0, "All titles");
            var dispatch = typeof(MainWindow).GetMethod("OnRoomMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void Receive(WireMessage message) => ((Task)dispatch.Invoke(window, [message])!).GetAwaiter().GetResult();
            Receive(new WireMessage("catalog-page", Number: requestNumber, Data: Wire.Serialize(catalog)));
            var entries = (System.Windows.Controls.ItemsControl)window.FindName("PosterGrid");
            Check(entries.Items.Count == 3, "active guest renders host catalog cards in the Library tab");
            Receive(new WireMessage("catalog-page", Number: requestNumber - 1, Data: Wire.Serialize(catalog with { Entries = [] })));
            Check(entries.Items.Count == 3, "stale library responses cannot replace current results");
            typeof(MainWindow).GetField("guestBrowsePending", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            Receive(new WireMessage("catalog-error", Number: requestNumber, Text: "The host has no available titles."));
            Check(((System.Windows.Controls.TextBlock)window.FindName("GuestLibraryStatus")).Text == "The host has no available titles." &&
                !(bool)typeof(MainWindow).GetField("guestBrowsePending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!, "catalog error dispatch ends loading and displays the host response");
            Receive(new WireMessage("catalog-page", Number: requestNumber, Data: Wire.Serialize(catalog)));
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var imageFile = File.Create(Path.GetFullPath("artifacts/shared-library-ui.png"))) encoder.Save(imageFile);
            typeof(RoomClient).GetProperty(nameof(RoomClient.Snapshot))!.SetValue(waitingClient, busy with { LibraryBrowsing = false, LibraryActivity = null });
            RefreshLibraryUi();
            Check(entries.Items.Count == 0 && !(bool)typeof(MainWindow).GetField("guestBrowsePending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!, "revocation immediately clears visible catalog and pending requests");
            typeof(MainWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [previousPage!]);
        }
        finally
        {
            roomField.SetValue(window, previousRoom);
            typeof(MainWindow).GetMethod("RefreshSharedLibrary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, []);
            waitingClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            list.SelectedItem = rooms.Single(x => x.Id == first.Id);
        }
        Check(!first.ToString().Contains(first.HostKey!), "saved-room accessibility labels exclude the private host key");
    }

    private static async Task CheckScrolling(Action<bool, string> check)
    {
        var innerContent = new System.Windows.Controls.Border { Height = 1200 };
        var inner = new System.Windows.Controls.ScrollViewer { Height = 100, Content = innerContent };
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(inner);
        content.Children.Add(new System.Windows.Controls.Border { Height = 2000 });
        var outer = new System.Windows.Controls.ScrollViewer { Content = content };
        var host = new Window { Content = outer, Width = 300, Height = 220, Left = -20000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        host.Show();
        try
        {
            await Task.Delay(50);
            check(PrecisionScrolling.GetEnabled(outer) && PrecisionScrolling.GetEnabled(inner), "precision scrolling is enabled by the application style");
            async Task Flush() { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
            void Wheel(System.Windows.UIElement source, int delta) => source.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = System.Windows.UIElement.PreviewMouseWheelEvent });
            var step = SystemParameters.WheelScrollLines < 0 ? inner.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
            inner.ScrollToVerticalOffset(100); await Flush();
            Wheel(innerContent, -6); await Flush();
            check(Math.Abs(inner.VerticalOffset - (100 + step / 20)) < .1 && outer.VerticalOffset == 0, "tiny trackpad delta moves proportionally inside the nearest scroll viewer");
            inner.ScrollToVerticalOffset(100); await Flush();
            for (var index = 0; index < 20; index++) Wheel(innerContent, -6);
            await Flush();
            check(Math.Abs(inner.VerticalOffset - (100 + step)) < .1, "a burst of small trackpad events totals one wheel notch without losing movement");
            Wheel(innerContent, 120); await Flush();
            check(Math.Abs(inner.VerticalOffset - 100) < .1, "reversing scroll direction moves back by the same amount");
            Wheel(innerContent, -240); await Flush();
            check(Math.Abs(inner.VerticalOffset - (100 + step * 2)) < .1, "larger wheel deltas preserve their proportional distance");
            inner.ScrollToBottom(); await Flush();
            Wheel(innerContent, -120); await Flush();
            var outerStep = SystemParameters.WheelScrollLines < 0 ? outer.ViewportHeight : step;
            check(Math.Abs(outer.VerticalOffset - outerStep) < .1, "scrolling at a nested viewer edge continues in its parent");
            var rows = new System.Windows.Controls.StackPanel();
            for (var index = 0; index < 100; index++) rows.Children.Add(new System.Windows.Controls.Border { Height = 20 });
            inner.Content = rows; inner.CanContentScroll = true;
            outer.ScrollToTop(); await Flush(); inner.ScrollToVerticalOffset(10); await Flush();
            var logicalStep = SystemParameters.WheelScrollLines < 0 ? inner.ViewportHeight : SystemParameters.WheelScrollLines;
            if (SystemParameters.WheelScrollLines >= 0 && SystemParameters.WheelScrollLines < 20)
            {
                Wheel(rows, -6); await Flush();
                check(inner.VerticalOffset == 10, "logical lists retain small deltas instead of jumping a full item immediately");
                for (var index = 1; index < 20; index++) { Wheel(rows, -6); await Flush(); }
                check(Math.Abs(inner.VerticalOffset - (10 + logicalStep)) < .1, "logical lists accumulate fractional deltas across separate frames");
            }
        }
        finally { host.Close(); }
    }
}
