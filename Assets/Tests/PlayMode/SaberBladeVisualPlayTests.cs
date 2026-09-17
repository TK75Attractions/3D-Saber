using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class SaberBladeVisualPlayTests
{
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
