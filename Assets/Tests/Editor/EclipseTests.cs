using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class EclipseTests
{
    private GameObject root;

    private static StagePerformanceTimeline.Section Section(double start, double end, float intensity = 1) =>
        new StagePerformanceTimeline.Section { startSeconds = start, endSeconds = end, intensity = intensity };

    private static StagePerformanceTimeline Timeline(params StagePerformanceTimeline.Section[] sections) =>
        new StagePerformanceTimeline { sections = sections };

    [TearDown]
    public void Cleanup() { if (root != null) Object.DestroyImmediate(root); }

    [Test]
    public void SelectsOneStrongestSectionAndLaterSectionOnATieRegardlessOfArrayOrder()
    {
        var timeline = Timeline(Section(100,120,.9f), Section(10,30,1), Section(50,70,1), Section(150,170,.7f));
        Assert.AreEqual(0,timeline.EvaluateEclipse(20));
        Assert.AreEqual(1,timeline.EvaluateEclipse(60));
        Assert.AreEqual(0,timeline.EvaluateEclipse(110));
        Assert.AreEqual(0,timeline.EvaluateEclipse(160));
        Array.Reverse(timeline.sections);
        Assert.AreEqual(1,timeline.EvaluateEclipse(60));
        Assert.AreEqual(0,timeline.EvaluateEclipse(20));
    }

    [Test]
    public void OnlyAnAuthoredStrongSectionAtLeastEightSecondsLongIsEligible()
    {
        var timeline = Timeline(Section(10,17.999,1), Section(30,60,.6499f), Section(70,78,.65f));
        Assert.AreEqual(0,timeline.EvaluateEclipse(14));
        Assert.AreEqual(0,timeline.EvaluateEclipse(45));
        Assert.AreEqual(1,timeline.EvaluateEclipse(74));
        Assert.AreEqual(0,timeline.EvaluateEclipse(70));
        Assert.AreEqual(0,timeline.EvaluateEclipse(78));
    }

    [Test]
    public void CapsTheWindowAtSixteenSecondsAndKeepsItInsideTheChosenSection()
    {
        var timeline = Timeline(Section(10,50));
        foreach (double time in new[]{0d,10,21.999,22,38,38.001,50,100})
            Assert.AreEqual(0,timeline.EvaluateEclipse(time),"time="+time);
        Assert.AreEqual(1,timeline.EvaluateEclipse(30));
        Assert.Greater(timeline.EvaluateEclipse(22.01),0);
        Assert.Greater(timeline.EvaluateEclipse(37.99),0);
    }

    [Test]
    public void RemainsBoundedSmoothAndReproducibleAcrossPauseAndSeek()
    {
        var timeline = Timeline(Section(20,28));
        for (double t = 19.9; t <= 28.1; t += .01)
        {
            float a = timeline.EvaluateEclipse(t), b = timeline.EvaluateEclipse(t+.001);
            Assert.That(a,Is.InRange(0f,1f));
            Assert.Less(Mathf.Abs(a-b),.0005f);
        }
        float earlier = timeline.EvaluateEclipse(21.75);
        timeline.EvaluateEclipse(80);
        Assert.AreEqual(earlier,timeline.EvaluateEclipse(21.75));
        Assert.AreEqual(earlier,timeline.EvaluateEclipse(21.75));
    }

    [Test]
    public void EmptyAndMalformedDataNeverCreatesAnEclipse()
    {
        Assert.AreEqual(0,Timeline().EvaluateEclipse(10));
        Assert.AreEqual(0,Timeline(null).EvaluateEclipse(10));
        var invalidFade = Section(0,100); invalidFade.fadeInSeconds = float.NaN;
        var timeline = Timeline(null, Section(-1,10), Section(4,4), Section(9,2),
            Section(double.NaN,50), Section(0,double.PositiveInfinity),
            Section(0,100,float.NaN), Section(0,100,float.PositiveInfinity), invalidFade);
        foreach (double time in new[]{0d,1,10,25,50,99,double.NaN,double.NegativeInfinity,double.PositiveInfinity})
            Assert.AreEqual(0,timeline.EvaluateEclipse(time));
        var valid = Timeline(Section(0,8));
        foreach (double time in new[]{-1d,0,double.NaN,double.NegativeInfinity,double.PositiveInfinity})
            Assert.AreEqual(0,valid.EvaluateEclipse(time));
    }

    [Test]
    public void AndalusiaWithoutAuthoredSectionsStaysUnchanged()
    {
        var timeline = StagePerformanceTimeline.Load("Andalusia");
        Assert.IsEmpty(timeline.sections);
        for (double time = 0; time < 240; time += .5) Assert.AreEqual(0,timeline.EvaluateEclipse(time));
    }

    private ScenicStageWorld Build(StageTheme theme)
    {
        root = new GameObject("EclipseTest");
        var stage = root.AddComponent<FloorRenderer>(); stage.Build(theme);
        return root.GetComponentInChildren<ScenicStageWorld>();
    }

    [Test]
    public void EclipseMaterialScopeContainsOnlyTheLargePlanetAndAddsNoSceneObjects()
    {
        var world = Build(StageTheme.AstralOrbit);
        var renderers = world.GetComponentsInChildren<Renderer>();
        var planet = renderers.Single(r=>r.sharedMaterial.name=="BandedPlanet");
        var material = planet.sharedMaterial;
        var bounds = material.GetVector("_EclipsePlanet");
        Assert.AreEqual(new Vector4(17,12,87,11),bounds);
        var center = new Vector3(bounds.x,bounds.y,bounds.z);
        var vertices = planet.GetComponent<MeshFilter>().sharedMesh.vertices;
        Assert.IsTrue(vertices.Any(v=>v.x>0)); Assert.IsTrue(vertices.Any(v=>v.x<0));
        foreach (var v in vertices)
        {
            float distance = Vector3.Distance(v,center);
            if (v.x>0) Assert.LessOrEqual(distance,bounds.w+.001f,"大惑星を局所範囲に収める");
            else Assert.Greater(distance,bounds.w*2,"小惑星は局所範囲外");
        }
        world.Tick(4,.8f,0);
        var transforms = world.GetComponentsInChildren<Transform>();
        var positions = transforms.Select(t=>t.localPosition).ToArray();
        var rotations = transforms.Select(t=>t.localRotation).ToArray();
        int moving = world.MovingObjectCount;
        world.Tick(4,.8f,1);
        Assert.AreEqual(1,world.EclipseIntensity);
        Assert.AreEqual(1,material.GetFloat("_Eclipse"));
        Assert.AreEqual(renderers.Length,world.GetComponentsInChildren<Renderer>().Length);
        Assert.AreEqual(transforms.Length,world.GetComponentsInChildren<Transform>().Length);
        Assert.AreEqual(moving,world.MovingObjectCount);
        CollectionAssert.AreEqual(positions,transforms.Select(t=>t.localPosition).ToArray());
        CollectionAssert.AreEqual(rotations,transforms.Select(t=>t.localRotation).ToArray());
        Assert.IsEmpty(world.GetComponentsInChildren<Camera>());
        Assert.IsEmpty(world.GetComponentsInChildren<Light>());
        foreach (var renderer in renderers.Where(r=>r!=planet && r.sharedMaterial.HasProperty("_Eclipse")))
        {
            Assert.AreEqual(0,renderer.sharedMaterial.GetFloat("_Eclipse"),renderer.name);
            Assert.AreEqual(0,renderer.sharedMaterial.GetVector("_EclipsePlanet").w,renderer.name);
        }
    }

    [Test]
    public void ResetDisableAndInvalidValuesRemoveTheShadow()
    {
        var world = Build(StageTheme.AstralOrbit);
        var material = world.GetComponentsInChildren<Renderer>().Single(r=>r.sharedMaterial.name=="BandedPlanet").sharedMaterial;
        world.Tick(4,0,1); world.Tick(0,0,1);
        Assert.AreEqual(0,material.GetFloat("_Eclipse"));
        world.Tick(4,0,1); world.Tick(5);
        Assert.AreEqual(0,material.GetFloat("_Eclipse"),"既存の2引数呼出しは通常表示に戻す");
        world.Tick(4,0,1); world.enabled = false;
        Assert.AreEqual(0,world.EclipseIntensity); Assert.AreEqual(0,material.GetFloat("_Eclipse"));
        world.enabled = true;
        foreach (float value in new[]{float.NaN,float.NegativeInfinity,float.PositiveInfinity,-1f})
        {
            world.Tick(4,0,1); world.Tick(4,0,value);
            Assert.AreEqual(0,world.EclipseIntensity); Assert.AreEqual(0,material.GetFloat("_Eclipse"));
        }
        world.Tick(4,0,2); Assert.AreEqual(1,world.EclipseIntensity);
        world.Tick(double.NaN,0,1); Assert.AreEqual(0,world.EclipseIntensity);
        world.Tick(4,0,1); world.Tick(-1,0,1); Assert.AreEqual(0,world.EclipseIntensity);
    }

    [TestCase(StageTheme.AbyssalRuins)] [TestCase(StageTheme.SkySanctuary)]
    [TestCase(StageTheme.MoonlitGarden)] [TestCase(StageTheme.CrystalGrotto)] [TestCase(StageTheme.DesertSanctum)]
    public void OtherBackgroundsDoNotReceiveEclipseValues(StageTheme theme)
    {
        var world = Build(theme); world.Tick(4,.5f,1);
        Assert.AreEqual(0,world.EclipseIntensity);
        foreach (var material in world.GetComponentsInChildren<Renderer>().Select(r=>r.sharedMaterial).Where(m=>m.HasProperty("_Eclipse")))
            Assert.AreEqual(0,material.GetFloat("_Eclipse"),material.name);
    }
}
