using System.IO;
using System.Windows;
using Watchroom.Core;

namespace Watchroom.Desktop;
public partial class MainWindow
{
    private int testSequence;
    private long testCaptureAt;
    private readonly Queue<long> testCaptures = new();
    private async Task StartTestDriver()
    {
        var role = Environment.GetEnvironmentVariable("WATCHROOM_TEST_ROLE");
        if (role is not ("host" or "guest")) return;
        try {
            while(player is null) await Task.Delay(100);
            player.SnapshotTaken += (_, e) => PlaybackDiagnostics.Record("frame", new { serverMs = room?.ServerNowMs, file = Path.GetFileName(e.Filename) });
            var client=NewRoom();
            if(role=="host") { selected=FileNames.Parse(Environment.GetEnvironmentVariable("WATCHROOM_TEST_MEDIA") ?? throw new IOException("Missing test media")); playingItem=selected; client.SetHostedFile(selected.Path,selected.Title); }
            await client.ConnectAsync(ServerBox.Text,DisplayNameBox.Text,role=="guest"?Environment.GetEnvironmentVariable("WATCHROOM_TEST_INVITE"):null,persistent:false);
            File.WriteAllText(Path.Combine(App.DataDirectory,"test-invite.txt"),client.Identity!.Room);
            roomPanelVisible=true; ShowPage("Room");
        } catch(Exception ex) { File.AppendAllText(Path.Combine(App.DataDirectory,"test-driver-errors.log"),ex+Environment.NewLine); }
    }
    private void TestDriverTick()
    {
        if(Environment.GetEnvironmentVariable("WATCHROOM_TEST_ROLE") is null) return;
        try {
            if(room?.Identity?.Host==true && room.Snapshot is { } snapshot)
                foreach(var person in snapshot.People.Where(p=>!p.IsHost&&!p.Approved)) room.Send(new("admit",Target:person.Id));
            var captures = Path.Combine(App.DataDirectory, "test-captures.txt");
            if (File.Exists(captures))
            {
                foreach (var line in File.ReadAllLines(captures)) if (long.TryParse(line, out var at)) testCaptures.Enqueue(at);
                File.Delete(captures);
            }
            if (testCaptureAt == 0 && testCaptures.TryDequeue(out var capture)) testCaptureAt = capture;
            if (testCaptureAt > 0 && room is not null && room.ServerNowMs >= testCaptureAt)
            {
                var scheduled = testCaptureAt; testCaptureAt = 0;
                var image = Path.Combine(App.DataDirectory, "frame-" + scheduled + ".png");
                var accepted = player!.TakeSnapshot(0, image, 0, 0);
                PlaybackDiagnostics.Record("frame-request", new { scheduled, serverMs = room.ServerNowMs, accepted, position = player.Time });
            }
            var path=Path.Combine(App.DataDirectory,"test-command.json");
            if(!File.Exists(path)) return;
            var cmd=Wire.Read<TestCommand>(File.ReadAllText(path));
            if(cmd.Sequence<=testSequence || room?.Snapshot?.Media is null || !ready) return;
            testSequence=cmd.Sequence;
            PlaybackDiagnostics.Record("command", new { cmd.Sequence, cmd.Action, cmd.Position, serverMs = room.ServerNowMs, revision = target?.Revision });
            switch(cmd.Action) {
                case "capture": testCaptureAt=cmd.Position; break;
                case "play": SendPlayback(true,Math.Max(0,player!.Time)); break;
                case "pause": SendPlayback(false,Math.Max(0,player!.Time)); break;
                case "seek": SendPlayback(target?.Playing==true,cmd.Position); break;
                case "controls": room.Send(new("controls",Number:cmd.Position)); break;
                case "stop": SendPlayback(false,0); break;
                case "toggle": TogglePlayback(this,new RoutedEventArgs()); break;
            }
        } catch(Exception ex) { File.AppendAllText(Path.Combine(App.DataDirectory,"test-driver-errors.log"),ex+Environment.NewLine); }
    }
    private record TestCommand(int Sequence,string Action,long Position=0);
}
