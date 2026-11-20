using Snug.Windows.Core;
using Xunit;

namespace Snug.Windows.Core.Tests;

public sealed class UuStreamLogActivityTrackerTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequiresTwoGrowthsWithinWindow()
    {
        var tracker = new UuStreamLogActivityTracker();
        Assert.False(tracker.Observe(@"D:\UU\streamer_42.slog", 100, T));
        Assert.False(tracker.Observe(@"D:\UU\streamer_42.slog", 1200, T.AddSeconds(1)));
        Assert.True(tracker.Observe(@"D:\UU\streamer_42.slog", 2300, T.AddSeconds(2)));
    }

    [Fact]
    public void ASingleFlushDoesNotBecomeAConnectedSession()
    {
        var tracker = new UuStreamLogActivityTracker();
        Assert.False(tracker.Observe("streamer.slog", 100, T));
        Assert.False(tracker.Observe("streamer.slog", 200, T.AddSeconds(1)));
        Assert.False(tracker.Observe("streamer.slog", 200, T.AddSeconds(4)));
    }

    [Fact]
    public void PeriodicHeartbeatBelowTheFloorNeverCounts()
    {
        // GameViewerServer appends a small heartbeat line every ~3 seconds while idle; two
        // such appends inside the window must not read as session activity.
        var tracker = new UuStreamLogActivityTracker();
        var length = 1000L;
        for (var t = 0; t <= 30; t += 3)
        {
            Assert.False(tracker.Observe("streamer.slog", length, T.AddSeconds(t)));
            length += 300;
        }
    }


    [Fact]
    public void ActivityExpiresWhenLogStopsGrowing()
    {
        var tracker = new UuStreamLogActivityTracker();
        tracker.Observe("streamer.slog", 100, T);
        tracker.Observe("streamer.slog", 1200, T.AddSeconds(1));
        Assert.True(tracker.Observe("streamer.slog", 2300, T.AddSeconds(2)));
        Assert.True(tracker.Observe("streamer.slog", 2300, T.AddSeconds(3.5)));
        Assert.False(tracker.Observe("streamer.slog", 2300, T.AddSeconds(4.1)));
    }

    [Fact]
    public void NewFileOrTruncationRequiresFreshGrowthSequence()
    {
        var tracker = new UuStreamLogActivityTracker();
        tracker.Observe("old.slog", 100, T);
        tracker.Observe("old.slog", 1200, T.AddSeconds(1));
        Assert.True(tracker.Observe("old.slog", 2300, T.AddSeconds(2)));
        Assert.False(tracker.Observe("new.slog", 500, T.AddSeconds(3)));
        Assert.False(tracker.Observe("new.slog", 1600, T.AddSeconds(4)));
        Assert.True(tracker.Observe("new.slog", 2700, T.AddSeconds(5)));
        Assert.False(tracker.Observe("new.slog", 50, T.AddSeconds(6)));
        Assert.False(tracker.Observe("new.slog", 1100, T.AddSeconds(7)));
        Assert.True(tracker.Observe("new.slog", 2200, T.AddSeconds(8)));
    }

    [Fact]
    public void LockedButStaticFileDoesNotBecomeAnActiveStream()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".slog");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = new FileInfo(path).Length;
            var tracker = new UuStreamLogActivityTracker();

            Assert.False(tracker.Observe(path, length, T));
            Assert.False(tracker.Observe(path, length, T.AddSeconds(1)));
            Assert.False(tracker.Observe(path, length, T.AddSeconds(2)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClockRegressionResetsTheGrowthSequence()
    {
        var tracker = new UuStreamLogActivityTracker();
        tracker.Observe("streamer.slog", 100, T);
        tracker.Observe("streamer.slog", 1200, T.AddSeconds(2));
        Assert.False(tracker.Observe("streamer.slog", 2300, T.AddSeconds(1)));
        Assert.False(tracker.Observe("streamer.slog", 3400, T.AddSeconds(2)));
        Assert.True(tracker.Observe("streamer.slog", 4500, T.AddSeconds(3)));
    }
}