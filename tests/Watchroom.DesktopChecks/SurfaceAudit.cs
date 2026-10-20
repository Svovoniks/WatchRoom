using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Watchroom.Core;
using Watchroom.Desktop;

// Opens the surfaces that live outside the main window's tree (dialogs, the guest library window,
// context menus, dropdowns and player menus) and runs BorderAudit over each one.
static class SurfaceAudit
{
    public static async Task Run(MainWindow window, Func<int, Task> settle, Action<string, string> report)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(window, args);
        async Task Audit(string name)
        {
            await settle(500);
            foreach (var source in PresentationSource.CurrentSources.OfType<PresentationSource>().ToList())
                if (source.RootVisual is Visual root && !ReferenceEquals(root, window) && root is not UIElement { IsVisible: false })
                    foreach (var finding in BorderAudit.Find(root)) report(name, finding);
        }

        // Dialogs: audit each window as it appears, then dismiss it so ShowDialog returns.
        var dialog = "";
        var hook = new RoutedEventHandler(async (sender, _) =>
        {
            if (sender is not Window shown || ReferenceEquals(shown, window)) return;
            await Audit(dialog);
            shown.Close();
        });
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, hook);
        dialog = "dialog-unsaved-settings"; Dialogs.ConfirmUnsavedSettings(window);
        dialog = "dialog-prompt"; Dialogs.Prompt(window, "Create queue", "Queue name", "Saturday night", help: "Queues stay on this computer.");
        SavedRoom[] rooms = [new("room-1", "Movie night", "https://rooms.example", "ABCD", "host-key"), new("room-2", "Anime club", "https://rooms.example", "EFGH")];
        dialog = "dialog-watch-room"; Dialogs.ChooseWatchRoom(window, rooms, rooms[0], _ => null);
        dialog = "dialog-metadata-picker";
        Dialogs.Choose(window, [new(1, "movie", "Example title", "2024", null, "An overview long enough to wrap onto a second line in the picker."),
            new(2, "tv", "Another title", "2019", null, "A second candidate.")], null, "Pick the matching title.");

        // The guest library window, populated the same way a room shares it.
        var host = new RoomLibrary(true, "host"); var guest = new RoomLibrary(false, "guest");
        Participant[] people = [new("host", "Fixture Host", true, true), new("guest", "Fixture Guest", false, true)];
        var traffic = new Queue<Action>();
        host.Send += (_, message) => traffic.Enqueue(() => guest.Receive("host", message));
        guest.Send += (_, message) => traffic.Enqueue(() => host.Receive("guest", message));
        host.SetPeople(people);
        host.Publish(Enumerable.Range(1, 40).Select(i => new SharedLibraryItem("item-" + i, "Fixture movie " + i, "Movie", 2026, null, null, null, "A generated library fixture.", true)));
        host.SetAccess("guest", new(true, true, true));
        while (traffic.TryDequeue(out var action)) action();
        dialog = "guest-library"; new RoomLibraryWindow(guest, false, () => "host", () => people, _ => { }) { Owner = window }.Show();
        await settle(1500);
        dialog = "host-library"; new RoomLibraryWindow(host, true, () => "host", () => people, _ => { }) { Owner = window }.Show();
        await settle(1500);

        // Card context menus with each submenu opened in turn.
        foreach (var (level, navigate) in new (string, Action)[] { ("library", () => Call("NavigateLibrary", 0, null, null, null)) })
        {
            navigate(); await settle(1500);
            var card = Descendants(window).OfType<Button>().FirstOrDefault(x => x.Tag is LibraryCard && x.IsVisible);
            if (card is null) continue;
            var args = (ContextMenuEventArgs)typeof(ContextMenuEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(c => c.GetParameters().Length == 2).Invoke([card, true]);
            Call("CardContextMenu", card, args);
            if (card.ContextMenu is not { } menu) continue;
            menu.IsOpen = true; await Audit($"{level}-card-menu");
            foreach (var item in menu.Items.OfType<MenuItem>().Where(x => x.HasItems))
            { item.IsSubmenuOpen = true; await Audit($"{level}-card-menu-{item.Header}"); item.IsSubmenuOpen = false; }
            menu.IsOpen = false;
        }

        // Dropdowns on every settings section, then the player's own menus.
        Call("ShowPage", "Settings");
        foreach (var section in new[] { "Profile", "Metadata", "Playback", "Appearance", "Updates" })
        {
            Call("ShowPreferenceSection", section); await settle(800);
            foreach (var combo in Descendants(window).OfType<ComboBox>().Where(x => x.IsVisible && x.IsEnabled).ToList())
            { combo.IsDropDownOpen = true; await Audit($"settings-{section.ToLowerInvariant()}-dropdown-{combo.Name}"); combo.IsDropDownOpen = false; }
        }
        Call("ShowPage", "Room"); await settle(1000);
        foreach (var field in typeof(MainWindow).GetFields(flags).Where(x => typeof(Popup).IsAssignableFrom(x.FieldType)))
        {
            if (field.GetValue(window) is not Popup popup) continue;
            popup.IsOpen = true; await Audit("player-" + field.Name); popup.IsOpen = false;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
