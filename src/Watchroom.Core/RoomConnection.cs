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
    void UseDiscoveryPolling() { }
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
    private readonly SemaphoreSlim pollWake = new(0, 1);
    private readonly Task polling;
    private long cursor;
    private bool disposed;
    private readonly bool idempotentRequests;
    private int pollingIntervalMs = 100;
    public void UseDiscoveryPolling() => Volatile.Write(ref pollingIntervalMs, 1000);

    internal SitesRoomConnection(HttpClient http, bool idempotentRequests = false)
    {
        this.http = http;
        this.idempotentRequests = idempotentRequests;
        polling = PollAsync();
    }
    public static async Task<IRoomConnection> OpenAsync(Uri server, WireMessage first, CancellationToken ct, bool idempotentRequests = false)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await http.PostAsJsonAsync("sessions", first, Wire.Json, ct);
            await CheckAsync(response, ct);
            var session = await response.Content.ReadFromJsonAsync<Session>(Wire.Json, ct) ?? throw new IOException("Missing room session");
            if (session.Token.Length != 64) throw new IOException("Invalid room session");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            return new SitesRoomConnection(http, idempotentRequests);
        }
        catch { http.Dispose(); throw; }
    }
    public async Task SendAsync(WireMessage message, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        using var response = await RequestAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "session") { Content = JsonContent.Create(message, options: Wire.Json) };
            request.Headers.Add("X-Watchroom-Request-Id", id);
            return await http.SendAsync(request, token);
        }, idempotentRequests || message.Type == "ping", linked.Token);
        // Fetch the authoritative reply immediately after our command commits.
        try { pollWake.Release(); } catch (SemaphoreFullException) { }
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
                using var response = await RequestAsync(token => http.GetAsync("session?since=" + cursor, token), true, lifetime.Token);
                var batch = await response.Content.ReadFromJsonAsync<Events>(Wire.Json, lifetime.Token) ?? throw new IOException("Missing room events");
                if (batch.Items.Length > 128) throw new IOException("Room event limit exceeded");
                foreach (var item in batch.Items)
                {
                    if (item.Sequence <= cursor) continue;
                    if (item.Sequence != cursor + 1) throw new IOException("Room events expired. Rejoin the room.");
                    await incoming.Writer.WriteAsync(item.Message, lifetime.Token); cursor = item.Sequence;
                }
                await pollWake.WaitAsync(Volatile.Read(ref pollingIntervalMs), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { incoming.Writer.TryComplete(ex); }
        finally { incoming.Writer.TryComplete(); }
    }
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json") return;
        if ((int)response.StatusCode is 408 or 429 or >= 500) throw new TransientRoomException($"Room service temporarily unavailable ({(int)response.StatusCode}).");
        if ((int)response.StatusCode is >= 300 and < 400 || response.Content.Headers.ContentType?.MediaType == "text/html")
            throw new IOException("This Site requires browser sign-in. Ask its owner to enable access for Windows clients, or use the local server.");
        string? error = null;
        try { error = (await response.Content.ReadFromJsonAsync<Failure>(Wire.Json, ct))?.Error; } catch (System.Text.Json.JsonException) { }
        throw new IOException(error ?? $"Room request failed ({(int)response.StatusCode})");
    }
    private static async Task<HttpResponseMessage> RequestAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, bool retry, CancellationToken ct)
    {
        // Below the server's 60-second heartbeat lease, including request timeouts.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var attempt = 0;
        while (true)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await send(budget.Token);
                await CheckAsync(response, budget.Token);
                return response;
            }
            catch (Exception ex) when (retry && !ct.IsCancellationRequested && ex is TransientRoomException or HttpRequestException or OperationCanceledException)
            {
                response?.Dispose();
                if (budget.IsCancellationRequested) throw new IOException("Room service did not recover within 30 seconds. Rejoin to reconnect.", ex);
                try { await Task.Delay(Math.Min(2000, 250 * (1 << Math.Min(attempt++, 3))), budget.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Room service did not recover within 30 seconds. Rejoin to reconnect.", ex); }
            }
            catch { response?.Dispose(); throw; }
        }
    }
    private sealed class TransientRoomException(string message) : IOException(message);
    public void Abort() { if (!disposed) lifetime.Cancel(); }
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
