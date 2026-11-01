using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlackPearl.Core;
using BlackPearl.Desktop;

// The acquisition picker and plugin settings keep their actions inside the owner window at short window heights.
static class AcquisitionLayoutChecks
{
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    public static void Run(MainWindow window, string directory, Action<bool, string> check)
    {
        static void Settle() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
        var plugin = new AcquisitionPlugin("interactive", "EZTV", "", ["show"], "unused.exe", [], directory, 30, true, AcquisitionChecks.WriteSettingsSchema(directory));
        var request = new AcquisitionRequest(1, "season", "show", "Dark Matter S02", "Dark Matter", null, 2024, Path.Combine(directory, "Dark Matter"), false,
            Path.Combine(directory, "Dark Matter"), null, 2, [new(2, 1, null, "2024-01-01", false)], [], []);
        foreach (var (width, height) in new[] { (0.0, 0.0), (1280.0, 672.0), (1000.0, 560.0), (900.0, 480.0) })
        {
            if (width == 0) window.WindowState = WindowState.Maximized; else { window.WindowState = WindowState.Normal; window.Width = width; window.Height = height; } window.UpdateLayout(); Settle();
            foreach (var (name, dialog, action) in new (string, InAppDialog, string)[] {
                ("picker", new AcquisitionPicker(window, [plugin], request, null, _ => null, (_, _, _) => Task.FromResult(new AcquisitionResult(true, "Select releases to download (2 match).", "releases", [
                    new("a", "Dark.Matter.2024.S02E01.1080p.WEB.h264-ETHEL", "Requested: S02E01 | 1080p", true, "S02E01", 1080),
                    new("b", "Dark Matter 2024 S02E01 1080p HEVC x265-MeGusta", "Requested: S02E01 | 1080p", true, "S02E01", 1080),
                    new("c", "Dark.Matter.2024.S02E02.1080p.WEB.h264-ETHEL", "Requested: S02E02 | 1080p", true, "S02E02", 1080),
                    new("d", "Dark.Matter.2024.S02E01.720p.WEB.h264-ETHEL", "Different quality (selected 1080p) | 720p", false, null, 720),
                    new("e", "Dark.Matter.2024.S02E03.2160p.WEB.h265-ETHEL", "Different quality (selected 1080p) | 2160p", false, null, 2160)])), automatic: false), "AcquireDownload"),
                ("settings", AcquisitionPluginSettings.Create(window, plugin), "SavePluginSettings") })
            {
                dialog.Show(); Settle();
                var popup = (Popup)typeof(InAppDialog).GetField("popup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
                var layer = (FrameworkElement)popup.Child; layer.UpdateLayout();
                var button = (FrameworkElement)dialog.FindName(action);
                var bounds = button.TransformToAncestor(layer).TransformBounds(new Rect(button.RenderSize));
                var content = (FrameworkElement)window.Content;
                GetWindowRect(((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(layer)).Handle, out var rect);
                var scale = VisualTreeHelper.GetDpi(window).DpiScaleY;
                Console.WriteLine($"popup window {rect.Right - rect.Left}x{rect.Bottom - rect.Top} px at scale {scale}; screen {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");
                Console.WriteLine($"{name} {width}x{height}: owner content {content.ActualWidth:F0}x{content.ActualHeight:F0}, layer {layer.ActualWidth:F0}x{layer.ActualHeight:F0}, dialog {dialog.ActualWidth:F0}x{dialog.ActualHeight:F0} (max {dialog.MaxHeight:F0}), action bottom {bounds.Bottom:F0}");
                var image = new RenderTargetBitmap((int)Math.Ceiling(layer.ActualWidth), (int)Math.Ceiling(layer.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(layer);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(directory, $"{name}-{width}x{height}.png"))) encoder.Save(file);
                var whole = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); whole.Render(window);
                encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(whole));
                using (var file = File.Create(Path.Combine(directory, $"{name}-{width}x{height}-window.png"))) encoder.Save(file);
                // The popup's own window is what Windows shows; WPF can lay out more than fits in it.
                var corner = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                var title = button.PointToScreen(new Point(0, 0));
                var size = width == 0 ? "maximized" : $"{width}x{height}";
                check(title.Y >= rect.Top && corner.Y <= rect.Bottom && corner.X <= rect.Right && bounds.Bottom <= layer.ActualHeight,
                    $"the {name} action is visible in a {size} window (action {title.Y:F0}-{corner.Y:F0} px, popup {rect.Top}-{rect.Bottom} px)");
                if (dialog is AcquisitionPicker)
                {
                    ((Button)dialog.FindName("AcquireSearch")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Settle();
                    var list = (ListBox)dialog.FindName("AcquireResults"); Settle(); layer.UpdateLayout(); Settle();
                    // A mouse click on a release row toggles it, keeping the others, as its round check suggests. A row
                    // passes its click to the list only once it has keyboard focus, which a background test process
                    // can't take, so the click goes to the list's handler directly.
                    var clicked = typeof(ListBox).GetMethod("NotifyListItemClicked", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    void ClickRow(int index) { clicked.Invoke(list, [list.ItemContainerGenerator.ContainerFromIndex(index), System.Windows.Input.MouseButton.Left]); Settle(); }
                    ClickRow(0); ClickRow(2);
                    var both = list.SelectedItems.Count == 2 && list.SelectedItems.Contains(list.Items[0]) && list.SelectedItems.Contains(list.Items[2]);
                    ClickRow(2);
                    check(both && list.SelectedItems.Count == 1 && list.SelectedItems.Contains(list.Items[0]), $"clicking release rows adds and removes each one in a {size} window");
                    var listCorner = list.PointToScreen(new Point(0, list.ActualHeight));
                    var filterBox = (FrameworkElement)dialog.FindName("AcquireFilter");
                    check(list.Items.Count > 0 && list.ActualHeight >= 200 && listCorner.Y <= rect.Bottom && filterBox.IsVisible,
                        $"the release list and its options share the dialog in a {size} window (list {list.ActualHeight:F0} high)");
                    var advancedImage = new RenderTargetBitmap((int)Math.Ceiling(layer.ActualWidth), (int)Math.Ceiling(layer.ActualHeight), 96, 96, PixelFormats.Pbgra32); advancedImage.Render(layer);
                    encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(advancedImage));
                    using (var file = File.Create(Path.Combine(directory, $"{name}-{width}x{height}-options.png"))) encoder.Save(file);
                }
                dialog.Close(); Settle();
            }
        }
    }
}
