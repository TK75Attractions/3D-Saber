using System.Linq;
using NUnit.Framework;

public class SongPreviewWindowTests
{
    [TestCase("ElDorado",167.4,223.248)] [TestCase("Epilogue",133.859,166.408707)]
    [TestCase("揺籠",140.245,184.24163)] [TestCase("Morning",113.898,165.329)]
    [TestCase("2_23_AM",129.333,196.075)]
    public void AllDifficultyExcerptsUseAuthoredClimaxAndContainRealNotes(string song,double expected,double length)
    {
        var timeline=StagePerformanceTimeline.Load(song);
        var window=SongPreviewWindow.Resolve(timeline,length);
        Assert.That(window.Start,Is.EqualTo(expected).Within(.001)); Assert.AreEqual(10,window.Duration);
        Assert.Less(window.Start+window.Duration,length);
        foreach(var difficulty in SongSelectController.StandardDifficulties)
        {
            var chart=ChartLoader.LoadFromStreamingAssets(song,difficulty);
            Assert.IsTrue(chart.notes.Any(n=>SongPreviewWindow.Intersects(chart,n,window,0)),song+" / "+difficulty);
        }
    }

    [Test]
    public void FallbackChoosesLatestStrongestCompleteSection()
    {
        var timeline=new StagePerformanceTimeline { sections=new[]{
            new StagePerformanceTimeline.Section {startSeconds=5,endSeconds=20,intensity=.5f},
            new StagePerformanceTimeline.Section {startSeconds=30,endSeconds=50,intensity=1},
            new StagePerformanceTimeline.Section {startSeconds=70,endSeconds=90,intensity=1}} };
        Assert.AreEqual(70,SongPreviewWindow.Resolve(timeline,100).Start);
        Assert.AreEqual(0,SongPreviewWindow.Resolve(null,100).Start);
    }

    [Test]
    public void ShortOrInvalidAudioAndOverrunningStartAreSafe()
    {
        Assert.IsFalse(SongPreviewWindow.Resolve(null,double.NaN).IsValid);
        Assert.IsFalse(SongPreviewWindow.Resolve(null,0).IsValid);
        var w=SongPreviewWindow.Resolve(new StagePerformanceTimeline{previewStartSeconds=98},100);
        Assert.AreEqual(90,w.Start); Assert.AreEqual(10,w.Duration);
        w=SongPreviewWindow.Resolve(null,3); Assert.AreEqual(0,w.Start); Assert.AreEqual(3,w.Duration);
    }

    [Test]
    public void ExcerptIncludesCrossingLongNotesAndOnlyUsesChartOffset()
    {
        var chart=new ChartData{offsetMs=1250}; var window=new SongPreviewWindow(10,10);
        var longNote=new NoteData{time=7000,count=5,lengthMs=4000};
        Assert.AreEqual(8.25,SongPreviewWindow.NoteTime(chart,longNote));
        Assert.IsTrue(SongPreviewWindow.Intersects(chart,longNote,window,2));
        Assert.IsFalse(SongPreviewWindow.Intersects(chart,new NoteData{time=7000},window,2));
        Assert.IsFalse(SongPreviewWindow.Intersects(chart,new NoteData{time=25000},window,2));
        Assert.AreEqual(1250,chart.offsetMs); Assert.AreEqual(7000,longNote.time);
    }
}
