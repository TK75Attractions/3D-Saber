using System.Linq;
using NUnit.Framework;

public class SongPreviewWindowTests
{
    [TestCase("2_23_AM",65.333,110.741769,76.026203)]
    [TestCase("Andalusia",53.44,130.40181,63.834197)]
    [TestCase("Aobasou",109.714286,138.857052,120.000012)]
    [TestCase("BenkyouRemaster",6.024,138.762449,16.524000)]
    [TestCase("ElDorado",100.203,144.065306,110.426200)]
    [TestCase("Epilogue",111.208,166.408707,120.642)]
    [TestCase("escapade",85.914737,109.944036,96.019996)]
    [TestCase("logic",144.153,206.759184,154.403000)]
    [TestCase("ManicSonic",81.166667,149.451938,91.833333)]
    [TestCase("Morning",114.422,165.329,124.607797)]
    [TestCase("Phobia PARTⅠ",89.106909,110.968163,98.207818)]
    [TestCase("揺籠",140.245,184.24163,150.726672)]
    [TestCase("製作中",134.713878,174.864,144.815922)]
    public void AllDifficultyExcerptsKeepTheirStartAndEndOnTheSameBar(string song,double expected,double length,double expectedEnd)
    {
        var timeline=StagePerformanceTimeline.Load(song);
        var window=SongSelectChartPreview.ResolveWindow(song,timeline,length);
        Assert.That(window.Start,Is.EqualTo(expected).Within(.001));
        Assert.That(window.Start+window.Duration,Is.EqualTo(expectedEnd).Within(.001));
        Assert.That(window.Duration,Is.InRange(8d,12d));
        Assert.LessOrEqual(window.Start+window.Duration,length);
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
