using NUnit.Framework;
using UnityEngine;

public class FloorTimingGuideTests
{
    GameObject root, prefab;
    NoteSpawner spawner;
    [SetUp] public void Setup()
    {
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("GuideTest");
        spawner = root.AddComponent<NoteSpawner>();
        prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.AddComponent<CuttableNote>();
        spawner.notePrefab = prefab; spawner.noteRoot = root.transform;
        spawner.buildTimingCues = false;
        spawner.ConfigureFloorGuide(-2.5f);
    }
    [TearDown] public void Cleanup()
    {
        Object.DestroyImmediate(root); Object.DestroyImmediate(prefab);
        DisplaySettings.SetReducedEffectsForTest(false);
    }
    void Chart(string direction = null, int count = 1)
    {
        var chart = new ChartData { offsetMs = 125, coordScale = 1.5f };
        chart.notes.Add(new NoteData { time = 3000, x = -1, y = 1, count = count, direction = direction });
        spawner.SetExtraOffsetSeconds(-.025);
        spawner.SetChart(chart);
    }
    [TestCase(.6f)] [TestCase(1f)] [TestCase(2.4f)]
    public void FloorAndNoteReachCustomJudgmentLineTogetherWithOffsets(float approach)
    {
        spawner.approachTime = approach; spawner.judgeZ = 1.75f; spawner.spawnZ = 30;
        Chart();
        foreach (double lead in new[] { (double)approach * .8, approach * .3, 0.0 })
        {
            spawner.Tick(3.1 - lead);
            Assert.AreEqual(1, spawner.FloorGuide.MarkerCount);
            var note = spawner.LiveNotes[0];
            var vertices = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices;
            // 先頭8頂点は固定線、次の4頂点はノーツ下のガイド外周。
            Vector3 center = (vertices[8] + vertices[10]) * .5f;
            Assert.AreEqual(note.transform.position.z, center.z, .0001f);
            Assert.AreEqual(-1.5f, center.x, .0001f);
            if (lead == 0) Assert.AreEqual(spawner.judgeZ, center.z, .0001f);
        }
    }
    [TestCase("up")] [TestCase("down")] [TestCase("left")] [TestCase("right")]
    [TestCase("upleft")] [TestCase("upright")] [TestCase("downleft")] [TestCase("downright")]
    public void FloorArrowPointsInTheRequiredDirection(string direction)
    {
        Chart(direction); spawner.Tick(2.8);
        var vertices = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices;
        var center = spawner.LiveNotes[0].transform.position;
        var tip = vertices[8 + 4] - center;
        var projected = new Vector2(tip.x, tip.z / 2.2f).normalized;
        Assert.Greater(Vector2.Dot(CutDirectionHelper.ToVector(CutDirectionHelper.Parse(direction)), projected), .999f);
        Assert.AreEqual(1, spawner.FloorGuide.MarkerCount);
    }
    [Test] public void PauseLowEffectsAndResetDoNotDesynchronizeOrLeaveMarkers()
    {
        Chart(); spawner.Tick(2.8);
        var mesh = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        var before = mesh.vertices;
        DisplaySettings.SetReducedEffectsForTest(true); spawner.Tick(2.8);
        CollectionAssert.AreEqual(before, mesh.vertices);
        spawner.enabled = false; spawner.Tick(2.8); Assert.AreEqual(0, mesh.vertexCount);
        spawner.enabled = true; spawner.Tick(2.8); Assert.AreEqual(1, spawner.FloorGuide.MarkerCount);
        spawner.SetChart(new ChartData()); Assert.AreEqual(0, mesh.vertexCount);
    }
    [Test] public void LongOnsetArrivesAtLineAndFinishedOrMissedNotesLeaveNoGuide()
    {
        Chart(null, 4); spawner.Tick(3.1);
        Assert.AreEqual(1, spawner.FloorGuide.MarkerCount);
        Assert.AreEqual(spawner.judgeZ, spawner.LiveNotes[0].transform.position.z, .0001f);
        spawner.Tick(3.25); Assert.AreEqual(0, spawner.FloorGuide.MarkerCount);
        Chart(); spawner.Tick(3.1); spawner.LiveNotes[0].MarkMiss();
        spawner.Tick(3.1); Assert.AreEqual(0, spawner.FloorGuide.MarkerCount);
    }
}
