using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class DopamineFlourishTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly List<GameObject> objects = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in objects)
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
        objects.Clear();
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    [Test]
    public void PerfectChangesWithoutChangingGreatOrGood()
    {
        var perfect = GameplayCutFeedback.ProfileFor(JudgmentTier.Perfect, false);
        var great = GameplayCutFeedback.ProfileFor(JudgmentTier.Great, false);
        var good = GameplayCutFeedback.ProfileFor(JudgmentTier.Good, false);
        Assert.AreEqual((20, 10, 2, 2f), perfect);
        Assert.AreEqual((8, 0, 1, .85f), great);
        Assert.AreEqual((4, 0, 1, .62f), good);
        Assert.AreEqual(.36f, GameplayCutFeedback.LifetimeFor(JudgmentTier.Perfect));
        Assert.AreEqual(.24f, GameplayCutFeedback.LifetimeFor(JudgmentTier.Great));
        Assert.AreEqual(.24f, GameplayCutFeedback.LifetimeFor(JudgmentTier.Good));
        Assert.AreEqual(.10f, (float)typeof(GameplayCutFeedback).GetField("FlashLifetime",
            BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue());
        var low = GameplayCutFeedback.ProfileFor(JudgmentTier.Perfect, true);
        Assert.AreEqual(2, low.sparks);
        Assert.AreEqual(0, low.shards);
        Assert.AreEqual(1, low.rings);
    }

    [Test]
    public void SixteenBurstsFitAndFifthNewBurstShrinks()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        var root = new GameObject("DopamineCutTest");
        objects.Add(root);
        var owner = root.AddComponent<NoteSpawner>();
        var view = GameplayCutFeedback.Create(owner);
        var items = new List<GameplayCutFeedback.StaticBurst>();
        for (int i = 0; i < 24; i++)
            items.Add(new GameplayCutFeedback.StaticBurst {
                tier = JudgmentTier.Perfect, position = new Vector3(i * 4, 0, 0),
                direction = Vector2.right, color = Color.cyan, scale = 1, release = 1, age = .12f
            });
        view.DrawBursts(items);
        Assert.AreEqual(16, view.ActiveCount);
        var bursts = (Array)typeof(GameplayCutFeedback).GetField("bursts", Private).GetValue(view);

        float Scale(int index)
        {
            object item = bursts.GetValue(index);
            return (float)item.GetType().GetField("scale").GetValue(item);
        }

        Assert.AreEqual(1, Scale(3));
        Assert.AreEqual(.8f, Scale(4));
        Assert.AreEqual(.8f, Scale(15));
        Assert.LessOrEqual(view.GetComponent<MeshFilter>().sharedMesh.vertexCount,
            16 * GameplayCutFeedback.MaximumVerticesPerBurst);
        view.Tick(.20f);
        Assert.AreEqual(16, view.ActiveCount);
        view.Tick(.05f);
        Assert.AreEqual(0, view.ActiveCount);
    }

    [Test]
    public void PerfectBagNeverRepeatsEvenAtTheBagBoundary()
    {
        var a = new PerfectCutBag("song", "run");
        var b = new PerfectCutBag("song", "run");
        int previous = -1;
        for (int group = 0; group < 100; group++)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < 4; i++)
            {
                int next = a.Next();
                Assert.AreEqual(next, b.Next());
                Assert.AreNotEqual(previous, next);
                Assert.True(seen.Add(next));
                previous = next;
            }
        }
    }

    static double Rms(float[] samples)
    {
        double sum = 0;
        foreach (float value in samples) sum += value * value;
        return Math.Sqrt(sum / samples.Length);
    }

    [Test]
    public void PerfectVariantsKeepTheOriginalPeakLoudnessAndDurationLimits()
    {
        var original = Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut_Perfect");
        Assert.NotNull(original);
        Assert.AreEqual(1, original.channels);
        var samples = new float[original.samples];
        Assert.True(original.GetData(samples, 0));
        FlourishSfx.PreparePerfectSource();
        FlourishSfx.PrewarmGameplay();
        for (int i = 0; i < 4; i++)
        {
            Assert.True(FlourishSfx.TryGetCached(FlourishSfx.PerfectCutName(i), out var variant));
            Assert.That(Math.Abs(FlourishSfx.PerfectSemitones(i)), Is.LessThanOrEqualTo(1));
            Assert.That(variant.Length / (double)samples.Length, Is.InRange(.9, 1.1));
            Assert.AreEqual(SfxLab.Peak(samples), SfxLab.Peak(variant), 1e-5);
            Assert.AreEqual(Rms(samples), Rms(variant), 1e-5);
            Assert.AreSame(variant, FlourishSfx.Cached(FlourishSfx.PerfectCutName(i),
                () => throw new AssertionException("再生時に合成しない")));
        }
        for (int tonic = 0; tonic < 12; tonic++)
            for (int minor = 0; minor < 2; minor++)
                Assert.True(FlourishSfx.TryGetCached(
                    FlourishSfx.FinaleName(new VoltageMusicKey(tonic, minor != 0)), out _));
    }

    [Test]
    public void GoldKeepsOrdinarySuccessAndExpiresAfterFourTempoMappedBeats()
    {
        var map = new ChartTempoMap(120, 0, new[] {
            new ChartTempoPoint { beat = 0, timeMs = 0 },
            new ChartTempoPoint { beat = 2, timeMs = 1000 },
            new ChartTempoPoint { beat = 6, timeMs = 2000 }
        });
        var stairs = new GoldStaircase();
        stairs.Gold(map.BeatAt(500), true);
        stairs.Judge(JudgmentTier.Perfect);
        stairs.Judge(JudgmentTier.Great);
        stairs.Judge(JudgmentTier.Good);
        Assert.AreEqual(1, stairs.Count);
        stairs.Tick(map.BeatAt(1749));
        Assert.AreEqual(1, stairs.Count);
        // 同じ曲時刻を何回渡しても期限を進めない。
        for (int i = 0; i < 100; i++) stairs.Tick(map.BeatAt(1749));
        Assert.AreEqual(1, stairs.Count);
        stairs.Tick(map.BeatAt(1750));
        Assert.AreEqual(0, stairs.Count);
        stairs.Gold(8, true);
        stairs.Gold(9, false);
        stairs.Tick(12);
        Assert.AreEqual(1, stairs.Count, "金の Good は期限だけ延ばす");
        stairs.Judge(JudgmentTier.Miss);
        Assert.AreEqual(0, stairs.Count);
        stairs.Gold(13, true);
        stairs.Judge(JudgmentTier.Bad);
        Assert.AreEqual(0, stairs.Count);
    }

    [Test]
    public void OneGoldGroupAdvancesOnlyOnceEvenOutsideThePairSoundWindow()
    {
        var gate = new GoldChordGate();
        var stairs = new GoldStaircase();
        int group = gate.Register(10);
        Assert.AreEqual(group, gate.Register(10.001));
        foreach (double time in new[] { 10.0, 10.04, 10.2 })
        {
            var decision = gate.Decide(group, time, stairs.NextStep);
            stairs.Gold(time * 2, decision.advance);
        }
        Assert.AreEqual(1, stairs.Count);
        for (int i = 1; i <= 5; i++) stairs.Gold(21 + i, true);
        Assert.AreEqual(3, stairs.Count);
        Assert.AreEqual(2, stairs.NextStep);
    }

    [Test]
    public void MilestoneLotteryIsRepeatableNeverRepeatsAndCapsJackpotsAtTwo()
    {
        var a = new MilestoneLottery("song", "run");
        var b = new MilestoneLottery("song", "run");
        int previous = -1, jackpots = 0;
        for (int i = 0; i < 1000; i++)
        {
            var x = a.Next();
            var y = b.Next();
            Assert.AreEqual(x.Combination, y.Combination);
            Assert.AreEqual(x.Jackpot, y.Jackpot);
            Assert.AreNotEqual(previous, x.Combination);
            previous = x.Combination;
            Assert.That(x.Shape, Is.InRange(0, 2));
            Assert.That(x.Color, Is.InRange(0, 2));
            Assert.That(x.Inversion, Is.InRange(0, 2));
            if (x.Jackpot) jackpots++;
            Assert.LessOrEqual(jackpots, 2);
        }
        Assert.AreEqual(2, jackpots);
    }

    [Test]
    public void FinalGroupIncludesLongEndsOffsetsAndSimultaneousNotes()
    {
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, count = 3, lengthMs = 2000 });
        chart.notes.Add(new NoteData { time = 3000 });
        chart.notes.Add(new NoteData { time = 2500 });
        chart.notes.Add(new NoteData { time = 1599.5f, count = 3 });
        var group = new FinalNoteGroup(chart, .25, .7f);
        Assert.AreEqual(3.25, group.LastTime, 1e-6);
        CollectionAssert.AreEqual(new[] { 0, 1, 3 }, group.Indices);
        Assert.False(group.Contains(3.248));
        Assert.AreEqual(0, new FinalNoteGroup(new ChartData(), 0, .7f).Indices.Count);
    }

    // 2026-10-28 夜の全件テスト: 大きい光(FinalStageLight)は舞台の下に置くので、舞台ごと壊れたらメッシュと材質も壊す。
    // 壊れたあとに持ち主が描いても・片付けても例外にしない。
    [Test]
    public void ExcitementMeshDiesWithTheObjectItIsPlacedUnder()
    {
        var stage = new GameObject("Stage");
        objects.Add(stage);
        var light = new PlayExcitementMesh(stage.transform, "FinalStageLight", 3000, -190);
        Assume.That(light.Available, "PlayExcitement のシェーダーが読める");
        var filter = stage.GetComponentInChildren<MeshFilter>(true);
        Mesh mesh = filter.sharedMesh;
        Material material = filter.GetComponent<MeshRenderer>().sharedMaterial;
        UnityEngine.Object.DestroyImmediate(stage);
        Assert.IsTrue(mesh == null, "舞台ごと壊れたらメッシュも壊す");
        Assert.IsTrue(material == null, "材質も壊す");
        Assert.IsFalse(light.Available);
        light.Clear(); light.Quad(Vector3.zero, Vector3.right, Vector3.one, Vector3.up, Color.white, true);
        Assert.DoesNotThrow(() => { light.Flush(); light.Hide(); light.Dispose(); });
    }
}
