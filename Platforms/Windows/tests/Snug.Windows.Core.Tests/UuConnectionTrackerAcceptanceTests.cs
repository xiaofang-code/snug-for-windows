using Snug.Windows.Core;
namespace Snug.Windows.Core.Tests;

public sealed class UuConnectionTrackerAcceptanceTests
{
    [Fact]
    public void CaptureStopThenTwoSecondControllerTableExpiryHonorsReconnectGrace()
    {
        var tracker=new UuConnectionTracker();
        var t=DateTimeOffset.UnixEpoch;
        var phone=new ControllerInfo("test",ControllerKind.Phone);
        Assert.Contains(tracker.Update(new(false,null,true,LogFallback:true,PeerConnection:true),t),x=>x.Kind==ConnectionTransitionKind.Connected);
        var cachedController=new UuSignals(false,null,false,[phone]);
        tracker.Update(cachedController,t.AddSeconds(1));
        tracker.Update(cachedController,t.AddSeconds(2));
        Assert.Empty(tracker.Update(new(false,null,false),t.AddSeconds(3)));
        var interrupted=tracker.Update(new(false,null,false),t.AddSeconds(3.51));
        Assert.Contains(interrupted,x=>x.Kind==ConnectionTransitionKind.Interrupted);
        Assert.True(tracker.WantsPhoneScreen(t.AddSeconds(6))); // still within reconnect window
        var ended=tracker.Update(new(false,null,false),t.AddSeconds(23.52));
        Assert.Contains(ended,x=>x.Kind==ConnectionTransitionKind.Ended);
        Assert.False(tracker.WantsPhoneScreen(t.AddSeconds(23.52)));
    }
}