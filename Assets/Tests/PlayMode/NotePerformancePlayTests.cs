using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 再利用で壊れやすい判定・購読・表示・シーン所有権を実際の寿命で確認する。
public class NotePerformancePlayTests
{
    Scene previous,scene;
    bool projector;
    NoteSpawner spawner;
    CuttableNote current;
    [SetUp] public void Setup()
    {
        previous=SceneManager.GetActiveScene(); scene=SceneManager.CreateScene("NotePerformance"); SceneManager.SetActiveScene(scene);
        projector=DisplaySettings.ProjectorMode; DisplaySettings.SetProjectorModeForTest(true);
        var prefab=GameObject.CreatePrimitive(PrimitiveType.Cube); prefab.name="PerformancePrefab"; prefab.SetActive(false);
        spawner=new GameObject("PerformanceSpawner").AddComponent<NoteSpawner>(); spawner.notePrefab=prefab;
        spawner.OnNoteSpawned+=n=>current=n;
    }
    [UnityTearDown] public IEnumerator Cleanup()
    {
        DisplaySettings.SetProjectorModeForTest(projector); SceneManager.SetActiveScene(previous);
        if(scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        yield return null;
    }
    ChartData Chart(int count=1,float length=0)
    {
        var chart=new ChartData(); chart.notes.Add(new NoteData { time=1000,color="blue",direction="up",count=count,lengthMs=length }); return chart;
    }
    void Spawn(int count=1,float length=0) { spawner.SetChart(Chart(count,length)); spawner.Tick(1); }
    void Cut() { current.Cut(current.transform.position,Vector3.up*6,CutDirection.Up,SaberHand.Left); }

    [UnityTest] public IEnumerator ReuseKeepsMeshesMaterialsAndResetsScoreSubscriptions()
    {
        var score=new GameObject("Score").AddComponent<ScoreManager>(); score.Bind(spawner);
        Spawn(); yield return null;
        var first=current; var materials=first.GetComponentsInChildren<Renderer>().Select(r=>r.sharedMaterial).ToArray();
        var meshes=first.GetComponentsInChildren<MeshFilter>().Select(f=>f.sharedMesh).ToArray();
        int created=spawner.CreatedNoteCount;
        for(int i=0;i<30;i++) {
            Cut(); Assert.IsTrue(current.IsCut); spawner.Tick(1.01);
            spawner.SetChart(Chart()); spawner.Tick(1);
            Assert.AreSame(first,current); Assert.False(current.IsCut); Assert.False(current.IsMissed); Assert.True(current.LastCutCorrectDirection);
            Assert.AreEqual(1,current.RemainingCuts); Assert.AreEqual(SaberHand.Any,current.LastCutterHand);
        }
        Assert.AreEqual(30,score.HitCount+score.MissCount,"以前のスコア購読が残ると二重加算になる");
        Assert.AreEqual(created,spawner.CreatedNoteCount);
        CollectionAssert.AreEqual(materials,current.GetComponentsInChildren<Renderer>().Select(r=>r.sharedMaterial));
        CollectionAssert.AreEqual(meshes,current.GetComponentsInChildren<MeshFilter>().Select(f=>f.sharedMesh));
    }
    [UnityTest] public IEnumerator MissThenReuseRestoresColorTimingFramesAndLongCount()
    {
        Spawn(3,1400); yield return null;
        var first=current; var body=first.GetComponent<Renderer>().sharedMaterial; var color=body.GetColor("_BaseColor");
        Cut(); current.MarkMiss(); Assert.AreEqual(2,current.RemainingCuts);
        spawner.Tick(10); Assert.False(first.gameObject.activeInHierarchy);
        Spawn(3,2800);
        Assert.AreSame(first,current); Assert.AreEqual(3,current.RemainingCuts); Assert.AreEqual(2.8f,current.OverrideLingerSeconds,.001);
        Assert.AreEqual("3",current.countLabel.text); Assert.True(current.countLabel.gameObject.activeInHierarchy);
        Assert.True(current.TimingCue.GhostRoot.activeInHierarchy); Assert.True(current.TimingCue.ApproachRoot.activeInHierarchy);
        Assert.AreEqual(color,body.GetColor("_BaseColor"));
        Assert.AreEqual(0,current.transform.Cast<Transform>().Count(t=>t.name=="Crack" && t.gameObject.activeSelf));
    }
    [UnityTest] public IEnumerator OldBladeContactCannotCutRecycledNote()
    {
        Spawn(); yield return null;
        var rig=new GameObject("Rig"); var tracker=rig.AddComponent<SaberTracker>(); var judge=rig.AddComponent<SaberCutJudge>();
        judge.autonomous=false; judge.saber=tracker; judge.maxCutDistance=5;
        tracker.Tick(new Vector3(-2,0,0),.02f); tracker.Tick(Vector3.zero,.02f); judge.TryCut();
        Assert.AreEqual(1,judge.PendingCount);
        var first=current; uint version=first.SpawnVersion; Spawn(); Assert.AreSame(first,current); Assert.AreNotEqual(version,current.SpawnVersion);
        tracker.Tick(new Vector3(2,0,0),.02f); judge.TryCut();
        Assert.False(current.IsCut); Assert.AreEqual(0,judge.PendingCount);
    }
    [UnityTest] public IEnumerator RegistryTracksActivationAndRemovesDestroyedNotes()
    {
        Spawn(); yield return null; var first=current;
        Assert.Contains(first,CuttableNote.ActiveNotes.ToList());
        first.gameObject.SetActive(false); Assert.False(CuttableNote.ActiveNotes.Contains(first));
        first.gameObject.SetActive(true); Assert.AreEqual(1,CuttableNote.ActiveNotes.Count(n=>n==first));
        Object.Destroy(first.gameObject); yield return null;
        Assert.False(CuttableNote.ActiveNotes.Contains(first));
    }
    [UnityTest] public IEnumerator FragmentsReuseTheirMeshesWithoutPhysicsAndReleaseOnSceneExit()
    {
        Spawn(3); yield return null;
        var pool=spawner.FragmentPool; int created=pool.CreatedCount;
        Cut(); Cut(); Cut(); spawner.Tick(1.01);
        var pieces=pool.GetComponentsInChildren<SlicePieceDecay>(); Assert.AreEqual(8,pieces.Length);
        Assert.True(pieces.All(p=>p.GetComponent<Rigidbody>()==null && p.GetComponent<Collider>()==null));
        var meshes=pool.GetComponentsInChildren<SlicePieceDecay>(true).Select(p=>p.ReusableMesh).ToArray();
        var materials=pool.GetComponentsInChildren<Renderer>(true).Select(p=>p.sharedMaterial).ToArray();
        foreach(var p in pieces) p.Step(4);
        Spawn(3); Cut(); Cut(); Cut();
        Assert.AreEqual(created,pool.CreatedCount);
        Object.Destroy(spawner.gameObject); yield return null; yield return null;
        Assert.True(meshes.All(m=>m==null),"未使用の事前生成メッシュも解放する");
        Assert.True(materials.All(m=>m==null));
    }
    [UnityTest] public IEnumerator CombinedGeometryPreservesShapeAndCutsRendererCount()
    {
        Spawn(); yield return null; yield return null;
        Assert.AreEqual(2,current.transform.Find("Arrow").GetComponentsInChildren<Renderer>().Length);
        Assert.AreEqual(1,current.TimingCue.GhostRoot.GetComponentsInChildren<Renderer>().Length);
        Assert.AreEqual(1,current.TimingCue.ApproachRoot.GetComponentsInChildren<Renderer>().Length);
        var frame=current.TimingCue.GhostRoot.GetComponentInChildren<MeshFilter>().sharedMesh;
        Assert.AreEqual(4*24,frame.vertexCount); Assert.AreEqual(4*36,frame.triangles.Length);
        var rail=current.transform.Find("EdgeRails").GetComponent<MeshFilter>().sharedMesh;
        Assert.AreEqual(4*24,rail.vertexCount); Assert.AreEqual(4*36,rail.triangles.Length);
    }
    [UnityTest] public IEnumerator ChangingChartsEvictsUnusedKindsAndKeepsTheNextChartReusable()
    {
        Spawn(); yield return null;
        var previousNote=current; var previousArrow=previousNote.transform.Find("Arrow").GetComponentInChildren<Renderer>().sharedMaterial;
        Spawn(3); yield return null; yield return null;
        Assert.True(previousNote==null); Assert.True(previousArrow==null);
        int created=spawner.CreatedNoteCount; var longNote=current;
        for(int i=0;i<6;i++) { Cut(); Cut(); Cut(); spawner.Tick(1.01); Spawn(3); Assert.AreSame(longNote,current); }
        Assert.AreEqual(created,spawner.CreatedNoteCount);
    }
}
