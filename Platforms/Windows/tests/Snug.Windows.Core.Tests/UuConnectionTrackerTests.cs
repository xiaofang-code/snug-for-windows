using Snug.Windows.Core;

namespace Snug.Windows.Core.Tests;

public sealed class UuConnectionTrackerTests
{
    [Fact]
    public void AnyPositiveSignalConnectsImmediately()
    {
        var tracker = new UuConnectionTracker();
        var now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");

        var events = tracker.Update(new(false, null, true, LogFallback: true, PeerConnection: true), now);

        Assert.True(tracker.IsConnected);
        Assert.Single(events);
        Assert.Equal(ConnectionTransitionKind.Connected, events[0].Kind);
    }

    [Fact]
    public void SelfInducedDisplayChangeDelaysDisconnect()
    {
        var tracker = new UuConnectionTracker();
        var start = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        tracker.Update(new(true, null, null), start);
        tracker.NoteSelfInducedChange(start.AddSeconds(1));

        Assert.Empty(tracker.Update(new(false, null, false), start.AddSeconds(4)));
        Assert.True(tracker.IsConnected);

        var events = tracker.Update(new(false, null, false), start.AddSeconds(6.1));
        Assert.Single(events);
        Assert.Equal(ConnectionTransitionKind.Interrupted, events[0].Kind);
    }

    [Fact]
    public void ReconnectWithinWindowResumesSameSession()
    {
        var tracker = new UuConnectionTracker();
        var start = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        tracker.Update(new(true, null, null), start);
        tracker.Update(new(false, null, false), start.AddSeconds(2));

        var events = tracker.Update(new(true, new("iPhone", ControllerKind.Phone), null), start.AddSeconds(8));

        Assert.Single(events);
        Assert.Equal(ConnectionTransitionKind.Resumed, events[0].Kind);
        Assert.True(tracker.WantsPhoneScreen(start.AddSeconds(8)));
    }


    [Fact]
    public void SixSecondReconnectBoundaryRetainsPhoneUntilExpiry()
    {
        var tracker = new UuConnectionTracker();
        var t = DateTimeOffset.UnixEpoch;
        var active = new UuSignals(false, new("test", ControllerKind.Phone), true);
        var absent = new UuSignals(false, null, false);
        tracker.Update(active, t);
        Assert.Empty(tracker.Update(absent, t.AddSeconds(1.5)));
        Assert.Equal(ConnectionTransitionKind.Interrupted,
            Assert.Single(tracker.Update(absent, t.AddSeconds(2))).Kind);
        Assert.True(tracker.WantsPhoneScreen(t.AddSeconds(8)));
        Assert.Empty(tracker.Update(absent, t.AddSeconds(8)));
        Assert.Equal(ConnectionTransitionKind.Ended,
            Assert.Single(tracker.Update(absent, t.AddSeconds(8.5))).Kind);
        Assert.False(tracker.WantsPhoneScreen(t.AddSeconds(8.5)));
    }

    [Fact]
    public void ReconnectJustBeforeNewBoundaryResumesSameEpisode()
    {
        var tracker = new UuConnectionTracker();
        var t = DateTimeOffset.UnixEpoch;
        var active = new UuSignals(false, new("test", ControllerKind.Phone), true);
        var absent = new UuSignals(false, null, false);
        tracker.Update(active, t);
        tracker.Update(absent, t.AddSeconds(2));
        Assert.Equal(ConnectionTransitionKind.Resumed,
            Assert.Single(tracker.Update(active, t.AddSeconds(7.9))).Kind);
        Assert.True(tracker.WantsPhoneScreen(t.AddSeconds(7.9)));
    }

    [Fact]
    public void UnknownControllerWithCaptureRebuildAndRepeatedSwitchesKeepsPhone()
    {
        var tracker = new UuConnectionTracker();
        var t = DateTimeOffset.UnixEpoch;
        var active = new UuSignals(false, null, true, LogFallback: true, PeerConnection: true);
        var absent = new UuSignals(false, null, null);
        tracker.Update(active, t);
        for (var i = 0; i < 5; i++)
        {
            var start = t.AddSeconds(3 + 6 * i);
            tracker.Update(active, start);
            Assert.Empty(tracker.Update(absent, start.AddSeconds(1)));
            Assert.Equal(ConnectionTransitionKind.Interrupted,
                Assert.Single(tracker.Update(absent, start.AddSeconds(2))).Kind);
            Assert.True(tracker.WantsPhoneScreen(start.AddSeconds(5)));
            Assert.Equal(ConnectionTransitionKind.Resumed,
                Assert.Single(tracker.Update(active, start.AddSeconds(5))).Kind);
            Assert.True(tracker.WantsPhoneScreen(start.AddSeconds(5)));
        }
    }

    [Fact]
    public void ReconnectAfterSixSecondBoundaryStartsNewEpisode()
    {
        var tracker = new UuConnectionTracker();
        var t = DateTimeOffset.UnixEpoch;
        var active = new UuSignals(false, null, true, LogFallback: true, PeerConnection: true);
        tracker.Update(active, t);
        tracker.Update(new(false, null, null), t.AddSeconds(2));
        var resumed = tracker.Update(active, t.AddSeconds(8.5));
        Assert.Equal(new[] { ConnectionTransitionKind.Ended, ConnectionTransitionKind.Connected },
            resumed.Select(x => x.Kind).ToArray());
        Assert.False(tracker.WantsPhoneScreen(t.AddSeconds(8.5)));
        Assert.True(tracker.WantsPhoneScreen(t.AddSeconds(10.5)));
    }

    [Fact]
    public void RebuildingCaptureAndQuietWindowStillProtectScreenSwitches()
    {
        var tracker = new UuConnectionTracker();
        var t = DateTimeOffset.UnixEpoch;
        var phone = new ControllerInfo("test", ControllerKind.Phone);
        tracker.Update(new(false, phone, true), t);
        tracker.NoteSelfInducedChange(t);
        Assert.Empty(tracker.Update(new(false, null, null), t.AddSeconds(4.9)));
        var rebuilding = UuCaptureLifecycle.Combine(new(false, null, null), new(UuCapturePhase.Rebuilding, 0, true));
        Assert.Empty(tracker.Update(rebuilding, t.AddSeconds(5.1)));
        Assert.True(tracker.IsConnected);
        Assert.True(tracker.WantsPhoneScreen(t.AddSeconds(5.1)));
    }

    [Fact]
    public void ContinuousUnknownSessionCreatesPhoneScreenAfterGrace()
    {
        var tracker = new UuConnectionTracker();
        var start = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var signal = new UuSignals(false, new("UU Remote", ControllerKind.Unknown), true);

        for (var step = 0; step <= 8; step++)
        {
            var now = start.AddMilliseconds(step * 500);
            tracker.Update(signal, now);
            if (step < 4)
                Assert.False(tracker.WantsPhoneScreen(now));
        }

        Assert.True(tracker.IsConnected);
        Assert.True(tracker.WantsPhoneScreen(start.AddSeconds(4)));
    }

    [Fact]
    public void FinishedUnknownSessionStartsNewGracePeriod()
    {
        var tracker = new UuConnectionTracker();
        var start = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var signal = new UuSignals(false, new("UU Remote", ControllerKind.Unknown), true);
        tracker.Update(signal, start);
        Assert.True(tracker.WantsPhoneScreen(start.AddSeconds(3)));

        tracker.Update(new(false, null, false), start.AddSeconds(3));
        tracker.Update(new(false, null, false), start.AddSeconds(20));
        tracker.Update(signal, start.AddSeconds(21));

        Assert.False(tracker.WantsPhoneScreen(start.AddSeconds(21)));
        Assert.True(tracker.WantsPhoneScreen(start.AddSeconds(24)));
    }

    [Fact]
    public void SameNamedControllerChangingBetweenPhoneAndDesktopUpdatesImmediately()
    {
        var tracker = new UuConnectionTracker();
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var name = "UU Remote";

        var connected = tracker.Update(new(false, new(name, ControllerKind.Phone), true), now);
        Assert.Equal(ConnectionTransitionKind.Connected, Assert.Single(connected).Kind);
        Assert.True(tracker.WantsPhoneScreen(now.AddSeconds(3)));

        var desktop = tracker.Update(
            new(false, new(name, ControllerKind.Desktop), true),
            now.AddSeconds(4));
        Assert.Equal(ConnectionTransitionKind.ControllerChanged, Assert.Single(desktop).Kind);
        Assert.Equal(ControllerKind.Desktop, tracker.Controller!.Kind);
        Assert.False(tracker.WantsPhoneScreen(now.AddSeconds(4)));

        var phoneAgain = tracker.Update(
            new(false, new(name, ControllerKind.Phone), true),
            now.AddSeconds(5));
        Assert.Equal(ConnectionTransitionKind.ControllerChanged, Assert.Single(phoneAgain).Kind);
        Assert.True(tracker.WantsPhoneScreen(now.AddSeconds(5)));
    }

    [Fact]
    public void RepeatedIdenticalControllerDoesNotTriggerRedundantTransitions()
    {
        var tracker = new UuConnectionTracker();
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var current = new UuSignals(false, new("UU Remote", ControllerKind.Phone), true);
        tracker.Update(current, now);

        Assert.Empty(tracker.Update(current, now.AddMilliseconds(500)));
    }
    [Fact]
    public void UnknownControllerGetsGracePeriodButDesktopDoesNotCreatePhoneScreen()
    {
        var tracker = new UuConnectionTracker();
        var start = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        tracker.Update(new(true, null, null), start);

        Assert.False(tracker.WantsPhoneScreen(start.AddSeconds(1)));
        Assert.True(tracker.WantsPhoneScreen(start.AddSeconds(3)));

        var desktop = new UuConnectionTracker();
        desktop.Update(new(true, new("Desktop", ControllerKind.Desktop), null), start);
        Assert.False(desktop.WantsPhoneScreen(start.AddSeconds(10)));
    }
}
