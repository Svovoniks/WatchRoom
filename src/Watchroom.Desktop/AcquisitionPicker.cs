using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Interactive plugins are still separate processes. The picker only searches, previews and
// returns an explicit selection; its caller performs the download after the dialog closes.
internal sealed class AcquisitionPicker : InAppDialog
{
    private readonly AcquisitionRequest request;
    // The show's files on disk: releases like them, and from their group, are preferred.
    private readonly string[] existing;
    private readonly Func<AcquisitionPlugin, AcquisitionRequest, CancellationToken, Task<AcquisitionResult>> run;
    private readonly Func<AcquisitionPlugin, AcquisitionOptions?> saved;
    private readonly ComboBox providers, quality, numbering;
    private readonly TextBox query, source, filter, offset;
    private readonly ListBox results;
    private readonly TextBlock status, listHeading, emptyTitle, emptyDetail;
    private readonly FrameworkElement empty, offsetRow;
    private readonly Button search, savedPage, download, selectMatches, template, configure;
    private readonly TextBlock selectLabel;
    // Changed release options reload the release list once typing pauses.
    private readonly DispatcherTimer reload = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly CheckBox remember;
    private readonly DataTemplate releaseTemplate = RowTemplate(ReleaseRow), titleTemplate = RowTemplate(TitleRow);
    private CancellationTokenSource? pending;
    private bool closed, updating, fresh, shows, failed, releasesShown, started, autoPick;
    // A title was picked automatically and its releases are about to load: the list stays in its loading state.
    private bool following;
    // Opening searches, picks the provider title that matches and ticks the best release of each episode; the
    // download still waits for the user. Checks turn this off to walk through each step.
    private readonly bool automatic;
    private string? pickedTitle;
    private int generation;
    private const double SidebarWidth = 250;
    private AcquisitionPlugin Plugin => (AcquisitionPlugin)providers.SelectedItem;
    internal (AcquisitionPlugin Plugin, AcquisitionRequest Request, bool Remember)? Selection { get; private set; }

    internal AcquisitionPicker(Window owner, AcquisitionPlugin[] plugins, AcquisitionRequest request, AcquisitionPlugin? initial,
        Func<AcquisitionPlugin, AcquisitionOptions?> saved,
        Func<AcquisitionPlugin, AcquisitionRequest, CancellationToken, Task<AcquisitionResult>>? run = null, bool automatic = true, string[]? existing = null)
    {
        Owner = owner; Title = "Acquire · " + request.Name; Width = 880; Height = 640;
        this.request = request; this.saved = saved; this.run = run ?? ContentAcquisition.RunAsync; this.automatic = automatic; this.existing = existing ?? [];
        NameScope.SetNameScope(this, new NameScope());
        var movie = request.Scope == "movie";
        var root = new DockPanel();

        // What is being acquired and where it goes, then the provider search.
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var context = new Grid { Margin = new Thickness(0, -6, 0, 14) }; header.Children.Add(context);
        context.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); context.ColumnDefinitions.Add(new());
        context.Children.Add(Muted(Summary(request), 12.5));
        var destination = new DockPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 0, 0, 0), ToolTip = "Downloads go to " + request.TargetDirectory };
        Grid.SetColumn(destination, 1); context.Children.Add(destination);
        var folder = Glyph("", 12); folder.Margin = new Thickness(0, 0, 6, 0); folder.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(folder, Dock.Left); destination.Children.Add(folder);
        var path = Muted(request.TargetDirectory, 12.5); path.TextTrimming = TextTrimming.CharacterEllipsis; path.TextWrapping = TextWrapping.NoWrap; destination.Children.Add(path);
        var searchRow = new Grid(); header.Children.Add(searchRow);
        foreach (var width in new[] { new GridLength(170), new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) searchRow.ColumnDefinitions.Add(new() { Width = width });
        providers = new ComboBox { ItemsSource = plugins, DisplayMemberPath = nameof(AcquisitionPlugin.Name), Margin = new Thickness(0, 0, 10, 0), MinWidth = 0 };
        query = new TextBox { Margin = new Thickness(0, 0, 10, 0), MinHeight = 40 }; Grid.SetColumn(query, 1);
        search = new Button { Content = Labelled("", "Search"), Margin = new Thickness(0) }; Grid.SetColumn(search, 2);
        configure = new Button { Content = "", ToolTip = "Provider settings" }; configure.SetResourceReference(StyleProperty, "IconButton"); configure.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(configure, 3);
        searchRow.Children.Add(providers); searchRow.Children.Add(query); searchRow.Children.Add(search); searchRow.Children.Add(configure);

        // The choices sit beside the release options, so changing options never takes room from the list.
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) });
        download = new Button { Content = "Download", IsEnabled = false, MinWidth = 150 }; download.SetResourceReference(StyleProperty, "Primary"); download.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(download);
        remember = new CheckBox { Content = "Remember these options for " + request.Title, IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        footer.Children.Add(remember);

        var body = new Grid { Margin = new Thickness(0, 18, 0, 0) }; root.Children.Add(body);
        body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = new GridLength(SidebarWidth + 22) });
        var list = new DockPanel(); body.Children.Add(list);
        var listHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(listHeader, Dock.Top); list.Children.Add(listHeader);
        var listActions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(listActions, Dock.Right); listHeader.Children.Add(listActions);
        // Picks the best release of each episode: the main shortcut, so it carries the accent.
        var selectContent = Labelled("\uE73E", ""); selectLabel = (TextBlock)selectContent.Children[1];
        selectMatches = Compact("", null); selectMatches.Content = selectContent; selectMatches.FontWeight = FontWeights.SemiBold;
        selectMatches.SetResourceReference(BackgroundProperty, "AccentSoft"); selectMatches.SetResourceReference(ForegroundProperty, "Accent");
        selectMatches.ToolTip = movie ? "Select the best matching release" : "Select the best matching release of each missing episode"; listActions.Children.Add(selectMatches);
        listHeading = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; listHeader.Children.Add(listHeading);
        status = Muted("", 12.5); status.Margin = new Thickness(2, 8, 0, 0); status.MaxHeight = 40; status.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); DockPanel.SetDock(status, Dock.Bottom); list.Children.Add(status);
        var listArea = new Grid(); list.Children.Add(listArea);
        results = new ListBox { SelectionMode = SelectionMode.Multiple, HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemTemplate = releaseTemplate };
        VirtualizingPanel.SetIsVirtualizing(results, true); VirtualizingPanel.SetVirtualizationMode(results, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        listArea.Children.Add(results);
        var emptyPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
        var emptyIcon = Glyph("", 26); emptyIcon.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); emptyIcon.HorizontalAlignment = HorizontalAlignment.Center; emptyIcon.Margin = new Thickness(0, 0, 0, 12); emptyPanel.Children.Add(emptyIcon);
        emptyTitle = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }; emptyPanel.Children.Add(emptyTitle);
        emptyDetail = Muted("", 12.5); emptyDetail.TextAlignment = TextAlignment.Center; emptyDetail.MaxWidth = 340; emptyDetail.Margin = new Thickness(0, 6, 0, 0); emptyPanel.Children.Add(emptyDetail);
        savedPage = new Button { Content = "Show releases from the saved page", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        emptyPanel.Children.Add(savedPage);
        listArea.Children.Add(empty = emptyPanel);

        var sidebar = new StackPanel();
        var sidebarFrame = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(20, 0, 0, 0), Margin = new Thickness(22, 0, 0, 0),
            Child = new ScrollViewer { Content = sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) } };
        sidebarFrame.SetResourceReference(Border.BorderBrushProperty, "Line"); Grid.SetColumn(sidebarFrame, 1); body.Children.Add(sidebarFrame);
        quality = new ComboBox { ItemsSource = new[] { "Any quality", "2160p", "1080p", "720p", "480p" }, Margin = new Thickness(0), MinWidth = 0 };
        sidebar.Children.Add(Section("Quality", quality));
        filter = new TextBox { MinHeight = 40 };
        template = Compact("Use selected release", "QuietButton"); template.HorizontalAlignment = HorizontalAlignment.Left; template.Margin = new Thickness(-10, 4, 0, 0);
        sidebar.Children.Add(Section("Release filter", filter, template, Muted("Write <<variable>> where release names change, such as the episode.", 12)));
        numbering = new ComboBox { ItemsSource = new[] { "Automatic", "Season starts at episode 1", "Absolute episode offset" }, Margin = new Thickness(0), MinWidth = 0 };
        offset = new TextBox { Text = "0", MinHeight = 40, ToolTip = "Added to Watchroom's episode number" };
        var offsetPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var offsetLabel = Muted("Offset added to each episode number", 12); offsetLabel.Margin = new Thickness(0, 0, 0, 6);
        offsetPanel.Children.Add(offsetLabel); offsetPanel.Children.Add(offset); offsetRow = offsetPanel;
        var numberingSection = Section("Episode numbering", numbering, offsetPanel); numberingSection.Visibility = movie ? Visibility.Collapsed : Visibility.Visible;
        sidebar.Children.Add(numberingSection);
        source = new TextBox { MinHeight = 40 };
        sidebar.Children.Add(Section("Provider page", source, Muted("Optional. A show page or search address on the provider.", 12)));
        Content = root;

        foreach (var (name, control, label) in new (string, FrameworkElement, string)[] {
            ("AcquireProvider", providers, "Acquisition provider"), ("AcquireQuery", query, "Provider title search"), ("AcquireQuality", quality, "Release quality"),
            ("AcquireSearch", search, "Search provider"), ("AcquireSavedPage", savedPage, "Show releases from the saved provider page"), ("AcquireResults", results, "Provider titles and releases"),
            ("AcquireDownload", download, "Download selected releases"), ("AcquireFilter", filter, "Release filter"), ("AcquireSource", source, "Provider URL"),
            ("AcquireNumbering", numbering, "Episode numbering"), ("AcquireOffset", offset, "Episode offset"), ("AcquireStatus", status, "Acquisition status"),
            ("AcquireSelectMatches", selectMatches, movie ? "Select matching release" : "Select matching episodes"), ("AcquireTemplate", template, "Create release filter from the selected release"),
            ("AcquireSettings", configure, "Provider settings"), ("AcquireRemember", remember, "Remember these options") })
        { RegisterName(name, control); AutomationProperties.SetName(control, label); }
        providers.SelectionChanged += async (_, _) => { LoadProvider(); if (started) await Start(); };
        query.TextChanged += (_, _) => Invalidate(true);
        source.TextChanged += (_, _) => Invalidate(); filter.TextChanged += (_, _) => Invalidate(); offset.TextChanged += (_, _) => Invalidate();
        quality.SelectionChanged += (_, _) => Invalidate();
        numbering.SelectionChanged += (_, _) => { offset.IsEnabled = numbering.SelectedIndex == 2; offsetRow.Visibility = numbering.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; Invalidate(); };
        search.Click += async (_, _) => await Browse("search"); savedPage.Click += async (_, _) => await RefreshReleases();
        reload.Tick += async (_, _) => { reload.Stop(); await RefreshReleases(); };
        configure.Click += (_, _) =>
        {
            try { if (AcquisitionPluginSettings.Create(owner, Plugin).ShowDialog() == true) LoadProvider(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { status.Text = "Couldn't load provider settings: " + ex.Message; }
        };
        query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Browse("search"); } };
        // Choosing a provider title lists its releases.
        results.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if (shows && fresh && ItemsControl.ContainerFromElement(results, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: AcquisitionChoice title }) await ShowReleases(title);
        };
        results.KeyDown += async (_, e) => { if (e.Key == Key.Enter && shows && results.SelectedItem is AcquisitionChoice title) { e.Handled = true; await ShowReleases(title); } };
        results.SelectionChanged += (_, _) => UpdateActions();
        selectMatches.Click += (_, _) => { if (!fresh || shows) return; results.SelectedItems.Clear(); foreach (var choice in Picks()) results.SelectedItems.Add(choice); };
        template.Click += (_, _) => { if (results.SelectedItem is AcquisitionChoice { SuggestedFilter.Length: > 0 } choice) filter.Text = choice.SuggestedFilter; };
        download.Click += (_, _) => Confirm();
        void Stop() { closed = true; generation++; reload.Stop(); pending?.Cancel(); }
        Closed += (_, _) => Stop(); Unloaded += (_, _) => Stop();
        Loaded += async (_, _) => { query.Focus(); if (!started) { started = true; await Start(); } };
        providers.SelectedItem = initial ?? plugins.FirstOrDefault(x => x.Interactive) ?? plugins.First();
    }

    private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");
    private static string Summary(AcquisitionRequest request)
    {
        if (request.Scope == "movie") return request.Year is { } year ? $"Film · {year}" : "Film";
        var aired = request.Episodes.Count(x => !x.Upcoming); var upcoming = request.Episodes.Length - aired;
        return Count(aired, "missing episode") + (upcoming > 0 ? $" · {upcoming} upcoming, skipped" : "");
    }
    private static TextBlock Muted(string text, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return block;
    }
    private static TextBlock Glyph(string glyph, double size) { var icon = new TextBlock { Text = glyph }; icon.SetResourceReference(StyleProperty, "Icon"); icon.FontSize = size; return icon; }
    private static StackPanel Labelled(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; var icon = Glyph(glyph, 13); icon.Margin = new Thickness(0, 0, 8, 0);
        panel.Children.Add(icon); panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }); return panel;
    }
    // A small header action; the style's own size and margins are for full-size buttons.
    private static Button Compact(string text, string? style)
    {
        var button = new Button { Content = text, FontSize = 13 };
        if (style is not null) button.SetResourceReference(StyleProperty, style);
        button.MinHeight = 32; button.Padding = new Thickness(12, 4, 12, 4); button.Margin = new Thickness(6, 0, 0, 0); return button;
    }
    private static FrameworkElement Section(string heading, params FrameworkElement[] content)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        var label = new TextBlock { Text = heading.ToUpperInvariant() }; label.SetResourceReference(StyleProperty, "Overline"); panel.Children.Add(label);
        foreach (var item in content) { if (item is TextBlock && item.Margin == default) item.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(item); }
        return panel;
    }
    private static DataTemplate RowTemplate(string xaml) => (DataTemplate)XamlReader.Parse(xaml);
    // A release: a round check, its name and how it compares with the request, and the episode it provides.
    private const string ReleaseRow = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
         <Grid x:Name="Row" AutomationProperties.Name="{Binding Title}">
          <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
          <Grid Width="20" Height="20" Margin="2,0,12,0" VerticalAlignment="Center">
           <Ellipse x:Name="Ring" Stroke="{DynamicResource Muted}" StrokeThickness="1.5"/>
           <Ellipse x:Name="Fill" Fill="{DynamicResource Accent}" Visibility="Collapsed"/>
           <TextBlock x:Name="Tick" Text="&#xE73E;" FontFamily="{DynamicResource IconFont}" FontSize="11" Foreground="{DynamicResource AccentInk}" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
          </Grid>
          <StackPanel Grid.Column="1" VerticalAlignment="Center">
           <TextBlock Text="{Binding Title}" TextWrapping="Wrap" FontSize="13.5"/>
           <TextBlock Text="{Binding Detail}" TextWrapping="Wrap" FontSize="12" Foreground="{DynamicResource Muted}" Margin="0,2,0,0"/>
          </StackPanel>
          <Border x:Name="Tag" Grid.Column="2" Background="{DynamicResource AccentSoft}" BorderBrush="Transparent" BorderThickness="1" CornerRadius="10" Padding="9,3" Margin="12,0,2,0" VerticalAlignment="Center">
           <TextBlock x:Name="TagText" Text="{Binding EpisodeCode}" FontSize="11.5" FontWeight="SemiBold" Foreground="{DynamicResource Accent}"/>
          </Border>
         </Grid>
         <DataTemplate.Triggers>
          <DataTrigger Binding="{Binding Matches}" Value="False">
           <Setter TargetName="Row" Property="Opacity" Value="0.6"/><Setter TargetName="Tag" Property="Background" Value="{DynamicResource Elevated}"/><Setter TargetName="TagText" Property="Foreground" Value="{DynamicResource Muted}"/>
          </DataTrigger>
          <!-- A season pack or special of this title: downloadable when ticked on its own, so not dimmed. -->
          <DataTrigger Binding="{Binding Assignable}" Value="True"><Setter TargetName="Row" Property="Opacity" Value="1"/></DataTrigger>
          <!-- An episode that wasn't asked for: outlined, so requested episodes stand out. -->
          <DataTrigger Binding="{Binding Episode}" Value="{x:Null}"><Setter TargetName="Tag" Property="Background" Value="Transparent"/><Setter TargetName="Tag" Property="BorderBrush" Value="{DynamicResource Line}"/></DataTrigger>
          <DataTrigger Binding="{Binding EpisodeCode}" Value="{x:Null}"><Setter TargetName="Tag" Property="Visibility" Value="Collapsed"/></DataTrigger>
          <DataTrigger Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True">
           <Setter TargetName="Fill" Property="Visibility" Value="Visible"/><Setter TargetName="Tick" Property="Visibility" Value="Visible"/><Setter TargetName="Ring" Property="Stroke" Value="{DynamicResource Accent}"/>
          </DataTrigger>
         </DataTemplate.Triggers>
        </DataTemplate>
        """;
    // A provider title: choose one, then Show releases lists its releases.
    private const string TitleRow = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
         <DockPanel AutomationProperties.Name="{Binding Title}">
          <Border DockPanel.Dock="Left" Width="32" Height="32" CornerRadius="8" Background="{DynamicResource Elevated}" Margin="0,0,12,0" VerticalAlignment="Center">
           <TextBlock Text="&#xE7F4;" FontFamily="{DynamicResource IconFont}" FontSize="14" Foreground="{DynamicResource Accent}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <StackPanel VerticalAlignment="Center">
           <TextBlock Text="{Binding Title}" TextWrapping="Wrap" FontSize="13.5" FontWeight="SemiBold"/>
           <TextBlock Text="{Binding Detail}" TextWrapping="Wrap" FontSize="12" Foreground="{DynamicResource Muted}" Margin="0,2,0,0"/>
          </StackPanel>
         </DockPanel>
        </DataTemplate>
        """;
    // The list heading, its actions, and what the list says while it has nothing to show.
    private void RefreshList()
    {
        if (providers.SelectedItem is not AcquisitionPlugin plugin) return;
        var count = results.Items.Count;
        var matching = results.Items.OfType<AcquisitionChoice>().Count(x => x.Matches);
        listHeading.Text = !fresh ? "Results" : shows ? $"Titles on {plugin.Name} · {count}" : (pickedTitle is null ? "Releases" : "Releases for " + pickedTitle) + $" · {count}" + (matching > 0 ? $" · {matching} requested" : "");
        empty.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
        (emptyTitle.Text, emptyDetail.Text) =
            !plugin.Interactive ? ($"{plugin.Name} gets the video for you", "It doesn't list releases first. Choose Acquire to send it the request.")
            : pending is not null || following ? ($"Looking on {plugin.Name}…", pickedTitle is null ? "" : $"Loading the releases of {pickedTitle}.")
            : failed ? ($"Couldn't get results from {plugin.Name}", "Try again, choose another provider or check its settings.")
            : fresh ? (shows ? "No matching titles" : "No releases to show", shows ? "Try a shorter or different search title." : "Try another quality, filter or provider.")
            : reload.IsEnabled ? ("Updating releases…", "")
            : ($"Find {request.Title} on {plugin.Name}", string.IsNullOrWhiteSpace(source.Text) ? "Search to list the provider's matching titles." : "Search for titles, or use the provider page you saved.");
        savedPage.Visibility = plugin.Interactive && !fresh && pending is null && !failed && !reload.IsEnabled && !string.IsNullOrWhiteSpace(source.Text) ? Visibility.Visible : Visibility.Collapsed;
        var picks = Picks().Length;
        selectLabel.Text = request.Scope == "movie" ? "Select best match" : $"Select best matches · {picks}";
        selectMatches.Visibility = fresh && !shows && picks > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void LoadProvider()
    {
        if (providers.SelectedItem is not AcquisitionPlugin plugin) return;
        updating = true;
        var options = saved(plugin) ?? new AcquisitionOptions(Query: request.Title);
        query.Text = options.Query ?? request.Title; source.Text = options.SourceUrl ?? ""; filter.Text = options.Filter ?? "";
        quality.SelectedItem = options.Resolution is null or 1080 ? "1080p" : options.Resolution == 0 ? "Any quality" : options.Resolution + "p";
        if (quality.SelectedIndex < 0) quality.SelectedIndex = 0;
        numbering.SelectedIndex = options.EpisodeOffset is not null ? 2 : options.ProviderSeason is not null ? 1 : 0;
        offset.Text = (options.EpisodeOffset ?? 0).ToString(); offset.IsEnabled = numbering.SelectedIndex == 2; offsetRow.Visibility = numbering.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        // Another provider's titles and picked title mean nothing to this one, so its list always starts empty.
        updating = false; releasesShown = false; shows = false; pickedTitle = null; Invalidate(reload: false);
        search.IsEnabled = plugin.Interactive;
        configure.IsEnabled = plugin.SettingsSchema is not null;
        status.Text = ""; UpdateActions();
    }
    // Clears results that no longer fit the options. A changed title waits for Search; other changes reload the
    // releases already shown, or those of a typed provider page, once typing pauses.
    private void Invalidate(bool clearSource = false, bool reload = true)
    {
        if (updating) return;
        this.reload.Stop();
        if (shows && fresh && !clearSource) return; // provider titles don't depend on release options
        generation++; pending?.Cancel(); fresh = false; failed = false; following = false;
        results.ItemsSource = null;
        if (clearSource) { updating = true; source.Text = ""; updating = false; releasesShown = false; }
        if (reload && !clearSource && providers.SelectedItem is AcquisitionPlugin { Interactive: true } && (releasesShown || !string.IsNullOrWhiteSpace(source.Text))) this.reload.Start();
        status.Text = ""; UpdateActions();
    }
    private AcquisitionChoice[] Picks() => fresh && !shows ? results.Items.OfType<AcquisitionChoice>().Where(x => x.Matches).GroupBy(x => x.Episode).Select(x => x.First()).ToArray() : [];
    // Lists the releases of a provider title, or reloads those shown with the current options.
    internal Task ShowReleases(AcquisitionChoice title) { if (shows) results.SelectedItem = title; return Browse("releases", title); }
    internal Task RefreshReleases() => Browse("releases");
    private AcquisitionOptions Options() => new(query.Text.Trim(), string.IsNullOrWhiteSpace(source.Text) ? null : source.Text.Trim(),
        quality.SelectedIndex == 0 ? 0 : int.Parse(((string)quality.SelectedItem).TrimEnd('p')), filter.Text.Trim(),
        numbering.SelectedIndex == 2 ? int.Parse(offset.Text) : null, numbering.SelectedIndex == 1 ? request.Season : null);
    private void UpdateActions()
    {
        if (providers.SelectedItem is not AcquisitionPlugin plugin) return;
        var selected = results.SelectedItems.OfType<AcquisitionChoice>().ToArray();
        // Releases tagged with requested episodes, one per episode; or a single season pack or special of the title.
        var single = selected is [{ Assignable: true }];
        download.IsEnabled = pending is null && (!plugin.Interactive || fresh && !shows && (single || selected.Length > 0 && selected.All(x => x.Matches) && selected.Select(x => x.Episode).Distinct().Count() == selected.Length));
        selectMatches.IsEnabled = fresh && !shows;
        template.IsEnabled = fresh && !shows && selected.FirstOrDefault()?.SuggestedFilter is { Length: > 0 };
        if (fresh && !shows && selected.Length == 0 && Picks().Length > 0) status.Text = request.Scope == "movie" ? "Tick a release, or use Select best match." : "Tick releases, or use Select best matches to take the best one of each episode.";
        if (fresh && !shows && selected.Length > 0) status.Text = single ? $"Downloads this release for {request.Name}. It has no episode number, so check it is the episode or season you want."
            : selected.Any(x => x.Assignable) ? "A season pack or special downloads on its own. Untick the other releases."
            : selected.Any(x => !x.Matches) ? "Only releases tagged with a requested episode can be downloaded. Adjust the title, filter or numbering if needed." : selected.Select(x => x.Episode).Distinct().Count() != selected.Length ? "Choose one release per episode, or use Select best matches." : $"{Count(selected.Length, "release")} ticked. Nothing downloads until you choose Download" + (pickedTitle is null ? "." : "; Search to choose a different title.");
        download.Content = !plugin.Interactive ? "Acquire with " + plugin.Name : selected.Length == 0 || shows ? "Download" : "Download " + Count(selected.Length, "release");
        RefreshList();
    }
    private async Task Browse(string action, AcquisitionChoice? title = null)
    {
        if (!Plugin.Interactive || closed) return;
        reload.Stop();
        AcquisitionOptions options;
        try { options = Options(); }
        catch (FormatException) { status.Text = "Enter a whole number for the episode offset."; return; }
        catch (OverflowException) { status.Text = "The episode offset is too large."; return; }
        var picking = autoPick; autoPick = false;
        if (string.IsNullOrWhiteSpace(options.Query)) { status.Text = "Enter a title to search for."; return; }
        var show = action != "releases" ? null : title ?? (shows ? results.SelectedItem as AcquisitionChoice : null);
        if (show is not null)
        {
            updating = true; query.Text = show.Title; source.Text = show.Id; updating = false;
            options = options with { Query = show.Title, SourceUrl = show.Id };
            pickedTitle = show.Title;
        }
        if (action == "search") { options = options with { SourceUrl = null }; updating = true; source.Text = ""; updating = false; pickedTitle = null; }
        // A chosen title's releases replace the title list; clear it so the list shows that they're loading instead of
        // looking as if the picker stopped at the titles.
        if (action == "releases" && shows) { results.ItemsSource = null; shows = false; }
        pending?.Cancel();
        var cancellation = pending = new CancellationTokenSource(); var revision = ++generation; following = false;
        fresh = false; status.Text = action == "search" ? "Searching…" : "Loading releases…"; UpdateActions();
        AcquisitionChoice? follow = null;
        try
        {
            var result = await run(Plugin, request with { Action = action, Options = options, SelectedReleases = null }, cancellation.Token);
            if (closed || revision != generation) return;
            // A title that is clearly this one goes straight to its releases, without showing the titles first.
            if (picking && result is { Success: true, Stage: "shows" } && BestTitle(result.Choices ?? []) is { } best) { follow = best; following = true; status.Text = "Loading releases…"; }
            else
            {
                status.Text = result.Message;
                shows = result.Stage == "shows";
                fresh = result.Success && result.Stage is "shows" or "releases"; failed = !result.Success;
                releasesShown = fresh && !shows;
                results.SelectionMode = shows ? SelectionMode.Single : SelectionMode.Multiple; // a click toggles a release, as its check suggests
                results.ItemTemplate = shows ? titleTemplate : releaseTemplate;
                results.ItemsSource = shows ? result.Choices ?? [] : ContentAcquisition.RankChoices(result.Choices ?? [], options.Query, existing);
                if (shows && results.Items.Count > 0) results.SelectedIndex = 0;
                if (picking && shows && fresh && results.Items.Count > 1) status.Text = "Several titles could be this one. Click the right one to see its releases.";
                // In the automatic flow every list of releases arrives with the best release of each episode ticked.
                if (automatic && fresh && !shows) foreach (var pick in Picks()) results.SelectedItems.Add(pick);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException) { if (!closed && revision == generation) { failed = true; status.Text = "Couldn't load provider choices: " + ex.Message; } }
        finally { if (ReferenceEquals(pending, cancellation)) pending = null; cancellation.Dispose(); if (!closed) UpdateActions(); }
        if (follow is not null && !closed && revision == generation) await ShowReleases(follow);
    }
    // Searches the provider, or opens the saved provider page, and lets the results pick themselves.
    private Task Start()
    {
        if (!automatic || closed || providers.SelectedItem is not AcquisitionPlugin { Interactive: true }) return Task.CompletedTask;
        autoPick = true;
        return Browse(string.IsNullOrWhiteSpace(source.Text) ? "search" : "releases");
    }
    // The provider title that is clearly this show or film: its name with the year, then its name alone when only one
    // title has it, then the only title found.
    private AcquisitionChoice? BestTitle(AcquisitionChoice[] titles)
    {
        static string Key(string? text) => new((text ?? "").Normalize(System.Text.NormalizationForm.FormD).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var names = new[] { request.Title, request.OriginalTitle, query.Text }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Key).ToHashSet();
        var dated = request.Year is { } year ? names.Select(x => x + year).ToHashSet() : [];
        var named = titles.Where(x => names.Contains(Key(x.Title))).ToArray();
        return titles.FirstOrDefault(x => dated.Contains(Key(x.Title))) ?? (named.Length == 1 ? named[0] : null) ?? (titles.Length == 1 ? titles[0] : null);
    }
    private void Confirm()
    {
        if (!download.IsEnabled) return;
        if (!Plugin.Interactive) { Selection = (Plugin, request, false); DialogResult = true; return; }
        Selection = (Plugin, request with { Action = "acquire", Options = Options(), SelectedReleases = results.SelectedItems.Cast<AcquisitionChoice>().Select(x => x.Id).ToArray() }, remember.IsChecked == true);
        DialogResult = true;
    }
}

internal static class AcquisitionPluginSettings
{
    internal static InAppDialog Create(Window owner, AcquisitionPlugin plugin)
    {
        var schema = Wire.Read<AcquisitionSetting[]>(File.ReadAllText(Path.GetFullPath(Path.Combine(plugin.Directory, plugin.SettingsSchema!))));
        var path = Path.Combine(plugin.Directory, "settings.json");
        var settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Plugin settings must be an object.") : new JsonObject();
        var dialog = new InAppDialog { Owner = owner, Title = plugin.Name + " settings", Width = 580, Height = 590 };
        NameScope.SetNameScope(dialog, new NameScope());
        var root = new DockPanel(); var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; footer.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var save = new Button { Content = "Save settings" }; save.SetResourceReference(FrameworkElement.StyleProperty, "Primary"); actions.Children.Add(save); footer.Children.Add(actions);
        var fields = new StackPanel(); root.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var readers = new List<(AcquisitionSetting Field, JsonNode? Original, Func<string> Read)>();
        foreach (var field in schema)
        {
            fields.Children.Add(new TextBlock { Text = field.Label });
            JsonNode? node = settings; foreach (var part in field.Key.Split('.')) node = (node as JsonObject)?[part];
            var value = node?.ToString() ?? field.Default ?? "";
            Control control; Func<string> read;
            if (field.Type == "password") { var input = new PasswordBox { Password = value }; control = input; read = () => input.Password; }
            else if (field.Type == "choice")
            {
                // A stored value the schema doesn't list stays selectable, so saving doesn't drop it.
                var choices = field.Choices ?? []; if (value.Length > 0 && !choices.Contains(value)) choices = [.. choices, value];
                var input = new ComboBox { ItemsSource = choices, SelectedItem = value }; control = input; read = () => input.SelectedItem as string ?? "";
            }
            else { var input = new TextBox { Text = value }; control = input; read = () => input.Text.Trim(); }
            fields.Children.Add(control); AutomationProperties.SetName(control, field.Label); dialog.RegisterName("Setting_" + field.Key.Replace('.', '_'), control); readers.Add((field, node, read));
            if (field.Help is { } help) { var hint = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 14) }; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); fields.Children.Add(hint); }
        }
        save.Click += (_, _) =>
        {
            try
            {
                var updated = (JsonObject)settings.DeepClone();
                foreach (var (field, original, read) in readers)
                {
                    var value = read(); var parts = field.Key.Split('.'); var parent = updated;
                    foreach (var part in parts.SkipLast(1)) { if (parent[part] is not JsonObject) parent[part] = new JsonObject(); parent = (JsonObject)parent[part]!; }
                    if (value.Length == 0) parent.Remove(parts[^1]);
                    // An unchanged value keeps its stored JSON type, such as a number under a text field.
                    else if (original is JsonValue && value == original.ToString()) parent[parts[^1]] = original.DeepClone();
                    else parent[parts[^1]] = field.Type == "number" ? JsonValue.Create(int.Parse(value)) : JsonValue.Create(value);
                }
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, updated.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { error.Text = "Enter a whole number for numeric settings."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { error.Text = "Couldn't save plugin settings: " + ex.Message; }
        };
        dialog.RegisterName("SavePluginSettings", save); dialog.Content = root; return dialog;
    }
}
