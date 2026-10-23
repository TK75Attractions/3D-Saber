using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class StageLaserShapeTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    bool projector, reduced;
    Color fog;

    [SetUp]
    public void SaveSettings()
    {
        projector = DisplaySettings.ProjectorMode; reduced = DisplaySettings.ReducedEffects;
        fog = RenderSettings.fogColor;
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in objects) Object.DestroyImmediate(go);
        objects.Clear();
        RenderSettings.fogColor = fog;
        DisplaySettings.SetProjectorModeForTest(projector);
        DisplaySettings.SetReducedEffectsForTest(reduced);
    }

    static StagePerformanceTimeline.Section Section(double start, double end, float intensity)
        => new StagePerformanceTimeline.Section {
            startSeconds = start, endSeconds = end, intensity = intensity, fadeInSeconds = 20
        };

    StageFeverShow Show(ChartData chart, params StagePerformanceTimeline.Section[] sections)
    {
        var go = new GameObject("LaserShapeTest"); objects.Add(go);
        var show = go.AddComponent<StageFeverShow>();
        show.Setup(StageTheme.PulseArray, -2.5f, new StagePerformanceTimeline { sections = sections }, chart);
        var camera = new GameObject("LaserShapeCamera", typeof(Camera)); objects.Add(camera);
        show.AtmosphereCamera = camera.GetComponent<Camera>();
        return show;
    }

    [Test]
    public void ClassificationUsesRawFiniteIntensity()
    {
        Assert.IsFalse(StageFeverShow.IsClimax(.65f));
        Assert.IsFalse(StageFeverShow.IsClimax(.94999f));
        Assert.IsTrue(StageFeverShow.IsClimax(.95f));
        Assert.IsTrue(StageFeverShow.IsClimax(1f));
        Assert.IsFalse(StageFeverShow.IsClimax(float.NaN));
        Assert.IsFalse(StageFeverShow.IsClimax(float.PositiveInfinity));
    }

    [Test]
    public void OverlappingSectionsChangeShapeInOneBeatAndIgnoreArrayOrder()
    {
        var sections = new[] { Section(10, 30, .85f), Section(12, 14, .95f), Section(12.2, 13, 1f),
            null, Section(0, 30, float.NaN), Section(-1, 30, 1f), Section(20, 19, 1f) };
        var show = Show(new ChartData { bpm = 120 }, sections);
        var reversed = Show(new ChartData { bpm = 120 }, sections.Reverse().ToArray());
        double[] times = { 9, 10, 12, 12.25, 12.5, 13, 14, 14.25, 14.5, 29 };
        float[] expected = { 0, 0, 0, .5f, 1, 1, 1, .5f, 0, 0 };
        for (int i = 0; i < times.Length; i++)
        {
            Assert.AreEqual(expected[i], show.LaserFanAt(times[i]), 1e-5f, times[i].ToString());
            Assert.AreEqual(expected[i], reversed.LaserFanAt(times[i]), 1e-5f);
        }
        // 短い最高潮も、巻き取り中に扇の開きが進まない。
        show.Setup(StageTheme.PulseArray, -2.5f,
            new StagePerformanceTimeline { sections = new[] { Section(10, 10.2, 1) } }, new ChartData { bpm = 120 });
        var end = show.LaserAt(10.2); var tail = show.LaserAt(10.4);
        Assert.AreEqual(.352f, show.LaserFanAt(end.poseTime), 1e-5f);
        Assert.AreEqual(show.LaserFanAt(end.poseTime), show.LaserFanAt(tail.poseTime));
        Assert.AreEqual(0f, show.LaserFanAt(double.NaN));
    }

    [Test]
    public void MorphUsesContinuousBeatDistanceAcrossTempoChangeAndOffset()
    {
        var chart = new ChartData { bpm = 120, offsetMs = 200,
            editorTempoMap = new List<ChartTempoPoint> {
                new ChartTempoPoint { beat = 0, timeMs = 0 },
                new ChartTempoPoint { beat = 2, timeMs = 1000 },
                new ChartTempoPoint { beat = 4, timeMs = 1500 }
            } };
        var show = Show(chart, Section(.95, 4, 1));
        Assert.AreEqual(0, show.LaserFanAt(.95));
        Assert.AreEqual(.5f, show.LaserFanAt(1.2), 1e-5f);
        Assert.AreEqual(1f, show.LaserFanAt(1.325), 1e-5f);
        Assert.AreEqual(show.LaserFanAt(1.2 - 1e-7), show.LaserFanAt(1.2 + 1e-7), 1e-5f);
        Assert.AreEqual(.216f, show.LaserFanAt(1.1), 1e-5f, "未来の評価後に戻っても同じ形");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SixLasersKeepBrightnessLengthAndCorridorWhileTheirShapeChanges(bool projection)
    {
        DisplaySettings.SetProjectorModeForTest(projection);
        var show = Show(new ChartData { bpm = 120 }, Section(10, 15, .94f), Section(20, 25, .95f));
        show.Tick(10.75, 0);
        var parallel = show.Mesh.vertices; var colors = show.Mesh.colors;
        Assert.GreaterOrEqual(parallel.Length, 48);
        show.Tick(20.75, 0);
        var fan = show.Mesh.vertices;
        Assert.GreaterOrEqual(fan.Length, 48);
        Assert.LessOrEqual(fan.Length, 144, "レーザー6本と流れる柱12本の2層だけ");
        for (int beam = 0; beam < 6; beam++)
        {
            Assert.AreEqual(18f, Direction(parallel, beam).magnitude, 1e-4f);
            Assert.AreEqual(18f, Direction(fan, beam).magnitude, 1e-4f);
            Assert.Greater(Vector3.Dot(Direction(parallel, beam).normalized,
                Direction(parallel, beam % 2).normalized), .99999f);
            for (int i = beam * 8; i < beam * 8 + 8; i++)
                Assert.AreEqual(colors[i], show.Mesh.colors[i], "形を変えても光量は増やさない");
        }
        foreach (var vertex in fan) Assert.GreaterOrEqual(Mathf.Abs(vertex.x), StageFeverShow.CorridorHalfWidth);
        for (int side = -1; side <= 1; side += 2)
            for (int k = 0; k < 3; k++)
            {
                var d = StageFeverShow.LaserDirection(1, 0, k, side);
                Assert.AreEqual(12f * (k + 1), Mathf.Atan2(side * d.x, d.y) * Mathf.Rad2Deg, 1e-4f);
                Assert.Greater(StageFeverShow.LaserDirection(1, -.2f, k, side).x * side, 0);
            }
        show.Tick(25, 0); var end = Direction(show.Mesh.vertices, 0).normalized;
        show.Tick(25.25, 0);
        Assert.Greater(Vector3.Dot(end, Direction(show.Mesh.vertices, 0).normalized), .99999f);
        show.Tick(25.5, 0); Assert.AreEqual(0, show.Mesh.vertexCount);
        DisplaySettings.SetReducedEffectsForTest(true);
        show.Tick(20.75, 0); Assert.AreEqual(0, show.Mesh.vertexCount);
    }

    static Vector3 Direction(Vector3[] vertices, int beam)
    {
        int i = beam * 8;
        return (vertices[i + 1] + vertices[i + 2] - vertices[i] - vertices[i + 3]) * .5f;
    }

    // 曲ごとに書いた強さのまま分ける(0.95 以上が扇、0.65〜0.95 未満は平行)。最高潮の数は曲によって 0〜4 か所。
    [TestCase("2_23_AM", new double[0], new[] { 65.333, 129.333 })]
    [TestCase("Andalusia", new[] { 86.358 }, new[] { 52.37 })]
    [TestCase("Aobasou", new[] { 61.714286, 82.285714, 109.714286 }, new[] { 6.857143 })]
    [TestCase("BenkyouRemaster", new[] { 73.674, 91.674, 119.531143 }, new[] { 6.024 })]
    [TestCase("ElDorado", new[] { 100.203 }, new[] { 52.203 })]
    [TestCase("Epilogue", new[] { 133.859 }, new[] { 62.631, 111.208 })]
    [TestCase("escapade", new[] { 25.283158, 50.546316, 65.704211, 85.914737 }, new[] { 10.125263 })]
    [TestCase("logic", new[] { 54.403, 70.403, 128.153, 144.153 }, new[] { 36.028 })]
    [TestCase("ManicSonic", new[] { 21.333333, 81.166667 }, new[] { 5.333333 })]
    [TestCase("Morning", new double[0], new[] { 40.678, 113.898 })]
    [TestCase("Phobia PARTⅠ", new[] { 89.106909 }, new[] { 49.106909 })]
    [TestCase("RushE", new[] { 112.22494 }, new[] { 40.05941, 87.676 })]
    [TestCase("揺籠", new[] { 140.245 }, new[] { 66.989, 83.733, 102.221 })]
    [TestCase("製作中", new[] { 134.713878 }, new[] { 48.999592 })]
    public void EverySongFansExactlyItsSectionsAtOrAboveTheClimaxStrength(string song, double[] climaxes, double[] parallel)
    {
        var timeline = StagePerformanceTimeline.Load(song);
        var sections = timeline.sections.Where(s => StageFeverShow.IsClimax(s.intensity)).ToArray();
        CollectionAssert.AreEqual(climaxes, sections.Select(s => s.startSeconds).ToArray());
        var strong = timeline.sections.Where(s => s.intensity >= StagePerformanceTimeline.StrongIntensity
            && !StageFeverShow.IsClimax(s.intensity)).ToArray();
        CollectionAssert.AreEqual(parallel, strong.Select(s => s.startSeconds).ToArray());
        var show = Show(new ChartData { bpm = 120 }, timeline.sections);
        foreach (var s in sections)
            Assert.AreEqual(1f, show.LaserFanAt((s.startSeconds + s.endSeconds) / 2), song);
        foreach (var s in strong)
            Assert.AreEqual(0f, show.LaserFanAt((s.startSeconds + s.endSeconds) / 2), song);
    }
}
