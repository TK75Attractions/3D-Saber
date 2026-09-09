using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class StageVariantTests
{
    [Test]
    public void FirstSelectionCanReachEveryTheme()
    {
        CollectionAssert.AreEquivalent(System.Enum.GetValues(typeof(StageTheme)),
            Enumerable.Range(0, StageThemeCatalog.Count).Select(i => StageThemeCatalog.Choose(i, -1)).ToArray());
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
    [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)] [TestCase(9)]
    public void NextSelectionIsUniformOverOtherThemes(int previous)
    {
        var choices = Enumerable.Range(0, StageThemeCatalog.Count - 1).Select(i => StageThemeCatalog.Choose(i, previous)).ToArray();
        Assert.AreEqual(StageThemeCatalog.Count - 1, choices.Distinct().Count());
        Assert.IsFalse(choices.Contains((StageTheme)previous));
        Assert.IsTrue(choices.All(t => (int)t >= 0 && (int)t < StageThemeCatalog.Count));
    }

    [Test]
    public void RandomChoiceDoesNotConsumeGameplayRandomStateOrRepeat()
    {
        var before = Random.state;
        var previous = StageThemeCatalog.NextForPlay();
        for (int i = 0; i < 50; i++)
        {
            var next = StageThemeCatalog.NextForPlay();
            Assert.AreNotEqual(previous, next);
            previous = next;
        }
        Assert.AreEqual(before, Random.state);
    }

    [TestCase(StageTheme.ObsidianRelay)] [TestCase(StageTheme.VioletVault)]
    [TestCase(StageTheme.AmberFoundry)] [TestCase(StageTheme.AzurePrism)]
    public void AllThemesAreBoundedStaticAndLeaveNotesClear(StageTheme theme)
    {
        var go = new GameObject("StageTest");
        try
        {
            var stage = go.AddComponent<FloorRenderer>(); stage.Build(theme);
            Assert.AreEqual(theme, stage.ActiveTheme);
            int children = stage.transform.childCount;
            stage.Build(); stage.Build((StageTheme)(((int)theme + 1) % 4));
            Assert.AreEqual(theme, stage.ActiveTheme, "曲の途中やEnsure再呼び出しで抽選し直さない");
            Assert.AreEqual(children, stage.transform.childCount);
            Assert.IsEmpty(go.GetComponentsInChildren<Collider>());
            var filters = go.GetComponentsInChildren<MeshFilter>();
            Assert.LessOrEqual(filters.Length, 28);
            Assert.LessOrEqual(go.GetComponentsInChildren<MeshRenderer>().Select(r => r.sharedMaterial).Distinct().Count(), 9);
            foreach (var filter in filters)
            {
                var mesh = filter.sharedMesh;
                Assert.Greater(mesh.vertexCount, 0);
                Assert.AreEqual(mesh.vertexCount, mesh.normals.Length);
                foreach (var v in mesh.vertices)
                {
                    Assert.IsFalse(float.IsNaN(v.sqrMagnitude) || float.IsInfinity(v.sqrMagnitude));
                    if (filter.name.StartsWith("Wall")) Assert.GreaterOrEqual(Mathf.Abs(v.x), FloorRenderer.ClearCorridorHalfWidth);
                }
                Assert.IsTrue(mesh.triangles.All(i => i >= 0 && i < mesh.vertexCount));
            }
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void VariationsChangeActualGeometryNotJustPalette()
    {
        var wallShapes = new System.Collections.Generic.List<int>();
        var floorShapes = new System.Collections.Generic.List<int>();
        // 従来の金属4種の形状の回帰テスト。新しい自然系6種は ScenicStageWorldTests で確認する。
        foreach (StageTheme theme in new[] { StageTheme.ObsidianRelay, StageTheme.VioletVault, StageTheme.AmberFoundry, StageTheme.AzurePrism })
        {
            var go = new GameObject("ShapeTest");
            try
            {
                var stage = go.AddComponent<FloorRenderer>(); stage.Build(theme);
                wallShapes.Add(stage.transform.Find("WallPanels").GetComponent<MeshFilter>().sharedMesh.vertexCount);
                floorShapes.Add(stage.transform.Find("FloorPanels").GetComponent<MeshFilter>().sharedMesh.vertexCount);
            }
            finally { Object.DestroyImmediate(go); }
        }
        Assert.GreaterOrEqual(wallShapes.Distinct().Count(), 3);
        Assert.AreEqual(4, floorShapes.Distinct().Count());
    }

    [Test]
    public void DestroyReleasesVariantAssets()
    {
        var go = new GameObject("ReleaseTest");
        var stage = go.AddComponent<FloorRenderer>(); stage.Build(StageTheme.AzurePrism);
        var mesh = go.GetComponentInChildren<MeshFilter>().sharedMesh;
        var material = go.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        Object.DestroyImmediate(go);
        Assert.IsTrue(mesh == null); Assert.IsTrue(material == null);
    }

    [Test]
    public void GatePreservesJudgePanelAndUsesSharedBeatInlay()
    {
        var guide = new GameObject("JudgeGuide");
        var panel = new GameObject("JudgePanel", typeof(MeshRenderer)); panel.transform.SetParent(guide.transform);
        panel.transform.localPosition = new Vector3(.1f, .2f, .3f); panel.transform.localScale = new Vector3(7, 4, .1f);
        var original = new Material(Resources.Load<Shader>("Stage/ObsidianMetal")); panel.GetComponent<MeshRenderer>().sharedMaterial = original;
        var position = panel.transform.localPosition; var size = panel.transform.localScale;
        try
        {
            GameStageSkin.RestyleJudgeGuide(guide);
            var gate = guide.transform.Find("JudgeGate"); var frame = gate.GetComponent<JudgeGateFrame>();
            Assert.IsNotNull(frame); Assert.AreEqual(position, panel.transform.localPosition); Assert.AreEqual(size, panel.transform.localScale);
            Assert.IsEmpty(gate.GetComponentsInChildren<Collider>());
            Assert.IsNotNull(gate.Find("GateHousing")); Assert.IsNotNull(gate.Find("GateOuterBevel"));
            var light = gate.Find("GateTop").GetComponent<MeshRenderer>().sharedMaterial;
            foreach (var name in new[] { "GateBottom", "GateLeft", "GateRight" })
                Assert.AreSame(light, gate.Find(name).GetComponent<MeshRenderer>().sharedMaterial);
            Assert.AreNotSame(light, gate.Find("GateHousing").GetComponent<MeshRenderer>().sharedMaterial);
            Assert.IsNotNull(GateBeatPulse.Ensure(120, 0, null));
            var copy = panel.GetComponent<MeshRenderer>().sharedMaterial;
            GameStageSkin.RestyleJudgeGuide(guide);
            Assert.AreSame(copy, panel.GetComponent<MeshRenderer>().sharedMaterial, "二重適用でパネル材質を増やさない");
            foreach (var f in gate.GetComponentsInChildren<MeshFilter>())
                foreach (var v in f.sharedMesh.vertices)
                    Assert.IsTrue(Mathf.Abs(v.x) >= size.x * .5f - .7f || Mathf.Abs(v.y) >= size.y * .5f - .7f, "中央のノーツ領域を覆わない");
            Object.DestroyImmediate(gate.gameObject);
            Assert.IsTrue(light == null); Assert.IsTrue(copy == null);
            Assert.AreSame(original, panel.GetComponent<MeshRenderer>().sharedMaterial);
        }
        finally { Object.DestroyImmediate(guide); Object.DestroyImmediate(original); }
    }
}
