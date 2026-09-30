using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Watchroom.Core;

internal interface IRoomConnection : IAsyncDisposable
{
    Task SendAsync(WireMessage message, CancellationToken ct);
    Task<WireMessage?> ReadAsync(CancellationToken ct);
    void Abort();
}

internal sealed class WebSocketRoomConnection(ClientWebSocket socket) : IRoomConnection
{
    public Task SendAsync(WireMessage message, CancellationToken ct) => SocketMessages.SendAsync(socket, message, ct);
    public Task<WireMessage?> ReadAsync(CancellationToken ct) => SocketMessages.ReadAsync(socket, ct);
    public void Abort() => socket.Abort();
    public ValueTask DisposeAsync() { socket.Dispose(); return ValueTask.CompletedTask; }
}

// Hosted Sites use HTTP polling for room coordination. Media still travels over
// the existing WebRTC channel. Session tokens stay in memory and never enter URLs.
internal sealed class SitesRoomConnection : IRoomConnection
{
    private readonly HttpClient http;
    private readonly Channel<WireMessage> incoming = Channel.CreateBounded<WireMessage>(128);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task polling;
    private long cursor;
    private bool disposed;

    private SitesRoomConnection(HttpClient http)
    {
        this.http = http;
        polling = PollAsync();
    }
    public static async Task<IRoomConnection> OpenAsync(Uri server, WireMessage first, CancellationToken ct)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await http.PostAsJsonAsync("sessions", first, Wire.Json, ct);
            await CheckAsync(response, ct);
            var session = await response.Content.ReadFromJsonAsync<Session>(Wire.Json, ct) ?? throw new IOException("Missing room session");
            if (session.Token.Length != 64) throw new IOException("Invalid room session");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            return new SitesRoomConnection(http);
        }
        catch { http.Dispose(); throw; }
    }
    public async Task SendAsync(WireMessage message, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("session", message, Wire.Json, ct);
        await CheckAsync(response, ct);
    }
    public async Task<WireMessage?> ReadAsync(CancellationToken ct)
    {
        try { return await incoming.Reader.ReadAsync(ct); }
        catch (ChannelClosedException ex) { if (ex.InnerException is not null) throw new IOException(ex.InnerException.Message, ex.InnerException); return null; }
    }
    private async Task PollAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var response = await http.GetAsync("session?since=" + cursor, lifetime.Token);
                await CheckAsync(response, lifetime.Token);
                var batch = await response.Content.ReadFromJsonAsync<Events>(Wire.Json, lifetime.Token) ?? throw new IOException("Missing room events");
                if (batch.Items.Length > 128) throw new IOException("Room event limit exceeded");
                foreach (var item in batch.Items)
                {
                    if (item.Sequence <= cursor) continue;
                    if (item.Sequence != cursor + 1) throw new IOException("Room events expired. Rejoin the room.");
                    await incoming.Writer.WriteAsync(item.Message, lifetime.Token); cursor = item.Sequence;
                }
                await Task.Delay(250, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { incoming.Writer.TryComplete(ex); }
        finally { incoming.Writer.TryComplete(); }
    }
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json") return;
        if ((int)response.StatusCode is >= 300 and < 400 || response.Content.Headers.ContentType?.MediaType == "text/html")
            throw new IOException("This Site requires browser sign-in. Ask its owner to enable access for Windows clients, or use the local server.");
        string? error = null;
        try { error = (await response.Content.ReadFromJsonAsync<Failure>(Wire.Json, ct))?.Error; } catch (System.Text.Json.JsonException) { }
        throw new IOException(error ?? $"Room request failed ({(int)response.StatusCode})");
    }
    public void Abort() { lifetime.Cancel(); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        lifetime.Cancel();
        try { await polling; } catch { }
        // Tell the service immediately; heartbeats also clean up lost connections.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { using var response = await http.DeleteAsync("session", timeout.Token); } catch { }
        http.Dispose(); lifetime.Dispose();
    }
    private sealed record Session(string Token);
    private sealed record Failure(string Error);
    private sealed record Event(long Sequence, WireMessage Message);
    private sealed record Events([property: System.Text.Json.Serialization.JsonPropertyName("events")] Event[] Items);
}
