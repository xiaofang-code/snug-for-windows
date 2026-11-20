using Snug.Windows.Core;
namespace Snug.Windows.Core.Tests;
public sealed class UuEvidenceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    [Fact] public void LockOnlyIsNotCaptureOrVerifiedIdentity()
    {
        var sample = new UuSignals(false,null,null,LogFallback:true);
        Assert.True(sample.IsPresent);
        Assert.Null(sample.CaptureActive);
        Assert.Equal(UuEvidenceKind.LogFallback,sample.Evidence);
        var tracker = new UuConnectionTracker();
        tracker.Update(sample,Start);
        Assert.True(tracker.WantsPhoneScreen(Start.AddSeconds(3)));
        Assert.Empty(tracker.Controllers);
    }
    [Fact] public void PeerTcpAloneIsDiagnosticOnly()
    {
        var sample = new UuSignals(false,null,false,PeerConnection:true);
        Assert.False(sample.IsPresent);
        Assert.Equal(UuEvidenceKind.None,sample.Evidence);
        var tracker = new UuConnectionTracker();
        tracker.Update(sample,Start);
        Assert.False(tracker.WantsPhoneScreen(Start.AddSeconds(3)));
    }
    [Fact] public void ActualTableHasPrecedenceOverFallback()
    {
        var sample = new UuSignals(false,null,null,[new("DESKTOP-001",ControllerKind.Desktop)],true);
        Assert.Equal(UuEvidenceKind.ControllerTable,sample.Evidence);
        var tracker = new UuConnectionTracker();
        tracker.Update(sample,Start);
        Assert.False(tracker.WantsPhoneScreen(Start.AddSeconds(3)));
    }
    [Fact] public void TableUnavailableDoesNotClaimFreshIdentification()
    {
        var sample = new UuSignals(false,null,null,LogFallback:true);
        var tracker = new UuConnectionTracker();
        tracker.Update(sample with { Controllers = [new("iPhone",ControllerKind.Phone)] },Start);
        tracker.Update(sample,Start.AddSeconds(3));
        Assert.Equal(UuEvidenceKind.LogFallback,sample.Evidence);
        Assert.True(tracker.WantsPhoneScreen(Start.AddSeconds(3)));
    }
    [Fact] public void ActualSessionWithoutDeviceIsNotDeviceIdentification()
    {
        Assert.Equal(UuEvidenceKind.Session,new UuSignals(true,null,null).Evidence);
        Assert.Equal(UuEvidenceKind.Session,new UuSignals(false,null,true).Evidence);
        Assert.Equal(UuEvidenceKind.None,new UuSignals(false,null,false).Evidence);
    }
}