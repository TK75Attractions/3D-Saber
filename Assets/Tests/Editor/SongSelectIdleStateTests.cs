using NUnit.Framework;
using UnityEngine;

public class SongSelectIdleStateTests
{
    [Test] public void ShowsAtTenSecondsOnlyWhileAwaitingFirstOperation()
    {
        var state = new SongSelectIdleState();
        state.Tick(9.99f, true); Assert.False(state.ShouldShow);
        state.Tick(.011f, true); Assert.True(state.ShouldShow);
        state.RegisterActivity(); state.Tick(90, true); Assert.False(state.ShouldShow);
    }
    [Test] public void EarlyOperationSuppressesHintForTheRestOfThisVisit()
    {
        var state = new SongSelectIdleState(); state.Tick(4, true);
        state.RegisterActivity(); state.Tick(100, true); Assert.False(state.ShouldShow);
        var nextVisit = new SongSelectIdleState(); nextVisit.Tick(10, true); Assert.True(nextVisit.ShouldShow);
    }
    [Test] public void FocusAndTransitionPauseTheClockAndInvalidTimeIsIgnored()
    {
        var state = new SongSelectIdleState(); state.Tick(9, true);
        state.Tick(60, false); state.Tick(float.NaN, true); state.Tick(float.PositiveInfinity, true); state.Tick(-5, true);
        Assert.False(state.ShouldShow); state.Tick(1, true); Assert.True(state.ShouldShow);
    }
    [Test] public void StationaryTrackingJitterAndSourceChangesDoNotCountAsAnOperation()
    {
        var state = new SongSelectIdleState();
        state.ObservePointer(new Vector2(.5f, .5f), 1, true);
        for (int i = 0; i < 100; i++) state.ObservePointer(new Vector2(.5f + .003f * (i % 3 - 1), .5f), 1, true);
        state.ObservePointer(Vector2.zero, 2, true);
        state.ObservePointer(Vector2.one, 1, true);
        state.ObservePointer(Vector2.zero, 1, false);
        state.ObservePointer(Vector2.zero, 1, true);
        state.Tick(10, true); Assert.True(state.ShouldShow);
    }
    [Test] public void SlowAccumulatedMotionCountsEvenWithTinyPerFrameSteps()
    {
        var state = new SongSelectIdleState(); state.ObservePointer(Vector2.zero, 1, true);
        for (int i = 1; i <= 20; i++) state.ObservePointer(new Vector2(i * .001f, 0), 1, true);
        Assert.True(state.Completed);
    }
}
