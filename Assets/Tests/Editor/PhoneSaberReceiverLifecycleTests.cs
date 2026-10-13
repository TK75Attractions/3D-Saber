using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberReceiverLifecycleTests
{
    [Test]
    public void DisablingOverlayCancelsCaptureAndClearsProgressBeforeReenable()
    {
        var go = new GameObject("採取キャンセルテスト");
        var input = go.AddComponent<InputPoint>();
        var overlay = go.AddComponent<PhoneSaberOperatorOverlay>();
        try
        {
            Set(overlay, "calibrationInput", input);
            Set(overlay, "calibrationCorner", 2);
            Set(overlay, "captureStarted", 0.0);
            input.PositionCapture.Begin(false, 0);
            for (int i = 0; i < 30; i++)
                input.PositionCapture.Add(false, Vector2.zero, Vector2.right * 100, i / 30.0);
            Invoke(overlay, "OnDisable");
            Assert.AreEqual(-1, Field<int>(overlay, "calibrationCorner"));
            Assert.AreEqual(-1, Field<double>(overlay, "captureStarted"));
            Assert.IsNull(Field<InputPoint>(overlay, "calibrationInput"));
            Invoke(overlay, "OnEnable");
            input.PositionCapture.Add(false, Vector2.zero, Vector2.right * 100, 1.0);
            Assert.IsFalse(input.PositionCapture.Finish(out _, out _), "再有効化で古い採取結果を処理しない");
        }
        finally { Object.DestroyImmediate(go); }
    }

    // 2026-10-13: F9 は maxQueuedFrames を切り替えなくなった（画面条件を1ブロック内で固定し、
    // 画面の遅延を条件ごとに保存するため）。終了・破棄で値を書き換えないことと、状態が閉じることを確かめる。
    [TestCase("Finish", 2)]
    [TestCase("OnDisable", 3)]
    [TestCase("OnDestroy", 4)]
    public void ProbeCompletionLeavesQueuedFramesAloneAndClosesTheProbe(string completion, int queued)
    {
        int saved = QualitySettings.maxQueuedFrames;
        var go = new GameObject("キュー設定テスト");
        var probe = go.AddComponent<PhoneSaberLatencyProbe>();
        try
        {
            QualitySettings.maxQueuedFrames = queued;
            int expected = QualitySettings.maxQueuedFrames;
            Set(probe, "active", true);
            Invoke(probe, completion);
            Assert.AreEqual(expected, QualitySettings.maxQueuedFrames);
            Assert.IsFalse(Field<bool>(probe, "active"));
            Invoke(probe, "OnDisable");
            Assert.AreEqual(expected, QualitySettings.maxQueuedFrames, "二重の終了通知でも変えない");
        }
        finally { Object.DestroyImmediate(go); QualitySettings.maxQueuedFrames = saved; }
    }

    static void Invoke(object owner, string name)
    {
        // 旧コードに OnDestroy がない場合も、Unity と同じく何もしない。
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(owner, null);
    }
    static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    static void Set(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);
}
