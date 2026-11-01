using Jellyfin.Plugin.WatchParty.Playback;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class HostPickerTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);

    private static HostCandidate C(string id, string device, bool remote = true, bool playing = false, int minutesAgo = 0) =>
        new(id, device, remote, playing, T0.AddMinutes(-minutesAgo));

    [Fact]
    public void ConfiguredName_WinsOverPlayingSession()
    {
        var picked = HostPicker.Pick([C("1", "Firefox", playing: true), C("2", "Living Room TV")], "living room tv");

        Assert.Equal("2", picked!.SessionId);
    }

    [Fact]
    public void ConfiguredName_NotFound_ReturnsNull()
    {
        Assert.Null(HostPicker.Pick([C("1", "Firefox", playing: true)], "Living Room TV"));
    }

    [Fact]
    public void EmptyName_PrefersPlayingOverIdle()
    {
        var picked = HostPicker.Pick([C("1", "Idle TV", minutesAgo: 0), C("2", "Firefox", playing: true, minutesAgo: 30)], "");

        Assert.Equal("2", picked!.SessionId);
    }

    [Fact]
    public void EmptyName_NothingPlaying_MostRecentlyActive()
    {
        var picked = HostPicker.Pick([C("1", "Old", minutesAgo: 60), C("2", "Recent", minutesAgo: 1)], " ");

        Assert.Equal("2", picked!.SessionId);
    }

    [Fact]
    public void IgnoresSessionsWithoutRemoteControl()
    {
        var picked = HostPicker.Pick([C("1", "Phone app", remote: false, playing: true), C("2", "TV")], "");

        Assert.Equal("2", picked!.SessionId);
        Assert.Null(HostPicker.Pick([C("1", "TV", remote: false)], "TV"));
    }

    [Fact]
    public void NoCandidates_ReturnsNull()
    {
        Assert.Null(HostPicker.Pick([], ""));
    }
}
