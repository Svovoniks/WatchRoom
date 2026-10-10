using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Watchroom.Core;
using Watchroom.Desktop;

static class MetadataPickerChecks
{
    public static async Task Run(string directory)
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        var path = Path.Combine(directory, "poster.png");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII="));
        var matches = new List<MetadataMatch>
        {
            new(1719, "tv", "Mr. Bean", "1990", "fixture", "", "Show", Animated: false, Provider: "tvmaze", Evidence: "Already identified in another local release"),
            new(5251, "tv", "Mr. Bean", "2002", "fixture", "", "Show", Animated: true, Provider: "tvmaze")
        };
        var owner = new Window { Left = -20000, Top = 0, Width = 800, Height = 600, WindowStartupLocation = WindowStartupLocation.Manual };
        owner.Show();
        try
        {
            foreach (var width in new[] { 760.0, 620.0 })
            {
                var pending = matches.ToDictionary(m => m.Id, _ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
                Func<MetadataMatch, Task<string?>> loader = m => pending[m.Id].Task;
                var picker = (Window)typeof(Dialogs).GetMethod("MetadataPicker", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                    [owner, matches, loader, "TMDB needs a saved key; TVmaze is available."])!;
                picker.WindowStartupLocation = WindowStartupLocation.Manual; picker.Left = -20000; picker.Top = 0; picker.Width = width;
                picker.Show();
                try
                {
                    picker.UpdateLayout();
                    var list = (ListBox)picker.FindName("MetadataChoices"); var use = (Button)picker.FindName("UseMetadataMatch"); var image = (Image)picker.FindName("MetadataPreview");
                    Check(list.Items.Count == 2 && !use.IsEnabled, "picker requires an explicit choice at width " + width);
                    Check(Text(picker).Any(t => t.Contains("1990") && t.Contains("Live action") && t.Contains("tvmaze")) &&
                        Text(picker).Any(t => t.Contains("2002") && t.Contains("Animation")), "candidate rows display years, animation type and provider at width " + width);
                    Check(Text(picker).Contains("Already identified in another local release"), "neighboring release evidence is visible at width " + width);
                    list.SelectedIndex = 0; list.SelectedIndex = 1; pending[5251].SetResult(path);
                    await Task.Delay(70); await picker.Dispatcher.InvokeAsync(() => picker.UpdateLayout(), DispatcherPriority.Render);
                    Check(use.IsEnabled && image.Source is BitmapSource, "selected candidate's poster loads at width " + width);
                    var selectedPoster = image.Source; pending[1719].SetResult(path); await Task.Delay(70);
                    Check(ReferenceEquals(image.Source, selectedPoster), "late preview from an earlier choice cannot replace the current poster at width " + width);
                    var bitmap = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(picker);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(Path.Combine(directory, "metadata-picker-" + width + ".png"))) encoder.Save(output);
                    list.SelectedIndex = -1;
                    Check(!use.IsEnabled && image.Source is null, "clearing selection disables the match action and clears its preview at width " + width);
                }
                finally { picker.Close(); }
            }
            _ = owner.Dispatcher.BeginInvoke(() =>
            {
                var picker = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "Choose the correct title");
                ((ListBox)picker.FindName("MetadataChoices")).SelectedIndex = 1;
                ((Button)picker.FindName("UseMetadataMatch")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            Check(Dialogs.Choose(owner, matches)?.Id == 5251, "the picker returns the exact provider identity selected by the user");
        }
        finally { owner.Close(); }
        Console.WriteLine($"{checks} metadata picker checks passed. Renders: {directory}");
    }
    private static IEnumerable<string> Text(DependencyObject element)
    {
        if (element is TextBlock text) yield return text.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) foreach (var value in Text(VisualTreeHelper.GetChild(element, i))) yield return value;
    }
}
