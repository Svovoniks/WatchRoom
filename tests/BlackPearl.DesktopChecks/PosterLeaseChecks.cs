using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlackPearl.Core;
using BlackPearl.Desktop;

// A card decodes its artwork for the frame it is drawn in, and holds it only while an image on screen shows it.
static class PosterLeaseChecks
{
    public static async Task Run(string directory, MediaItem entry, Action<bool, string> check)
    {
        async Task Until(Func<bool> condition, string waitingFor)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!condition()) throw new TimeoutException("Timed out waiting for " + waitingFor);
        }
        string Artwork(string name, int width, int height)
        {
            var path = Path.Combine(directory, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * height * 3], width * 3)));
            using var file = File.Create(path); encoder.Save(file);
            return path;
        }
        LibraryCardView Card(string path, string level = "movie") => new(new LibraryCard("Leased", "Movie", path, entry, level));
        var scale = LibraryCardView.PixelScale;
        var portrait = Artwork("lease-portrait.png", 600, 900); var still = Artwork("lease-still.png", 1280, 720);

        var unframed = Card(portrait); await unframed.LoadPosterAsync();
        check(unframed.Poster is { PixelHeight: 400 }, "artwork without a frame decodes at the size it always has");
        var framed = Card(portrait); framed.ShowIn(100, 150); await framed.LoadPosterAsync();
        check(framed.Poster is { } small && Math.Abs(small.PixelWidth - 100 * scale) <= 1 && Math.Abs(small.PixelHeight - 150 * scale) <= 2, "artwork is decoded to fill the frame it is drawn in");
        var filling = Card(still, "episode"); filling.ShowIn(100, 150); await filling.LoadPosterAsync();
        check(filling.Poster is { } wide && Math.Abs(wide.PixelHeight - 150 * scale) <= 1 && wide.PixelWidth > wide.PixelHeight, "wide artwork in a tall frame is decoded to the frame's height");
        var before = framed.Poster; framed.ShowIn(100, 100, whole: true); await framed.LoadPosterAsync();
        check(!ReferenceEquals(before, framed.Poster) && framed.Poster is { } whole && Math.Abs(whole.PixelHeight - 100 * scale) <= 2 && whole.PixelWidth < whole.PixelHeight,
            "a new frame decodes the artwork again, and artwork shown whole fits inside it");
        var huge = Card(portrait); huge.ShowIn(4000, 6000); await huge.LoadPosterAsync();
        check(huge.Poster is { PixelHeight: >= 399 and <= 401 }, "a frame larger than the display never decodes more than artwork without a frame");
        var tiny = Card(Artwork("lease-tiny.png", 40, 60)); tiny.ShowIn(100, 150); await tiny.LoadPosterAsync();
        check(tiny.Poster is { PixelWidth: 40, PixelHeight: 60 }, "artwork smaller than its frame is not enlarged");
        var twin = Card(portrait); twin.ShowIn(100, 150); await twin.LoadPosterAsync();
        var third = Card(portrait); third.ShowIn(100, 150); _ = third.LoadPosterAsync();
        check(third.Poster is not null && ReferenceEquals(twin.Poster, third.Poster), "a card drawing artwork another card holds, in the same frame, shows it at once");

        var grace = LibraryCardView.HiddenForMs;
        var unasked = Card(portrait); unasked.Update(unasked.Card with { Poster = still }); await Task.Delay(150);
        check(unasked.Poster is null, "a card nothing has shown does not load artwork just because it changed");
        var first = Card(portrait); var second = Card(still, "episode");
        var image = new Image { DataContext = first };
        var host = new Window { Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -400, Top = -400, Content = image };
        try
        {
            LibraryCardView.HiddenForMs = 0;
            image.Loaded += (_, _) => PosterLease.Watch(image);
            host.Show();
            await Until(() => first.Poster is not null, "artwork of a card on screen");
            check(true, "an image coming on screen loads its card's artwork");
            image.Visibility = Visibility.Collapsed; LibraryCardView.Sweep();
            check(first.Poster is null, "a card whose image is hidden lets go of its artwork");
            var replacement = Artwork("lease-replacement.png", 300, 450);
            first.Update(first.Card with { Poster = replacement }); await Task.Delay(150);
            check(first.Poster is null, "artwork that changes while its card is off screen is not loaded");
            image.Visibility = Visibility.Visible;
            await Until(() => first.Poster is not null, "artwork of a card shown again");
            check(true, "a card shown again loads its current artwork");
            image.DataContext = second;
            await Until(() => second.Poster is not null, "artwork of the card an image was recycled for");
            LibraryCardView.Sweep();
            check(first.Poster is null && second.Poster is not null, "an image recycled for another card moves its hold on the artwork");
            LibraryCardView.HiddenForMs = 60000;
            image.Visibility = Visibility.Collapsed; LibraryCardView.Sweep();
            check(second.Poster is not null, "artwork that just left the screen is kept for a while");
            image.Visibility = Visibility.Visible; LibraryCardView.HiddenForMs = 0; LibraryCardView.Sweep();
            check(second.Poster is not null, "artwork back on screen is not let go of by its earlier hiding");
            host.Close(); LibraryCardView.Sweep();
            check(second.Poster is null, "closing the window an image is in lets go of its artwork");
        }
        finally { LibraryCardView.HiddenForMs = grace; if (host.IsVisible) host.Close(); }
    }
}
