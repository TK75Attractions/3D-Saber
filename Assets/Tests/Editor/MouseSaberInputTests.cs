using NUnit.Framework;
using UnityEngine;

public class MouseSaberInputTests
{
    private Camera MakeCamera(Vector3 pos, Vector3 euler)
    {
        var go = new GameObject("testCam");
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
        foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c != null) Object.DestroyImmediate(c.gameObject);
        }
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
}
