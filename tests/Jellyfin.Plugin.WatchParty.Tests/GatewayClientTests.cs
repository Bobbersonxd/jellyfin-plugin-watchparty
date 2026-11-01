using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Plugin.WatchParty.Discord;
using Jellyfin.Plugin.WatchParty.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public sealed class GatewayClientTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly ConcurrentQueue<FakeGatewaySocket> _sockets = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly GatewayClient _client;
    private Task? _run;

    public GatewayClientTests()
    {
        _client = new GatewayClient(
            () =>
            {
                var s = new FakeGatewaySocket();
                _sockets.Enqueue(s);
                return s;
            },
            _time,
            new Random(1),
            NullLogger<GatewayClient>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_run is not null)
        {
            await Task.WhenAny(_run, Task.Delay(2000));
        }
    }

    private void Start() => _run = Task.Run(() => _client.RunAsync("tok", _cts.Token));

    /// <summary>Advances fake time in small steps until the condition holds (real-time bounded).</summary>
    private async Task AdvanceUntil(Func<bool> condition, int stepMs = 50, int maxRealMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxRealMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(5);
            _time.Advance(TimeSpan.FromMilliseconds(stepMs));
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int maxRealMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxRealMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(5);
        }
    }

    private FakeGatewaySocket Socket(int index) => _sockets.ElementAt(index);

    private async Task<FakeGatewaySocket> ConnectedReady()
    {
        Start();
        await WaitUntil(() => _sockets.Count == 1);
        var s = Socket(0);
        s.PushHello();
        s.PushReady(seq: 1);
        await WaitUntil(() => _client.State == BotState.Connected);
        return s;
    }

    [Fact]
    public async Task Hello_SendsIdentify_WithIntentsZero()
    {
        Start();
        await WaitUntil(() => _sockets.Count == 1);
        var s = Socket(0);
        Assert.Equal("wss://gateway.discord.gg/?v=10&encoding=json", s.ConnectedTo!.ToString());

        s.PushHello();
        await WaitUntil(() => s.SentOps(2).Any());

        var identify = s.SentOps(2).Single().GetProperty("d");
        Assert.Equal("tok", identify.GetProperty("token").GetString());
        Assert.Equal(0, identify.GetProperty("intents").GetInt32());
        Assert.Equal("jellyfin-watchparty", identify.GetProperty("properties").GetProperty("browser").GetString());
        Assert.Equal(BotState.Connecting, _client.State);
    }

    [Fact]
    public async Task Hello_SchedulesHeartbeat_WithLastSeq()
    {
        var s = await ConnectedReady();

        await AdvanceUntil(() => s.SentOps(1).Any());

        var beat = s.SentOps(1).First();
        Assert.Equal(1, beat.GetProperty("d").GetInt32());
    }

    [Fact]
    public async Task ServerOp1_SendsImmediateHeartbeat()
    {
        var s = await ConnectedReady();

        s.Push("""{"op":1,"d":null}""");

        await WaitUntil(() => s.SentOps(1).Any());
    }

    [Fact]
    public async Task Ready_SetsConnected_AndApplicationId()
    {
        var readyRaised = false;
        _client.Ready += () =>
        {
            readyRaised = true;
            return Task.CompletedTask;
        };

        await ConnectedReady();

        Assert.Equal("app123", _client.ApplicationId);
        Assert.Equal("PartyBot", _client.BotUserName);
        await WaitUntil(() => readyRaised);
    }

    [Fact]
    public async Task Dispatch_InteractionCreate_RaisesEvent()
    {
        var got = new TaskCompletionSource<string>();
        _client.InteractionCreated += d =>
        {
            got.TrySetResult(d.GetProperty("id").GetString()!);
            return Task.CompletedTask;
        };
        var s = await ConnectedReady();

        s.Push("""{"op":0,"s":2,"t":"INTERACTION_CREATE","d":{"id":"int1","type":2}}""");

        Assert.Equal("int1", await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task MissedHeartbeatAck_Reconnects_WithResume()
    {
        var s = await ConnectedReady();

        await AdvanceUntil(() => _sockets.Count == 2);
        Assert.Equal(4000, s.ClosedWith);

        var second = Socket(1);
        Assert.StartsWith("wss://resume.example", second.ConnectedTo!.ToString());
        second.PushHello();
        await WaitUntil(() => second.SentOps(6).Any());

        var resume = second.SentOps(6).Single().GetProperty("d");
        Assert.Equal("sess1", resume.GetProperty("session_id").GetString());
        Assert.Equal(1, resume.GetProperty("seq").GetInt32());
        Assert.Empty(second.SentOps(2));
    }

    [Fact]
    public async Task HeartbeatAcked_StaysConnected()
    {
        var s = await ConnectedReady();

        for (var i = 0; i < 3; i++)
        {
            var beats = s.SentOps(1).Count();
            await AdvanceUntil(() => s.SentOps(1).Count() > beats);
            s.Push("""{"op":11}""");
            await Task.Delay(20);
        }

        Assert.Single(_sockets);
        Assert.Equal(BotState.Connected, _client.State);
    }

    [Fact]
    public async Task Op7_Reconnects_WithResume()
    {
        var s = await ConnectedReady();

        s.Push("""{"op":7,"d":null}""");
        await AdvanceUntil(() => _sockets.Count == 2);
        Socket(1).PushHello();

        await WaitUntil(() => Socket(1).SentOps(6).Any());
    }

    [Fact]
    public async Task Op9False_ReIdentifies()
    {
        var s = await ConnectedReady();

        s.Push("""{"op":9,"d":false}""");
        await AdvanceUntil(() => _sockets.Count == 2);
        var second = Socket(1);
        Assert.Equal("wss://gateway.discord.gg/?v=10&encoding=json", second.ConnectedTo!.ToString());
        second.PushHello();

        await WaitUntil(() => second.SentOps(2).Any());
        Assert.Empty(second.SentOps(6));
    }

    [Fact]
    public async Task Close4004_StopsAndReportsInvalidToken()
    {
        Start();
        await WaitUntil(() => _sockets.Count == 1);

        Socket(0).PushClose(4004);
        await _run!.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(BotState.InvalidToken, _client.State);
        Assert.Single(_sockets);
    }

    [Fact]
    public async Task OtherClose_ReconnectsWithBackoff()
    {
        Start();
        await WaitUntil(() => _sockets.Count == 1);

        Socket(0).PushClose(1006);
        await Task.Delay(50);
        Assert.Single(_sockets);

        await AdvanceUntil(() => _sockets.Count == 2);
        Socket(1).PushClose(1006);
        var before = _time.GetUtcNow();
        await AdvanceUntil(() => _sockets.Count == 3);

        Assert.True(_time.GetUtcNow() - before >= TimeSpan.FromSeconds(2), "second backoff should be at least 2 s");
    }

    [Fact]
    public async Task HandlerThrows_LoopContinues()
    {
        var calls = 0;
        _client.InteractionCreated += _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        };
        var s = await ConnectedReady();

        s.Push("""{"op":0,"s":2,"t":"INTERACTION_CREATE","d":{"id":"a"}}""");
        s.Push("""{"op":0,"s":3,"t":"INTERACTION_CREATE","d":{"id":"b"}}""");

        await WaitUntil(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(BotState.Connected, _client.State);
    }

    [Fact]
    public async Task Cancel_StopsRun_Disconnected()
    {
        await ConnectedReady();

        await _cts.CancelAsync();
        await _run!.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(BotState.Disconnected, _client.State);
    }
}
