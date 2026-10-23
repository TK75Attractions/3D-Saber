using System.Collections.Generic;
using NUnit.Framework;

// 86B: 始まりを動かさず、音源時刻で最寄りの小節頭を選ぶ。
public class SongPreviewBarEndTests
{
    static SongPreviewWindow Resolve(ChartData chart,double start=0,double length=100,double requested=10)
        => SongPreviewWindow.Resolve(new StagePerformanceTimeline{previewStartSeconds=start},length,requested,
            new SongSelectBeatTimeline(new[]{chart}));

    [TestCase(.2,10)]
    [TestCase(1.2,12)]
    [TestCase(1,10)]
    public void NearestBarMayBeBeforeOrAfterTargetAndTiesChooseEarlier(double start,double end)
    {
        var window=Resolve(new ChartData{bpm=120},start);
        Assert.AreEqual(start,window.Start);
        Assert.AreEqual(end,window.Start+window.Duration,1e-9);
    }

    [TestCase(0,8)]
    [TestCase(4,16)]
    public void EightAndTwelveSecondLimitsAreInclusive(double start,double end)
    {
        var window=Resolve(new ChartData{bpm=30},start);
        Assert.AreEqual(start,window.Start);
        Assert.AreEqual(end,window.Start+window.Duration,1e-9);
        Assert.That(window.Duration,Is.InRange(8d,12d));
    }

    [Test]
    public void MissingCandidateKeepsTenSecondsAndExplicitOtherDurationIsUnchanged()
    {
        Assert.AreEqual(10,Resolve(new ChartData{bpm=10}).Duration);
        var window=Resolve(new ChartData{bpm=120},.3,100,5);
        Assert.AreEqual(.3,window.Start);
        Assert.AreEqual(5,window.Duration,1e-9);
    }

    [Test]
    public void TempoMapOverridesNotesAndAppliesChartOffsetOnce()
    {
        var chart=new ChartData{
            bpm=120,beatZeroMs=1000,offsetMs=250,
            editorTempoMap=new List<ChartTempoPoint>{
                new ChartTempoPoint{beat=0,timeMs=1000},
                new ChartTempoPoint{beat=8,timeMs=5000},
                new ChartTempoPoint{beat=16,timeMs=8000},
                new ChartTempoPoint{beat=24,timeMs=12000}},
            notes=new List<NoteData>{new NoteData{beat=0,time=0},new NoteData{beat=40,time=40000}}};
        var window=Resolve(chart,.6);
        Assert.AreEqual(.6,window.Start);
        Assert.AreEqual(10.25,window.Start+window.Duration,1e-9);
        Assert.AreEqual(250,chart.offsetMs);
        Assert.AreEqual(8000,chart.editorTempoMap[2].timeMs);
    }

    [Test]
    public void SingleTempoAnchorKeepsItsOriginAndBpm()
    {
        var chart=new ChartData{bpm=120,offsetMs=250,editorTempoMap=new List<ChartTempoPoint>{
            new ChartTempoPoint{beat=4,timeMs=5000}},
            notes=new List<NoteData>{new NoteData{beat=0,time=0},new NoteData{beat=40,time=40000}}};
        var window=Resolve(chart,.6);
        Assert.AreEqual(11.25,window.Start+window.Duration,1e-9);
    }

    [Test]
    public void LegacyVariableTempoUsesNoteAnchorsInsteadOfInitialBpm()
    {
        var chart=new ChartData{bpm=120,offsetMs=250,notes=new List<NoteData>{
            new NoteData{beat=0,time=1000},new NoteData{beat=8,time=5000},
            new NoteData{beat=16,time=8000},new NoteData{beat=24,time=12000}}};
        var window=Resolve(chart,.6);
        Assert.AreEqual(10.25,window.Start+window.Duration,1e-9);
    }

    [Test]
    public void SevenEightIncludesHalfQuarterNoteBarStarts()
    {
        var chart=new ChartData{bpm=120,timeSignatures=new List<ChartTimeSignature>{
            new ChartTimeSignature{beat=0,numerator=7,denominator=8}}};
        var window=Resolve(chart,2.3);
        Assert.AreEqual(12.25,window.Start+window.Duration,1e-9);
    }

    [Test]
    public void MeterChangeStartsANewBarEvenInsideThePreviousBar()
    {
        var chart=new ChartData{bpm=120,timeSignatures=new List<ChartTimeSignature>{
            new ChartTimeSignature{beat=0,numerator=4,denominator=4},
            new ChartTimeSignature{beat=19.5f,numerator=7,denominator=8}}};
        var window=Resolve(chart);
        Assert.AreEqual(9.75,window.Start+window.Duration,1e-9);
    }

    [Test]
    public void TwelveEightAndLegacyYurikagoHaveTheSameBarDuration()
    {
        // 四分音符BPM86の6拍と、旧データの付点四分音符BPM57.333…の4拍は同じ。
        var chart=new ChartData{bpm=86,offsetMs=12,timeSignatures=new List<ChartTimeSignature>{
            new ChartTimeSignature{beat=0,numerator=12,denominator=8}}};
        var window=Resolve(chart,140.245,184.24163);
        Assert.AreEqual(140.245,window.Start);
        Assert.AreEqual(150.709674,window.Start+window.Duration,.001);
        var legacy=SongSelectChartPreview.ResolveWindow("揺籠",StagePerformanceTimeline.Load("揺籠"),184.24163);
        Assert.AreEqual(window.Start,legacy.Start);
        Assert.AreEqual(window.Duration,legacy.Duration,.001);
    }

    [Test]
    public void ClipEndLimitsCandidatesWithoutMovingTheExistingStart()
    {
        var chart=new ChartData{bpm=120,beatZeroMs=1500};
        var timeline=new StagePerformanceTimeline{previewStartSeconds=98};
        var old=SongPreviewWindow.Resolve(timeline,100);
        var window=SongPreviewWindow.Resolve(timeline,100,10,new SongSelectBeatTimeline(new[]{chart}));
        Assert.AreEqual(old.Start,window.Start);
        Assert.AreEqual(99.5,window.Start+window.Duration,1e-9);
        Assert.LessOrEqual(window.Start+window.Duration,100);

        var shortWindow=Resolve(new ChartData{bpm=120},0,9);
        Assert.AreEqual(0,shortWindow.Start);
        Assert.AreEqual(8,shortWindow.Duration);
        Assert.AreEqual(3,Resolve(chart,0,3).Duration);
    }
}
