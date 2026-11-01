namespace Jellyfin.Plugin.WatchParty.Voting;

/// <summary>What a vote decides.</summary>
public enum VoteAction
{
    Pause,
    Resume,
    Skip,
    Stop,
    Pick,
}

/// <summary>One choice in a vote. Yes/no keys are "yes"/"no"; pick keys are item ids in "N" format.</summary>
public sealed record VoteOption(string Key, string Label);

/// <summary>Timing and quorum settings read when a vote starts and resolves.</summary>
public sealed record VoteSettings(int DurationSeconds, int MinVoters);

/// <summary>Result of casting a vote.</summary>
public enum CastStatus
{
    Counted,
    Changed,
    NoActiveVote,
    UnknownOption,
}

/// <summary>Standard option sets.</summary>
public static class VoteOptions
{
    public static IReadOnlyList<VoteOption> YesNo { get; } = [new("yes", "Yes"), new("no", "No")];
}

/// <summary>An immutable snapshot of a vote and its tally at one moment.</summary>
public sealed class Vote
{
    internal Vote(Guid id, VoteAction action, IReadOnlyList<VoteOption> options, string startedBy, DateTimeOffset endsAt, string? discordToken, IReadOnlyDictionary<string, int> counts)
    {
        Id = id;
        Action = action;
        Options = options;
        StartedBy = startedBy;
        EndsAt = endsAt;
        DiscordToken = discordToken;
        Counts = counts;
    }

    public Guid Id { get; }

    public VoteAction Action { get; }

    public IReadOnlyList<VoteOption> Options { get; }

    public string StartedBy { get; }

    public DateTimeOffset EndsAt { get; }

    public string? DiscordToken { get; }

    /// <summary>Option key to number of votes.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; }
}

/// <summary>How a vote ended.</summary>
public sealed record VoteResult(Vote Vote, bool Passed, VoteOption? Winner, string Reason);
