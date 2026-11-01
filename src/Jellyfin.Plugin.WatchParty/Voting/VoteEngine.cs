namespace Jellyfin.Plugin.WatchParty.Voting;

/// <summary>
/// Runs one timed vote at a time. Thread-safe: every state change happens under one lock,
/// and <see cref="Resolved"/> is raised outside the lock after the vote is cleared.
/// </summary>
public sealed class VoteEngine
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly Func<VoteSettings> _settings;
    private ActiveVote? _active;

    public VoteEngine(TimeProvider time, Random random, Func<VoteSettings> settings)
    {
        _time = time;
        _random = random;
        _settings = settings;
    }

    public event Action<VoteResult>? Resolved;

    public Vote? Active
    {
        get
        {
            lock (_gate)
            {
                return _active?.Snapshot();
            }
        }
    }

    public Vote? TryStart(VoteAction action, IReadOnlyList<VoteOption> options, string startedBy, string? discordToken)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                return null;
            }

            var duration = TimeSpan.FromSeconds(Math.Max(1, _settings().DurationSeconds));
            var active = new ActiveVote(Guid.NewGuid(), action, options.ToArray(), startedBy, _time.GetUtcNow() + duration, discordToken);
            if (action != VoteAction.Pick)
            {
                active.Ballots[startedBy] = "yes";
            }

            _active = active;
            active.Timer = _time.CreateTimer(_ => OnTimer(active), null, duration, Timeout.InfiniteTimeSpan);
            return active.Snapshot();
        }
    }

    public CastStatus Cast(Guid voteId, string user, string optionKey)
    {
        lock (_gate)
        {
            if (_active is null || _active.Id != voteId)
            {
                return CastStatus.NoActiveVote;
            }

            if (!_active.Options.Any(o => o.Key == optionKey))
            {
                return CastStatus.UnknownOption;
            }

            var changed = _active.Ballots.ContainsKey(user);
            _active.Ballots[user] = optionKey;
            return changed ? CastStatus.Changed : CastStatus.Counted;
        }
    }

    public VoteResult? Cancel()
    {
        VoteResult result;
        lock (_gate)
        {
            if (_active is null)
            {
                return null;
            }

            result = new VoteResult(_active.Snapshot(), false, null, "cancelled");
            Clear();
        }

        Resolved?.Invoke(result);
        return result;
    }

    private void OnTimer(ActiveVote vote)
    {
        VoteResult result;
        lock (_gate)
        {
            if (!ReferenceEquals(_active, vote))
            {
                return;
            }

            result = Decide(vote, _settings().MinVoters);
            Clear();
        }

        Resolved?.Invoke(result);
    }

    private void Clear()
    {
        _active?.Timer?.Dispose();
        _active = null;
    }

    private VoteResult Decide(ActiveVote vote, int minVoters)
    {
        var snapshot = vote.Snapshot();
        var counts = snapshot.Counts;
        var total = vote.Ballots.Count;

        if (vote.Action == VoteAction.Pick)
        {
            if (total == 0)
            {
                return new VoteResult(snapshot, false, null, "no votes");
            }

            if (total < minVoters)
            {
                return new VoteResult(snapshot, false, null, "not enough voters");
            }

            var best = counts.Values.Max();
            var tied = vote.Options.Where(o => counts[o.Key] == best).ToArray();
            var winner = tied.Length == 1 ? tied[0] : tied[_random.Next(tied.Length)];
            return new VoteResult(snapshot, true, winner, "passed");
        }

        if (total < minVoters)
        {
            return new VoteResult(snapshot, false, null, "not enough voters");
        }

        return counts["yes"] > counts["no"]
            ? new VoteResult(snapshot, true, vote.Options.First(o => o.Key == "yes"), "passed")
            : new VoteResult(snapshot, false, null, "rejected");
    }

    private sealed class ActiveVote
    {
        public ActiveVote(Guid id, VoteAction action, VoteOption[] options, string startedBy, DateTimeOffset endsAt, string? discordToken)
        {
            Id = id;
            Action = action;
            Options = options;
            StartedBy = startedBy;
            EndsAt = endsAt;
            DiscordToken = discordToken;
        }

        public Guid Id { get; }

        public VoteAction Action { get; }

        public VoteOption[] Options { get; }

        public string StartedBy { get; }

        public DateTimeOffset EndsAt { get; }

        public string? DiscordToken { get; }

        public Dictionary<string, string> Ballots { get; } = new(StringComparer.Ordinal);

        public ITimer? Timer { get; set; }

        public Vote Snapshot()
        {
            var counts = Options.ToDictionary(o => o.Key, _ => 0, StringComparer.Ordinal);
            foreach (var key in Ballots.Values)
            {
                counts[key]++;
            }

            return new Vote(Id, Action, Options, StartedBy, EndsAt, DiscordToken, counts);
        }
    }
}
