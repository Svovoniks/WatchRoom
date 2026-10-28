using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Watchroom.Core;

namespace Watchroom.Desktop;

// What the Add title dialog settled on: a title already in the library to open, or a title to add with its seasons.
public sealed record AddTitleChoice(MediaItem? Existing, MetadataMatch? Match = null, MetadataSeason[]? Seasons = null, string Kind = "Show",
    int[]? Tracked = null, bool AddNewSeasons = true, int WatchedThrough = 0, bool Seen = false, string? Poster = null);

// Searches the metadata providers for a show or film, then asks for its category and the seasons to track.
public static class AddTitleDialog
{
    public sealed class Result(MetadataMatch match, MediaItem? existing) : INotifyPropertyChanged
    {
        public MetadataMatch Match { get; } = match;
        public MediaItem? Existing { get; } = existing;
        public string Title => Match.Title;
        public string Detail => string.Join(" · ", new[] { Match.Year, KindName(Match), Match.Provider == "tmdb" ? "TMDB" : Match.Provider == "tvmaze" ? "TVmaze" : "Wikipedia" }.OfType<string>());
        public string Badge => Existing is not null ? "In library" : Match.Type == "movie" && Upcoming(Match.ReleaseDate) ? "Upcoming" : "";
        public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public ImageSource? Poster { get; private set; }
        public void SetPoster(ImageSource image) { Poster = image; PropertyChanged?.Invoke(this, new(nameof(Poster))); }
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => $"{Title} · {Detail}" + (Badge.Length > 0 ? " · " + Badge : "");
    }
    private static bool Upcoming(string? date) => DateOnly.TryParse(date, out var day) && day > DateOnly.FromDateTime(DateTime.UtcNow);
    private static string KindName(MetadataMatch match) => match.Kind == "Anime" ? "Anime" : match.Type == "tv" ? "Show" : "Movie";
    public static string SeasonName(int number) => number == 0 ? "Specials" : $"Season {number}";
    // A library entry for the same title: the same provider identity, a wanted title's key, or the same name and year.
    public static MediaItem? Existing(MetadataMatch match, IEnumerable<MediaItem> items)
    {
        var key = WantedTitles.Key(match.Provider, match.Type, match.Id); var id = match.Id.ToString();
        var name = MetadataTitles.Normalize(match.Title); int? year = int.TryParse(match.Year, out var parsed) ? parsed : null;
        var candidates = items.Where(x => !x.IsExtra && x.ExtraOf is null && (match.Type == "movie" ? x.Series is null : x.Series is not null)).ToArray();
        return candidates.FirstOrDefault(x => LibraryIdentity.ShowKey(x) == key || LibraryIdentity.ProviderId(x, match.Provider) == id) ??
            candidates.FirstOrDefault(x => year is not null && x.Year == year && MetadataTitles.Normalize(x.Series ?? x.Title) == name);
    }
    private static TextBlock Text(string text, double size = 14, bool muted = false, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        if (muted) block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return block;
    }
    // A row of buttons acting as one choice; the chosen one is highlighted.
    private static WrapPanel Segment(string name, string[] labels, int selected, Action<int> changed)
    {
        var panel = new WrapPanel(); AutomationProperties.SetName(panel, name);
        var buttons = labels.Select((label, index) => new Button { Content = label, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(14, 6, 14, 6), MinHeight = 0 }).ToArray();
        void Select(int index)
        {
            for (var i = 0; i < buttons.Length; i++)
            {
                if (i == index) buttons[i].SetResourceReference(FrameworkElement.StyleProperty, "Primary"); else buttons[i].ClearValue(FrameworkElement.StyleProperty);
                AutomationProperties.SetItemStatus(buttons[i], i == index ? "Selected" : "");
            }
        }
        for (var i = 0; i < buttons.Length; i++) { var index = i; buttons[i].Click += (_, _) => { Select(index); changed(index); }; panel.Children.Add(buttons[i]); }
        Select(selected); return panel;
    }
    private static BitmapSource? Load(string path, int width)
    {
        try
        {
            using var file = File.OpenRead(path);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = width;
            bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
    private static FrameworkElement PosterFrame(double width, double height, out Image image)
    {
        image = new Image { Stretch = Stretch.UniformToFill };
        var frame = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(width > 60 ? 10 : 6), ClipToBounds = true, Child = image };
        frame.SetResourceReference(Border.BackgroundProperty, "Elevated");
        RoundedClip.SetRadius(frame, width > 60 ? 10 : 6);
        return frame;
    }

    public static AddTitleChoice? Show(Window owner, LibraryStore library, string? token, string query, IReadOnlyList<MediaItem> items, CancellationToken lifetime)
    {
        var win = new InAppDialog { Owner = owner, Title = "Add a title", Width = 720, Height = 600 };
        AddTitleChoice? choice = null; var closed = false; win.Closed += (_, _) => closed = true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        win.Closed += (_, _) => stop.Cancel();

        // Step one: search.
        var search = new DockPanel();
        var input = new TextBox { Text = query, Margin = new Thickness(0, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, "Search for a show or film");
        var kind = 0;
        List<MetadataMatch> found = []; string[] warnings = []; var version = 0; CancellationTokenSource? searching = null;
        var results = new ObservableCollection<Result>();
        var status = Text("", 13, muted: true); status.Margin = new Thickness(0, 10, 0, 6);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var list = new ListBox { ItemsSource = results, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(list, "Search results");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.ItemTemplate = ResultTemplate();
        var next = new Button { Content = "Continue", IsDefault = true, IsEnabled = false };
        next.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(cancel); actions.Children.Add(next);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        footer.Children.Add(new TextBlock { Text = "Can't find it? Try its original title or the year.", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = (Brush)owner.FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        var kinds = Segment("Kind", ["Any", "Movie", "Show", "Anime"], 0, index => { kind = index; Render(); });
        kinds.VerticalAlignment = VerticalAlignment.Center; kinds.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(kinds, Dock.Right); row.Children.Add(kinds); row.Children.Add(input);
        DockPanel.SetDock(row, Dock.Top); DockPanel.SetDock(status, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        search.Children.Add(row); search.Children.Add(status); search.Children.Add(footer); search.Children.Add(list);

        void Render()
        {
            results.Clear();
            foreach (var match in found.Where(m => kind switch { 1 => m.Type == "movie", 2 => m.Type == "tv" && m.Kind != "Anime", 3 => m.Kind == "Anime", _ => true }))
                results.Add(new(match, Existing(match, items)));
            status.Text = input.Text.Trim().Length < 2 ? "Type the name of a show, film or anime." : results.Count == 0 ? $"No matches for “{input.Text.Trim()}”." + (warnings.Length > 0 ? " " + string.Join(" ", warnings) : "")
                : $"{results.Count} {(results.Count == 1 ? "match" : "matches")}" + (warnings.Length > 0 ? " · " + string.Join(" ", warnings) : "");
            _ = LoadPosters(results.Take(10).ToArray());
        }
        async Task LoadPosters(Result[] shown)
        {
            foreach (var result in shown.Where(r => r.Match.Poster is not null))
            {
                try
                {
                    var path = await MetadataIdentification.PosterAsync(result.Match, App.DataDirectory, token, stop.Token);
                    if (closed || path is null) continue;
                    if (await Task.Run(() => Load(path, 88)) is { } image) result.SetPoster(image);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or InvalidOperationException or ArgumentException) { if (closed) return; }
            }
        }
        async Task Search()
        {
            var text = input.Text.Trim(); var current = ++version;
            searching?.Cancel();
            if (text.Length < 2) { found = []; warnings = []; Render(); return; }
            searching = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); var ct = searching.Token;
            status.Text = "Searching…";
            try { await Task.Delay(350, ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                var result = await MetadataIdentification.SearchAsync(library, new MediaItem("add-title", "", text, "Mixed"), text, token, ct);
                if (closed || current != version) return;
                found = result.Matches.Where(m => m.Type == "movie" || m.Provider is "tmdb" or "tvmaze").ToList(); warnings = result.Warnings; Render();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            { if (current == version) status.Text = "Search failed: " + ex.Message; }
        }
        input.TextChanged += (_, _) => _ = Search();
        list.SelectionChanged += (_, _) => { next.IsEnabled = list.SelectedItem is Result; next.Content = list.SelectedItem is Result { Existing: not null } ? "Open" : "Continue"; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is Result) next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down && results.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex); (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem)?.Focus(); e.Handled = true; } };

        next.Click += async (_, _) =>
        {
            if (list.SelectedItem is not Result picked) return;
            if (picked.Existing is not null) { choice = new(picked.Existing); win.DialogResult = true; return; }
            win.Title = picked.Title;
            var loading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            loading.Children.Add(Text(picked.Match.Type == "tv" ? "Loading seasons…" : "Loading details…", 15, muted: true));
            win.Content = loading;
            try
            {
                var (details, seasons) = await WantedTitles.DetailsAsync(library, picked.Match, token, stop.Token);
                string? poster = null;
                try { poster = await MetadataIdentification.PosterAsync(details, App.DataDirectory, token, stop.Token); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ArgumentException) { }
                if (closed) return;
                win.Content = Configure(owner, details, seasons, poster, () => { win.Title = "Add a title"; win.Content = search; }, made => { choice = made; win.DialogResult = true; });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                if (closed) return;
                var failed = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
                failed.Children.Add(Text("Couldn't load this title's details. " + ex.Message, 15));
                var back = new Button { Content = "Back to results", Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                back.Click += (_, _) => { win.Title = "Add a title"; win.Content = search; };
                failed.Children.Add(back); win.Content = failed;
            }
        };
        win.Content = search;
        win.Loaded += (_, _) => { input.Focus(); input.CaretIndex = input.Text.Length; };
        if (query.Length > 0) _ = Search(); else Render();
        return win.ShowDialog() == true ? choice : null;
    }

    private static DataTemplate ResultTemplate()
    {
        var template = new DataTemplate(typeof(Result));
        var grid = new FrameworkElementFactory(typeof(DockPanel)); grid.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 6, 4, 6));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(FrameworkElement.WidthProperty, 44.0); frame.SetValue(FrameworkElement.HeightProperty, 66.0);
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); frame.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        frame.SetResourceReference(Border.BackgroundProperty, "Elevated"); frame.SetValue(DockPanel.DockProperty, Dock.Left);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Result.Poster)));
        image.SetValue(Image.StretchProperty, Stretch.UniformToFill); frame.AppendChild(image); grid.AppendChild(frame);
        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(DockPanel.DockProperty, Dock.Right); badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); badge.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft"); badge.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(Result.BadgeVisibility)));
        var badgeText = new FrameworkElementFactory(typeof(TextBlock)); badgeText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Result.Badge)));
        badgeText.SetValue(TextBlock.FontSizeProperty, 11.0); badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        badge.AppendChild(badgeText); grid.AppendChild(badge);
        var texts = new FrameworkElementFactory(typeof(StackPanel)); texts.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Result.Title)));
        title.SetValue(TextBlock.FontSizeProperty, 15.0); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Result.Detail)));
        detail.SetValue(TextBlock.FontSizeProperty, 12.0); detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); detail.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        texts.AppendChild(title); texts.AppendChild(detail); grid.AppendChild(texts);
        template.VisualTree = grid; return template;
    }

    // Step two: the category, and for a show the seasons to track and how much has been watched already.
    private static FrameworkElement Configure(Window owner, MetadataMatch details, MetadataSeason[] seasons, string? poster, Action back, Action<AddTitleChoice> add)
    {
        var movie = details.Type == "movie";
        var root = new DockPanel();
        var body = new StackPanel();
        var categories = new[] { "Movie", "Show", "Anime" };
        var category = Array.IndexOf(categories, details.Kind == "Anime" ? "Anime" : movie ? "Movie" : "Show");
        body.Children.Add(Text("Category", 13, muted: true));
        var categoryChoice = Segment("Category", ["Movies", "Shows", "Anime"], category, index => category = index);
        categoryChoice.Margin = new Thickness(0, 6, 0, 10); body.Children.Add(categoryChoice);
        var summary = Text("", 13, muted: true); summary.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        var tracked = seasons.Where(s => s.Number > 0).Select(s => s.Number).ToHashSet();
        var addNew = new CheckBox { Content = "Add new seasons and episodes as they're announced", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        var watched = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(watched, "Already watched");
        var seen = new CheckBox { Content = "I've already seen it", Margin = new Thickness(0, 6, 0, 0) };
        var add_ = new Button { Content = "Add to library", IsDefault = true };
        add_.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        void Summarize()
        {
            if (movie) { summary.Text = Upcoming(details.ReleaseDate) ? $"Shows as Upcoming until {DateOnly.Parse(details.ReleaseDate!):MMM d, yyyy}, then as Missing until a scan finds the file." : "Shows as Missing until a scan finds the file."; return; }
            var episodes = seasons.Where(s => tracked.Contains(s.Number)).SelectMany(s => s.Episodes).Where(e => DateOnly.TryParse(e.AirDate, out _)).ToArray();
            var upcoming = episodes.Count(e => Upcoming(e.AirDate));
            summary.Text = tracked.Count == 0 ? "Choose at least one season to track." :
                $"{episodes.Length - upcoming} {(episodes.Length - upcoming == 1 ? "episode" : "episodes")} will show as Missing" + (upcoming > 0 ? $", {upcoming} as Upcoming" : "") + " · 0 files";
            add_.IsEnabled = tracked.Count > 0;
        }
        if (movie) body.Children.Add(seen);
        else if (seasons.Length == 0)
        {
            body.Children.Add(Text("This source has no episode list for the show. Choose its TMDB or TVmaze result to track episodes.", 13));
            add_.IsEnabled = false;
        }
        else
        {
            body.Children.Add(Text("Seasons to track", 13, muted: true));
            var picker = SeasonPicker(seasons.Select(WantedTitles.Summary).ToArray(), tracked, Summarize);
            picker.Margin = new Thickness(0, 6, 0, 4); body.Children.Add(picker);
            body.Children.Add(addNew);
            body.Children.Add(Text("New episodes appear as Upcoming, then as Missing once they air.", 12, muted: true));
            var hint = (TextBlock)body.Children[^1]; hint.Margin = new Thickness(26, 2, 0, 12);
            body.Children.Add(Text("Already watched", 13, muted: true));
            watched.Items.Add(new ComboBoxItem { Content = "Nothing yet", Tag = 0 });
            foreach (var season in seasons.Where(s => s.Number > 0 && s.Episodes.Any(e => !Upcoming(e.AirDate) && DateOnly.TryParse(e.AirDate, out _))).OrderBy(s => s.Number))
                watched.Items.Add(new ComboBoxItem { Content = $"Through season {season.Number}", Tag = season.Number });
            watched.SelectedIndex = 0; body.Children.Add(watched);
        }
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var frame = PosterFrame(110, 165, out var image); frame.Margin = new Thickness(0, 0, 20, 0); frame.VerticalAlignment = VerticalAlignment.Top;
        if (poster is not null && Load(poster, 220) is { } bitmap) image.Source = bitmap;
        DockPanel.SetDock(frame, Dock.Left); heading.Children.Add(frame);
        var info = new StackPanel();
        info.Children.Add(Text(details.Title, 20, bold: true));
        var facts = new[] { details.Year, movie ? details.RuntimeMinutes is > 0 ? $"{details.RuntimeMinutes} min" : null : $"{seasons.Count(s => s.Number > 0)} {(seasons.Count(s => s.Number > 0) == 1 ? "season" : "seasons")}",
            details.Provider == "tmdb" ? "TMDB" : details.Provider == "tvmaze" ? "TVmaze" : "Wikipedia" }.OfType<string>();
        var factLine = Text(string.Join(" · ", facts), 13, muted: true); factLine.Margin = new Thickness(0, 4, 0, 8); info.Children.Add(factLine);
        if (!string.IsNullOrWhiteSpace(details.Overview)) { var overview = Text(details.Overview, 13, muted: true); overview.MaxHeight = 84; overview.TextTrimming = TextTrimming.WordEllipsis; info.Children.Add(overview); }
        heading.Children.Add(info);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var backButton = new Button { Content = "Back" }; backButton.Click += (_, _) => back();
        actions.Children.Add(backButton); actions.Children.Add(add_);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); footer.Children.Add(summary);
        add_.Click += (_, _) => add(new(null, details, seasons, categories[category], tracked.Order().ToArray(), addNew.IsChecked == true,
            watched.SelectedItem is ComboBoxItem { Tag: int through } ? through : 0, seen.IsChecked == true, poster));
        DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(heading); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Summarize();
        return root;
    }

    // One button per season, highlighted while tracked.
    public static WrapPanel SeasonPicker(IReadOnlyList<WantedSeason> seasons, HashSet<int> tracked, Action changed)
    {
        var panel = new WrapPanel(); AutomationProperties.SetName(panel, "Seasons to track");
        foreach (var season in seasons.OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number))
        {
            var label = $"{SeasonName(season.Number)} · {season.Episodes} ep" + (season.Episodes == 0 ? " · not announced" : season.Aired == 0 ? " · upcoming" : "");
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 6, 12, 6), MinHeight = 0 };
            void Style()
            {
                if (tracked.Contains(season.Number)) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary"); else button.ClearValue(FrameworkElement.StyleProperty);
                AutomationProperties.SetItemStatus(button, tracked.Contains(season.Number) ? "Tracked" : "Not tracked");
            }
            button.Click += (_, _) => { if (!tracked.Remove(season.Number)) tracked.Add(season.Number); Style(); changed(); };
            Style(); panel.Children.Add(button);
        }
        return panel;
    }

    // Changes which seasons of a wanted show are tracked; null when cancelled.
    public static (int[] Seasons, bool AddNewSeasons)? EditSeasons(Window owner, WantedTitle title)
    {
        var win = new InAppDialog { Owner = owner, Title = "Tracked seasons", Width = 600 };
        var panel = new StackPanel();
        var tracked = title.Seasons.ToHashSet();
        var summary = Text("", 13, muted: true); summary.Margin = new Thickness(0, 8, 0, 16);
        var save = new Button { Content = "Save", IsDefault = true }; save.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        var known = title.KnownSeasons ?? title.Seasons.Select(n => new WantedSeason(n, 0, 0)).ToArray();
        void Summarize()
        {
            var counted = known.Where(s => tracked.Contains(s.Number)).ToArray();
            summary.Text = tracked.Count == 0 ? "Choose at least one season to track." : $"{counted.Sum(s => s.Aired)} aired and {counted.Sum(s => s.Episodes - s.Aired)} upcoming episodes in the tracked seasons. Files already here stay, even in seasons you stop tracking.";
            save.IsEnabled = tracked.Count > 0;
        }
        panel.Children.Add(Text($"Choose the seasons of {title.Title} whose episodes count as missing.", 14));
        var picker = SeasonPicker(known, tracked, Summarize); picker.Margin = new Thickness(0, 14, 0, 6); panel.Children.Add(picker);
        var addNew = new CheckBox { Content = "Add new seasons and episodes as they're announced", IsChecked = title.AddNewSeasons, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(addNew); panel.Children.Add(summary);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => win.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        win.Content = panel; Summarize();
        return win.ShowDialog() == true ? (tracked.Order().ToArray(), addNew.IsChecked == true) : null;
    }

    // A dashed button, for something that could be added: an untracked season.
    public static Button Dashed(string text)
    {
        var template = new ControlTemplate(typeof(Button));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var edge = new FrameworkElementFactory(typeof(Rectangle));
        edge.SetValue(Rectangle.RadiusXProperty, 8.0); edge.SetValue(Rectangle.RadiusYProperty, 8.0); edge.SetValue(Shape.StrokeThicknessProperty, 1.2);
        edge.SetValue(Shape.StrokeDashArrayProperty, new DoubleCollection([4, 3])); edge.SetResourceReference(Shape.StrokeProperty, "Muted");
        edge.SetValue(Shape.FillProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 6, 12, 6));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(edge); grid.AppendChild(content); template.VisualTree = grid;
        var button = new Button { Content = text, Template = template, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, FocusVisualStyle = (Style)Application.Current.FindResource("FocusRing") };
        button.SetResourceReference(Control.ForegroundProperty, "Muted");
        return button;
    }
}
