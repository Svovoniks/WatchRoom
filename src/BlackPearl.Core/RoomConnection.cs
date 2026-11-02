using System.Net.WebSockets;

namespace BlackPearl.Core;

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
