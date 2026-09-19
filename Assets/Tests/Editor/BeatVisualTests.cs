using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class BeatVisualTests
{
    [Test]
    public void LightCuesRespectIrregularTimesAndOffsetWithoutEditingChart()
    {
        var chart = new ChartData { offsetMs = 125, bpm = 173 };
        chart.notes.Add(new NoteData { time = 2317 });
        chart.notes.Add(new NoteData { time = 800 });
        chart.notes.Add(new NoteData { time = 800 });
        chart.notes.Add(new NoteData { time = 1090 });
        string original = JsonUtility.ToJson(chart);
        var cues = new StageLightCues(chart);
        Assert.AreEqual(3, cues.Count);
        Assert.AreEqual(0, cues.Evaluate(.8));
        Assert.AreEqual(1, cues.Evaluate(.925), .0001);
        Assert.AreEqual(1, cues.Evaluate(2.442), .0001);
        Assert.AreEqual(0, cues.Evaluate(3));
        Assert.AreEqual(1, cues.Evaluate(1.215), .0001, "巻き戻しても実時刻へ追従する");
        Assert.AreEqual(original, JsonUtility.ToJson(chart));
    }

    [Test]
    public void DenseOrInvalidCuesDoNotCauseUnboundedFlashes()
    {
        var chart = new ChartData();
        chart.notes.Add(null); chart.notes.Add(new NoteData { time = float.NaN });
        chart.notes.Add(new NoteData { time = -100 });
        for (int i = 0; i < 100; i++) chart.notes.Add(new NoteData { time = i * 10 });
        var cues = new StageLightCues(chart);
        Assert.LessOrEqual(cues.Count, 6);
        Assert.AreEqual(0, cues.Evaluate(double.NaN));
        Assert.AreEqual(0, cues.Evaluate(double.PositiveInfinity));
        Assert.AreEqual(0, new StageLightCues(null).Evaluate(1));
    }

    [TestCase(false)] [TestCase(true)]
    public void NewStageKeepsCentralSpaceClearAndResourcesBounded(bool projector)
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        var go = new GameObject("PulseArrayTest");
        Mesh[] meshes = null; Material[] materials = null;
        try
        {
            var floor = go.AddComponent<FloorRenderer>(); floor.Build(StageTheme.PulseArray);
            var chart = new ChartData(); chart.notes.Add(new NoteData { time = 1000 }); floor.SetRhythm(chart);
            var stage = go.GetComponentInChildren<PulseArrayStage>();
            Assert.NotNull(stage);
            var filters = go.GetComponentsInChildren<MeshFilter>();
            Assert.AreEqual(3, filters.Length);
            Assert.IsEmpty(go.GetComponentsInChildren<Collider>());
            meshes = filters.Select(f => f.sharedMesh).ToArray();
            materials = go.GetComponentsInChildren<MeshRenderer>().Select(r => r.sharedMaterial).Distinct().ToArray();
            Assert.AreEqual(2, materials.Length);
            Assert.IsTrue(materials.All(m => m.shader.isSupported));
            var random = Random.state;
            for (int frame = 0; frame < 60; frame++)
            {
                floor.Tick(frame * .37, frame / 59f);
                Assert.AreEqual(frame * .37, stage.LastTickSeconds, .00001);
                foreach (var filter in filters)
                    foreach (var vertex in filter.sharedMesh.vertices)
                    {
                        Assert.IsFalse(float.IsNaN(vertex.sqrMagnitude) || float.IsInfinity(vertex.sqrMagnitude));
                        if (vertex.y > -2.4f && vertex.y < 4.8f) Assert.GreaterOrEqual(Mathf.Abs(vertex.x), 5.8f);
                    }
                Assert.LessOrEqual(stage.transform.Find("LightBanks").GetComponent<MeshFilter>().sharedMesh.vertexCount, PulseArrayStage.VertexBudget);
            }
            Assert.AreEqual(random, Random.state);
            floor.Build(StageTheme.ObsidianRelay);
            Assert.AreEqual(StageTheme.PulseArray, floor.ActiveTheme);
            Assert.AreEqual(3, go.GetComponentsInChildren<MeshRenderer>().Length);
        }
        finally { Object.DestroyImmediate(go); DisplaySettings.SetProjectorModeForTest(false); }
        Assert.IsTrue(meshes.All(m => m == null));
        Assert.IsTrue(materials.All(m => m == null));
    }

    [Test]
    public void BladeRibbonExpiresClearsOnTeleportAndOwnsItsResources()
    {
        var go = new GameObject("BladeVisualTest");
        Mesh mesh = null; Material material = null;
        try
        {
            var visual = go.AddComponent<SaberBladeVisual>();
            for (int i = 0; i < 40; i++) visual.Show(new Vector3(i * .02f, 0, 0), new Vector3(i * .02f, 1, 0), .12f, Color.cyan, i * .003f);
            mesh = go.GetComponent<MeshFilter>().sharedMesh;
            material = go.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.Greater(mesh.vertexCount, 8);
            Assert.LessOrEqual(mesh.vertexCount, SaberBladeVisual.PoseCapacity * 4 + 8);
            visual.Show(Vector3.zero, Vector3.up, .12f, Color.cyan, 1);
            Assert.AreEqual(8, mesh.vertexCount, "停止後の古い帯を描かない");
            visual.Show(Vector3.right * 20, Vector3.right * 20 + Vector3.up, .12f, Color.cyan, 1.01f);
            Assert.AreEqual(8, mesh.vertexCount, "ワープを横断する帯を描かない");
            go.SetActive(false);
            Assert.AreEqual(0, mesh.vertexCount);
            Assert.IsFalse(go.GetComponent<MeshRenderer>().enabled);
        }
        finally { Object.DestroyImmediate(go); }
        Assert.IsTrue(mesh == null); Assert.IsTrue(material == null);
    }
}
