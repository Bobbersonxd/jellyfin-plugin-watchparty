using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchParty.Discord;

/// <summary>What the settings page shows about the bot.</summary>
public interface IBotStatus
{
    BotState State { get; }

    string? BotUserName { get; }

    string? ApplicationId { get; }
}

/// <summary>
/// Runs the Discord bot inside Jellyfin: starts the gateway when a token is configured, restarts it when the
/// token changes, answers interactions and edits vote messages when votes end.
/// </summary>
public sealed class DiscordBotService : IHostedService, IBotStatus, IDisposable
{
    private readonly Func<string, CancellationToken, Task> _runner;
    private readonly Func<string?> _applicationId;
    private readonly DiscordRest _rest;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<DiscordBotService> _log;
    private readonly GatewayClient? _gateway;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, (string Token, JsonObject Message)> _finished = new();
    private readonly ConcurrentQueue<Guid> _finishedOrder = new();

    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;
    private string _token = string.Empty;
    private bool _subscribed;

    public DiscordBotService(GatewayClient gateway, InteractionHandler handler, DiscordRest rest, CommandRouter router, Func<PluginConfiguration> config, ILogger<DiscordBotService> log)
        : this(gateway.RunAsync, () => gateway.ApplicationId, rest, router, config, log)
    {
        _gateway = gateway;
        gateway.InteractionCreated += interaction => HandleInteractionAsync(handler, interaction);
        gateway.Ready += OnReadyAsync;
    }

    internal DiscordBotService(Func<string, CancellationToken, Task> runner, Func<string?> applicationId, DiscordRest rest, CommandRouter router, Func<PluginConfiguration> config, ILogger<DiscordBotService> log)
    {
        _runner = runner;
        _applicationId = applicationId;
        _rest = rest;
        _config = config;
        _log = log;
        router.Completed += OnVoteCompletedAsync;
    }

    public BotState State
    {
        get
        {
            lock (_gate)
            {
                if (_token.Length == 0)
                {
                    return BotState.Disconnected;
                }
            }

            return _gateway?.State ?? BotState.Connecting;
        }
    }

    public string? BotUserName => _gateway?.BotUserName;

    public string? ApplicationId => _applicationId();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_subscribed && Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += (_, _) => OnConfigurationChanged();
            _subscribed = true;
        }

        Restart(_config().DiscordBotToken.Trim());
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task run;
        lock (_gate)
        {
            _cts?.Cancel();
            run = _run;
        }

        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>Called when the plugin settings are saved.</summary>
    public void OnConfigurationChanged()
    {
        var token = _config().DiscordBotToken.Trim();
        lock (_gate)
        {
            if (token == _token)
            {
                return;
            }
        }

        _log.LogInformation("Watch Party bot token changed; restarting the Discord connection");
        Restart(token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Restart(string token)
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _token = token;
            if (token.Length == 0)
            {
                _run = Task.CompletedTask;
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            var previous = _run;
            _run = Task.Run(async () =>
            {
                await Task.WhenAny(previous, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
                try
                {
                    await _runner(token, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Watch Party Discord connection stopped unexpectedly");
                }
            });
        }
    }

    internal async Task HandleInteractionAsync(InteractionHandler handler, JsonElement interaction)
    {
        var id = interaction.GetProperty("id").GetString()!;
        var token = interaction.GetProperty("token").GetString()!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        var response = await handler.HandleAsync(interaction, timeout.Token).ConfigureAwait(false);
        await _rest.RespondAsync(id, token, response, CancellationToken.None).ConfigureAwait(false);

        // A button reply (type 7) races the end-of-vote edit. If the vote finished while the reply was
        // in flight, the reply may have landed last and restored the live buttons, so send the result again.
        if (response["type"]?.GetValue<int>() == 7
            && interaction.GetProperty("data").TryGetProperty("custom_id", out var customId)
            && InteractionHandler.TryParseCustomId(customId.GetString() ?? string.Empty, out var voteId, out _)
            && _finished.TryGetValue(voteId, out var final)
            && _applicationId() is { } appId)
        {
            await _rest.EditOriginalAsync(appId, final.Token, final.Message, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task OnReadyAsync()
    {
        string token;
        lock (_gate)
        {
            token = _token;
        }

        if (_applicationId() is { } appId && token.Length > 0
            && await _rest.RegisterCommandsAsync(appId, token, CommandDefinitions.All, CancellationToken.None).ConfigureAwait(false))
        {
            _log.LogInformation("Watch Party registered its Discord slash commands");
        }
    }

    private async Task OnVoteCompletedAsync(VoteOutcome outcome)
    {
        if (outcome.Result.Vote.DiscordToken is { } interactionToken && _applicationId() is { } appId)
        {
            var message = InteractionHandler.OutcomeMessage(outcome);
            _finished[outcome.Result.Vote.Id] = (interactionToken, message);
            _finishedOrder.Enqueue(outcome.Result.Vote.Id);
            while (_finishedOrder.Count > 20 && _finishedOrder.TryDequeue(out var oldest))
            {
                _finished.TryRemove(oldest, out _);
            }

            await _rest.EditOriginalAsync(appId, interactionToken, message, CancellationToken.None).ConfigureAwait(false);
        }

        var webhook = _config().ChannelWebhookUrl.Trim();
        if (webhook.Length > 0)
        {
            await _rest.PostChannelWebhookAsync(webhook, outcome.Message, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
