using NUnit.Framework;
using UnityEngine;

public class SongSelectAimTrackerTests
{
    readonly Rect area = new Rect(100, 100, 200, 100);
    readonly Vector2 point = new Vector2(180, 150);
    static void Hold(SongSelectAimTracker t, object key, Rect area, Vector2 p, int frames)
    { for (int i = 0; i < frames; i++) Assert.False(t.Tick(key, area, p, .1f, true)); }
    [Test] public void FullSecondRequiredAndHeldAimNeverRepeats()
    {
        var t = new SongSelectAimTracker(); var key = new object();
        Hold(t, key, area, point, 9); Assert.That(t.Progress01, Is.EqualTo(.9f).Within(.001f));
        Assert.True(t.Tick(key, area, point, .1f, true)); Hold(t, key, area, point, 100); Assert.True(t.NeedsRelease);
    }
    [Test] public void TargetChangeAndInputLossRequireAFreshSecond()
    {
        var t = new SongSelectAimTracker(); var a = new object(); var b = new object();
        Hold(t,a,area,point,8); Hold(t,b,area,point,8); t.Cancel(); Hold(t,b,area,point,9);
        Assert.True(t.Tick(b,area,point,.1f,true));
    }
    [Test] public void MovingListOrDestroyedTargetCannotRearmAtShotPosition()
    {
        var t = new SongSelectAimTracker(); var key = new object();
        Hold(t,key,area,point,9); Assert.True(t.Tick(key,area,point,.1f,true)); t.Cancel();
        Hold(t,new object(),new Rect(0,0,600,600),point,50); Assert.True(t.NeedsRelease);
        t.Tick(null,default,Vector2.zero,.1f,false); t.Tick(null,default,Vector2.zero,.1f,false);
        Assert.False(t.NeedsRelease); Hold(t,key,area,point,9); Assert.True(t.Tick(key,area,point,.1f,true));
    }
    [Test] public void BriefJitterOutsideAfterShotDoesNotRearm()
    {
        var t = new SongSelectAimTracker(); t.BlockUntilExit(area);
        t.Tick(null,default,Vector2.zero,.05f,true); t.Tick(null,default,point,.05f,true);
        t.Tick(null,default,Vector2.zero,.05f,true); Assert.True(t.NeedsRelease);
    }
    [Test] public void DisabledTargetAndLongFrameNeverCompleteCharge()
    {
        var t = new SongSelectAimTracker(); var key = new object(); Hold(t,key,area,point,9);
        Assert.False(t.Tick(key,area,point,.1f,false)); Hold(t,key,area,point,9);
        Assert.False(t.Tick(key,area,point,1.2f,true)); Assert.Zero(t.Progress01);
    }
}
