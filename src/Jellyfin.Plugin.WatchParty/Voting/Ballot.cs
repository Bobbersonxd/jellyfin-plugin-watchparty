namespace Jellyfin.Plugin.WatchParty.Voting;

/// <summary>A title nominated for the next pick vote.</summary>
public sealed record Nominee(Guid ItemId, string Title);

/// <summary>Result of nominating a title.</summary>
public enum AddStatus
{
    Added,
    Duplicate,
    Full,
}

/// <summary>The list of nominees for the next pick vote. Thread-safe.</summary>
public sealed class Ballot
{
    /// <summary>Discord allows five buttons in one row.</summary>
    public const int MaxNominees = 5;

    private readonly object _gate = new();
    private readonly List<Nominee> _nominees = [];

    public IReadOnlyList<Nominee> Nominees
    {
        get
        {
            lock (_gate)
            {
                return _nominees.ToArray();
            }
        }
    }

    public AddStatus Add(Guid itemId, string title)
    {
        lock (_gate)
        {
            if (_nominees.Any(n => n.ItemId == itemId))
            {
                return AddStatus.Duplicate;
            }

            if (_nominees.Count >= MaxNominees)
            {
                return AddStatus.Full;
            }

            _nominees.Add(new Nominee(itemId, title));
            return AddStatus.Added;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _nominees.Clear();
        }
    }
}
