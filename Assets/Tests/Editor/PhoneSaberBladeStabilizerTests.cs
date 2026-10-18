using NUnit.Framework;
using UnityEngine;

public class PhoneSaberBladeStabilizerTests
{
    const double Frame = 1.0 / 60;

    static void Step(PhoneSaberBladeStabilizer s, int i, Vector2 a, Vector2 b, out Vector2 ra, out Vector2 rb) =>
        s.Apply(10 + i * Frame, a, b, out ra, out rb);

    [Test]
    public void SmoothSwingPassesThroughUnchanged()
    {
        var s = new PhoneSaberBladeStabilizer();
        for (int i = 0; i < 30; i++)
        {
            // 速い横振り（1 コマで刃の長さの 5 %）と、ゆっくりした傾き。
            Vector2 mid = new Vector2(-1 + i * .05f, 0);
            float angle = i * .02f;
            Vector2 half = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * .5f;
            Step(s, i, mid - half, mid + half, out Vector2 ra, out Vector2 rb);
            Assert.AreEqual(mid - half, ra, $"frame {i}");
            Assert.AreEqual(mid + half, rb, $"frame {i}");
        }
        Assert.AreEqual(0, s.RejectedJumps);
        Assert.AreEqual(0, s.LimitedLengths);
    }

    [Test]
    public void LengthFlickerIsLimitedWithoutMovingTheCentreOrDirection()
    {
        var s = new PhoneSaberBladeStabilizer();
        Step(s, 0, new Vector2(0, -.5f), new Vector2(0, .5f), out _, out _);
        // 次のコマで刃が 1.6 倍に伸びて見えた（誤認識）。
        Step(s, 1, new Vector2(0, -.8f), new Vector2(0, .8f), out Vector2 ra, out Vector2 rb);
        Assert.AreEqual(1 + PhoneSaberBladeStabilizer.MaximumLengthChange, (rb - ra).magnitude, 1e-4);
        Assert.AreEqual(Vector2.zero, (ra + rb) * .5f);
        Assert.AreEqual(0, ra.x, 1e-6);
        Assert.AreEqual(1, s.LimitedLengths);
    }

    [Test]
    public void SingleJumpIsDroppedButAPersistentMoveIsAccepted()
    {
        var s = new PhoneSaberBladeStabilizer();
        Vector2 a = new Vector2(0, -.5f), b = new Vector2(0, .5f);
        for (int i = 0; i < 5; i++) Step(s, i, a, b, out _, out _);
        Vector2 far = new Vector2(3, 0);
        // 1 コマだけ刃の長さの 3 倍離れた所に跳んだ → 前の刃を保つ。
        Step(s, 5, a + far, b + far, out Vector2 ra, out Vector2 rb);
        Assert.AreEqual(a, ra); Assert.AreEqual(b, rb);
        Step(s, 6, a, b, out ra, out rb);
        Assert.AreEqual(a, ra);
        Assert.AreEqual(1, s.RejectedJumps);
        // 同じ所へ続けて跳ぶ（本当に動いた）なら、1 回捨てた後に受け入れる（固まるのは最大 1 コマ）。
        Step(s, 7, a + far, b + far, out ra, out _);
        Assert.AreEqual(a, ra);
        Step(s, 8, a + far, b + far, out ra, out rb);
        Assert.AreEqual(a + far, ra); Assert.AreEqual(b + far, rb);
        Assert.AreEqual(2, s.RejectedJumps);
    }

    [Test]
    public void ThirtyFpsFastSwingIsNotRejected()
    {
        // 2026-10-18: 30 fps で本物の振りを捨てて剣が固まった。1 受信で刃の長さの 2.5 倍動く速い振りでも捨てない。
        var s = new PhoneSaberBladeStabilizer();
        const double thirty = 1.0 / 30;
        Vector2 half = new Vector2(0, .5f);
        for (int i = 0; i < 4; i++) s.Apply(10 + i * thirty, -half, half, out _, out _);
        Vector2 moved = new Vector2(2.5f, 0);
        s.Apply(10 + 4 * thirty, moved - half, moved + half, out Vector2 ra, out _);
        Assert.AreEqual(moved - half, ra);
        Assert.AreEqual(0, s.RejectedJumps);
    }

    [Test]
    public void SwappedEndsKeepThePreviousOrder()
    {
        var s = new PhoneSaberBladeStabilizer();
        Vector2 a = new Vector2(-.1f, -.5f), b = new Vector2(.1f, .5f);
        Step(s, 0, a, b, out _, out _);
        Step(s, 1, b, a, out Vector2 ra, out Vector2 rb);
        Assert.AreEqual(a, ra); Assert.AreEqual(b, rb);
    }

    [Test]
    public void SameStampRepeatsInvalidResetsAndLongGapRestarts()
    {
        var s = new PhoneSaberBladeStabilizer();
        Vector2 a = new Vector2(0, -.5f), b = new Vector2(0, .5f);
        s.Apply(1, a, b, out _, out _);
        s.Apply(1, a * 9, b * 9, out Vector2 ra, out _);
        Assert.AreEqual(a, ra); // 同じ受信を描画フレームごとに二度処理しない
        s.Apply(2, new Vector2(float.NaN, 0), b, out ra, out _);
        Assert.IsTrue(float.IsNaN(ra.x)); // 無効値はそのまま既存経路へ
        Vector2 far = new Vector2(5, 0);
        s.Apply(3, a + far, b + far, out ra, out _);
        Assert.AreEqual(a + far, ra); // リセット後の最初の受信は跳びとみなさない
        s.Apply(3.5, a, b, out ra, out _);
        Assert.AreEqual(a, ra); // 0.25 秒を超える途切れの後もやり直し
        Assert.AreEqual(0, s.RejectedJumps);
    }

    [Test]
    public void SettingDefaultsOnAndPersistsPerStation()
    {
        string key = PhoneSaberBladeStabilizerSettings.Key("stab-a");
        PlayerPrefs.DeleteKey(key);
        try
        {
            Assert.IsTrue(PhoneSaberBladeStabilizerSettings.Load("stab-a"));
            int revision = PhoneSaberBladeStabilizerSettings.Revision;
            PhoneSaberBladeStabilizerSettings.Save("stab-a", false);
            Assert.IsFalse(PhoneSaberBladeStabilizerSettings.Load("stab-a"));
            Assert.AreNotEqual(revision, PhoneSaberBladeStabilizerSettings.Revision);
        }
        finally { PlayerPrefs.DeleteKey(key); }
    }
}
