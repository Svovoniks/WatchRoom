using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Both views use the same paged grid. The guest reports explicit IDs, never an
// inferred scroll range; the preview scales the entire grid rather than clipping it.
public sealed class RoomLibraryWindow : Window
{
    private readonly RoomLibrary session;
    private readonly bool host;
    private readonly Func<string> hostId;
    private readonly Action<string[]> share;
    private readonly Func<Participant[]> people;
    private readonly Grid viewport = new();
    private readonly UniformGrid grid = new();
    private readonly TextBox search = new() { MaxLength = 200, Width = 240 };
    private readonly ComboBox category = new() { Width = 130 };
    private readonly ComboBox season = new() { Width = 130 };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 75 };
    private readonly TextBlock pageLabel = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox queue = new() { DisplayMemberPath = "Title", MinHeight = 150 };
    private readonly ListBox activity = new();
    private readonly ComboBox guests = new() { Width = 180 };
    private readonly CheckBox browse = new() { Content = "Browse" }, edit = new() { Content = "Manage queue" }, start = new() { Content = "Start videos" };
    private readonly StackPanel previews = new();
    private readonly ScrollViewer previewScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TabControl tabs = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Dictionary<string, ImageSource?> posters = new();
    private readonly List<Button> authorizedQueueButtons = [];
    private Button play = null!, add = null!, next = null!;
    private int page, columns = 1, rows = 1;
    private long sequence;
    private long lastViewAt;
    private string? selected, hovered;
    private string signature = "", lastView = "", previewSignature = "";
    private string browserDataSignature = "";
    private long posterGeneration = -1;
    private string accessSignature = "";
    private string[] visible = [];
    private bool dirty = true;
    private bool disposed;

    public RoomLibraryWindow(RoomLibrary session, bool host, Func<string> hostId,
        Func<Participant[]> people, Action<string[]> share)
    {
        this.session = session; this.host = host; this.hostId = hostId; this.people = people; this.share = share;
        SetResourceReference(StyleProperty, typeof(Window));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Watchroom;component/RoomLibraryStyles.xaml", UriKind.Relative) });
        Title = host ? "Room library · Guest views" : "Host library · Your browsing is visible to the host";
        Width = 1050; Height = 780; MinWidth = 700; MinHeight = 540;
        var root = new DockPanel { Margin = new Thickness(16) };
        root.SetResourceReference(Panel.BackgroundProperty, "Canvas"); root.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status); root.Children.Add(tabs); Content = root;
        var browser = new DockPanel();
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); browser.Children.Add(top);
        top.Children.Add(new TextBlock { Text = host ? "Shared room library" : "The host can see this grid, your selection and hovered title.", Margin = new Thickness(4, 8, 4, 8) });
        var filters = new WrapPanel(); filters.Children.Add(search); filters.Children.Add(category); filters.Children.Add(season); top.Children.Add(filters);
        foreach (var value in new[] { "All titles", "Movie", "Show", "Anime" }) category.Items.Add(value); category.SelectedIndex = 0;
        season.Items.Add("All seasons"); season.SelectedIndex = 0;
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); browser.Children.Add(bottom);
        var paging = new WrapPanel(); paging.Children.Add(Button("Previous page", () => { page = Math.Max(0, page - 1); dirty = true; })); paging.Children.Add(pageLabel);
        paging.Children.Add(Button("Next page", () => { page++; dirty = true; })); bottom.Children.Add(paging);
        bottom.Children.Add(detail);
        var actions = new WrapPanel(); add = Button("Add to queue", () => Command("add", selected)); play = Button("Play now", PlayNow);
        actions.Children.Add(add); actions.Children.Add(play); bottom.Children.Add(actions);
        viewport.Children.Add(grid); browser.Children.Add(viewport);
        tabs.Items.Add(new TabItem { Header = "Host library", Content = browser });
        search.TextChanged += (_, _) => { page = 0; dirty = true; };
        category.SelectionChanged += (_, _) => { page = 0; dirty = true; };
        season.SelectionChanged += (_, _) => { page = 0; dirty = true; };
        viewport.SizeChanged += (_, _) => dirty = true;
        var queuePanel = new DockPanel(); var queueActions = new WrapPanel(); DockPanel.SetDock(queueActions, Dock.Bottom); queuePanel.Children.Add(queueActions);
        foreach (var action in new[] { "Remove", "Move up", "Move down", "Clear" })
        {
            var button = Button(action, () => EditQueue(action)); authorizedQueueButtons.Add(button); queueActions.Children.Add(button);
        }
        next = Button("Start next", () => Command("next")); queueActions.Children.Add(next);
        if (host) queueActions.Children.Add(Button("Undo queue edit", () => Command("undo")));
        queuePanel.Children.Add(queue); tabs.Items.Add(new TabItem { Header = "Queue", Content = queuePanel });
        if (host)
        {
            var hostPanel = new DockPanel(); var settings = new StackPanel(); DockPanel.SetDock(settings, Dock.Top); hostPanel.Children.Add(settings);
            settings.Children.Add(Button("Choose shared collections", ChooseCollections));
            settings.Children.Add(new TextBlock { Text = "Select an admitted guest and grant access. Revocation takes effect immediately." });
            var permissions = new WrapPanel(); permissions.Children.Add(guests); permissions.Children.Add(browse); permissions.Children.Add(edit); permissions.Children.Add(start);
            permissions.Children.Add(Button("Apply permissions", () =>
            {
                if (guests.SelectedItem is GuestChoice guest) session.SetAccess(guest.Id, new(browse.IsChecked == true, edit.IsChecked == true, start.IsChecked == true));
            })); settings.Children.Add(permissions);
            guests.SelectionChanged += (_, _) => LoadAccess();
            previewScroll.Content = previews; hostPanel.Children.Add(previewScroll);
            tabs.Items.Add(new TabItem { Header = "Guest views & access", Content = hostPanel });
        }
        tabs.Items.Add(new TabItem { Header = "Activity", Content = activity });
        foreach (var entry in session.History) OnActivity(entry);
        session.Changed += OnChanged; session.Activity += OnActivity; session.Result += OnResult;
        timer.Tick += (_, _) => Tick(); timer.Start();
        Closing += (_, e) => { if (!disposed) { e.Cancel = true; Hide(); SendView(false); } };
        RefreshState();
    }
    public void Shutdown()
    {
        disposed = true; timer.Stop(); session.Changed -= OnChanged; session.Activity -= OnActivity; session.Result -= OnResult; Close();
    }
    private Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }; return button;
    }
    private void OnChanged() { dirty = true; RefreshState(); }
    private void OnActivity(LibraryActivity entry)
    {
        activity.Items.Add($"{DateTimeOffset.FromUnixTimeMilliseconds(entry.AtUnixMs).LocalDateTime:T} · {entry.Actor} {entry.Text}");
        if (activity.Items.Count > 200) activity.Items.RemoveAt(0); activity.ScrollIntoView(activity.Items[^1]);
    }
    private void OnResult(LibraryResult result) => status.Text = result.Text;
    private void RefreshState()
    {
        var selectedEntry = (queue.SelectedItem as RoomQueueEntry)?.Id;
        queue.ItemsSource = session.Queue.Entries; queue.SelectedItem = session.Queue.Entries.FirstOrDefault(e => e.Id == selectedEntry);
        add.IsEnabled = host || session.OwnAccess.Queue; play.IsEnabled = host || session.OwnAccess.Start; next.IsEnabled = play.IsEnabled;
        foreach (var button in authorizedQueueButtons) button.IsEnabled = add.IsEnabled;
        if (!host && !session.OwnAccess.Browse) { selected = hovered = null; detail.Text = "Waiting for the host to share collections and grant browsing access."; }
        if (host)
        {
            var current = (guests.SelectedItem as GuestChoice)?.Id;
            var admitted = people().Where(p => p.Approved && !p.IsHost).Select(p => new GuestChoice(p.Id, p.Name)).ToArray();
            if (!guests.Items.Cast<GuestChoice>().SequenceEqual(admitted))
            { guests.ItemsSource = admitted; guests.SelectedItem = admitted.FirstOrDefault(p => p.Id == current) ?? admitted.FirstOrDefault(); }
            LoadAccess();
        }
    }
    private void LoadAccess()
    {
        var access = guests.SelectedItem is GuestChoice p ? session.Access.GetValueOrDefault(p.Id, new()) : new();
        var key = (guests.SelectedItem as GuestChoice)?.Id + Wire.Serialize(access);
        if (key == accessSignature) return; accessSignature = key;
        browse.IsChecked = access.Browse; edit.IsChecked = access.Queue; start.IsChecked = access.Start;
    }
    private void ChooseCollections()
    {
        var dialog = new Window { Owner = this, Title = "Share collections for this room", Width = 370, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = "Only checked categories are exposed to guests.", TextWrapping = TextWrapping.Wrap });
        var choices = new[] { "Movie", "Show", "Anime", "Mixed" }.Select(kind => new CheckBox { Content = kind, Margin = new Thickness(5) }).ToArray();
        foreach (var choice in choices) { choice.IsChecked = session.Catalog.Values.Any(i => i.Kind == (string)choice.Content); panel.Children.Add(choice); }
        panel.Children.Add(Button("Share selected", () => { share(choices.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToArray()); dialog.Close(); }));
        dialog.ShowDialog();
    }
    private void Command(string action, string? item = null, string? entry = null, int index = 0) => session.Request(hostId(), action, item, entry, index);
    private void PlayNow()
    {
        if (selected is null) return;
        if (MessageBox.Show(this, "Start this title for everyone and replace the current video?", "Play now", MessageBoxButton.YesNo) == MessageBoxResult.Yes) Command("play", selected);
    }
    private void EditQueue(string action)
    {
        var entry = queue.SelectedItem as RoomQueueEntry; var index = queue.SelectedIndex;
        if (action == "Clear") { Command("clear"); return; }
        if (entry is null) return;
        Command(action == "Remove" ? "remove" : "move", entry: entry.Id, index: action == "Move up" ? index - 1 : index + 1);
    }
    private void Tick()
    {
        if (!IsVisible) return;
        if (dirty) { RenderBrowser(); dirty = false; }
        foreach (var button in grid.Children.OfType<Button>())
        {
            var card = (Border)button.Content; var id = (string)button.Tag;
            card.BorderBrush = hovered == id ? Brushes.Gold : selected == id ? Brushes.DeepSkyBlue : Brushes.Transparent;
        }
        if (host) RenderPreviews(); else SendView(tabs.SelectedIndex == 0);
    }
    private void RenderBrowser()
    {
        var dataKey = Wire.Serialize(new { session.Generation, Count = session.Catalog.Count, Search = search.Text,
            Category = category.SelectedIndex, Season = season.SelectedItem?.ToString(), Width = Math.Round(viewport.ActualWidth), Height = Math.Round(viewport.ActualHeight) });
        if (dataKey == browserDataSignature) return; browserDataSignature = dataKey;
        if (posterGeneration != session.Generation) { posters.Clear(); posterGeneration = session.Generation; }
        if (selected is not null && !session.Catalog.ContainsKey(selected)) { selected = null; detail.Text = ""; }
        columns = Math.Clamp((int)(viewport.ActualWidth / 150), 1, 8);
        rows = Math.Clamp((int)(viewport.ActualHeight / 220), 1, 4);
        var selectedSeason = season.SelectedItem?.ToString() ?? "All seasons";
        var seasons = session.Catalog.Values.Where(i => i.Season is not null).Select(i => "Season " + i.Season).Distinct().Order().Prepend("All seasons").ToArray();
        if (!season.Items.Cast<string>().SequenceEqual(seasons)) { season.ItemsSource = seasons; season.SelectedItem = seasons.Contains(selectedSeason) ? selectedSeason : seasons[0]; }
        var filtered = session.Catalog.Values.Where(i => (i.Title + " " + i.Series).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (category.SelectedIndex == 0 || i.Kind == category.SelectedItem?.ToString()) &&
            (selectedSeason == "All seasons" || selectedSeason == "Season " + i.Season)).ToArray();
        var count = columns * rows; var pages = Math.Max(1, (filtered.Length + count - 1) / count); page = Math.Clamp(page, 0, pages - 1);
        var entries = filtered.Skip(page * count).Take(count).ToArray(); visible = entries.Select(i => i.Id).ToArray();
        pageLabel.Text = $"Page {page + 1} / {pages} · {filtered.Length} titles · {columns} × {rows}";
        var key = Wire.Serialize(new { session.Generation, columns, rows, visible, Width = Math.Round(viewport.ActualWidth), Height = Math.Round(viewport.ActualHeight) });
        if (key == signature) return; signature = key; hovered = null;
        grid.Columns = columns; grid.Rows = rows; grid.Children.Clear();
        foreach (var item in entries)
        {
            var card = Card(item, false); var button = new Button { Tag = item.Id, Content = card, Padding = new Thickness(0), Margin = new Thickness(3), BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            card.Height = Math.Max(70, viewport.ActualHeight / rows - 18);
            button.Click += (_, _) => { selected = item.Id; detail.Text = item.Title + " · " + item.Caption + "\n" + item.Overview; dirty = true; };
            button.MouseEnter += (_, _) => { hovered = item.Id; card.BorderBrush = Brushes.Gold; card.BorderThickness = new Thickness(3); };
            button.MouseLeave += (_, _) => { if (hovered == item.Id) hovered = null; card.BorderBrush = selected == item.Id ? Brushes.DeepSkyBlue : Brushes.Transparent; card.BorderThickness = new Thickness(3); };
            grid.Children.Add(button);
        }
        while (grid.Children.Count < count) grid.Children.Add(new Border());
    }
    private Border Card(SharedLibraryItem item, bool preview, bool hover = false, bool chosen = false)
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var poster = Poster(item);
        if (poster is not null) panel.Children.Add(new Image { Source = poster, Stretch = Stretch.Uniform });
        else panel.Children.Add(new TextBlock { Text = "◉", FontSize = 35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = item.Title + "\n" + item.Caption, TextWrapping = TextWrapping.Wrap, MaxHeight = 65, Margin = new Thickness(4), FontSize = preview ? 12 : 13 }; Grid.SetRow(title, 1); panel.Children.Add(title);
        var card = new Border { Child = panel, Padding = new Thickness(3), Margin = new Thickness(2), BorderThickness = new Thickness(3), BorderBrush = hover ? Brushes.Gold : chosen || selected == item.Id && !preview ? Brushes.DeepSkyBlue : Brushes.Transparent };
        card.SetResourceReference(BackgroundProperty, "Elevated"); return card;
    }
    private ImageSource? Poster(SharedLibraryItem item)
    {
        if (posters.TryGetValue(item.Id, out var existing)) return existing;
        ImageSource? image = null;
        try
        {
            if (item.Thumbnail is { Length: <= 18000 } thumbnail)
            {
                using var stream = new MemoryStream(Convert.FromBase64String(thumbnail));
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 140; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); image = bitmap;
            }
        }
        catch (Exception) { }
        if (posters.Count >= RoomLibrary.MaxCatalog) posters.Clear(); posters[item.Id] = image; return image;
    }
    private void SendView(bool browsing)
    {
        if (host || !session.OwnAccess.Browse) return;
        var view = new GuestLibraryView(0, browsing, search.Text, category.SelectedItem + " · " + season.SelectedItem, columns, rows, page, visible, selected, hovered, Math.Round(viewport.ActualWidth), Math.Round(viewport.ActualHeight));
        var key = Wire.Serialize(view); if (key == lastView && Wire.Now - lastViewAt < 2000) return;
        try { session.SendView(hostId(), view with { Sequence = ++sequence }); lastView = key; lastViewAt = Wire.Now; }
        catch (IOException ex) { status.Text = ex.Message; }
    }
    private void RenderPreviews()
    {
        var key = Wire.Serialize(session.Views) + Wire.Serialize(people().Select(p => new { p.Id, p.Name })) + Math.Round(previewScroll.ActualHeight);
        if (key == previewSignature) return; previewSignature = key; previews.Children.Clear();
        foreach (var guest in people().Where(p => p.Approved && !p.IsHost))
        {
            session.Views.TryGetValue(guest.Id, out var view);
            previews.Children.Add(new TextBlock { Text = guest.Name + (view?.Browsing == true ? $" · {view.Category} · Search: {view.Search} · Page {view.Page + 1}" : " · Not browsing"), Margin = new Thickness(5, 12, 5, 5), TextWrapping = TextWrapping.Wrap });
            if (view?.Browsing != true) continue;
            var mirror = new UniformGrid { Columns = view.Columns, Rows = view.Rows, Width = view.GridWidth > 0 ? view.GridWidth : view.Columns * 150, Height = view.GridHeight > 0 ? view.GridHeight : view.Rows * 220 };
            foreach (var id in view.VisibleIds)
                if (session.Catalog.TryGetValue(id, out var item)) mirror.Children.Add(Card(item, true, id == view.HoveredId, id == view.SelectedId));
            while (mirror.Children.Count < view.Columns * view.Rows) mirror.Children.Add(new Border());
            previews.Children.Add(new Viewbox { Child = mirror, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                MaxHeight = Math.Clamp(previewScroll.ActualHeight - 90, 120, 1000), HorizontalAlignment = HorizontalAlignment.Left });
            var selection = view.SelectedId is not null ? session.Catalog.GetValueOrDefault(view.SelectedId) : null;
            previews.Children.Add(new TextBlock { Text = "Gold: hovered · Blue: selected" + (selection is null ? "" : " · " + selection.Title + " · " + selection.Caption), TextWrapping = TextWrapping.Wrap });
        }
    }
    private record GuestChoice(string Id, string Name) { public override string ToString() => Name; }
}
