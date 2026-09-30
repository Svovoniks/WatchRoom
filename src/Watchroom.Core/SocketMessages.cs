using System.Net.WebSockets;
using System.Text;

namespace Watchroom.Core;
public static class SocketMessages
{
    public static async Task<WireMessage?> ReadAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192]; using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536) throw new InvalidDataException("Message too large or not text");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Wire.Read<WireMessage>(Encoding.UTF8.GetString(message.ToArray()));
    }
    public static Task SendAsync(WebSocket socket, WireMessage message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(Wire.Serialize(message)), WebSocketMessageType.Text, true, ct);
}
