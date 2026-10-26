using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 29A・31A・32A・33A。実際のメッシュ・通知経路と境界値を読む。
public class Round2EffectsTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    bool low, projector;

    [SetUp] public void Setup()
    {
        low = DisplaySettings.ReducedEffects;
        projector = DisplaySettings.ProjectorMode;
        DisplaySettings.SetReducedEffectsForTest(false);
        DisplaySettings.SetProjectorModeForTest(false);
    }

    [TearDown] public void Cleanup()
    {
        for (int i = objects.Count - 1; i >= 0; i--)
            if (objects[i] != null) Object.DestroyImmediate(objects[i]);
        objects.Clear();
        DisplaySettings.SetReducedEffectsForTest(low);
        DisplaySettings.SetProjectorModeForTest(projector);
    }

    GameObject Go(string name)
    {
        var go = new GameObject(name);
        objects.Add(go);
        return go;
    }

    CuttableNote Note(int count = 1)
    {
        var note = Go("Round2Note").AddComponent<CuttableNote>();
        note.RequiredCutCount = note.RemainingCuts = count;
        return note;
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void CutMaskUsesTwelvePixelsAndOnlyOldRingsAndSparks(bool reduced, bool projection)
    {
        DisplaySettings.SetReducedEffectsForTest(reduced);
        DisplaySettings.SetProjectorModeForTest(projection);
        var owner = Go("Round2Owner").AddComponent<NoteSpawner>();
        var feedback = GameplayCutFeedback.Create(owner);
        var camera = Go("Round2Camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -10);
        camera.orthographic = true; camera.orthographicSize = 2;
        camera.pixelRect = new Rect(100, 50, 800, 400); camera.aspect = 2;
        var note = Note();
        note.gameObject.AddComponent<MeshFilter>().sharedMesh = NoteFragmentPool.CubeMesh;
        note.gameObject.AddComponent<MeshRenderer>();
        note.HitTime = .24;
        feedback.Track(note);
        feedback.PrepareProtection(camera, 0);
        Assert.AreEqual(1, feedback.ProtectionCount);
        Vector4 r = feedback.ProtectionRect(0);
        Assert.AreEqual(.5 - .0625 - 12.0 / camera.pixelWidth, r.x, 1e-5);
        Assert.AreEqual(.5 - .125 - 12.0 / camera.pixelHeight, r.y, 1e-5);
        Assert.AreEqual(1 - r.x, r.z, 1e-5);
        Assert.AreEqual(1 - r.y, r.w, 1e-5);
        note.HitTime = .240002;
        feedback.PrepareProtection(camera, 0);
        Assert.AreEqual(0, feedback.ProtectionCount);
        note.HitTime = -.000002;
        feedback.PrepareProtection(camera, 0);
        Assert.AreEqual(0, feedback.ProtectionCount);
        note.HitTime = 0;
        feedback.PrepareProtection(camera, 0);
        Assert.AreEqual(1, feedback.ProtectionCount);
        // MarkMiss の暗くする処理は、NoteVisuals の無いノーツでは renderer.material を使う(本編のノーツには NoteVisuals がある)。
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"Instantiating material due to calling renderer\.material during edit mode"));
        note.MarkMiss();
        feedback.PrepareProtection(camera, 0);
        Assert.AreEqual(0, feedback.ProtectionCount);

        var burst = new GameplayCutFeedback.StaticBurst {
            tier = JudgmentTier.Perfect, color = Color.red, direction = Vector2.right,
            scale = 1, release = 1, age = .069f
        };
        var uv = new List<Vector2>();
        var mesh = feedback.GetComponent<MeshFilter>().sharedMesh;
        feedback.DrawBursts(new[] { burst });
        mesh.GetUVs(1, uv);
        Assert.True(uv.All(v => v.x == 0));
        burst.age = .07f;
        feedback.DrawBursts(new[] { burst });
        mesh.GetUVs(1, uv);
        // 光1 で Perfect の白い十字は 0.10 秒まで残るので、0.07 秒からは十字の2枚も保護の対象になる(LOW は十字なし)。
        Assert.AreEqual(((reduced ? 1 : 2) * GameplayCutFeedback.RingSegments
            + (reduced ? 2 : GameplayCutFeedback.SparkCount) + (reduced ? 0 : 2)) * 4, uv.Count(v => v.x == 1));
        Assert.True(uv.Take(8).All(v => v.x == 0), "切断の帯には保護マスクを掛けない");
        Assert.False(ShaderUtil.ShaderHasError(feedback.GetComponent<MeshRenderer>().sharedMaterial.shader));
    }

    [Test]
    public void CutMaskHasNoFixedNoteLimitAndClearsOnChartReset()
    {
        var owner = Go("Round2Owner").AddComponent<NoteSpawner>();
        var feedback = GameplayCutFeedback.Create(owner);
        var camera = Go("Round2Camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -10);
        for (int i = 0; i < 130; i++)
        {
            var note = Note();
            note.gameObject.AddComponent<MeshFilter>().sharedMesh = NoteFragmentPool.CubeMesh;
            note.gameObject.AddComponent<MeshRenderer>();
            note.HitTime = .1;
            feedback.Track(note);
        }
        feedback.PrepareProtection(camera, 0);
        Assert.AreEqual(130, feedback.ProtectionCount, "上限で保護対象を落とさない");
        feedback.ResetState();
        feedback.PrepareProtection(camera, 0);
        Assert.AreEqual(0, feedback.ProtectionCount);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void OnlyTheNewCrackGrowsFromTheHitAndItsClockDoesNotRestart(bool reduced, bool projection)
    {
        DisplaySettings.SetReducedEffectsForTest(reduced);
        DisplaySettings.SetProjectorModeForTest(projection);
        var note = Note(4);
        Vector3 hit = new Vector3(.2f, -.1f, 0);
        bool sawStart = false;
        note.OnPartialCut += (n, index, count) =>
        {
            if (index != 0) return;
            sawStart = true;
            Assert.AreEqual(0, n.transform.Find("Crack").localScale.y, "刻み音を通知する時点で伸長も開始済み");
        };
        note.Cut(hit, Vector3.right * 8);
        Assert.True(sawStart);
        var first = note.transform.Find("Crack");
        var material = first.GetComponent<MeshRenderer>().sharedMaterial;
        Assert.AreEqual(.2f, first.localPosition.x, 1e-6);
        Assert.AreEqual(-.1f, first.localPosition.y, 1e-6);
        float full = CuttableNote.CrackScale(0, 1f / 3f).y;
        note.TickCracks(.04f);
        Assert.AreEqual(full * .5f, first.localScale.y, 1e-6);
        note.TickCracks(.04f);
        Assert.AreEqual(full, first.localScale.y, 1e-6);
        note.TickCracks(.06f);
        int color = Shader.PropertyToID("_BaseColor");
        Assert.AreEqual(reduced ? .7f : 1f, material.GetColor(color).r, 1e-5);
        note.TickCracks(.06f);
        Assert.AreEqual(reduced ? .7f : .85f, material.GetColor(color).r, 1e-5);
        note.TickCracks(.06f);
        Assert.AreEqual(.7f, material.GetColor(color).r, 1e-5);
        Vector3 settled = first.localScale;
        note.TickCracks(1f);
        first.hasChanged = false;
        note.TickCracks(1f);
        Assert.False(first.hasChanged, "終了済みのひびへスケールを再設定しない");
        Assert.AreEqual(settled, first.localScale);
        Assert.AreEqual(.7f, material.GetColor(color).r, 1e-5);
        note.Cut(hit, Vector3.up * 8);
        Assert.AreEqual(settled, first.localScale);
        Assert.AreEqual(.7f, material.GetColor(color).r, 1e-5);
        Assert.AreEqual(0, note.transform.GetChild(1).localScale.y);
        note.ResetForSpawn();
        Assert.False(first.gameObject.activeSelf);
        note.RequiredCutCount = note.RemainingCuts = 4;
        note.Cut(hit, Vector3.right * 8);
        Assert.AreEqual(0, first.localScale.y);
        Assert.AreEqual(reduced ? .7f : 1f, material.GetColor(color).r, 1e-5);
    }

    [TestCase(.060, true)]
    [TestCase(.060001, false)]
    [TestCase(-.060, true)]
    public void GoldGroupsKeepChartAndCutTimeBoundariesSeparate(double delta, bool paired)
    {
        var gate = new GoldChordGate();
        int first = gate.Register(10);
        Assert.AreEqual(first, gate.Register(10.001));
        Assert.AreNotEqual(first, gate.Register(10.001002));
        var a = gate.Decide(first, 20, 1);
        Assert.True(a.main && a.coin && !a.bell);
        var b = gate.Decide(first, 20 + delta, 2);
        Assert.AreEqual(!paired, b.main);
        Assert.AreEqual(paired, b.bell);
        Assert.False(b.coin);
        Assert.AreEqual(paired ? 1 : 2, b.step, "低い鐘は最初の主音の段を基準にする");
        if (paired)
        {
            var third = gate.Decide(first, 20, 2);
            Assert.False(third.main || third.bell || third.coin, "3個以上でも低い鐘を積み重ねない");
        }
        gate.Reset();
        Assert.True(gate.Decide(gate.Register(10), 20, 0).coin);
    }

    CuttableNote Gold(GoldNoteSfx sound, NoteSpawner owner, double hit)
    {
        var note = Note();
        note.IsGold = true; note.HitTime = hit; note.JudgmentOwner = owner;
        typeof(GoldNoteSfx).GetMethod("HandleSpawned", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(sound, new object[] { note });
        note.OnCut += (n, point, velocity) => n.NotifyJudgment(JudgmentTier.Perfect, point, velocity);
        return note;
    }

    [Test]
    public void GoldNotificationKeepsBothCutsButPlaysOneMainOneBellAndOneCoin()
    {
        var owner = Go("Round2Owner").AddComponent<NoteSpawner>();
        var sound = Go("Round2Gold").AddComponent<GoldNoteSfx>();
        sound.Bind(owner);
        int visuals = 0;
        sound.OnCritical += (point, step) => visuals++;
        var a = Gold(sound, owner, 10);
        var b = Gold(sound, owner, 10.001);
        a.CutAtSongTime(Vector3.zero, Vector3.up * 8, CutDirection.None, SaberHand.Any, 10);
        b.CutAtSongTime(Vector3.zero, Vector3.up * 8, CutDirection.None, SaberHand.Any, 10.06);
        Assert.AreEqual(2, sound.CriticalCount);
        Assert.AreEqual(2, visuals);
        Assert.AreEqual(1, sound.MainSoundCount);
        Assert.AreEqual(1, sound.PairBellCount);
        Assert.AreEqual(1, sound.CoinCount);
        Assert.AreEqual(1, sound.CriticalStep);
        // 別の譜面に同じ時刻があっても、前の組を持ち越さない。
        owner.SetChart(new ChartData());
        var c = Gold(sound, owner, 10);
        c.CutAtSongTime(Vector3.zero, Vector3.up * 8, CutDirection.None, SaberHand.Any, 10);
        Assert.AreEqual(2, sound.MainSoundCount);
        Assert.AreEqual(2, sound.CoinCount);
    }

    [TestCase(0, 79)]
    [TestCase(1, 83)]
    [TestCase(2, 86)]
    public void GoldPairBellIsAnOctaveLowerForExactlyPoint18Seconds(int step, int midi)
    {
        var data = FlourishSfx.GoldPairBell(step);
        Assert.AreEqual(7938, data.Length);
        Assert.AreEqual(-22, SfxLab.PeakDb(data), .01);
        double hz = SfxLab.Midi(midi);
        Assert.Greater(Magnitude(data, hz), Magnitude(data, hz * 2) * 3);
        Assert.AreEqual(0, data[data.Length - 1]);
        var coin = FlourishSfx.GoldCoinDelayed();
        Assert.AreEqual(28665, coin.Length);
        Assert.True(coin.Take(6615).All(x => x == 0));
        Assert.Greater(SfxLab.Peak(coin), 0);
    }

    static double Magnitude(float[] data, double hz)
    {
        double a = 0, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            double phase = 2 * Math.PI * hz * i / SfxLab.Rate;
            a += data[i] * Math.Cos(phase); b += data[i] * Math.Sin(phase);
        }
        return Math.Sqrt(a * a + b * b);
    }

    StageFeverShow Show(params StagePerformanceTimeline.Section[] sections)
    {
        var show = Go("Round2Stage").AddComponent<StageFeverShow>();
        show.Setup(StageTheme.PulseArray, -2.5f,
            new StagePerformanceTimeline { sections = sections }, new ChartData { bpm = 120 });
        return show;
    }

    static StagePerformanceTimeline.Section Section(double start, double end) =>
        new StagePerformanceTimeline.Section { startSeconds = start, endSeconds = end, intensity = 1 };

    [TestCase(false)]
    [TestCase(true)]
    public void LaserTipReturnsToItsRootAndWidthWaitsPointThreeSeconds(bool projection)
    {
        DisplaySettings.SetProjectorModeForTest(projection);
        var show = Show(Section(10, 20));
        show.Tick(20, 0);
        var start = show.Mesh.vertices;
        float width = (start[3] - start[0]).magnitude;
        show.Tick(20.25, 0);
        var half = show.Mesh.vertices;
        Vector3 root = (half[0] + half[3]) * .5f;
        Vector3 tip = (half[1] + half[2]) * .5f;
        Assert.AreEqual(9f, (tip - root).magnitude, 1e-4);
        Assert.AreEqual(width, (half[3] - half[0]).magnitude, 1e-5);
        Assert.Less((root - (start[0] + start[3]) * .5f).magnitude, 1e-5);
        show.Tick(20.4, 0);
        var tail = show.Mesh.vertices;
        Assert.AreEqual(width * .5f, (tail[3] - tail[0]).magnitude, 1e-5);
        Assert.AreEqual(3.6f, ((tail[1] + tail[2] - tail[0] - tail[3]) * .5f).magnitude, 1e-4);
        show.Tick(15, 0);
        show.Tick(20.4, 0);
        CollectionAssert.AreEqual(tail, show.Mesh.vertices, "シークしても同じ曲時刻なら同じ形");
        show.Tick(20.5, 0);
        Assert.AreEqual(0, show.Mesh.vertexCount);
        DisplaySettings.SetReducedEffectsForTest(true);
        show.Tick(20.25, 0);
        Assert.AreEqual(0, show.Mesh.vertexCount, "LOWでは巻き取り用のレーザーも追加しない");
    }

    [Test]
    public void TouchingAndOverlappingChorusesDoNotRetractAtAnInternalEnd()
    {
        var show = Show(Section(20, 30), Section(10, 20), Section(15, 25));
        show.Tick(19.99, 0);
        show.Tick(20.15, 0);
        Assert.AreEqual(0, show.EndSoundCount);
        Assert.AreEqual(1f, show.Fever);
        Assert.AreEqual(18f, show.LaserAt(20.15).length);
        Assert.AreEqual(1f, show.LaserAt(20.15).width);
        show.Tick(29.99, 0);
        show.Tick(30, 0);
        Assert.AreEqual(1, show.EndSoundCount);
        Assert.AreEqual(9f, show.LaserAt(30.25).length, 1e-5);
        show.ResetState();
        Assert.AreEqual(0, show.Mesh.vertexCount);
    }
}
