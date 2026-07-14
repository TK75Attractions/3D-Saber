using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class MouseSaberInputTests
{
    private readonly List<GameObject> createdObjects = new List<GameObject>();

    private Camera MakeCamera(Vector3 pos, Vector3 euler)
    {
        var go = new GameObject("testCam");
        createdObjects.Add(go);
        var cam = go.AddComponent<Camera>();
        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(euler);
        cam.orthographic = false;
        cam.fieldOfView = 60f;
        // ScreenPointToRay は Camera.pixelRect を使うので明示的に設定。
        cam.pixelRect = new Rect(0, 0, 800, 600);
        return cam;
    }

    [TearDown]
    public void Cleanup()
    {
        // エディタで開いているシーンの Main Camera 等を巻き込まないよう、テストで生成したものだけ破棄する。
        foreach (var go in createdObjects)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        createdObjects.Clear();
    }

    [Test]
    public void TryProjectToPlane_ReturnsFalseOnNullCamera()
    {
        bool ok = MouseSaberInput.TryProjectToPlane(new Vector2(400, 300), 0f, null, out _);
        Assert.IsFalse(ok);
    }

    [Test]
    public void TryProjectToPlane_AlwaysLandsOnRequestedZ()
    {
        var cam = MakeCamera(new Vector3(0f, 0f, -10f), Vector3.zero);
        float cx = cam.pixelWidth * 0.5f;
        float cy = cam.pixelHeight * 0.5f;
        bool ok = MouseSaberInput.TryProjectToPlane(new Vector2(cx, cy), 0f, cam, out Vector3 world);
        Assert.IsTrue(ok);
        Assert.AreEqual(0f, world.z, 0.01f);
    }

    [Test]
    public void TryProjectToPlane_RightScreen_MapsToPositiveX()
    {
        var cam = MakeCamera(new Vector3(0f, 0f, -10f), Vector3.zero);
        float cx = cam.pixelWidth * 0.5f;
        float cy = cam.pixelHeight * 0.5f;
        MouseSaberInput.TryProjectToPlane(new Vector2(cx, cy), 0f, cam, out Vector3 center);
        MouseSaberInput.TryProjectToPlane(new Vector2(cx + 100f, cy), 0f, cam, out Vector3 right);
        Assert.Greater(right.x, center.x);
        Assert.AreEqual(center.y, right.y, 0.01f);
    }

    [Test]
    public void TryProjectToPlane_UpperScreen_MapsToPositiveY()
    {
        var cam = MakeCamera(new Vector3(0f, 0f, -10f), Vector3.zero);
        float cx = cam.pixelWidth * 0.5f;
        float cy = cam.pixelHeight * 0.5f;
        MouseSaberInput.TryProjectToPlane(new Vector2(cx, cy), 0f, cam, out Vector3 center);
        MouseSaberInput.TryProjectToPlane(new Vector2(cx, cy + 100f), 0f, cam, out Vector3 upper);
        Assert.Greater(upper.y, center.y);
    }

    [Test]
    public void TryProjectToPlane_TopDownView_KeepsJudgeFocusNearScreenCenter()
    {
        // 現行視点 (y=1.6, 6度) と同じく、判定面中央の y≒0.86 を画面中央へ置く。
        var cam = MakeCamera(new Vector3(0f, 2.35f, -7f), new Vector3(12f, 0f, 0f));
        float cx = cam.pixelWidth * 0.5f;
        float cy = cam.pixelHeight * 0.5f;

        bool ok = MouseSaberInput.TryProjectToPlane(
            new Vector2(cx, cy), 0f, cam, out Vector3 world);

        Assert.IsTrue(ok);
        Assert.AreEqual(0f, world.z, 0.01f);
        Assert.AreEqual(0.86f, world.y, 0.03f);
    }
}
