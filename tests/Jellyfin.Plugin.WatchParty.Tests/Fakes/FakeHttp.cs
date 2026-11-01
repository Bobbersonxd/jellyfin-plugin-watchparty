using System.Net;

namespace Jellyfin.Plugin.WatchParty.Tests.Fakes;

public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();

    public Queue<HttpResponseMessage> Responses { get; } = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    /// <summary>Runs before a request is recorded, i.e. while it is still "in flight".</summary>
    public Func<HttpRequestMessage, Task>? BeforeRecord { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        if (BeforeRecord is { } hook)
        {
            await hook(request);
        }

        lock (_gate)
        {
            Requests.Add((request, body));
            return Responses.Count > 0 ? Responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
