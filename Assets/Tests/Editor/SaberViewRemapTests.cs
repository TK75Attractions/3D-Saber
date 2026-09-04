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
    public void TryComputePlaneView_OffsetPixelRect_StaysCenteredOnCameraAxis()
    {
        // レターボックス等でカメラの描画矩形が右へずれていても、写像の中心はカメラ軸(0,0)のまま。
        // (旧実装は (0,0)〜(pixelWidth,pixelHeight) を使い、rect のオフセット分だけ左へずれていた
        //  → セーバーが右端まで届かない)
        var cam = MakeCamera(new Vector3(0f, 0f, -10f));
        cam.pixelRect = new Rect(200f, 0f, 1200f, 900f);
        cam.aspect = 1200f / 900f;
        bool ok = SaberInputBridge.TryComputePlaneView(cam, 0f, out Vector2 center, out Vector2 half);

        Assert.IsTrue(ok);
        Assert.AreEqual(0f, center.x, 0.05f, "rect オフセットがあっても中心はずれない");
        Assert.AreEqual(0f, center.y, 0.05f);
        Assert.AreEqual(5.774f, half.y, 0.05f);
        Assert.AreEqual(5.774f * 1200f / 900f, half.x, 0.1f);
    }

    private SaberInputBridge MakeMouseBridge(Camera cam, bool remap)
    {
        var go = new GameObject("mouseBridge");
        created.Add(go);
        var bridge = go.AddComponent<SaberInputBridge>();
        bridge.targetCamera = cam;      // EditMode では Awake が呼ばれないので明示する
        bridge.remapToCameraView = remap;
        bridge.useBladeMode = false;    // LineRenderer/Material 生成を抑える(位置の検証だけ)
        bridge.enableSmoothing = false;
        return bridge;
    }

    [Test]
    public void MouseFallback_WithRemap_IsClampedToCameraView_NotJudgePlane()
    {
        // 選曲画面の再現: カメラ(0,0,-10)/FOV60 → z=0 の可視範囲 ≒ ±10.3×±5.8。
        // 旧実装はマウスだけ判定面 ±5.5×±3 で再クランプされ、赤い線が画面の約 75% で止まっていた。
        var cam = MakeCamera(new Vector3(0f, 0f, -10f));
        var bridge = MakeMouseBridge(cam, remap: true);

        bridge.ApplyMouseWorld(new Vector3(10f, 5f, 0f)); // 画面右上寄り(可視範囲内)
        Assert.AreEqual(10f, bridge.transform.position.x, 1e-3f, "判定面の 5.5 で止まらない");
        Assert.AreEqual(5f, bridge.transform.position.y, 1e-3f, "判定面の 3 で止まらない");
        Assert.IsTrue(bridge.UsingMouseFallback);

        bridge.ApplyMouseWorld(new Vector3(50f, -50f, 0f)); // 可視範囲外は画面端で止まる
        Assert.AreEqual(5.774f * 1600f / 900f, bridge.transform.position.x, 0.1f, "右端 = カメラ可視範囲の端");
        Assert.AreEqual(-5.774f, bridge.transform.position.y, 0.05f, "下端 = カメラ可視範囲の端");
    }

    [Test]
    public void MouseFallback_WithoutRemap_KeepsJudgePlaneClamp()
    {
        // 本編(remap 無効)は従来どおり判定面 ±5.5×±3 でクランプする
        var cam = MakeCamera(new Vector3(0f, 0f, -10f));
        var bridge = MakeMouseBridge(cam, remap: false);

        bridge.ApplyMouseWorld(new Vector3(10f, 5f, 0f));
        Assert.AreEqual(5.5f, bridge.transform.position.x, 1e-3f);
        Assert.AreEqual(3f, bridge.transform.position.y, 1e-3f);
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
