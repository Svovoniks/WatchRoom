using System.Text.Json;
using Watchroom.Core;

static class DiagnosticLogFixture
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "rotation");
        await using (var log = new DiagnosticLog(directory, maximumBytes: 1024, retainedFiles: 3))
        {
            log.Record("oversized", new { text = new string('x', 5000) });
            for (var index = 0; index < 60; index++) log.Record("fixture", new { index, detail = new string('x', 100) });
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var files = Directory.GetFiles(directory);
            check(files.Length == 3 && files.All(path => new FileInfo(path).Length <= 1024), "diagnostics rotate within the configured storage limit");
            var lines = ReadLines(Path.Combine(directory, "diagnostics.jsonl"));
            using var latest = JsonDocument.Parse(lines.Last());
            check(latest.RootElement.GetProperty("data").GetProperty("index").GetInt32() == 59 &&
                latest.RootElement.GetProperty("utcMs").GetInt64() > 0, "flushed diagnostics retain the latest event with timestamps");
            check(files.SelectMany(ReadLines).All(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("kind").GetString() == "fixture"; }),
                "rotation retains complete structured records");
        }
        var blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "fixture");
        await using (var log = new DiagnosticLog(blocked))
        {
            log.Record("fixture", new { test = true });
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(true, "unwritable diagnostic directory cannot interrupt or hang playback");
        }
    }
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
