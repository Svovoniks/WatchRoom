using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Design review: renders the real window over a copy of a library (BLACKPEARL_DATA) to PNG files.
// Usage: --render-screens <output directory> [width height] [--scale 1.25]
// --scale draws as a display at that scaling would, closely but not exactly: text and elements created after it is set
// (dialogs, a rolling count) can still draw at the display's own scaling. CheckScalings in Program.cs checks exact pixels.
static class ScreenRender
{
    // A focus visual as WPF draws one: a control in the focus style, sized to the element, in the adorner layer over it.
    private sealed class FocusRingAdorner : System.Windows.Documents.Adorner
    {
        private readonly Control ring;
        public FocusRingAdorner(FrameworkElement element) : base(element) { ring = new Control { Style = element.FocusVisualStyle }; AddVisualChild(ring); IsHitTestVisible = false; }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => ring;
        protected override Size MeasureOverride(Size constraint) { ring.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { ring.Arrange(new Rect(AdornedElement.RenderSize)); return finalSize; }
    }

    public static async Task Run(MainWindow window, string output, int width, int height)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(window, args);
        async Task Settle(int milliseconds = 1500)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i); yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        Directory.CreateDirectory(output);
        // Screens are drawn at the window's own DPI, the user's display scaling unless --scale sets another, so fractional
        // sizes round the way they do on screen. The animation strips below stay at 96 DPI: they crop by layout position.
        static RenderTargetBitmap Picture(FrameworkElement root)
        {
            var dpi = VisualTreeHelper.GetDpi(root);
            return new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        }
        Console.WriteLine($"Rendering at {VisualTreeHelper.GetDpi(window).DpiScaleX * 100:F0}% scaling, {width}x{height} DIP");
        async Task Capture(string name, int settle = 1500)
        {
            await Settle(settle);
            var root = (FrameworkElement)window.Content;
            var bitmap = Picture(root); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
            Console.WriteLine("Saved " + name);
            foreach (var finding in BorderAudit.Find(root)) Console.WriteLine($"CLIPPED {name}: {finding}");
            // Audit the rest of long pages scrolled into view.
            var scrolled = Descendants(root).OfType<ScrollViewer>().Where(x => x.IsVisible && x.ScrollableHeight > 0 && x.VerticalOffset == 0).ToList();
            if (scrolled.Count > 0)
            {
                foreach (var scroll in scrolled) scroll.ScrollToEnd();
                await Settle(500);
                foreach (var finding in BorderAudit.Find(root)) Console.WriteLine($"CLIPPED {name} (scrolled): {finding}");
                foreach (var scroll in scrolled) scroll.ScrollToHome();
                await Settle(300);
            }
            // Audit collapsed sections opened as well, then restore them.
            var closed = Descendants(root).OfType<Expander>().Where(x => x.IsVisible && !x.IsExpanded).ToList();
            if (closed.Count == 0) return;
            foreach (var expander in closed) expander.IsExpanded = true;
            await Settle(300);
            foreach (var finding in BorderAudit.Find(root)) Console.WriteLine($"CLIPPED {name} (expanded): {finding}");
            foreach (var expander in closed) expander.IsExpanded = false;
        }
        // The item count rolling from one number to another, drawn every 30 ms and stacked top to bottom at four times size.
        async Task CaptureCountRoll()
        {
            var count = (RollingText)window.FindName("ResultCount"); var shown = count.Text;
            var frames = new List<BitmapSource>();
            foreach (var (from, to) in new[] { ("218 items", "242 items"), ("242 items", "97 items"), ("9 items", "10 items"), ("1 item", "2 items") })
            {
                count.Text = from; await Settle(700);
                // As a search runs: the count dims, then the new one rolls in.
                count.ShowPending("Searching…"); await Settle(250);
                count.Text = to;
                for (var at = DateTime.UtcNow; DateTime.UtcNow - at < TimeSpan.FromMilliseconds(660);)
                {
                    await Dispatcher.Yield(DispatcherPriority.Render); window.UpdateLayout();
                    // Rendering the count alone would draw it at its offset in the window, off the bitmap; so the window is
                    // drawn and the count's box, with a little room above and below, cut from it.
                    var root = (FrameworkElement)window.Content;
                    var full = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); full.Render(root);
                    var spot = count.TranslatePoint(new Point(0, 0), root);
                    frames.Add(new CroppedBitmap(full, new Int32Rect((int)spot.X - 60, (int)spot.Y - 6, (int)Math.Ceiling(count.ActualWidth) + 60, (int)Math.Ceiling(count.ActualHeight) + 12)));
                    await Task.Delay(30);
                }
            }
            var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            var w = frames.Max(f => f.PixelWidth) * 4; var h = frames[0].PixelHeight * 4;
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRectangle((Brush)window.FindResource("Canvas"), null, new Rect(0, 0, w, h * frames.Count));
                for (var i = 0; i < frames.Count; i++) draw.DrawImage(frames[i], new Rect(w - frames[i].PixelWidth * 4, i * h, frames[i].PixelWidth * 4, h));
            }
            var strip = new RenderTargetBitmap(w, h * frames.Count, 96, 96, PixelFormats.Pbgra32); strip.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(strip));
            await using (var file = File.Create(Path.Combine(output, "dark-count-roll.png"))) encoder.Save(file);
            count.Text = shown;
        }
        // The Home hero's Resume pebble and Details beside it, in each state they can be in, stacked top to bottom at twice size.
        // Hover and press set what their triggers set; the focus ring is drawn from the buttons' focus style over the picture.
        async Task CaptureHeroButtons(string theme)
        {
            if (window.FindName("HomeHeroResume") is not Button { IsVisible: true } resume || resume.Parent is not Panel row) return;
            var details = row.Children.OfType<Button>().First(button => button != resume);
            var label = (TextBlock)window.FindName("HomeHeroResumeLabel"); var left = (TextBlock)window.FindName("HomeHeroResumeLeft");
            var shown = (label.Text, left.Text); var progress = BindingOperations.GetBinding(resume, FrameworkElement.TagProperty);
            void Tint(Button button, double opacity) { if (button.Template.FindName("Tint", button) is Border tint) tint.Opacity = opacity; }
            void Rest() { Tint(resume, 0); Tint(details, 0); resume.ClearValue(Pebble.LitProperty); }
            var states = new (string Name, Action Set)[]
            {
                ("rest", () => { }),
                ("hover", () => { Tint(resume, 0.06); Tint(details, 0.07); }),
                ("pressed", () => { Tint(resume, 0.11); Tint(details, 0.14); }),
                ("focus", () => { }),
                ("nothing watched", () => { resume.Tag = 0.0; label.Text = "Play next episode"; left.Text = ""; }),
                ("just started", () => { resume.Tag = 3.0; label.Text = "Resume"; left.Text = "1 h 52 m"; }),
                ("almost done", () => { resume.Tag = 97.0; label.Text = "Resume"; left.Text = "2 min"; }),
                // A main action (Primary) is the same pebble, lit all over.
                ("lit", () => { resume.SetValue(Pebble.LitProperty, true); label.Text = "Play"; left.Text = ""; }),
            };
            var frames = new List<BitmapSource>(); var root = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(root);
            foreach (var (name, set) in states)
            {
                Rest(); set(); await Settle(400);
                var full = Picture(root); full.Render(root);
                var at = row.TranslatePoint(new Point(-20, -16), root);
                var area = new Int32Rect((int)(at.X * dpi.DpiScaleX), (int)(at.Y * dpi.DpiScaleY), (int)((row.ActualWidth + 60) * dpi.DpiScaleX), (int)((row.ActualHeight + 36) * dpi.DpiScaleY));
                BitmapSource frame = new CroppedBitmap(full, area);
                if (name == "focus")
                {
                    // The focus visual as WPF draws it: the style's template laid over the button's box.
                    var ring = new Control { Style = resume.FocusVisualStyle, Width = resume.ActualWidth, Height = resume.ActualHeight };
                    ring.Measure(new Size(ring.Width, ring.Height)); ring.Arrange(new Rect(0, 0, ring.Width, ring.Height));
                    var spot = resume.TranslatePoint(new Point(0, 0), row);
                    var visual = new DrawingVisual();
                    using (var draw = visual.RenderOpen())
                    {
                        draw.DrawImage(frame, new Rect(0, 0, frame.PixelWidth / dpi.DpiScaleX, frame.PixelHeight / dpi.DpiScaleY));
                        draw.PushTransform(new TranslateTransform(spot.X + 20, spot.Y + 16)); draw.DrawRectangle(new VisualBrush(ring), null, new Rect(0, 0, ring.Width, ring.Height)); draw.Pop();
                    }
                    var withRing = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); withRing.Render(visual);
                    frame = withRing;
                }
                frames.Add(frame);
            }
            Rest(); (label.Text, left.Text) = shown; if (progress is not null) resume.SetBinding(FrameworkElement.TagProperty, progress);
            var strip = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(strip, BitmapScalingMode.NearestNeighbor);
            var w = frames.Max(f => f.PixelWidth) * 2; var h = frames.Max(f => f.PixelHeight) * 2;
            using (var draw = strip.RenderOpen())
                for (var i = 0; i < frames.Count; i++) draw.DrawImage(frames[i], new Rect(0, i * h, frames[i].PixelWidth * 2, frames[i].PixelHeight * 2));
            var picture = new RenderTargetBitmap(w, h * frames.Count, 96, 96, PixelFormats.Pbgra32); picture.Render(strip);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(picture));
            await using (var file = File.Create(Path.Combine(output, $"{theme}-hero-buttons.png"))) encoder.Save(file);
            Console.WriteLine($"Saved {theme}-hero-buttons: " + string.Join(", ", states.Select(state => state.Name)));
        }
        // The lit stretch of the category thread gliding from All to Movies and on to Unmatched, drawn every 30 ms at twice size.
        async Task CaptureCategoryGlide()
        {
            var bar = (FrameworkElement)window.FindName("LibraryCategoryBar"); var root = (FrameworkElement)window.Content;
            var frames = new List<BitmapSource>();
            foreach (var target in new[] { "MoviesLibraryNav", "UnmatchedLibraryNav", "AllLibraryNav" })
            {
                if (window.FindName(target) is not Button { IsVisible: true } button) continue;
                Call("LibraryCategoryClick", button, new RoutedEventArgs());
                for (var at = DateTime.UtcNow; DateTime.UtcNow - at < TimeSpan.FromMilliseconds(450);)
                {
                    await Dispatcher.Yield(DispatcherPriority.Render); window.UpdateLayout();
                    var full = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); full.Render(root);
                    var spot = bar.TranslatePoint(new Point(0, 0), root);
                    frames.Add(new CroppedBitmap(full, new Int32Rect((int)spot.X, (int)spot.Y, Math.Min((int)bar.ActualWidth + 60, (int)(root.ActualWidth - spot.X)), (int)bar.ActualHeight + 4)));
                    await Task.Delay(30);
                }
                await Settle(500);
            }
            var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            var w = frames.Max(f => f.PixelWidth) * 2; var h = frames[0].PixelHeight * 2;
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRectangle((Brush)window.FindResource("Canvas"), null, new Rect(0, 0, w, h * frames.Count));
                for (var i = 0; i < frames.Count; i++) draw.DrawImage(frames[i], new Rect(0, i * h, frames[i].PixelWidth * 2, h));
            }
            var strip = new RenderTargetBitmap(w, h * frames.Count, 96, 96, PixelFormats.Pbgra32); strip.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(strip));
            await using (var file = File.Create(Path.Combine(output, "dark-category-glide.png"))) encoder.Save(file);
        }
        // A dialog, opened the way the app opens it, drawn over the window once it has settled, then closed. Dialogs float in a
        // popup of their own, so their layer is drawn onto the window's picture. Opening may wait for an answer, so the picture is
        // taken and the dialog closed from a timer.
        async Task CaptureDialog(string name, Action open)
        {
            var before = InAppDialog.Open.Count; var done = new TaskCompletionSource(); var shown = DateTime.MinValue; var asked = DateTime.UtcNow;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (InAppDialog.Open.Count <= before) { if (DateTime.UtcNow - asked > TimeSpan.FromSeconds(10)) { timer.Stop(); Console.WriteLine($"DIALOG {name} never opened"); done.TrySetResult(); } return; }
                if (shown == DateTime.MinValue) { shown = DateTime.UtcNow; return; }
                if (DateTime.UtcNow - shown < TimeSpan.FromMilliseconds(1200)) return;
                timer.Stop();
                var dialog = InAppDialog.Open[^1];
                var layer = (FrameworkElement)typeof(InAppDialog).GetField("layer", flags)!.GetValue(dialog)!;
                var root = (FrameworkElement)window.Content;
                var picture = Picture(root);
                picture.Render(root); picture.Render(layer);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(picture));
                using (var file = File.Create(Path.Combine(output, $"dialog-{name}.png"))) encoder.Save(file);
                Console.WriteLine("Saved dialog-" + name);
                dialog.Close(); done.TrySetResult();
            };
            timer.Start();
            try { open(); } catch (Exception ex) { Console.WriteLine($"DIALOG {name} failed: {ex.Message}"); timer.Stop(); done.TrySetResult(); }
            await done.Task; await Settle(300);
        }
        async Task CaptureDialogs(LibraryStore store, IReadOnlyList<WantedTitle> wanted, MediaItem? movie)
        {
            var click = new RoutedEventArgs();
            await CaptureDialog("alert", () => Dialogs.Alert(window, "Choose a subfolder", @"Choose folders inside M:\Shows. To leave out a whole library folder, remove it instead."));
            await CaptureDialog("confirm", () => Dialogs.Confirm(window, "Remove Shows", @"Stop scanning M:\Shows? Its files stay where they are, and adding the folder again brings them back.", "Remove folder"));
            await CaptureDialog("unsaved-settings", () => Dialogs.ConfirmUnsavedSettings(window));
            Call("ShowPage", "Queues"); await CaptureDialog("create-queue", () => Call("CreateQueue", window, click));
            Call("ShowPage", "Rooms");
            await CaptureDialog("create-room", () => Call("CreatePersistentRoom", window, click));
            await CaptureDialog("join-room", () => Call("JoinClick", window, click));
            await CaptureDialog("choose-title", () => Dialogs.Choose(window, [new(1, "movie", "Blade Runner 2049", "2017", null, "Thirty years after the events of the first film, a new blade runner unearths a long-buried secret.", "Movie"),
                new(2, "movie", "Blade Runner", "1982", null, "A blade runner must pursue and terminate four replicants who stole a ship in space.", "Movie"),
                new(3, "tv", "Blade Runner: Black Lotus", "2021", null, "A young woman wakes with no memory in 2032 Los Angeles.", "Anime")]));
            if (movie is not null) { Call("Select", movie); await CaptureDialog("watch-together", () => Call("HostClick", window, click)); }
            Call("NavigateLibrary", 0, null, null, null); await CaptureDialog("add-title", () => Call("AddTitleClick", window, click));
            if (wanted.FirstOrDefault(x => !x.IsMovie) is { } show) await CaptureDialog("tracked-seasons", () => AddTitleDialog.EditSeasons(window, show));
        }
        window.Width = width; window.Height = height;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (Field("libraryLoaded") is not true && DateTime.UtcNow < deadline) await Settle(200);
        var items = ((List<MediaItem>)Field("items")!).Where(item => item.Available && !item.IsExtra).ToList();
        Call("SetRailCollapsed", false);
        // A video page's File details, open: the file with the most tracks among the first few hundred, so the columns wrap.
        var detailed = await Task.Run(() => items.Where(item => !item.IsMusic).Take(300).Select(item => (Item: item, Groups: VideoFileDetails.Read(item.Path).Count))
            .OrderByDescending(entry => entry.Groups).Select(entry => entry.Item).FirstOrDefault());
        async Task CaptureFileDetails(string theme)
        {
            if (detailed is null) return;
            Call("Select", detailed);
            var expander = (Expander)window.FindName("DetailFileExpander"); var details = (ScrollViewer)window.FindName("Details");
            expander.IsExpanded = true; await Settle(2500);
            details.ScrollToVerticalOffset(expander.TranslatePoint(new Point(0, 0), (UIElement)details.Content).Y - 24);
            await Capture($"{theme}-file-details");
            details.ScrollToEnd(); await Capture($"{theme}-file-details-end");
            expander.IsExpanded = false; details.ScrollToHome();
        }
        // Keyboard focus on an element: its focus style laid over it in the adorner layer, as WPF does.
        async Task CaptureFocused(string name, FrameworkElement focused)
        {
            var adorners = System.Windows.Documents.AdornerLayer.GetAdornerLayer(focused)!; var ring = new FocusRingAdorner(focused);
            adorners.Add(ring);
            // A ring in the window's own adorner layer, outside its content, is drawn with the whole window.
            if (adorners.IsDescendantOf((Visual)window.Content)) await Capture(name);
            else
            {
                await Settle();
                var bitmap = Picture(window); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
                Console.WriteLine("Saved " + name);
            }
            adorners.Remove(ring);
        }
        foreach (var theme in new[] { "dark", "light" })
        {
            Set("light", theme == "light"); Call("ApplyTheme");
            await CaptureFileDetails(theme);
            Call("ShowPage", "Home"); await Capture($"{theme}-home"); await CaptureHeroButtons(theme);
            // Home scrolled to the mystery deck, with its filters as they open, then with the last choice on each thread.
            var home = (ScrollViewer)window.FindName("HomePage"); var deck = (FrameworkElement)window.FindName("HomeMysteryPanel");
            home.ScrollToVerticalOffset(deck.TranslatePoint(new Point(0, 0), (UIElement)home.Content).Y - 24); await Capture($"{theme}-home-mystery");
            var filters = Descendants((DependencyObject)window.FindName("HomeMysteryFilters")).OfType<WrapPanel>().Select(row => row.Children.OfType<RadioButton>().ToList()).ToList();
            foreach (var row in filters) row[^1].IsChecked = true;
            await Capture($"{theme}-home-mystery-chosen");
            await CaptureFocused($"{theme}-home-mystery-focus", filters[1][^1]);
            foreach (var row in filters) row[0].IsChecked = true;
            home.ScrollToHome();
            Call("NavigateLibrary", 0, null, null, null); await Capture($"{theme}-library");
            if (theme == "dark") { await CaptureFocused($"{theme}-library-focus", (FrameworkElement)window.FindName("AllLibraryNav")); await CaptureCountRoll(); await CaptureCategoryGlide(); }
            // The sidebar folded into its strip of icons.
            Call("SetRailCollapsed", true); await Capture($"{theme}-library-collapsed");
            Call("ShowPage", "Home"); await Capture($"{theme}-home-collapsed");
            // The Return to player button, which shows while a video plays, folded and open. The app hides it whenever it
            // refreshes with nothing playing, so it is shown again each time until the pictures are taken.
            var nowPlaying = (FrameworkElement)window.FindName("NowPlayingButton");
            DependencyPropertyChangedEventHandler keepShown = (_, _) => window.Dispatcher.BeginInvoke(() => nowPlaying.Visibility = Visibility.Visible);
            nowPlaying.IsVisibleChanged += keepShown; nowPlaying.Visibility = Visibility.Visible;
            await Capture($"{theme}-home-collapsed-playing");
            Call("SetRailCollapsed", false); await Capture($"{theme}-home-playing");
            nowPlaying.IsVisibleChanged -= keepShown; nowPlaying.Visibility = Visibility.Collapsed;
            if (theme == "light") continue;
            Call("NavigateLibrary", 1, null, null, null); await Capture($"{theme}-movies");
            var episode = items.FirstOrDefault(item => item.Series is not null && item.Season > 0);
            if (episode is not null)
            {
                Call("NavigateLibrary", 2, LibraryIdentity.ShowKey(episode), "Show", null); await Capture($"{theme}-show");
                Call("NavigateLibrary", 2, LibraryIdentity.ShowKey(episode), "Show", episode.Season); await Capture($"{theme}-season");
                Call("Select", episode); await Capture($"{theme}-episode-details");
            }
            var movie = items.FirstOrDefault(item => item.Series is null && item.Kind == "Movie");
            if (movie is not null) { Call("Select", movie); await Capture($"{theme}-movie-details"); }
            // A film with several copies: its page's Version picker, and its one card found by Search.
            if (items.FirstOrDefault(item => item.Series is null && LibraryCatalog.Versions(items, item).Count(x => x.Available) > 1) is { } copies)
            {
                Call("Select", copies); await Capture($"{theme}-movie-versions");
                Call("NavigateLibrary", 0, null, null, null); Call("OpenSearch", false);
                var search = (TextBox)window.FindName("SearchBox"); search.Text = copies.ShownName; await Capture($"{theme}-search-versions", 2500); search.Text = "";
            }
            var store = (LibraryStore)Field("library")!; var wanted = store.Wanted();
            if (wanted.Count > 0) { Call("NavigateLibrary", 5, null, null, null); await Capture($"{theme}-missing"); }
            if (wanted.FirstOrDefault(x => !x.IsMovie) is { } wantedShow) { Call("NavigateLibrary", wantedShow.Kind == "Anime" ? 3 : 2, wantedShow.Id, wantedShow.Kind, null); await Capture($"{theme}-wanted-show"); }
            if (wanted.FirstOrDefault(x => x.IsMovie) is { } wantedFilm && store.Get(wantedFilm.Id) is { } placeholder) { Call("Select", placeholder); await Capture($"{theme}-wanted-film"); }
            Call("NavigateLibrary", 0, null, null, null); Call("OpenSearch", false); await Capture($"{theme}-search");
            foreach (var page in new[] { "Queues", "Rooms", "Folders", "Room" }) { Call("ShowPage", page); await Capture($"{theme}-{page.ToLowerInvariant()}"); }
            Call("ShowPage", "Settings");
            foreach (var section in new[] { "Plugins", "Downloads", "Profile", "Metadata", "Playback", "Appearance", "Updates" })
            { Call("ShowPreferenceSection", section); await Capture($"{theme}-settings-{section.ToLowerInvariant()}"); }
            await CaptureDialogs(store, wanted, movie);
        }
        // Surfaces outside the main window (the player's overlay, its menus, dropdowns and dialogs), audited and saved in both
        // modes: the player stays dark in light mode, and a light-mode menu once drew the page's light well inside it.
        foreach (var theme in new[] { "light", "dark" })
        {
            // Each pass starts from Home, so the player one pass ends on doesn't stay open behind the next pass's dialogs.
            Set("light", theme == "light"); Call("ApplyTheme"); Call("ShowPage", "Home");
            var surfaces = new Dictionary<string, int>();
            await SurfaceAudit.Run(window, Settle, (name, finding) => Console.WriteLine($"CLIPPED {(theme == "light" ? "light " : "")}{name}: {finding}"), (name, root) =>
            {
                var count = surfaces[name] = surfaces.GetValueOrDefault(name) + 1;
                var bitmap = Picture(root); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"surface-{theme}-{name}{(count > 1 ? "-" + count : "")}.png")); encoder.Save(file);
            });
        }
    }
}
