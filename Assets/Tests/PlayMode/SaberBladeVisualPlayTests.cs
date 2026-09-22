using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class SaberBladeVisualPlayTests
{
    [UnityTest]
    public IEnumerator BladeModeHidesLegacyBluePointEvenWithoutCameraInput()
    {
        var go = new GameObject("LegacySceneSaber");
        try
        {
            // Gameシーンに保存されている旧カーソル構成を再現する。
            foreach (string name in new[] { "Point", "PointGlow" })
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = name;
                sphere.transform.SetParent(go.transform, false);
            }
            var bridge = go.AddComponent<SaberInputBridge>();
            bridge.useInputPoint = false;
            bridge.fallbackToMouse = false;
            bridge.SetBladeColor(Color.red);
            yield return null;
            Assert.False(go.transform.Find("Point").gameObject.activeSelf);
            Assert.False(go.transform.Find("PointGlow").gameObject.activeSelf);
            bridge.ApplyMouseWorld(Vector3.zero);
            Assert.True(bridge.HasBlade);
            Assert.True(go.GetComponent<LineRenderer>().enabled);
            Assert.AreEqual(Color.red, go.GetComponent<LineRenderer>().startColor);
            Assert.False(go.transform.Find("Point").gameObject.activeSelf);
            bridge.enabled = false;
            Assert.False(go.transform.Find("PointGlow").gameObject.activeSelf);
        }
        finally { Object.Destroy(go); }
        yield return null;
    }

    [UnityTest]
    public IEnumerator BridgeKeepsJudgmentEndpointsAndClearsVisualsOnDisableAndDestroy()
    {
        var go = new GameObject("BridgeVisualPlayTest");
        Mesh mesh = null; Material material = null;
        try
        {
            var bridge = go.AddComponent<SaberInputBridge>();
            bridge.useInputPoint = false; bridge.fallbackToMouse = false;
            Vector3 a = new Vector3(-1, -.6f, 0), b = new Vector3(.3f, .9f, 0);
            typeof(SaberInputBridge).GetMethod("ApplyBladeImmediate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(bridge, new object[] { a, b });
            Assert.AreEqual(a, bridge.WorldEndA); Assert.AreEqual(b, bridge.WorldEndB); Assert.True(bridge.HasBlade);
            var visual = go.GetComponentInChildren<SaberBladeVisual>(); Assert.NotNull(visual);
            var renderer = visual.GetComponent<MeshRenderer>(); Assert.True(renderer.enabled);
            mesh = visual.GetComponent<MeshFilter>().sharedMesh; material = renderer.sharedMaterial;
            var trail = SaberRig.EnsureTrail(go.transform, Color.red, bridge.bladeWidth);
            Assert.False(trail.enabled); Assert.False(trail.emitting);
            bridge.enabled = false;
            Assert.False(bridge.HasBlade); Assert.False(renderer.enabled); Assert.AreEqual(0, mesh.vertexCount);
        }
        finally { Object.Destroy(go); }
        yield return null;
        yield return null;
        Assert.True(mesh == null); Assert.True(material == null);
    }
}
