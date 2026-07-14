using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// セーバーポインタ(UIホバー滞留クリック)の検証。滞留ロジックは純クラス DwellTracker。
public class SaberUIPointerTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        foreach (var p in Object.FindObjectsByType<SaberUIPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (p != null) Object.DestroyImmediate(p.gameObject);
        }
        foreach (var ip in Object.FindObjectsByType<InputPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (ip != null) Object.DestroyImmediate(ip.gameObject);
        }
    }

    [Test]
    public void Dwell_SameTarget_FiresOnceAtThreshold()
    {
        var tracker = new SaberUIPointer.DwellTracker(0.45f, 0.4f);
        var target = new object();

        Assert.IsFalse(tracker.Tick(target, 0.2f), "溜め始め");
        Assert.IsFalse(tracker.Tick(target, 0.2f));
        Assert.Greater(tracker.Progress01, 0.8f);
        Assert.IsTrue(tracker.Tick(target, 0.1f), "0.45秒で発火");
        Assert.IsFalse(tracker.Tick(target, 0.1f), "直後は連続発火しない(クールダウン)");
    }

    [Test]
    public void Dwell_TargetChange_ResetsProgress()
    {
        var tracker = new SaberUIPointer.DwellTracker(0.45f, 0.4f);
        var a = new object();
        var b = new object();

        tracker.Tick(a, 0.4f);
        Assert.Greater(tracker.Progress01, 0.8f);
        tracker.Tick(b, 0.0f); // 対象が変わった
        Assert.AreEqual(0f, tracker.Progress01, 1e-4f, "進捗はリセット");
        Assert.IsFalse(tracker.Tick(b, 0.44f));
        Assert.IsTrue(tracker.Tick(b, 0.02f), "新対象で改めて満ちる");
    }

    [Test]
    public void Dwell_LeavingTarget_ResetsProgress()
    {
        var tracker = new SaberUIPointer.DwellTracker(0.45f, 0.4f);
        var a = new object();
        tracker.Tick(a, 0.4f);
        tracker.Tick(null, 0.1f); // 何も無い場所へ
        Assert.AreEqual(0f, tracker.Progress01, 1e-4f);
        Assert.IsFalse(tracker.Tick(a, 0.44f), "戻っても溜め直し");
    }

    [Test]
    public void Dwell_CooldownExpires_AllowsRefire()
    {
        var tracker = new SaberUIPointer.DwellTracker(0.45f, 0.4f);
        var a = new object();
        tracker.Tick(a, 0.45f); // 発火1回目相当
        Assert.IsTrue(tracker.InCooldown);
        tracker.Tick(a, 0.5f);  // クールダウン消化
        Assert.IsFalse(tracker.InCooldown);
        Assert.IsFalse(tracker.Tick(a, 0.44f));
        Assert.IsTrue(tracker.Tick(a, 0.02f), "同じボタンに乗り続ければ再発火できる(曲送り連打)");
    }

    [Test]
    public void Build_CreatesHiddenCursor_AndInputPoint()
    {
        var pointer = SaberUIPointer.Build();
        created.Add(pointer.gameObject);

        Assert.IsNotNull(InputPoint.Instance, "UDP受信機が確保される");
        // UDP入力が無い状態ではカーソルは非表示のまま(=マウス操作の邪魔をしない)
        var images = pointer.GetComponentsInChildren<UnityEngine.UI.Image>(true);
        Assert.AreEqual(2, images.Length, "カーソル+進捗サークル");
        foreach (var img in images)
        {
            Assert.IsFalse(img.gameObject.activeSelf, "初期状態は非表示");
        }
    }
}
