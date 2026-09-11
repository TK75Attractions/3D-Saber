using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

public class StageDynamicsTests
{
    private GameObject root;
    [TearDown] public void Cleanup() { if(root!=null) Object.DestroyImmediate(root); }

    [TestCase(StageTheme.ObsidianRelay,"FloorPanels",1)]
    [TestCase(StageTheme.VioletVault,"FloorPanels",2)]
    [TestCase(StageTheme.AzurePrism,"WallPanels",3)]
    public void MetalPanelsUsePerPlateAnchorsAndSongClock(StageTheme theme,string part,int style)
    {
        root=new GameObject("TestStage"); var stage=root.AddComponent<FloorRenderer>(); stage.Build(theme);
        var filter=root.transform.Find(part).GetComponent<MeshFilter>();
        var renderer=filter.GetComponent<MeshRenderer>();
        Assert.AreEqual(style,renderer.sharedMaterial.GetFloat("_MotionStyle"));
        var anchors=new List<Vector4>(); filter.sharedMesh.GetUVs(1,anchors);
        Assert.AreEqual(filter.sharedMesh.vertexCount,anchors.Count);
        Assert.IsTrue(anchors.All(a=>a.w==1)); Assert.Greater(anchors.Distinct().Count(),10);
        Assert.Greater(renderer.localBounds.size.y,filter.sharedMesh.bounds.size.y);
        stage.Tick(7.25,.8f);
        Assert.AreEqual(7.25f,renderer.sharedMaterial.GetFloat("_MotionTime"));
        Assert.AreEqual(.8f,renderer.sharedMaterial.GetFloat("_Chorus"));
        stage.Tick(double.NaN,1); Assert.AreEqual(7.25,stage.LastTickSeconds);
        stage.enabled=false; stage.Tick(9,1); Assert.AreEqual(7.25,stage.LastTickSeconds);
        stage.enabled=true; stage.Tick(-1,float.NaN); Assert.AreEqual(0,stage.LastTickSeconds); Assert.AreEqual(0,stage.ChorusIntensity);
        Assert.IsNull(typeof(FloorRenderer).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public));
    }

    [Test]
    public void TimelineHasSmoothEntryExitAndDeterministicRewind()
    {
        var timeline=new StagePerformanceTimeline { sections=new[]{new StagePerformanceTimeline.Section {
            startSeconds=10,endSeconds=30,fadeInSeconds=2,fadeOutSeconds=4,intensity=.8f }} };
        Assert.AreEqual(0,timeline.Evaluate(9)); Assert.AreEqual(0,timeline.Evaluate(10));
        Assert.That(timeline.Evaluate(11),Is.EqualTo(.4f).Within(.0001));
        Assert.AreEqual(.8f,timeline.Evaluate(12)); Assert.AreEqual(.8f,timeline.Evaluate(26));
        Assert.That(timeline.Evaluate(28),Is.EqualTo(.4f).Within(.0001));
        Assert.AreEqual(0,timeline.Evaluate(30)); Assert.AreEqual(.8f,timeline.Evaluate(12));
        Assert.AreEqual(0,timeline.Evaluate(double.NaN)); Assert.AreEqual(0,timeline.Evaluate(double.PositiveInfinity));
    }

    [Test]
    public void MissingAndInvalidSectionsCannotBreakGameplay()
    {
        Assert.AreEqual(0,StagePerformanceTimeline.Load("missing-stage-song").Evaluate(60));
        Assert.AreEqual(0,StagePerformanceTimeline.Load("../bad").Evaluate(60));
        var timeline=new StagePerformanceTimeline { sections=new[]{null,
            new StagePerformanceTimeline.Section {startSeconds=5,endSeconds=3},
            new StagePerformanceTimeline.Section {startSeconds=0,endSeconds=20,intensity=float.NaN},
            new StagePerformanceTimeline.Section {startSeconds=0,endSeconds=20,fadeInSeconds=float.PositiveInfinity}} };
        Assert.AreEqual(0,timeline.Evaluate(10));
        timeline.sections=null; Assert.AreEqual(0,timeline.Evaluate(10));
    }

    [Test]
    public void OverlappingAndShortSectionsStayBoundedAndContinuous()
    {
        var timeline=new StagePerformanceTimeline {sections=new[]{
            new StagePerformanceTimeline.Section {startSeconds=1,endSeconds=2,fadeInSeconds=8,fadeOutSeconds=9},
            new StagePerformanceTimeline.Section {startSeconds=1.2,endSeconds=2.2,intensity=4}}};
        for(double t=0;t<3;t+=.01)
        {
            float a=timeline.Evaluate(t),b=timeline.Evaluate(t+.0001);
            Assert.That(a,Is.InRange(0,1)); Assert.Less(Mathf.Abs(a-b),.002);
        }
    }

    [TestCase(StageTheme.AbyssalRuins)] [TestCase(StageTheme.SkySanctuary)] [TestCase(StageTheme.MoonlitGarden)]
    [TestCase(StageTheme.CrystalGrotto)] [TestCase(StageTheme.AstralOrbit)] [TestCase(StageTheme.DesertSanctum)]
    public void ChorusKeepsMovingSceneryOutsideNoteVolume(StageTheme theme)
    {
        root=new GameObject("ScenicTest"); var floor=root.AddComponent<FloorRenderer>(); floor.Build(theme);
        var world=root.GetComponentInChildren<ScenicStageWorld>();
        Assert.LessOrEqual(root.GetComponentsInChildren<MeshFilter>().Length,32);
        for(int frame=0;frame<16;frame++)
        {
            world.Tick(frame*2.11,frame%2==0?1:.5f);
            foreach(var f in root.GetComponentsInChildren<MeshFilter>())
                foreach(var v in f.sharedMesh.vertices)
                {
                    Vector3 p=root.transform.InverseTransformPoint(f.transform.TransformPoint(v));
                    Assert.IsTrue(Mathf.Abs(p.x)>=5.8f || p.y<=floor.floorY+.13f || p.y>=4.8f || p.z>=45,theme+" / "+f.name+" / "+p);
                }
        }
        world.Tick(5,0); var children=world.GetComponentsInChildren<Transform>();
        var positions=children.Select(t=>t.localPosition).ToArray();
        world.Tick(5,1); Assert.IsTrue(children.Where((t,i)=>Vector3.Distance(t.localPosition,positions[i])>.4f).Any());
        world.Tick(5,0); for(int i=0;i<children.Length;i++) Assert.AreEqual(positions[i],children[i].localPosition);
        Assert.IsEmpty(root.GetComponentsInChildren<Collider>()); Assert.IsEmpty(root.GetComponentsInChildren<Light>());
    }

    [Test]
    public void SkyHasVisibleLayeredAmbientMotion()
    {
        root=new GameObject("SkyTest"); var floor=root.AddComponent<FloorRenderer>(); floor.Build(StageTheme.SkySanctuary);
        var world=root.GetComponentInChildren<ScenicStageWorld>(); Assert.AreEqual(16,world.MovingObjectCount);
        Assert.AreEqual(2,world.GetComponentsInChildren<Transform>().Count(t=>t.name.StartsWith("TempleWindWheel")));
        Assert.AreEqual(2,world.GetComponentsInChildren<Transform>().Count(t=>t.name.StartsWith("SkyBirds")));
        var cloud=world.GetComponentsInChildren<Transform>().First(t=>t.name.StartsWith("DriftingCloud"));
        world.Tick(0); Vector3 p=cloud.localPosition; world.Tick(4);
        Assert.Greater(Vector3.Distance(p,cloud.localPosition),1);
    }
}
