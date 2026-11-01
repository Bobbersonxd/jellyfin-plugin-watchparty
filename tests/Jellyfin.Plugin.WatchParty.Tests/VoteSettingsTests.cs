using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Voting;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class VoteSettingsTests
{
    [Theory]
    [InlineData(60, 1, 60, 1)]
    [InlineData(1200, 0, 600, 1)]
    [InlineData(0, -5, 5, 1)]
    public void FromConfig_ClampsToSafeRange(int duration, int minVoters, int expectedDuration, int expectedMin)
    {
        var settings = VoteSettings.FromConfig(new PluginConfiguration { VoteDurationSeconds = duration, MinVoters = minVoters });

        Assert.Equal(new VoteSettings(expectedDuration, expectedMin), settings);
    }
}
