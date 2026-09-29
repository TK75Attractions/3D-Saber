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
    // 曲送りの盤: 乗せたままでも、1秒ため直すたびに続けて発射する。
    static bool Repeat(SongSelectAimTracker t, object key, float dt = .1f) => t.Tick(key, new Rect(100, 100, 200, 100), new Vector2(180, 150), dt, true, 1, false, true);
    [Test] public void RepeatTargetKeepsFiringEveryFullHoldWithoutLeaving()
    {
        var t = new SongSelectAimTracker(); var key = new object();
        for (int shot = 0; shot < 3; shot++)
        {
            for (int i = 0; i < 9; i++) Assert.False(Repeat(t, key));
            Assert.True(Repeat(t, key)); Assert.False(t.NeedsRelease); Assert.Zero(t.Progress01);
        }
    }
    [Test] public void RepeatStillNeedsAFullHoldForTheNextDiscAndAfterLongFrames()
    {
        var t = new SongSelectAimTracker(); object a = new object(), b = new object();
        for (int i = 0; i < 9; i++) Repeat(t, a);
        Assert.True(Repeat(t, a));
        for (int i = 0; i < 5; i++) Assert.False(Repeat(t, b));
        Assert.False(Repeat(t, b, .25f)); Assert.Zero(t.Progress01);
        for (int i = 0; i < 9; i++) Assert.False(Repeat(t, b));
        Assert.True(Repeat(t, b));
    }
    [Test] public void RepeatShotDoesNotUnlockCommitTargetsUnderTheSamePoint()
    {
        // 連続送りの直後に同じ位置へ来たスタートの的は2秒ため直し、撃った後は外すまで受け付けない。
        var t = new SongSelectAimTracker(); object disc = new object(), start = new object();
        for (int i = 0; i < 9; i++) Repeat(t, disc);
        Assert.True(Repeat(t, disc));
        for (int i = 0; i < 19; i++) Assert.False(t.Tick(start, area, point, .1f, true, 2));
        Assert.True(t.Tick(start, area, point, .1f, true, 2)); Assert.True(t.NeedsRelease);
        for (int i = 0; i < 30; i++) Assert.False(t.Tick(start, area, point, .1f, true, 2));
    }
}
