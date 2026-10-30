using NUnit.Framework;

public class SongSelectIdleStateTests
{
    [Test] public void ShowsOnceOnEntryThenAgainAtTenSecondsWhileAwaitingFirstOperation()
    {
        // 2026-10-30 ユーザー「④もすべてやる」: 模型の一周ではなく、入場から3.5秒を境界にする。
        Assert.AreEqual(3.5f, SongSelectIdleState.EntryDisplaySeconds);
        var state = new SongSelectIdleState();
        Assert.True(state.ShouldShow, "入場直後から案内する");
        state.Tick(SongSelectIdleState.EntryDisplaySeconds - .01f, true); Assert.True(state.ShouldShow);
        state.Tick(.011f, true); Assert.False(state.ShouldShow, "3.5秒で一旦案内を閉じる");
        state.Tick(SongSelectIdleState.DelaySeconds - SongSelectIdleState.EntryDisplaySeconds - .011f, true);
        Assert.False(state.ShouldShow);
        state.Tick(.011f, true); Assert.True(state.ShouldShow);
        state.RegisterActivity(); state.Tick(90, true); Assert.False(state.ShouldShow);
    }
    [Test] public void EarlyOperationSuppressesHintForTheRestOfThisVisit()
    {
        var state = new SongSelectIdleState(); state.Tick(4, true);
        state.RegisterActivity(); state.Tick(100, true); Assert.False(state.ShouldShow);
        var nextVisit = new SongSelectIdleState(); Assert.True(nextVisit.ShouldShow, "再入場時も最初に案内する");
        nextVisit.Tick(10, true); Assert.True(nextVisit.ShouldShow);
    }
    [Test] public void InactiveScreenDoesNotConsumeTheEntryDemonstration()
    {
        var state = new SongSelectIdleState();
        state.Tick(SongSelectIdleState.EntryDisplaySeconds - .01f, true);
        state.Tick(60, false); Assert.True(state.ShouldShow);
        state.Tick(.02f, true); Assert.False(state.ShouldShow);
    }
    [Test] public void FocusAndTransitionPauseTheClockAndInvalidTimeIsIgnored()
    {
        var state = new SongSelectIdleState(); state.Tick(9, true);
        state.Tick(60, false); state.Tick(float.NaN, true); state.Tick(float.PositiveInfinity, true); state.Tick(-5, true);
        Assert.False(state.ShouldShow); state.Tick(1, true); Assert.True(state.ShouldShow);
    }
}
