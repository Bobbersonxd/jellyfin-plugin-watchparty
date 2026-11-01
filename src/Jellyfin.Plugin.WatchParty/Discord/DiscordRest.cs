using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchParty.Discord;

/// <summary>The few Discord REST calls Watch Party makes. Failures are logged and reported as false.</summary>
public sealed class DiscordRest
{
    public const string ApiBase = "https://discord.com/api/v10";
    public const string HttpClientName = "WatchPartyDiscord";

    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<DiscordRest> _log;

    public DiscordRest(IHttpClientFactory http, ILogger<DiscordRest> log)
    {
        _http = http;
        _log = log;
    }

    public Task<bool> RespondAsync(string interactionId, string token, JsonObject response, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{ApiBase}/interactions/{interactionId}/{token}/callback", response, null, ct);

    public Task<bool> EditOriginalAsync(string appId, string token, JsonObject message, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{ApiBase}/webhooks/{appId}/{token}/messages/@original", message, null, ct);

    public Task<bool> RegisterCommandsAsync(string appId, string botToken, JsonArray commands, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{ApiBase}/applications/{appId}/commands", commands, botToken, ct);

    public Task<bool> PostChannelWebhookAsync(string url, string content, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, url, new JsonObject { ["content"] = content, ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() } }, null, ct);

    private async Task<bool> SendAsync(HttpMethod method, string url, JsonNode body, string? botToken, CancellationToken ct)
    {
        var json = body.ToJsonString();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var client = _http.CreateClient(HttpClientName);
                using var request = new HttpRequestMessage(method, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.UserAgent.ParseAdd("DiscordBot (https://github.com/Bobbersonxd/jellyfin-plugin-watchparty, 1.0)");
                if (botToken is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
                }

                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    await Task.Delay(RetryAfter(text), ct).ConfigureAwait(false);
                    continue;
                }

                _log.LogWarning("Discord {Method} {Path} failed: {Status} {Body}", method, Redact(url), (int)response.StatusCode, text);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Discord {Method} {Path} failed", method, Redact(url));
                return false;
            }
        }

        return false;
    }

    private static TimeSpan RetryAfter(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("retry_after", out var value) && value.TryGetDouble(out var seconds))
            {
                var wait = TimeSpan.FromSeconds(Math.Max(0, seconds));
                return wait < MaxRetryWait ? wait : MaxRetryWait;
            }
        }
        catch (JsonException)
        {
        }

        return TimeSpan.FromSeconds(1);
    }

    /// <summary>Interaction and webhook tokens are secrets; replace every long path segment so none reach the logs.</summary>
    internal static string Redact(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "/";
        return string.Join('/', path.Split('/').Select(segment => segment.Length > 20 ? "…" : segment));
    }
}
