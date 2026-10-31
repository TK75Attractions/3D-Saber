using NUnit.Framework;
using UnityEngine;

// 照準の入力元の切り替え(2026-10-31 ユーザー「照準が消えても、ため(ゲージ)を0にしない」)。
public class SaberAimSourceTests
{
    static readonly Vector2 Mouse = new Vector2(100, 100);

    static SaberAimSource Aiming()
    {
        var source = new SaberAimSource();
        source.Update(false, true, Mouse);
        Assert.IsTrue(source.Update(true, true, Mouse), "赤が来たらマウスのためは取り消す");
        Assert.AreEqual(SaberAimSource.Saber, source.Kind);
        return source;
    }

    [Test]
    public void LosingTheSaberKeepsTheChargeUntilTheMouseIsUsed()
    {
        var source = Aiming();
        Assert.IsFalse(source.Update(false, true, Mouse), "見失っただけでは取り消さない");
        Assert.IsTrue(source.KindChanged);
        Assert.IsTrue(source.HoldingLostSaber);
        Assert.IsTrue(source.MouseNeedsMove, "置いたままのマウスの位置で勝手に始めない");
        for (int i = 0; i < 100; i++) Assert.IsFalse(source.Update(false, true, Mouse + new Vector2(2, -2)), "3px 以内の揺れは使ったことにしない");
        Assert.IsTrue(source.HoldingLostSaber, "何秒たっても、マウスが使われるまでは待つ");
    }

    [Test]
    public void TheSaberComingBackContinuesTheCharge()
    {
        var source = Aiming();
        source.Update(false, true, Mouse);
        Assert.IsFalse(source.Update(true, true, Mouse), "戻ったら続きから");
        Assert.IsFalse(source.HoldingLostSaber);
        Assert.IsFalse(source.MouseNeedsMove);
        Assert.AreEqual(SaberAimSource.Saber, source.Kind);
    }

    [Test]
    public void MovingOrClickingTheMouseHandsOverAndCancels()
    {
        var source = Aiming();
        source.Update(false, true, Mouse);
        Assert.IsTrue(source.Update(false, true, Mouse + new Vector2(10, 0)), "動かしたら持ち替え");
        Assert.IsFalse(source.HoldingLostSaber);
        Assert.IsFalse(source.MouseNeedsMove);
        Assert.IsTrue(source.Update(true, true, Mouse), "マウスを使ったあとに赤が戻ったら、ため直す");

        source.Update(false, true, Mouse);
        Assert.IsTrue(source.Update(false, true, Mouse, mouseClicked: true), "クリックしても持ち替え");
        Assert.IsFalse(source.HoldingLostSaber);
    }

    [Test]
    public void WithoutAMouseTheChargeWaitsForTheSaber()
    {
        var source = new SaberAimSource();
        source.Update(true, false, default);
        Assert.IsFalse(source.Update(false, false, default));
        Assert.AreEqual(SaberAimSource.None, source.Kind);
        Assert.IsTrue(source.HoldingLostSaber);
        Assert.IsFalse(source.Update(false, true, Mouse), "マウスがつながっても、使われるまでは待つ");
        Assert.IsTrue(source.HoldingLostSaber);
        Assert.IsFalse(source.Update(true, true, Mouse));
        Assert.IsFalse(source.HoldingLostSaber);
    }

    [Test]
    public void SwitchingBetweenMouseAndNothingStillCancelsLikeBefore()
    {
        var source = new SaberAimSource();
        source.Update(false, true, Mouse);
        Assert.IsTrue(source.Update(false, false, Mouse), "赤と関係ない切り替えは今までどおり取り消す");
        Assert.IsFalse(source.HoldingLostSaber);
    }

    [Test]
    public void TheCountdownStaysExpiredUntilItIsReset()
    {
        var countdown = new SongSelectCountdown();
        Assert.IsFalse(countdown.Expired);
        Assert.IsTrue(countdown.Tick(SongSelectCountdown.LimitSeconds + 1, true));
        Assert.IsTrue(countdown.Expired);
        Assert.IsFalse(countdown.Tick(5, true), "一度だけ知らせる");
        Assert.AreEqual(0, countdown.Remaining);
        countdown.Reset();
        Assert.IsFalse(countdown.Expired);
        Assert.AreEqual(SongSelectCountdown.LimitSeconds, countdown.Remaining);
    }
}
