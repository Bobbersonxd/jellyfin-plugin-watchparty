using System.Collections.Concurrent;
using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Discord;
using Jellyfin.Plugin.WatchParty.Tests.Fakes;
using Jellyfin.Plugin.WatchParty.Voting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public sealed class DiscordBotServiceTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeHost _host = new();
    private readonly FakeHttpHandler _http = new();
    private readonly PluginConfiguration _config = new();
    private readonly ConcurrentQueue<string> _runs = new();
    private readonly CommandRouter _router;
    private readonly DiscordBotService _service;

    public DiscordBotServiceTests()
    {
        var engine = new VoteEngine(_time, new Random(1), () => new VoteSettings(60, 1));
        _router = new CommandRouter(engine, new Ballot(), _host, NullLogger<CommandRouter>.Instance);
        var rest = new DiscordRest(new FakeHttpClientFactory(_http), NullLogger<DiscordRest>.Instance);
        _service = new DiscordBotService(
            async (token, ct) =>
            {
                _runs.Enqueue(token);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                }
            },
            () => "app123",
            rest,
            _router,
            () => _config,
            NullLogger<DiscordBotService>.Instance);
    }

    public async ValueTask DisposeAsync() => await _service.StopAsync(default);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task NoToken_DoesNotStartGateway()
    {
        await _service.StartAsync(default);
        await Task.Delay(50);

        Assert.Empty(_runs);
        Assert.Equal(BotState.Disconnected, _service.State);
    }

    [Fact]
    public async Task Token_StartsGateway()
    {
        _config.DiscordBotToken = "t1";

        await _service.StartAsync(default);

        await WaitUntil(() => _runs.Count == 1);
        Assert.Equal("t1", _runs.Single());
    }

    [Fact]
    public async Task TokenChange_RestartsGateway()
    {
        _config.DiscordBotToken = "t1";
        await _service.StartAsync(default);
        await WaitUntil(() => _runs.Count == 1);

        _config.DiscordBotToken = "t2";
        _service.OnConfigurationChanged();

        await WaitUntil(() => _runs.Count == 2);
        Assert.Equal(["t1", "t2"], _runs);
    }

    [Fact]
    public async Task SameToken_ConfigSave_DoesNotRestart()
    {
        _config.DiscordBotToken = "t1";
        await _service.StartAsync(default);
        await WaitUntil(() => _runs.Count == 1);

        _config.VoteDurationSeconds = 30;
        _service.OnConfigurationChanged();
        await Task.Delay(50);

        Assert.Single(_runs);
    }

    [Fact]
    public async Task Completed_WithToken_EditsOriginal()
    {
        await _service.StartAsync(default);
        await _router.RequestAsync(VoteAction.Pause, "<@1>", false, "itok", default);

        _time.Advance(TimeSpan.FromSeconds(60));

        await WaitUntil(() => _http.Requests.Count >= 1);
        var (request, body) = _http.Requests[0];
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.EndsWith("/webhooks/app123/itok/messages/@original", request.RequestUri!.ToString());
        Assert.Contains("passed", body);
    }

    [Fact]
    public async Task Completed_WithChannelWebhook_PostsResult()
    {
        _config.ChannelWebhookUrl = "https://discord.com/api/webhooks/1/abc";
        await _service.StartAsync(default);
        await _router.RequestAsync(VoteAction.Pause, "<@1>", false, null, default);

        _time.Advance(TimeSpan.FromSeconds(60));

        await WaitUntil(() => _http.Requests.Count >= 1);
        Assert.Equal("https://discord.com/api/webhooks/1/abc", _http.Requests[0].Request.RequestUri!.ToString());
    }
}
