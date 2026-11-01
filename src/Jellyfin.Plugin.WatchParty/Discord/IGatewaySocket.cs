using System.Net.WebSockets;
using System.Text;

namespace Jellyfin.Plugin.WatchParty.Discord;

/// <summary>One received gateway frame: a complete text message or a close.</summary>
public abstract record GatewayFrame
{
    public sealed record Text(string Json) : GatewayFrame;

    public sealed record Closed(int? Code) : GatewayFrame;
}

/// <summary>The minimum a gateway connection needs from a WebSocket. Abstracted for tests.</summary>
public interface IGatewaySocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);

    Task SendAsync(string json, CancellationToken ct);

    /// <summary>Returns the next complete text message (multi-frame messages are reassembled) or a close.</summary>
    Task<GatewayFrame> ReceiveAsync(CancellationToken ct);

    Task CloseAsync(int code, CancellationToken ct);
}

/// <summary><see cref="IGatewaySocket"/> over <see cref="ClientWebSocket"/>. Sends are serialized.</summary>
public sealed class ClientWebSocketGatewaySocket : IGatewaySocket
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _buffer = new byte[16 * 1024];

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

    public async Task SendAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<GatewayFrame> ReceiveAsync(CancellationToken ct)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(_buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new GatewayFrame.Closed(_socket.CloseStatus is { } status ? (int)status : null);
            }

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return new GatewayFrame.Text(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
        }
    }

    public async Task CloseAsync(int code, CancellationToken ct)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync((WebSocketCloseStatus)code, null, ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
