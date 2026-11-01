using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlackPearl.Core;
using BlackPearl.Desktop;

// The room's queues: a guest's Queues page shows the host's saved queues, and the host applies the guest's changes.
// Two windows are joined directly instead of over a peer connection; the peer transport is covered by the smoke checks.
static class GuestLibraryLayoutChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var directory = Environment.GetEnvironmentVariable("BLACKPEARL_DATA")!;
        var host = new MainWindow(); var guest = new MainWindow();
        Participant[] people = [new("host", "Fixture Host", true, true), new("guest", "Fixture Guest", false, true)];
        RoomClient Join(MainWindow window, string peer, bool isHost)
        {
            var client = new RoomClient();
            typeof(RoomClient).GetProperty("Identity")!.SetValue(client, new Welcome("fixture", peer, isHost, [], false));
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, new RoomSnapshot(people, null, null, false));
            typeof(MainWindow).GetField("room", Flags)!.SetValue(window, client);
            return client;
        }
        var hostClient = Join(host, "host", true); var guestClient = Join(guest, "guest", false);
        var videos = Enumerable.Range(1, 6).Select(i =>
        {
            var path = Path.Combine(directory, $"queue-video-{i}.mp4"); File.WriteAllBytes(path, []);
            return new MediaItem("video-" + i, path, $"Fixture film {i}", "Movie", 2020 + i, RuntimeMinutes: 90 + i);
        }).ToList();
        var hostItems = Field<List<MediaItem>>(host, "items"); hostItems.Clear(); hostItems.AddRange(videos);
        var hostQueues = Field<List<SavedQueue>>(host, "savedQueues"); hostQueues.Clear();
        hostQueues.Add(new("evening", "Evening films", ["video-1", "video-2", "video-3"]));
        var guestOwn = Field<List<SavedQueue>>(guest, "savedQueues"); guestOwn.Clear(); guestOwn.Add(new("mine", "My own queue", []));
        var toGuest = new List<WireMessage>();
        typeof(MainWindow).GetField("queueOutbox", Flags)!.SetValue(host, (Action<string, WireMessage>)((peer, message) =>
        { toGuest.Add(message); Call(guest, "ReceiveQueueMessage", guestClient, "host", message); }));
        typeof(MainWindow).GetField("queueOutbox", Flags)!.SetValue(guest, (Action<string, WireMessage>)((peer, message) =>
        { Check(peer == "host", "guests send queue changes to the host"); Call(host, "ReceiveQueueMessage", hostClient, "guest", message); }));

        Call(host, "ShareQueues", [null]);
        Call(guest, "ShowPage", "Queues");
        var gallery = Named<ItemsControl>(guest, "QueueGallery");
        Check(Named<TextBlock>(guest, "QueuesHeading").Text == "Fixture Host's queues" && GalleryNames(gallery).SequenceEqual(["Evening films", "New queue"]),
            "a guest's Queues page shows the host's queues instead of their own");
        Call(guest, "OpenQueue", "evening");
        var rows = Named<ListBox>(guest, "SavedQueueItems");
        string[] Titles() => rows.Items.Cast<object>().Select(row => (string)row.GetType().GetProperty("Title")!.GetValue(row)!).ToArray();
        Check(Titles().SequenceEqual(["Fixture film 1", "Fixture film 2", "Fixture film 3"]), "the guest sees the host's queue entries with their titles");
        Check(Named<Button>(guest, "PlaySavedQueueButton").Visibility != Visibility.Visible && Named<Button>(guest, "AddQueueVideosButton").Visibility != Visibility.Visible &&
            Named<Button>(guest, "QueueMoreButton").Visibility != Visibility.Visible && Named<TextBlock>(guest, "SavedQueueFeedback").Text.Contains("Fixture Host's library"),
            "guests can't play, rename or delete the host's queues and are told how to add videos");

        SavedQueue Opened() => (SavedQueue)typeof(MainWindow).GetProperty("OpenedQueue", Flags)!.GetValue(guest)!;
        string[] HostIds() => hostQueues.Single(q => q.Id == "evening").MediaIds;
        Call(guest, "MoveSavedEntry", Opened(), 2, 0);
        Check(HostIds().SequenceEqual(["video-3", "video-1", "video-2"]) && Titles()[0] == "Fixture film 3", "a guest's reorder is applied to the host's queue and shows at once");
        Pump();
        Check(Titles().SequenceEqual(["Fixture film 3", "Fixture film 1", "Fixture film 2"]), "the host shares its updated queue back to the guest");
        Call(guest, "RemoveSavedEntry", Opened(), rows.Items[1]);
        Pump();
        Check(HostIds().SequenceEqual(["video-3", "video-2"]) && Titles().Length == 2, "a guest can remove a video from the host's queue");
        var hostVideo = new MediaItem("video-5", "", "Fixture film 5", "Movie");
        Call(guest, "AddToSavedQueue", Opened(), new[] { hostVideo });
        Pump();
        Check(HostIds().SequenceEqual(["video-3", "video-2", "video-5"]) && Titles().Last() == "Fixture film 5", "a guest can add the host's videos to the host's queue");

        Call(guest, "SendQueueEdit", new QueueEdit("order", "evening", "0000000000000000", [1, 0, 2]));
        Pump();
        Check(HostIds().SequenceEqual(["video-3", "video-2", "video-5"]) && Named<TextBlock>(guest, "SavedQueueFeedback").Text.Contains("changed before your change"),
            "an edit made against an out-of-date queue is refused and the guest is told why");
        Call(guest, "SendQueueEdit", new QueueEdit("add", "evening", MediaIds: ["not-in-library"]));
        Check(HostIds().Length == 3 && Named<TextBlock>(guest, "SavedQueueFeedback").Text.Contains("not available"), "videos the host doesn't have are refused");
        Call(guest, "SendQueueCreate", "Weekend", new[] { hostVideo });
        Pump();
        Call(guest, "ShowAllQueues");
        Check(hostQueues.Any(q => q.Name == "Weekend" && q.MediaIds.SequenceEqual(["video-5"])) && GalleryNames(gallery).SequenceEqual(["Evening films", "Weekend", "New queue"]),
            "a guest can create a queue in the host's queues");
        Save(guest, "guest-host-queues.png");

        toGuest.Clear(); Field<HashSet<string>>(host, "queueSharePeers").Clear();
        Call(host, "ShareQueuesWithNewGuests", hostClient.Snapshot!);
        Check(toGuest.Count > 0 && toGuest.All(message => message.Type == QueueSharing.StateMessage), "a newly admitted guest receives the host's queues");
        toGuest.Clear(); Call(host, "ShareQueuesWithNewGuests", hostClient.Snapshot!);
        Check(toGuest.Count == 0, "guests who already have the queues are not sent them again for every room update");

        typeof(MainWindow).GetField("room", Flags)!.SetValue(guest, null); Call(guest, "ClearHostQueues");
        Check(Named<TextBlock>(guest, "QueuesHeading").Text == "Queues" && GalleryNames(gallery).SequenceEqual(["My own queue", "New queue"]),
            "leaving the room brings back the guest's own queues");
        host.Close(); guest.Close();
        Console.WriteLine("Room queue checks passed.");
    }
    static string[] GalleryNames(ItemsControl gallery) =>
        gallery.Items.Cast<object>().Select(card => (string)card.GetType().GetProperty("Name")!.GetValue(card)!).ToArray();
    // Lets the share timer and queued dispatcher work run.
    static void Pump(int milliseconds = 400)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(15);
        }
    }
    static T Named<T>(MainWindow window, string name) => (T)window.FindName(name);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    static object? Call(object target, string name, params object?[]? args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    static void Save(MainWindow window, string name)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 760)); root.Arrange(new Rect(0, 0, 1100, 760)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/library-ui"); using var output = File.Create(Path.Combine("artifacts/library-ui", name)); encoder.Save(output);
    }
}
