using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class SongSelectDiscTests
{
    [Test] public void TwoSecondTargetAndLongFrameRequireFreshHold()
    {
        var tracker=new SongSelectAimTracker(); var key=new object(); var rect=new Rect(0,0,200,200); var p=rect.center;
        for(int i=0;i<19;i++) Assert.False(tracker.Tick(key,rect,p,.1f,true,2));
        Assert.That(tracker.Progress01,Is.EqualTo(.95f).Within(.001));
        Assert.False(tracker.Tick(key,rect,p,.201f,true,2)); Assert.Zero(tracker.Progress01);
        for(int i=0;i<19;i++) Assert.False(tracker.Tick(key,rect,p,.1f,true,2));
        Assert.True(tracker.Tick(key,rect,p,.1f,true,2)); tracker.Cancel(); Assert.True(tracker.NeedsRelease);
    }
    [Test] public void ChangingDurationCannotReuseCharge()
    {
        var tracker=new SongSelectAimTracker(); var key=new object(); var rect=new Rect(0,0,200,200);
        for(int i=0;i<9;i++) tracker.Tick(key,rect,rect.center,.1f,true,1);
        tracker.Tick(key,rect,rect.center,.1f,true,2); Assert.That(tracker.Progress01,Is.EqualTo(.05f).Within(.001));
    }
    [Test] public void CircleReleaseNeedsTwelveHundredthsContinuouslyOutside()
    {
        var tracker=new SongSelectAimTracker(); var rect=new Rect(0,0,200,200); tracker.BlockUntilExit(rect,true);
        tracker.Tick(null,rect,Vector2.one,.1f,false); Assert.True(tracker.NeedsRelease);
        tracker.Tick(null,rect,rect.center,.1f,false);
        tracker.Tick(null,rect,Vector2.one,.1f,false); Assert.True(tracker.NeedsRelease);
        tracker.Tick(null,rect,Vector2.one,.021f,false); Assert.False(tracker.NeedsRelease);
    }
    [Test] public void CountdownPausesAndExpiresOnce()
    {
        var timer=new SongSelectCountdown(); Assert.False(timer.Tick(70,false)); Assert.AreEqual(100,timer.Remaining);
        Assert.False(timer.Tick(99,true)); Assert.AreEqual(1,timer.Remaining);
        Assert.True(timer.Tick(1,true)); Assert.False(timer.Tick(30,true));
        timer.Reset(); Assert.AreEqual(100,timer.Remaining);
    }
    [Test] public void TempoChangeUsesNotesAndOffsetOnlyOnce()
    {
        var chart=new ChartData { bpm=120,offsetMs=100 };
        chart.notes.Add(new NoteData {beat=0,time=0}); chart.notes.Add(new NoteData {beat=2,time=1000}); chart.notes.Add(new NoteData {beat=4,time=2200});
        var timeline=new SongSelectBeatTimeline(new[]{chart}); Assert.True(timeline.UsesNoteTiming);
        Assert.That(timeline.TimeAt(1),Is.EqualTo(.6).Within(.00001)); Assert.That(timeline.TimeAt(3),Is.EqualTo(1.7).Within(.00001));
    }
    [Test] public void SixEightChangesDownbeatsToThreeQuarterBeats()
    {
        var chart=new ChartData {bpm=120}; chart.notes.Add(new NoteData {beat=0,time=0}); chart.notes.Add(new NoteData {beat=12,time=6000});
        chart.timeSignatures.Add(new ChartTimeSignature {beat=0,numerator=4,denominator=4});
        chart.timeSignatures.Add(new ChartTimeSignature {beat=4,numerator=6,denominator=8});
        var events=new SongSelectBeatTimeline(new[]{chart}).Events(0,6);
        CollectionAssert.AreEqual(new[]{0,4,7,10},events.Where(e=>e.Downbeat).Select(e=>e.Beat));
    }
    [Test] public void MissingBeatKnotsUseChartOriginFallback()
    {
        var chart=new ChartData {bpm=120,beatZeroMs=250,offsetMs=100};
        var timeline=new SongSelectBeatTimeline(new[]{chart}); Assert.False(timeline.UsesNoteTiming);
        Assert.That(timeline.TimeAt(1),Is.EqualTo(.85).Within(.00001));
    }
    [TestCase("Epilogue")]
    [TestCase("Andalusia")]
    public void ActualVariableTimingChartsProduceOrderedEvents(string id)
    {
        var charts=SongSelectController.StandardDifficulties.Select(d=>ChartLoader.LoadFromStreamingAssets(id,d)).ToList();
        var timeline=new SongSelectBeatTimeline(charts); Assert.True(timeline.UsesNoteTiming);
        double start=StagePerformanceTimeline.Load(id).previewStartSeconds;
        var events=timeline.Events(start,start+10); Assert.Greater(events.Count,10); Assert.True(events.Any(e=>e.Downbeat));
        for(int i=1;i<events.Count;i++) Assert.Greater(events[i].Time,events[i-1].Time);
        if(id=="Epilogue") Assert.Greater(events.Zip(events.Skip(1),(a,b)=>b.Time-a.Time).Max()-events.Zip(events.Skip(1),(a,b)=>b.Time-a.Time).Min(),.015);
    }
}

public class SongAchievementStoreTests
{
    string song;
    [SetUp] public void Setup() { song="__AchievementTest_"+Guid.NewGuid().ToString("N"); }
    [TearDown] public void Cleanup() { foreach(var difficulty in new[]{"Easy","Hard"}) PlayerPrefs.DeleteKey(SongAchievementStore.Key(song,difficulty)); PlayerPrefs.Save(); }
    [Test] public void AllPerfectIncludesAllFourCountersAndDuplicateResultIsIgnored()
    {
        Assert.True(SongAchievementStore.Record(song,"Easy","run1",10,0,0,0,0));
        Assert.False(SongAchievementStore.Record(song,"Easy","run1",10,0,0,0,0));
        var c=SongAchievementStore.Load(song,"Easy"); Assert.AreEqual(1,c.s); Assert.AreEqual(1,c.sPlus); Assert.AreEqual(1,c.fc); Assert.AreEqual(1,c.ap);
        Assert.True(SongAchievementStore.Record(song,"Easy","run2",10,0,0,0,0));
        Assert.AreEqual(2,SongAchievementStore.Load(song,"Easy").ap);
        Assert.AreEqual(0,SongAchievementStore.Load(song,"Hard").ap);
    }
    [Test] public void RankAndFullComboAreIndependentAndEmptyResultDoesNotCount()
    {
        Assert.False(SongAchievementStore.Record(song,"Easy","empty",0,0,0,0,0));
        Assert.True(SongAchievementStore.Record(song,"Easy","rank",99,0,0,0,1));
        var c=SongAchievementStore.Load(song,"Easy"); Assert.AreEqual(1,c.s); Assert.AreEqual(1,c.sPlus); Assert.Zero(c.fc); Assert.Zero(c.ap);
    }
    [Test] public void CalibrationDoesNotSaveAchievements()
    {
        string oldSong=GameSession.SelectedSongId, oldDifficulty=GameSession.SelectedDifficulty, oldId=GameSession.AchievementRunId;
        bool oldCalibration=GameSession.IsCalibrationMode; int oldPerfect=GameSession.FinalPerfect;
        try { GameSession.SelectedSongId=song; GameSession.SelectedDifficulty="Easy"; GameSession.AchievementRunId="cal"; GameSession.IsCalibrationMode=true; GameSession.FinalPerfect=10;
            GameSession.RecordCompletedAchievements(); Assert.False(PlayerPrefs.HasKey(SongAchievementStore.Key(song,"Easy"))); }
        finally { GameSession.SelectedSongId=oldSong; GameSession.SelectedDifficulty=oldDifficulty; GameSession.AchievementRunId=oldId; GameSession.IsCalibrationMode=oldCalibration; GameSession.FinalPerfect=oldPerfect; }
    }
}
