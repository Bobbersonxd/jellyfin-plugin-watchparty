using Jellyfin.Plugin.WatchParty.Voting;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class BallotTests
{
    [Fact]
    public void Add_FirstItem_Added()
    {
        var ballot = new Ballot();
        var id = Guid.NewGuid();

        Assert.Equal(AddStatus.Added, ballot.Add(id, "Alien"));
        Assert.Equal(new Nominee(id, "Alien"), Assert.Single(ballot.Nominees));
    }

    [Fact]
    public void Add_SameItemTwice_Duplicate()
    {
        var ballot = new Ballot();
        var id = Guid.NewGuid();
        ballot.Add(id, "Alien");

        Assert.Equal(AddStatus.Duplicate, ballot.Add(id, "Alien"));
        Assert.Single(ballot.Nominees);
    }

    [Fact]
    public void Add_SixthItem_Full()
    {
        var ballot = new Ballot();
        for (var i = 0; i < Ballot.MaxNominees; i++)
        {
            Assert.Equal(AddStatus.Added, ballot.Add(Guid.NewGuid(), $"Movie {i}"));
        }

        Assert.Equal(5, Ballot.MaxNominees);
        Assert.Equal(AddStatus.Full, ballot.Add(Guid.NewGuid(), "One too many"));
        Assert.Equal(5, ballot.Nominees.Count);
    }

    [Fact]
    public void Clear_Empties()
    {
        var ballot = new Ballot();
        ballot.Add(Guid.NewGuid(), "Alien");
        ballot.Clear();

        Assert.Empty(ballot.Nominees);
    }
}
