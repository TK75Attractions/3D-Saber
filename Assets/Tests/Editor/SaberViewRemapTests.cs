using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// メニュー画面でセーバーを画面端まで届かせる写像(SaberInputBridge のリマップ純関数)の検証。
public class SaberViewRemapTests
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
    }

    private Camera MakeCamera(Vector3 pos)
    {
        var go = new GameObject("remapTestCam");
        created.Add(go);
        var cam = go.AddComponent<Camera>();
        go.transform.position = pos;
        cam.orthographic = false;
        cam.fieldOfView = 60f;
        cam.pixelRect = new Rect(0, 0, 1600, 900);
        // ヘッドレス環境では aspect が pixelRect に追従しないため明示する
        cam.aspect = 1600f / 900f;
        return cam;
    }

    [Test]
    public void TryComputePlaneView_StraightCamera_MatchesFrustumMath()
    {
        var cam = MakeCamera(new Vector3(0f, 0f, -10f));
        bool ok = SaberInputBridge.TryComputePlaneView(cam, 0f, out Vector2 center, out Vector2 half);

        Assert.IsTrue(ok);
        Assert.AreEqual(0f, center.x, 0.01f);
        Assert.AreEqual(0f, center.y, 0.01f);
        // 距離10・FOV60° → 半高 = 10·tan(30°) ≒ 5.774、半幅 = 半高×16/9
        Assert.AreEqual(5.774f, half.y, 0.05f);
        Assert.AreEqual(5.774f * 1600f / 900f, half.x, 0.1f);
    }

    [Test]
    public void RemapPoint_CenterAndEdgesMapToViewCenterAndEdges()
    {
        Vector2 sourceHalf = new Vector2(5.5f, 3f);
        Vector2 viewCenter = new Vector2(1f, 0.5f);
        Vector2 viewHalf = new Vector2(10f, 6f);

        Vector3 center = SaberInputBridge.RemapPoint(Vector3.zero, sourceHalf, viewCenter, viewHalf, 0f);
        Assert.AreEqual(1f, center.x, 1e-3f, "入力中心 → 画面中心");
        Assert.AreEqual(0.5f, center.y, 1e-3f);

        Vector3 edge = SaberInputBridge.RemapPoint(new Vector3(5.5f, 3f, 0f), sourceHalf, viewCenter, viewHalf, 0f);
        Assert.AreEqual(11f, edge.x, 1e-3f, "判定面の端 → 画面の端");
        Assert.AreEqual(6.5f, edge.y, 1e-3f);
    }

    [Test]
    public void RemapPoint_ClampsBeyondSourceExtents()
    {
        Vector2 sourceHalf = new Vector2(5.5f, 3f);
        Vector2 viewHalf = new Vector2(10f, 6f);
        Vector3 far = SaberInputBridge.RemapPoint(new Vector3(100f, -100f, 0f), sourceHalf, Vector2.zero, viewHalf, 0f);
        Assert.AreEqual(10f, far.x, 1e-3f, "範囲外入力は画面端で止まる");
        Assert.AreEqual(-6f, far.y, 1e-3f);
    }
}
