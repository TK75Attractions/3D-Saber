using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

// コンボ数の後ろの炎: AP中は虹色、FC中は金色、曲の進行で強くなり、条件が崩れたら消える(AP→FC は虹が消えてから金)。
public class ComboFlameTests
{
    readonly List<GameObject> created = new List<GameObject>();
    bool reduced;

    [SetUp] public void Remember() { reduced = DisplaySettings.ReducedEffects; }
    [TearDown]
    public void Cleanup()
    {
        DisplaySettings.SetReducedEffectsForTest(reduced);
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
    }

    [TestCase(0, 0, 0, 0, ComboFlameMode.None)]
    [TestCase(5, 5, 0, 0, ComboFlameMode.Rainbow)]
    [TestCase(5, 4, 0, 0, ComboFlameMode.Gold)]
    [TestCase(5, 5, 1, 0, ComboFlameMode.None)]
    [TestCase(5, 5, 0, 1, ComboFlameMode.None)]
    public void DesiredMode_RainbowForAllPerfect_GoldForFullCombo_NoneOtherwise(int hits, int perfect, int bad, int miss, ComboFlameMode expected)
    {
        Assert.AreEqual(expected, ComboFlameLogic.DesiredMode(hits, perfect, bad, miss));
    }

    [Test]
    public void Intensity_GrowsWithSongProgressAndClamps()
    {
        Assert.AreEqual(0f, ComboFlameLogic.Intensity(0, 120));
        Assert.AreEqual(.5f, ComboFlameLogic.Intensity(60, 120), 1e-5f);
        Assert.AreEqual(1f, ComboFlameLogic.Intensity(200, 120));
        Assert.AreEqual(0f, ComboFlameLogic.Intensity(30, 0), "曲の長さが無ければ燃えない");
    }

    [Test]
    public void Envelope_RisesThenExtinguishesBeforeChangingColor_AndDiesWhenConditionBreaks()
    {
        var envelope = new ComboFlameEnvelope();
        envelope.Tick(ComboFlameMode.Rainbow, .1f);
        Assert.AreEqual(ComboFlameMode.Rainbow, envelope.Shown);
        Assert.Greater(envelope.Level, 0f, "AP 中はすぐ虹が立ち上がる");
        for (int i = 0; i < 10; i++) envelope.Tick(ComboFlameMode.Rainbow, .1f);
        Assert.AreEqual(1f, envelope.Level, 1e-5f);

        // Great を出して AP が崩れ FC は維持: 虹が一度消えてから金が立ち上がる。
        envelope.Tick(ComboFlameMode.Gold, .1f);
        Assert.AreEqual(ComboFlameMode.Rainbow, envelope.Shown, "消えるまでは虹のまま");
        Assert.Less(envelope.Level, 1f);
        for (int i = 0; i < 4; i++) envelope.Tick(ComboFlameMode.Gold, .1f);
        Assert.AreEqual(ComboFlameMode.Gold, envelope.Shown, "消えた後は金へ切り替わる");
        for (int i = 0; i < 6; i++) envelope.Tick(ComboFlameMode.Gold, .1f);
        Assert.AreEqual(1f, envelope.Level, 1e-5f, "金が同じ強さまで立ち上がる");

        // Miss で FC も崩れる: 消えて戻らない。
        for (int i = 0; i < 6; i++) envelope.Tick(ComboFlameMode.None, .1f);
        Assert.AreEqual(ComboFlameMode.None, envelope.Shown);
        Assert.AreEqual(0f, envelope.Level);
        envelope.Tick(ComboFlameMode.None, 1f);
        Assert.AreEqual(0f, envelope.Level);
    }

    static int Vertices(ComboFlameMode mode, float intensity, float level, bool reduced)
    {
        using (var vh = new VertexHelper())
        {
            ComboFlameLogic.Fill(vh, mode, intensity, level, 1.2f, ComboFlameLogic.FlameWidth(3), reduced);
            return vh.currentVertCount;
        }
    }

    [Test]
    public void Fill_GrowsWithIntensity_LowModeUsesFewerTongues_NoneDrawsNothing()
    {
        int weak = Vertices(ComboFlameMode.Rainbow, 0f, 1f, false);
        int strong = Vertices(ComboFlameMode.Rainbow, 1f, 1f, false);
        int low = Vertices(ComboFlameMode.Rainbow, 1f, 1f, true);
        Assert.Greater(weak, 0, "序盤でも小さく燃える");
        Assert.Greater(strong, weak, "曲が進むほど炎の本数が増える");
        Assert.Greater(low, 0); Assert.Less(low, strong, "LOW は本数を減らす");
        Assert.AreEqual(0, Vertices(ComboFlameMode.None, 1f, 1f, false), "条件外では何も描かない");
        Assert.AreEqual(0, Vertices(ComboFlameMode.Gold, 1f, 0f, false), "消えた後は何も描かない");
        Assert.AreEqual(ComboFlameLogic.TongueCount(1f, false) * ComboFlameLogic.Layers * (ComboFlameLogic.Segments + 1) * 2, strong);
    }

    [Test]
    public void Presenter_ShowsRainbowWhileAllPerfect_ThenGoldAfterGreat_ThenNothingAfterMiss()
    {
        var owner = new GameObject("owner"); created.Add(owner);
        var spawner = owner.AddComponent<NoteSpawner>();
        var score = owner.AddComponent<ScoreManager>();
        var presenter = GameplayFeedbackPresenter.Create(spawner, score); created.Add(presenter.gameObject);
        Assert.IsNotNull(presenter.GetComponentInChildren<ComboFlameGraphic>(), "炎のグラフィックが HUD 数字の下に用意される");
        Assert.AreEqual(499, presenter.GetComponentInChildren<ComboFlameGraphic>().GetComponentInParent<Canvas>().sortingOrder, "コンボ数字(500)の下に描く");

        presenter.Tick(.1f, 10, 100, 90);
        Assert.AreEqual(ComboFlameMode.None, presenter.FlameMode, "判定が無い間は燃えない");
        score.RegisterHit(JudgmentTier.Perfect); score.RegisterHit(JudgmentTier.Perfect);
        presenter.Tick(.1f, 20, 100, 90);
        Assert.AreEqual(ComboFlameMode.Rainbow, presenter.FlameMode);
        score.RegisterHit(JudgmentTier.Great);
        for (int i = 0; i < 6; i++) presenter.Tick(.1f, 30, 100, 90);
        Assert.AreEqual(ComboFlameMode.Gold, presenter.FlameMode, "Great で AP が崩れると金になる");
        score.RegisterHit(JudgmentTier.Miss);
        for (int i = 0; i < 6; i++) presenter.Tick(.1f, 40, 100, 90);
        Assert.AreEqual(ComboFlameMode.None, presenter.FlameMode, "Miss で FC も崩れると消える");
        Assert.AreEqual(0f, presenter.FlameLevel);
    }
}
