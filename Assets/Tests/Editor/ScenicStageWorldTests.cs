using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public class ScenicStageWorldTests
{
    private GameObject root;
    private FloorRenderer stage;
    private ScenicStageWorld world;
    private static readonly StageTheme[] Themes = { StageTheme.AbyssalRuins,StageTheme.SkySanctuary,StageTheme.MoonlitGarden,
        StageTheme.CrystalGrotto,StageTheme.AstralOrbit,StageTheme.DesertSanctum };
    private void Build(StageTheme theme)
    {
        root = new GameObject("WorldTest"); stage = root.AddComponent<FloorRenderer>(); stage.Build(theme);
        world = root.GetComponentInChildren<ScenicStageWorld>();
    }
    [TearDown] public void Cleanup() { if (root != null) Object.DestroyImmediate(root); }

    [TestCaseSource(nameof(Themes))]
    public void EveryWorldBuildsDistinctSceneryAndIsIdempotent(StageTheme theme)
    {
        Build(theme); Assert.IsNotNull(world); Assert.AreEqual(theme,world.Theme);
        Assert.AreSame(world,ScenicStageWorld.Ensure(stage));
        int children = root.GetComponentsInChildren<Transform>().Length;
        stage.Build(); stage.Build(StageTheme.AmberFoundry);
        Assert.AreEqual(theme,stage.ActiveTheme); Assert.AreEqual(children,root.GetComponentsInChildren<Transform>().Length);
        Assert.IsNull(root.transform.Find("WallPanels"),"従来の工業壁の色替えではなく独立した世界");
        Assert.GreaterOrEqual(world.MovingObjectCount,4);
        Assert.IsNull(FoundryStageMotion.Ensure(stage));
    }

    [TestCaseSource(nameof(Themes))]
    public void GeometryAndDrawBudgetAreBoundedAndFinite(StageTheme theme)
    {
        Build(theme);
        Assert.IsEmpty(root.GetComponentsInChildren<Collider>());
        Assert.IsEmpty(root.GetComponentsInChildren<Light>());
        var filters = root.GetComponentsInChildren<MeshFilter>();
        Assert.LessOrEqual(filters.Length,32);
        Assert.Less(filters.Sum(f=>f.sharedMesh.vertexCount),150000);
        Assert.LessOrEqual(root.GetComponentsInChildren<Renderer>().Select(r=>r.sharedMaterial).Distinct().Count(),10);
        foreach (var f in filters)
        {
            var mesh = f.sharedMesh;
            Assert.Greater(mesh.vertexCount,0); Assert.AreEqual(mesh.vertexCount,mesh.normals.Length);
            Assert.IsTrue(mesh.triangles.All(i=>i>=0 && i<mesh.vertexCount));
            foreach (var v in mesh.vertices) Assert.IsFalse(float.IsNaN(v.sqrMagnitude)||float.IsInfinity(v.sqrMagnitude),f.name);
        }
    }

    [TestCaseSource(nameof(Themes))]
    public void MovingMeshesLeaveCentralNoteVolumeClear(StageTheme theme)
    {
        Build(theme);
        for (int i = 0; i < 12; i++)
        {
            world.Tick(i*1.41);
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
                foreach (var v in filter.sharedMesh.vertices)
                {
                    Vector3 p = stage.transform.InverseTransformPoint(filter.transform.TransformPoint(v));
                    Assert.IsTrue(Mathf.Abs(p.x)>=5.8f || p.y<=stage.floorY+.13f || p.y>=4.8f || p.z>=45,
                        theme+" / "+filter.name+" / "+p);
                }
        }
    }

    [TestCaseSource(nameof(Themes))]
    public void SongClockSupportsFreezeRewindAndFiniteInputs(StageTheme theme)
    {
        Build(theme); world.Tick(2.5);
        var children = world.GetComponentsInChildren<Transform>();
        var pos = children.Select(t=>t.localPosition).ToArray(); var rot = children.Select(t=>t.localRotation).ToArray();
        world.Tick(8.8);
        Assert.IsTrue(children.Where((t,i)=>Vector3.Distance(t.localPosition,pos[i])>.005f || Quaternion.Angle(t.localRotation,rot[i])>.05f).Any());
        world.Tick(2.5);
        for (int i = 0; i < children.Length; i++)
        {
            Assert.AreEqual(pos[i],children[i].localPosition); Assert.Less(Quaternion.Angle(rot[i],children[i].localRotation),.001f);
        }
        world.Tick(double.NaN); world.Tick(double.PositiveInfinity); Assert.AreEqual(2.5,world.LastTickSeconds);
        world.enabled = false; world.Tick(4); Assert.AreEqual(2.5,world.LastTickSeconds);
        Assert.AreEqual(0,world.GetComponentInChildren<ParticleSystem>().particleCount);
        world.enabled = true; world.Tick(-1); Assert.AreEqual(0,world.LastTickSeconds);
        var flags = BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        foreach (var name in new[]{"Update","LateUpdate","FixedUpdate"}) Assert.IsNull(typeof(ScenicStageWorld).GetMethod(name,flags));
    }

    [TestCaseSource(nameof(Themes))]
    public void ParticlesStaySmallOutsideLaneAndUsePausedSystem(StageTheme theme)
    {
        Build(theme); world.Tick(1.3);
        var system = world.GetComponentInChildren<ParticleSystem>();
        Assert.IsNotNull(system); Assert.AreEqual(72,system.main.maxParticles);
        Assert.IsFalse(system.emission.enabled); Assert.IsFalse(system.main.playOnAwake); Assert.IsTrue(system.isPaused);
        var particles = new ParticleSystem.Particle[72]; int count = system.GetParticles(particles);
        Assert.AreEqual(72,count);
        for (int i=0;i<count;i++) Assert.Greater(Mathf.Abs(particles[i].position.x),6.8f);
    }

    [TestCaseSource(nameof(Themes))]
    public void OwnMaterialsAndMeshesAreReleasedOnRemoval(StageTheme theme)
    {
        Build(theme);
        var meshes = world.GetComponentsInChildren<MeshFilter>().Select(f=>f.sharedMesh).ToArray();
        var mats = world.GetComponentsInChildren<Renderer>().Select(f=>f.sharedMaterial).Distinct().ToArray();
        Object.DestroyImmediate(root); root = null;
        Assert.IsTrue(meshes.All(m=>m==null)); Assert.IsTrue(mats.All(m=>m==null));
    }

    [Test]
    public void TenThemesAreSelectedPerPlayNotPerSong()
    {
        Assert.AreEqual(10,StageThemeCatalog.Count); Assert.AreEqual(10,Enum.GetValues(typeof(StageTheme)).Length);
        string original = GameSession.SelectedSongId;
        var randomState = UnityEngine.Random.state;
        try
        {
            GameSession.SelectedSongId = "ElDorado";
            StageTheme previous = StageThemeCatalog.NextForPlay();
            var seen = new System.Collections.Generic.HashSet<StageTheme>{previous};
            for (int i=0;i<1000;i++)
            {
                StageTheme next = StageThemeCatalog.NextForPlay(); Assert.AreNotEqual(previous,next);
                seen.Add(next); previous = next;
            }
            Assert.AreEqual(10,seen.Count);
            Assert.AreEqual(randomState,UnityEngine.Random.state,"譜面用乱数を消費しない");
        }
        finally { GameSession.SelectedSongId = original; }
    }

    [TestCase("Stage/ScenicSurface")] [TestCase("Stage/ScenicMotes")]
    public void ShadersSupportBothRendererPathsWithoutCompilerErrors(string path)
    {
        var shader = Resources.Load<Shader>(path); Assert.IsNotNull(shader); Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
        Assert.AreEqual(2,shader.passCount);
    }
}
