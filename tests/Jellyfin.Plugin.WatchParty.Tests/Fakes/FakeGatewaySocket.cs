using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Plugin.WatchParty.Discord;

namespace Jellyfin.Plugin.WatchParty.Tests.Fakes;

/// <summary>A scripted gateway socket: tests push server frames in and read what the client sent.</summary>
public sealed class FakeGatewaySocket : IGatewaySocket
{
    private readonly Channel<GatewayFrame> _inbound = Channel.CreateUnbounded<GatewayFrame>();

    public Uri? ConnectedTo { get; private set; }

    public ConcurrentQueue<string> Sent { get; } = new();

    public int? ClosedWith { get; private set; }

    public bool Disposed { get; private set; }

    public void Push(string json) => _inbound.Writer.TryWrite(new GatewayFrame.Text(json));

    public void PushClose(int code) => _inbound.Writer.TryWrite(new GatewayFrame.Closed(code));

    public void PushHello(int intervalMs = 1000) => Push($$$"""{"op":10,"d":{"heartbeat_interval":{{{intervalMs}}}}}""");

    public void PushReady(int seq = 1) => Push(
        "{\"op\":0,\"s\":" + seq + ",\"t\":\"READY\",\"d\":{\"session_id\":\"sess1\",\"resume_gateway_url\":\"wss://resume.example\","
        + "\"user\":{\"username\":\"PartyBot\"},\"application\":{\"id\":\"app123\"}}}");

    public IEnumerable<JsonElement> SentOps(int op) =>
        Sent.Select(s => JsonDocument.Parse(s).RootElement).Where(e => e.GetProperty("op").GetInt32() == op);

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ConnectedTo = uri;
        return Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken ct)
    {
        Sent.Enqueue(json);
        return Task.CompletedTask;
    }

    public async Task<GatewayFrame> ReceiveAsync(CancellationToken ct)
    {
        return await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    public Task CloseAsync(int code, CancellationToken ct)
    {
        ClosedWith = code;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
