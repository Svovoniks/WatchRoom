using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? roomReconnect;
    private Window? disconnectToast;

    private void NotifyDisconnected(string name)
    {
        System.Media.SystemSounds.Exclamation.Play();
        disconnectToast?.Close();
        var area = SystemParameters.WorkArea;
        var toast = new Window
        {
            Title = "Watchroom · Participant disconnected", Width = 360, Height = 110,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = area.Right - 380, Top = area.Bottom - 130,
            Content = new Border
            {
                Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Accent"),
                BorderThickness = new Thickness(1), Padding = new Thickness(20),
                Child = new TextBlock { Text = name + " disconnected", Foreground = (Brush)FindResource("Ink"),
                    FontSize = 18, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        disconnectToast = toast;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        timer.Tick += (_, _) => { timer.Stop(); toast.Close(); };
        toast.Closed += (_, _) => { timer.Stop(); if (disconnectToast == toast) disconnectToast = null; };
        toast.Show(); timer.Start();
    }

    private async Task ReconnectRoomAsync(RoomClient previous)
    {
        if (roomReconnect is not null || closing || previous.Identity is not { } identity || previous.ServerAddress is not { } server) return;
        // Rejoining uses the same saved guest admission or host credential.
        if (identity.Host && identity.HostKey is null) return;
        using var retry = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        roomReconnect = retry;
        var name = SavedDisplayName;
        var roomName = previous.Snapshot?.Name;
        var source = previous.HostedMedia as ILocalMediaSource;
        var playback = previous.Snapshot?.Playback;
        var position = playback is null ? 0 : SyncMath.TargetPosition(playback, previous.ServerNowMs);
        var attempt = 0;
        NotifyDisconnected("Room connection");
        try
        {
            while (!retry.IsCancellationRequested)
            {
                RoomSubtitle.Text = "Connection interrupted · Reconnecting…";
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, Math.Min(attempt++, 4)))), retry.Token);
                await Disconnect(reconnecting: true);
                retry.Token.ThrowIfCancellationRequested();
                var candidate = NewRoom();
                try
                {
                    await candidate.ConnectAsync(server, name, identity.Room, identity.HostKey, roomName: identity.Host ? roomName : null);
                    if (retry.IsCancellationRequested || room != candidate) { await candidate.DisposeAsync(); return; }
                    if (identity.Host && source is not null && File.Exists(source.LocalPath))
                    {
                        candidate.SetHostedFile(source.LocalPath, source.Media.Title);
                        candidate.Send(new("media", Data: Wire.Serialize(candidate.HostedMedia!.Media)));
                        candidate.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, candidate.HostedMedia.Media.Id, playback?.Playing == true, position, 0))));
                    }
                    RememberCurrentRoom();
                    RoomSubtitle.Text = identity.Host ? "Reconnected · You are the host" : "Reconnected · Waiting for the host";
                    SetStatus("Room connection restored."); UpdateSessionControls(); return;
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException)
                {
                    if (room == candidate) await Disconnect(reconnecting: true); else await candidate.DisposeAsync();
                    if (!retry.IsCancellationRequested) SetStatus("Reconnecting… " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (retry.IsCancellationRequested) { }
        finally { if (roomReconnect == retry) roomReconnect = null; }
    }
}
