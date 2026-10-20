using NUnit.Framework;
using UnityEngine;

// 派手さカタログ(2026-10-20)の効果音と、選曲・幕の音の流れ(響1・盤1・盤2・盤3・盤5・幕1)。
public class FlourishSfxTests
{
    // 150Hz 未満(台Bのスピーカーで出ない帯域)が音全体のエネルギーに占める割合。
    static float Below150Share(float[] x)
    {
        var low = SfxLab.Lp(SfxLab.Lp(x, 150), 150);
        double l = 0, all = 0;
        for (int i = 0; i < x.Length; i++) { l += low[i] * low[i]; all += x[i] * x[i]; }
        return (float)(l / System.Math.Max(1e-12, all));
    }

    [Test]
    public void NewDonKeepsItsWeightAbove150HzAndCanSwitchBack()
    {
        var legacy = ProceduralSfx.LegacyDon();
        var harmonic = ProceduralSfx.Don();
        Assert.Less(Below150Share(harmonic), .2f, "倍音型のドンは 150Hz 未満が2割未満(試作は12%)");
        Assert.Greater(Below150Share(legacy), .3f, "前のドンは 150Hz 未満が多い(42%)");
        Assert.AreEqual(.9f, SfxLab.Peak(harmonic), .01f, "ピークは前と同じ .9");
        try
        {
            ProceduralSfx.UseLegacyDon = true;
            CollectionAssert.AreEqual(legacy, ProceduralSfx.Don(), "会場で聞き比べるまで前の音に戻せる");
        }
        finally { ProceduralSfx.UseLegacyDon = false; }
    }

    [Test]
    public void SoundsAreDeterministicAndBakedAtTheirLevels()
    {
        CollectionAssert.AreEqual(FlourishSfx.StartHit(), FlourishSfx.StartHit(), "同じ種なら同じ音");
        Assert.AreEqual(-4, SfxLab.PeakDb(FlourishSfx.StartHit()), .2);
        Assert.AreEqual(-11, SfxLab.PeakDb(FlourishSfx.Fire()), .2);
        Assert.AreEqual(-10, SfxLab.PeakDb(FlourishSfx.DiscMove(true)), .2);
        Assert.AreEqual(-12, SfxLab.PeakDb(FlourishSfx.CurtainSwish(false)), .2);
        Assert.AreEqual(-6, SfxLab.PeakDb(FlourishSfx.CrossCut()), .2);
        Assert.AreEqual(-7, SfxLab.PeakDb(FlourishSfx.GoldCut(0)), .2);
        Assert.AreEqual(-9, SfxLab.PeakDb(FlourishSfx.TimerTick(3)), .2, "最後の3秒は大きく");
        Assert.Less(SfxLab.PeakDb(FlourishSfx.TimerTick(10)), -13, "10〜4秒は 5dB 小さい");
        Assert.AreEqual(.42 * SfxLab.Rate, FlourishSfx.CurtainSwish(true).Length, 2, "幕が閉じる 0.42 秒");
    }

    // ユーザー指摘「溜め後の音が他と同じすぎる」: ためた後の音には、ほかの決めの音に共通する低い「ドン」を入れない。
    [Test]
    public void PostChargeSoundsAvoidTheSharedLowThump()
    {
        Assert.Less(Below150Share(FlourishSfx.Fire()), .01f);
        Assert.Less(Below150Share(FlourishSfx.StartHit()), .02f);
        Assert.Greater(Below150Share(FlourishSfx.ChorusDrop()), Below150Share(FlourishSfx.StartHit()) * 2, "サビ入りの音とは低音の有無で分かれる");
    }

    [Test]
    public void ChargeRisesOverItsHoldAndMilestonesClimb()
    {
        var charge = FlourishSfx.Charge(1);
        float early = 0, late = 0;
        for (int i = 0; i < SfxLab.Rate / 5; i++) early = Mathf.Max(early, Mathf.Abs(charge[i]));
        for (int i = SfxLab.Rate * 4 / 5; i < SfxLab.Rate; i++) late = Mathf.Max(late, Mathf.Abs(charge[i]));
        Assert.Greater(late, early * 2, "ためるほど大きくなる");
        Assert.AreEqual(2.02 * SfxLab.Rate, FlourishSfx.StartCharge().Length, 2, "スタートは2秒のため");
        Assert.AreEqual(FlourishSfx.Milestone(3).Length, FlourishSfx.Milestone(9).Length, "4段目で上限");
        CollectionAssert.AreEqual(FlourishSfx.Milestone(3), FlourishSfx.Milestone(9));
    }

    [Test]
    public void CachedBuildsOnce()
    {
        int built = 0;
        var a = FlourishSfx.Cached("TestCachedBuildsOnce", () => { built++; return new float[] { 1 }; });
        var b = FlourishSfx.Cached("TestCachedBuildsOnce", () => { built++; return new float[] { 2 }; });
        Assert.AreSame(a, b);
        Assert.AreEqual(1, built);
    }

    [Test]
    public void MenuSoundsAreRecordedWithoutCreatingClipsOutsidePlay()
    {
        MenuSfx.DiscMove(true);
        Assert.AreEqual("DiscRight", MenuSfx.LastPlayed);
        MenuSfx.Difficulty(5);
        Assert.AreEqual("Difficulty2", MenuSfx.LastPlayed, "難易度は 0〜2 に収める");
        MenuSfx.Curtain(ScreenTransition.Style.Back);
        Assert.AreEqual("CurtainBack", MenuSfx.LastPlayed, "戻る幕は低い音");
        MenuSfx.Curtain(ScreenTransition.Style.Result);
        Assert.AreEqual("Curtain", MenuSfx.LastPlayed);
        Assert.IsNull(MenuSfx.ChargeClip(1, false), "プレイ中でなければ AudioClip を作らない");
        Assert.IsNull(Object.FindFirstObjectByType<MenuSfx>(), "EditMode では再生役を作らない");
    }

    [Test]
    public void CurtainSwishEndsWhenTheCurtainHasClosed()
    {
        Assert.AreEqual(0, ScreenTransition.SwishTime(false, ScreenTransition.CloseDuration), "ふつうの幕は閉じ始めに鳴らす");
        Assert.AreEqual(.5f, ScreenTransition.SwishTime(true, .92f), 1e-4f, "タイトルの前進演出は閉じきる 0.42 秒前から");
        float sequence = SongSelectController.StartSequenceSeconds;
        Assert.AreEqual(SongSelectController.StartHoldSeconds, ScreenTransition.SwishTime(true, sequence), 1e-4f, "スタートは 0.3 秒後に閉じ始める");
    }

    [Test]
    public void StartPreviewFadesToMinusSixThenSilence()
    {
        Assert.AreEqual(1f, SongSelectController.StartPreviewGain(0), 1e-4f);
        Assert.AreEqual(.5f, SongSelectController.StartPreviewGain(SongSelectController.StartPreviewFadeSeconds), 1e-4f, "0.4秒で -6dB");
        Assert.AreEqual(0f, SongSelectController.StartPreviewGain(SongSelectController.StartSequenceSeconds), 1e-4f, "幕が閉じきると消える");
    }

    [Test]
    public void StartCutSplitsTheJacketIntoFourShardsAfterTheSlashes()
    {
        var go = new GameObject("StartCutTest", typeof(RectTransform));
        try
        {
            var shards = SongSelectStartCut.Create((RectTransform)go.transform, SongSelectStartCut.Layer.Shards, null, Color.white, 227);
            var lights = SongSelectStartCut.Create((RectTransform)go.transform, SongSelectStartCut.Layer.Lights, null, Color.white, 227);
            Assert.AreEqual(0, Vertices(shards, .05f), "斬撃の途中ではまだ割れない");
            Assert.Greater(Vertices(lights, .05f), 0, "青と赤の斬撃");
            Assert.AreEqual(4 * 26, Vertices(shards, .2f), "X の4片(片ごとに中心+弧の25点)");
            Assert.AreEqual(0, Vertices(shards, SongSelectStartCut.SplitAt + SongSelectStartCut.FlySeconds + .01f), "飛び終えたら消える");
            Assert.IsFalse(SongSelectStartCut.IsSplit(-1));
        }
        finally { Object.DestroyImmediate(go); }
    }

    static int Vertices(SongSelectStartCut cut, float age)
    {
        cut.SetAge(age);
        var helper = new UnityEngine.UI.VertexHelper();
        typeof(SongSelectStartCut).GetMethod("OnPopulateMesh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(UnityEngine.UI.VertexHelper) }, null).Invoke(cut, new object[] { helper });
        int count = helper.currentVertCount;
        helper.Dispose();
        return count;
    }
}
