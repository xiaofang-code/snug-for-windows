using Snug.Windows.Core;

namespace Snug.Windows.Core.Tests;

public sealed class UuConnectionTrackerEvidenceTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;
    private static readonly UuSignals Active = new(false, new("test", ControllerKind.Phone), true);
    private static readonly UuSignals Unknown = new(false, null, null);
    private static readonly UuSignals Verified = new(false, null, false, CaptureVerifiedInactive: true);

    private static UuConnectionTracker Interrupt(UuSignals negative)
    {
        var tracker = new UuConnectionTracker();
        tracker.Update(Active, T);
        Assert.Equal(ConnectionTransitionKind.Interrupted,
            Assert.Single(tracker.Update(negative, T.AddSeconds(2))).Kind);
        return tracker;
    }

    [Fact]
    public void CompleteInactiveInventoryEndsAfterTwoSeconds()
    {
        var tracker = Interrupt(Verified);
        for (var t = 2.5; t <= 4; t += .5)
        {
            Assert.Empty(tracker.Update(Verified, T.AddSeconds(t)));
            Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(t)));
        }
        Assert.Equal(ConnectionTransitionKind.Ended,
            Assert.Single(tracker.Update(Verified, T.AddSeconds(4.5))).Kind);
        Assert.False(tracker.WantsPhoneScreen(T.AddSeconds(4.5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnverifiedOrLostInventoryRetainsSixSeconds(bool previouslyVerified)
    {
        var tracker = Interrupt(previouslyVerified ? Verified : Unknown);
        tracker.Update(Unknown, T.AddSeconds(4));
        Assert.Empty(tracker.Update(Unknown, T.AddSeconds(6.5)));
        Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(8)));
        Assert.Equal(ConnectionTransitionKind.Ended,
            Assert.Single(tracker.Update(Unknown, T.AddSeconds(8.5))).Kind);
    }

    [Fact]
    public void LateVerifiedEvidenceCannotBackdateEarlyDeadline()
    {
        var tracker = Interrupt(Unknown);
        tracker.Update(Verified, T.AddSeconds(5));
        Assert.Empty(tracker.Update(Verified, T.AddSeconds(6.5)));
        Assert.Empty(tracker.Update(Verified, T.AddSeconds(7)));
        Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(7)));
        Assert.Equal(ConnectionTransitionKind.Ended,
            Assert.Single(tracker.Update(Verified, T.AddSeconds(7.5))).Kind);
    }

    [Fact]
    public void ReturningCaptureWinsOverEarlyExpiry()
    {
        var tracker = Interrupt(Verified);
        tracker.Update(Verified, T.AddSeconds(3.5));
        Assert.Equal(ConnectionTransitionKind.Resumed,
            Assert.Single(tracker.Update(Active, T.AddSeconds(4.5))).Kind);
        Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(4.5)));
    }

    [Fact]
    public void VerifiedFastPathRetainsCaptureRebuildAndDisconnectDebounce()
    {
        var tracker = new UuConnectionTracker();
        var capture = new UuCaptureLifecycle();
        var server = new UuServerGeneration(1, 1);
        capture.Begin(server, true);
        capture.Observe(server, 1, true, T);
        UuSignals Sample(double seconds) =>
            UuCaptureLifecycle.Combine(Unknown, capture.Snapshot(T.AddSeconds(seconds)));
        tracker.Update(Active, T); // capture alone cannot seed the episode; a real signal must
        capture.Observe(server, 1, false, T.AddSeconds(1));
        for (double time = 1; time < 4; time += .5)
        {
            Assert.False(Sample(time).IsPresent); // capture alone is not session presence
            Assert.True(Sample(time).CaptureActive); // but it keeps the open episode alive
            Assert.Empty(tracker.Update(Sample(time), T.AddSeconds(time)));
        }
        Assert.True(Sample(4).CaptureVerifiedInactive);
        Assert.Empty(tracker.Update(Sample(4), T.AddSeconds(4)));
        Assert.Empty(tracker.Update(Sample(5), T.AddSeconds(5)));
        Assert.Equal(ConnectionTransitionKind.Interrupted,
            Assert.Single(tracker.Update(Sample(5.5), T.AddSeconds(5.5))).Kind);
        Assert.Empty(tracker.Update(Sample(6), T.AddSeconds(6)));
        Assert.Equal(ConnectionTransitionKind.Ended,
            Assert.Single(tracker.Update(Sample(6.5), T.AddSeconds(6.5))).Kind);
    }

    [Fact]
    public void ConfirmedActiveCanAdvanceUnknownDeviceGraceWithoutRebuildingFalsePositive()
    {
        var tracker = new UuConnectionTracker();
        var verified = UuCaptureLifecycle.Combine(Unknown, new(UuCapturePhase.Active, 1, true)) with { OverlayVisible = true };
        tracker.Update(verified, T);
        Assert.False(tracker.WantsPhoneScreen(T.AddSeconds(.5)));
        tracker.Update(verified, T.AddSeconds(1));
        Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(1)));
        tracker.Update(UuCaptureLifecycle.Combine(Unknown, new(UuCapturePhase.Rebuilding, 0, true)) with { OverlayVisible = true }, T.AddSeconds(1.5));
        Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(1.5)));
        tracker.Update(verified with { Controller = new("desktop", ControllerKind.Desktop) }, T.AddSeconds(1.6));
        Assert.False(tracker.WantsPhoneScreen(T.AddSeconds(1.6)));
    }

    [Theory]
    [InlineData(UuCapturePhase.Active, 1, false)]
    [InlineData(UuCapturePhase.Rebuilding, 0, true)]
    public void IncompleteOrRebuildingCaptureRetainsTwoSecondUnknownGrace(UuCapturePhase phase, int count, bool complete)
    {
        var tracker = new UuConnectionTracker();
        var signal = UuCaptureLifecycle.Combine(Unknown, new(phase, count, complete)) with { OverlayVisible = true };
        tracker.Update(signal, T);
        tracker.Update(signal, T.AddSeconds(1));
        Assert.False(tracker.WantsPhoneScreen(T.AddSeconds(1)));
        Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(2)));
    }

    [Fact]
    public void BriefVerifiedCaptureCannotExpediteUnknownDevice()
    {
        var tracker = new UuConnectionTracker();
        var verified = UuCaptureLifecycle.Combine(Unknown, new(UuCapturePhase.Active, 1, true)) with { OverlayVisible = true };
        tracker.Update(verified, T);
        tracker.Update(verified with { CaptureActive = null }, T.AddSeconds(.5));
        tracker.Update(verified, T.AddSeconds(1));
        Assert.False(tracker.WantsPhoneScreen(T.AddSeconds(1)));
    }

    [Fact]
    public void ContinuousVerifiedInactivityOverlapsDebounceButPreservesInterruptedTick()
    {
        var tracker = new UuConnectionTracker();
        tracker.Update(Active, T);
        Assert.Empty(tracker.Update(Verified, T.AddSeconds(.5)));
        Assert.Empty(tracker.Update(Verified, T.AddSeconds(1.5)));
        Assert.Equal(ConnectionTransitionKind.Interrupted,
            Assert.Single(tracker.Update(Verified, T.AddSeconds(2))).Kind);
        Assert.Equal(ConnectionTransitionKind.Ended,
            Assert.Single(tracker.Update(Verified, T.AddSeconds(3))).Kind);
    }

    [Fact]
    public void IndependentControllerAndTopologyProtectionPreventEarlyEnd()
    {
        var tracker = new UuConnectionTracker();
        tracker.Update(Active, T);
        tracker.NoteSelfInducedChange(T);
        Assert.Empty(tracker.Update(Verified, T.AddSeconds(4.9)));
        Assert.True(tracker.IsConnected);
        Assert.Equal(ConnectionTransitionKind.Interrupted,
            Assert.Single(tracker.Update(Verified, T.AddSeconds(5.1))).Kind);
        Assert.Empty(tracker.Update(Verified, T.AddSeconds(6.9)));
        var controller = Verified with { Controller = new("test", ControllerKind.Phone) };
        Assert.Equal(ConnectionTransitionKind.Resumed,
            Assert.Single(tracker.Update(controller, T.AddSeconds(7.2))).Kind);
        Assert.True(tracker.WantsPhoneScreen(T.AddSeconds(7.2)));
    }

    [Fact]
    public void CaptureAloneCannotStartEpisode()
    {
        // GameViewerServer opens DxgKrnl output-duplication contexts for its own health checks
        // with no remote client. Bare CaptureActive must not seed a session or the phone
        // display self-oscillates: phantom Connected → capture idles → accelerated Ended →
        // teardown → streamer-log heartbeat regrowth → Connected → repeat.
        var tracker = new UuConnectionTracker();
        var selfCapture = new UuSignals(false, null, true);
        Assert.Empty(tracker.Update(selfCapture, T));
        Assert.False(tracker.IsConnected);
        Assert.False(tracker.WantsPhoneScreen(T.AddSeconds(3)));
        Assert.Empty(tracker.Update(selfCapture, T.AddSeconds(5)));
        Assert.False(tracker.IsConnected);
        Assert.Empty(tracker.Update(selfCapture, T.AddSeconds(20)));
        Assert.False(tracker.IsConnected);
    }

    [Fact]
    public void LogFallbackAloneWithoutPeerCannotStartEpisode()
    {
        // Snug's own display start/stop churn makes GameViewerServer append device-change log
        // entries; bare streamer-log growth must not seed a phantom session. It needs a real
        // peer TCP alongside, or stronger evidence (overlay/controller/capture).
        var tracker = new UuConnectionTracker();
        var logOnly = new UuSignals(false, null, null, LogFallback: true);
        Assert.Empty(tracker.Update(logOnly, T));
        Assert.False(tracker.IsConnected);
        Assert.Empty(tracker.Update(logOnly, T.AddSeconds(10)));
        Assert.False(tracker.IsConnected);
        Assert.False(tracker.WantsPhoneScreen(T.AddSeconds(10)));
        // A real client pair (log growth + peer) still connects immediately.
        Assert.Equal(ConnectionTransitionKind.Connected,
            Assert.Single(tracker.Update(
                logOnly with { PeerConnection = true }, T.AddSeconds(11))).Kind);
    }
}
