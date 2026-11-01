using System.Text;
using Jellyfin.Plugin.WatchParty.Playback;
using Jellyfin.Plugin.WatchParty.Voting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchParty.Commands;

/// <summary>How a front end (Discord, webhook) should present a reply.</summary>
public enum ReplyKind
{
    VoteStarted,
    Done,
    Info,

    /// <summary>Shown only to the requester on Discord.</summary>
    Error,
}

public sealed record RouterReply(ReplyKind Kind, string Message, Vote? Vote = null);

/// <summary>A finished vote and whether its result reached the host screen.</summary>
public sealed record VoteOutcome(VoteResult Result, bool Applied, string Message);

/// <summary>Decides whether a request runs now or goes to a vote, and applies vote results.</summary>
public sealed class CommandRouter
{
    public const string HostOffline = "Host screen offline";
    public const string VoteInProgress = "A vote is already in progress";
    public const string VoteEnded = "This vote has ended";
    public const string OnlyHostCancels = "Only the host can cancel votes";

    private readonly VoteEngine _engine;
    private readonly Ballot _ballot;
    private readonly IHostController _host;
    private readonly ILogger<CommandRouter> _log;

    public CommandRouter(VoteEngine engine, Ballot ballot, IHostController host, ILogger<CommandRouter> log)
    {
        _engine = engine;
        _ballot = ballot;
        _host = host;
        _log = log;
        _engine.Resolved += result => _ = OnResolvedAsync(result);
    }

    public event Func<VoteOutcome, Task>? Completed;

    public Vote? ActiveVote => _engine.Active;

    public static string Verb(VoteAction action) => action.ToString().ToLowerInvariant();

    public async Task<RouterReply> RequestAsync(VoteAction action, string user, bool privileged, string? discordToken, CancellationToken ct)
    {
        if (!_host.GetStatus().Online)
        {
            return new RouterReply(ReplyKind.Error, HostOffline);
        }

        IReadOnlyList<VoteOption> options = VoteOptions.YesNo;
        if (action == VoteAction.Pick)
        {
            var nominees = _ballot.Nominees;
            if (nominees.Count < 2)
            {
                return new RouterReply(ReplyKind.Error, "Nominate at least 2 titles with /nominate before /pick");
            }

            options = nominees.Select(n => new VoteOption(n.ItemId.ToString("N"), n.Title)).ToArray();
        }

        if (privileged && action != VoteAction.Pick)
        {
            var ok = await ApplyAsync(action, ct).ConfigureAwait(false);
            return ok
                ? new RouterReply(ReplyKind.Done, $"{user} forced {Verb(action)}")
                : new RouterReply(ReplyKind.Error, HostOffline);
        }

        var vote = _engine.TryStart(action, options, user, discordToken);
        return vote is null
            ? new RouterReply(ReplyKind.Error, VoteInProgress)
            : new RouterReply(ReplyKind.VoteStarted, $"{user} started a vote to {Verb(action)}", vote);
    }

    public RouterReply Cast(Guid voteId, string user, string optionKey)
    {
        return _engine.Cast(voteId, user, optionKey) switch
        {
            CastStatus.Counted => new RouterReply(ReplyKind.Info, "Vote counted", _engine.Active),
            CastStatus.Changed => new RouterReply(ReplyKind.Info, "Vote changed", _engine.Active),
            CastStatus.UnknownOption => new RouterReply(ReplyKind.Error, "Unknown option"),
            _ => new RouterReply(ReplyKind.Error, VoteEnded),
        };
    }

    public RouterReply Cancel(bool privileged)
    {
        if (!privileged)
        {
            return new RouterReply(ReplyKind.Error, OnlyHostCancels);
        }

        return _engine.Cancel() is null
            ? new RouterReply(ReplyKind.Error, "No vote is running")
            : new RouterReply(ReplyKind.Done, "Vote cancelled");
    }

    public RouterReply Nominate(Guid itemId, string title)
    {
        return _ballot.Add(itemId, title) switch
        {
            AddStatus.Added => new RouterReply(ReplyKind.Info, $"Nominated **{title}** ({_ballot.Nominees.Count}/{Ballot.MaxNominees})"),
            AddStatus.Duplicate => new RouterReply(ReplyKind.Error, $"{title} is already nominated"),
            _ => new RouterReply(ReplyKind.Error, $"The ballot is full ({Ballot.MaxNominees} titles). Run /pick"),
        };
    }

    public RouterReply ShowBallot()
    {
        var nominees = _ballot.Nominees;
        if (nominees.Count == 0)
        {
            return new RouterReply(ReplyKind.Info, "The ballot is empty. Use /nominate to add titles");
        }

        var text = new StringBuilder("**Ballot**");
        for (var i = 0; i < nominees.Count; i++)
        {
            text.Append('\n').Append(i + 1).Append(". ").Append(nominees[i].Title);
        }

        return new RouterReply(ReplyKind.Info, text.ToString());
    }

    public RouterReply NowPlaying()
    {
        var status = _host.GetStatus();
        if (!status.Online)
        {
            return new RouterReply(ReplyKind.Error, HostOffline);
        }

        return new RouterReply(ReplyKind.Info, status.NowPlayingTitle is null ? "Nothing is playing" : $"Now playing: **{status.NowPlayingTitle}**");
    }

    private async Task<bool> ApplyAsync(VoteAction action, CancellationToken ct)
    {
        if (action == VoteAction.Skip && !_host.GetStatus().HasNextInQueue)
        {
            action = VoteAction.Stop;
        }

        return await _host.SendAsync(action, ct).ConfigureAwait(false);
    }

    private async Task OnResolvedAsync(VoteResult result)
    {
        try
        {
            var verb = Verb(result.Vote.Action);
            VoteOutcome outcome;
            if (!result.Passed)
            {
                var message = result.Reason == "cancelled" ? $"Vote to {verb} was cancelled" : $"Vote to {verb} failed: {result.Reason}";
                if (result.Reason == "rejected")
                {
                    message = $"Vote to {verb} rejected";
                }

                outcome = new VoteOutcome(result, false, message);
            }
            else if (result.Vote.Action == VoteAction.Pick)
            {
                var winner = result.Winner!;
                var played = _host.GetStatus().Online
                    && Guid.TryParseExact(winner.Key, "N", out var itemId)
                    && await _host.PlayAsync(itemId, CancellationToken.None).ConfigureAwait(false);
                if (played)
                {
                    _ballot.Clear();
                }

                var why = _host.GetStatus().Online ? $"{winner.Label} could not be played" : HostOffline;
                outcome = played
                    ? new VoteOutcome(result, true, $"Vote to pick passed: now playing **{winner.Label}**")
                    : new VoteOutcome(result, false, $"Vote to pick passed but could not be applied: {why}");
            }
            else
            {
                var applied = _host.GetStatus().Online && await ApplyAsync(result.Vote.Action, CancellationToken.None).ConfigureAwait(false);
                outcome = applied
                    ? new VoteOutcome(result, true, $"Vote to {verb} passed")
                    : new VoteOutcome(result, false, $"Vote to {verb} passed but could not be applied: {HostOffline}");
            }

            if (Completed is { } handlers)
            {
                foreach (var handler in handlers.GetInvocationList().Cast<Func<VoteOutcome, Task>>())
                {
                    try
                    {
                        await handler(outcome).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Watch Party outcome handler failed");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Watch Party failed to apply vote result");
        }
    }
}
