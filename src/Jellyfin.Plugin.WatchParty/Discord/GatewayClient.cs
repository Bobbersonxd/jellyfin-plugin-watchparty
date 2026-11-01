using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchParty.Discord;

/// <summary>Bot connection state shown on the settings page.</summary>
public enum BotState
{
    Disconnected,
    Connecting,
    Connected,
    InvalidToken,
}

/// <summary>
/// A minimal outbound Discord Gateway (v10) client that only listens for interactions.
/// Handles heartbeats, identify, resume, reconnect requests and backoff.
/// </summary>
public sealed class GatewayClient
{
    public const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly Func<IGatewaySocket> _socketFactory;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly ILogger<GatewayClient> _log;
    private readonly object _randomLock = new();

    private volatile BotState _state = BotState.Disconnected;
    private string? _sessionId;
    private string? _resumeUrl;
    private long _seq = -1;

    public GatewayClient(Func<IGatewaySocket> socketFactory, TimeProvider time, Random random, ILogger<GatewayClient> log)
    {
        _socketFactory = socketFactory;
        _time = time;
        _random = random;
        _log = log;
    }

    public event Func<JsonElement, Task>? InteractionCreated;

    public event Func<Task>? Ready;

    private enum Next
    {
        Stop,
        ReconnectNow,
        ReconnectAfterBackoff,
        ReconnectAfterRandomWait,
    }

    public BotState State => _state;

    public string? BotUserName { get; private set; }

    public string? ApplicationId { get; private set; }

    /// <summary>Runs until <paramref name="ct"/> is cancelled, the token is rejected, or Discord closes fatally.</summary>
    public async Task RunAsync(string token, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _state = BotState.Connecting;
                var (next, readied) = await RunSessionAsync(token, ct).ConfigureAwait(false);
                if (readied)
                {
                    backoff = TimeSpan.FromSeconds(1);
                }

                switch (next)
                {
                    case Next.Stop:
                        return;
                    case Next.ReconnectNow:
                        break;
                    case Next.ReconnectAfterRandomWait:
                        await Task.Delay(TimeSpan.FromSeconds(1 + (NextDouble() * 4)), _time, ct).ConfigureAwait(false);
                        break;
                    default:
                        _state = BotState.Connecting;
                        await Task.Delay(backoff, _time, ct).ConfigureAwait(false);
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (_state != BotState.InvalidToken)
            {
                _state = BotState.Disconnected;
            }
        }
    }

    private async Task<(Next Next, bool Readied)> RunSessionAsync(string token, CancellationToken ct)
    {
        var session = new SessionFlags();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task heartbeat = Task.CompletedTask;
        var socket = _socketFactory();
        var resuming = _sessionId is not null && _resumeUrl is not null;
        var uri = resuming ? new Uri(_resumeUrl!.TrimEnd('/') + "/?v=10&encoding=json") : new Uri(GatewayUrl);

        try
        {
            await socket.ConnectAsync(uri, sessionCts.Token).ConfigureAwait(false);
            while (true)
            {
                var frame = await socket.ReceiveAsync(sessionCts.Token).ConfigureAwait(false);
                if (frame is GatewayFrame.Closed closed)
                {
                    return (OnClose(closed.Code), session.Readied);
                }

                using var doc = JsonDocument.Parse(((GatewayFrame.Text)frame).Json);
                var root = doc.RootElement;
                var op = root.GetProperty("op").GetInt32();
                var d = root.TryGetProperty("d", out var data) ? data : default;

                switch (op)
                {
                    case 10:
                        var interval = TimeSpan.FromMilliseconds(d.GetProperty("heartbeat_interval").GetDouble());
                        heartbeat = HeartbeatLoopAsync(socket, interval, session, sessionCts);
                        await socket.SendAsync(resuming ? ResumePayload(token) : IdentifyPayload(token), sessionCts.Token).ConfigureAwait(false);
                        break;
                    case 11:
                        session.Acked = true;
                        break;
                    case 1:
                        await SendHeartbeatAsync(socket, sessionCts.Token).ConfigureAwait(false);
                        break;
                    case 0:
                        if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number)
                        {
                            Interlocked.Exchange(ref _seq, s.GetInt64());
                        }

                        OnDispatch(root.GetProperty("t").GetString(), d, session);
                        break;
                    case 7:
                        _log.LogInformation("Discord asked Watch Party to reconnect");
                        await TryCloseAsync(socket, 4000).ConfigureAwait(false);
                        return (Next.ReconnectNow, session.Readied);
                    case 9:
                        await TryCloseAsync(socket, 4000).ConfigureAwait(false);
                        if (d.ValueKind == JsonValueKind.True)
                        {
                            return (Next.ReconnectAfterBackoff, session.Readied);
                        }

                        ClearSession();
                        return (Next.ReconnectAfterRandomWait, session.Readied);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && session.Zombie)
        {
            _log.LogWarning("Discord heartbeat not acknowledged; reconnecting");
            await TryCloseAsync(socket, 4000).ConfigureAwait(false);
            return (Next.ReconnectAfterBackoff, session.Readied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Discord gateway connection lost; reconnecting");
            return (Next.ReconnectAfterBackoff, session.Readied);
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Next OnClose(int? code)
    {
        switch (code)
        {
            case 4004:
                _log.LogError("Discord rejected the Watch Party bot token (4004)");
                _state = BotState.InvalidToken;
                return Next.Stop;
            case >= 4010 and <= 4014:
                _log.LogError("Discord closed the Watch Party gateway with fatal code {Code}", code);
                _state = BotState.Disconnected;
                return Next.Stop;
            case 4007 or 4009:
                ClearSession();
                return Next.ReconnectAfterBackoff;
            default:
                _log.LogInformation("Discord gateway closed ({Code}); reconnecting", code);
                return Next.ReconnectAfterBackoff;
        }
    }

    private void OnDispatch(string? type, JsonElement d, SessionFlags session)
    {
        switch (type)
        {
            case "READY":
                _sessionId = d.GetProperty("session_id").GetString();
                _resumeUrl = d.GetProperty("resume_gateway_url").GetString();
                BotUserName = d.GetProperty("user").GetProperty("username").GetString();
                ApplicationId = d.GetProperty("application").GetProperty("id").GetString();
                session.Readied = true;
                _state = BotState.Connected;
                _log.LogInformation("Watch Party connected to Discord as {Bot}", BotUserName);
                Raise(Ready, h => h());
                break;
            case "RESUMED":
                session.Readied = true;
                _state = BotState.Connected;
                break;
            case "INTERACTION_CREATE":
                var interaction = d.Clone();
                Raise(InteractionCreated, h => h(interaction));
                break;
        }
    }

    private void Raise<T>(T? handlers, Func<T, Task> invoke)
        where T : Delegate
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<T>())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await invoke(handler).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Watch Party gateway event handler failed");
                }
            });
        }
    }

    private async Task HeartbeatLoopAsync(IGatewaySocket socket, TimeSpan interval, SessionFlags session, CancellationTokenSource sessionCts)
    {
        var token = sessionCts.Token;
        try
        {
            await Task.Delay(interval * NextDouble(), _time, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                if (!session.Acked)
                {
                    session.Zombie = true;
                    await sessionCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                session.Acked = false;
                await SendHeartbeatAsync(socket, token).ConfigureAwait(false);
                await Task.Delay(interval, _time, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discord heartbeat failed");
            session.Zombie = true;
            await sessionCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private Task SendHeartbeatAsync(IGatewaySocket socket, CancellationToken ct)
    {
        var seq = Interlocked.Read(ref _seq);
        return socket.SendAsync(seq < 0 ? """{"op":1,"d":null}""" : $$"""{"op":1,"d":{{seq}}}""", ct);
    }

    private static string IdentifyPayload(string token) => new JsonObject
    {
        ["op"] = 2,
        ["d"] = new JsonObject
        {
            ["token"] = token,
            ["intents"] = 0,
            ["properties"] = new JsonObject
            {
                ["os"] = RuntimeInformation.OSDescription,
                ["browser"] = "jellyfin-watchparty",
                ["device"] = "jellyfin-watchparty",
            },
        },
    }.ToJsonString();

    private string ResumePayload(string token) => new JsonObject
    {
        ["op"] = 6,
        ["d"] = new JsonObject
        {
            ["token"] = token,
            ["session_id"] = _sessionId,
            ["seq"] = Interlocked.Read(ref _seq) is var seq && seq >= 0 ? seq : null,
        },
    }.ToJsonString();

    private void ClearSession()
    {
        _sessionId = null;
        _resumeUrl = null;
        Interlocked.Exchange(ref _seq, -1);
    }

    private double NextDouble()
    {
        lock (_randomLock)
        {
            return _random.NextDouble();
        }
    }

    private async Task TryCloseAsync(IGatewaySocket socket, int code)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(code, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Closing the Discord socket failed");
        }
    }

    private sealed class SessionFlags
    {
        private volatile bool _acked = true;
        private volatile bool _zombie;
        private volatile bool _readied;

        public bool Acked { get => _acked; set => _acked = value; }

        public bool Zombie { get => _zombie; set => _zombie = value; }

        public bool Readied { get => _readied; set => _readied = value; }
    }
}
