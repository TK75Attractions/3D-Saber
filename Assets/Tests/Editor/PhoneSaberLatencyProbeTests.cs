using NUnit.Framework;
using System.Reflection;
using UnityEngine;

// Unity の時計や描画を使わず、遅延テストの切替・計測・失敗判定を検証する。
public class PhoneSaberLatencyProbeTests
{
    [TestCase(0.1f, 0f, 1f)]
    [TestCase(5.5f, 0f, 1f)]
    [TestCase(20f, 11f, 2f)]
    public void ProbeRejectsJitterAndMeasuresMovementIndependentOfWorldMapping(
        float scale, float offset, float sensitivity)
    {
        var go = new GameObject("遅延テスト座標");
        var input = go.AddComponent<InputPoint>();
        input.enabled = false; // ソケットや外部プロセスは開始しない。
        try
        {
            input.useDirectWorldMapping = true;
            input.worldScale = new Vector2(scale, 3f);
            input.worldOffset = new Vector2(offset, 0f);
            input.sensitivity = sensitivity;
            var loop = new PhoneSaberLatencyLoop();
            loop.Reset(0);
            SetMidpoint(input, -0.5f);
            Assert.AreEqual(-0.5f, PhoneSaberLatencyProbe.ReadNormalizedX(input), 1e-5f);
            loop.Tick(1.0, 0.99, PhoneSaberLatencyProbe.ReadNormalizedX(input), true);
            Assert.IsTrue(loop.Waiting);

            SetMidpoint(input, -0.44f);
            loop.Tick(1.03, 1.02, PhoneSaberLatencyProbe.ReadNormalizedX(input), true);
            Assert.IsTrue(loop.Waiting, "ワールド拡大で小さな揺れを応答として採用しない");
            Assert.AreEqual(0, loop.TotalSamples);

            SetMidpoint(input, 0.5f);
            loop.Tick(1.10, 1.085, PhoneSaberLatencyProbe.ReadNormalizedX(input), true);
            Assert.IsFalse(loop.Waiting, "ワールド縮小でも本来の移動を検出する");
            Assert.AreEqual(85.0, loop.SamplesMs[0], 1e-6);
        }
        finally { Object.DestroyImmediate(go); }
    }

    static void SetMidpoint(InputPoint input, float normalizedX)
    {
        float pixels = (normalizedX + 1f) * 960f;
        Set(input, "hasNewData", true);
        Set(input, "hasStickData", true);
        Set(input, "rawX", pixels); Set(input, "rawY", 540f);
        Set(input, "rawX1a", pixels); Set(input, "rawY1a", 300f);
        Set(input, "rawX1b", pixels); Set(input, "rawY1b", 780f);
        typeof(InputPoint).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
    }

    static void Set(InputPoint input, string name, object value)
        => typeof(InputPoint).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, value);

    [Test]
    public void MeasuresFromSwitchToFirstMovedPacket()
    {
        var loop = new PhoneSaberLatencyLoop();
        loop.Reset(0);
        loop.Tick(0.5, 0.49, -0.5f, true);
        Assert.IsFalse(loop.Waiting, "1秒経つまでは切り替えない");
        loop.Tick(1.0, 0.99, -0.5f, true);
        Assert.IsTrue(loop.Waiting);
        Assert.AreEqual(1, loop.Side);
        loop.Tick(1.03, 1.02, -0.45f, true);
        Assert.IsTrue(loop.Waiting, "閾値未満の揺れは切替として数えない");
        loop.Tick(1.10, 1.085, 0.5f, true);
        Assert.IsFalse(loop.Waiting);
        Assert.AreEqual(1, loop.SamplesMs.Count);
        Assert.AreEqual(85.0, loop.SamplesMs[0], 1e-6);
        Assert.AreEqual(1, loop.TotalSamples);
        StringAssert.StartsWith("median-ms=85 p95-ms=85 min-ms=85 max-ms=85 n=1 total=1 misses=0", loop.LogDetail());
    }

    [Test]
    public void IgnoresPacketsReceivedBeforeTheSwitch()
    {
        var loop = new PhoneSaberLatencyLoop();
        loop.Reset(0);
        loop.Tick(1.0, 0.99, -0.5f, true);
        Assert.IsTrue(loop.Waiting);
        // 切替前に受信していた古い値（同じ受信時刻）は新しいサンプルにならない。
        loop.Tick(1.05, 0.99, 0.5f, true);
        Assert.AreEqual(0, loop.SamplesMs.Count);
    }

    [Test]
    public void TimesOutWithoutMovementAndDoesNotSwitchWithoutReception()
    {
        var loop = new PhoneSaberLatencyLoop();
        loop.Reset(0);
        loop.Tick(1.0, 0.5, -0.5f, true);
        Assert.IsFalse(loop.Waiting, "0.2秒以上受信がなければ切り替えない");
        loop.Tick(1.1, 1.09, -0.5f, true);
        Assert.IsTrue(loop.Waiting);
        loop.Tick(2.7, 1.09, -0.5f, true);
        Assert.IsFalse(loop.Waiting);
        Assert.AreEqual(1, loop.Misses);
        StringAssert.Contains("失敗 1 回", loop.Summary());
    }
}
