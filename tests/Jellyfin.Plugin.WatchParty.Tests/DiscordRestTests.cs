using System.Net;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.WatchParty.Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class DiscordRestTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return Responses.Count > 0 ? Responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly Handler _handler = new();
    private readonly DiscordRest _rest;

    public DiscordRestTests()
    {
        _rest = new DiscordRest(new Factory(_handler), NullLogger<DiscordRest>.Instance);
    }

    [Fact]
    public async Task Respond_PostsToCallbackUrl()
    {
        Assert.True(await _rest.RespondAsync("int1", "itok", new JsonObject { ["type"] = 1 }, default));

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://discord.com/api/v10/interactions/int1/itok/callback", request.RequestUri!.ToString());
        Assert.Equal("""{"type":1}""", body);
    }

    [Fact]
    public async Task EditOriginal_PatchesWebhookMessage()
    {
        await _rest.EditOriginalAsync("app", "itok", new JsonObject { ["content"] = "x" }, default);

        var request = Assert.Single(_handler.Requests).Request;
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("https://discord.com/api/v10/webhooks/app/itok/messages/@original", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Register_UsesBotAuthHeader_AndPut()
    {
        Assert.True(await _rest.RegisterCommandsAsync("app", "bottok", CommandDefinitions.All, default));

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://discord.com/api/v10/applications/app/commands", request.RequestUri!.ToString());
        Assert.Equal("Bot bottok", request.Headers.Authorization!.ToString());
        Assert.Contains("\"nominate\"", body);
    }

    [Fact]
    public async Task Rest_429_RetriesOnceAfterRetryAfter()
    {
        _handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"retry_after":0.05}""") });
        _handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.True(await _rest.RespondAsync("int1", "itok", new JsonObject { ["type"] = 1 }, default));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Rest_500_ReturnsFalse()
    {
        _handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.False(await _rest.PostChannelWebhookAsync("https://discord.com/api/webhooks/1/abc", "hi", default));
    }

    [Fact]
    public void CommandDefinitions_HaveAllNineCommands()
    {
        var names = CommandDefinitions.All.Select(c => c!["name"]!.GetValue<string>()).ToArray();

        Assert.Equal(["pause", "resume", "skip", "stop", "nominate", "ballot", "pick", "cancelvote", "nowplaying"], names);
        var nominate = CommandDefinitions.All.Single(c => c!["name"]!.GetValue<string>() == "nominate")!;
        Assert.True(nominate["options"]![0]!["autocomplete"]!.GetValue<bool>());
    }
}
