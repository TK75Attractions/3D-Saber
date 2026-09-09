using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public class FoundryStageMotionTests
{
    private GameObject root;
    private FloorRenderer stage;
    private FoundryStageMotion motion;

    private void Build(StageTheme theme = StageTheme.AmberFoundry)
    {
        root = new GameObject("MotionTest");
        stage = root.AddComponent<FloorRenderer>(); stage.Build(theme);
        motion = FoundryStageMotion.Ensure(stage);
    }
    [TearDown] public void Cleanup() { if (root != null) Object.DestroyImmediate(root); }

    [TestCase(StageTheme.ObsidianRelay)] [TestCase(StageTheme.VioletVault)] [TestCase(StageTheme.AzurePrism)]
    public void OtherThemesRemainUnchanged(StageTheme theme)
    {
        Build(theme);
        Assert.IsNull(motion);
        Assert.IsEmpty(root.GetComponentsInChildren<ParticleSystem>());
    }

    [Test] public void EnsureIsScopedAndIdempotent()
    {
        Assert.IsNull(FoundryStageMotion.Ensure(null));
        Build();
        int count = motion.GetComponentsInChildren<Transform>().Length;
        Assert.AreSame(motion, FoundryStageMotion.Ensure(stage));
        Assert.AreEqual(count, motion.GetComponentsInChildren<Transform>().Length);
        Assert.AreEqual(stage.transform, motion.transform.parent);
    }

    [Test] public void DisabledArchitectureDoesNotAddEquipment()
    {
        root = new GameObject("NoWalls"); stage = root.AddComponent<FloorRenderer>();
        stage.addSideArchitecture = false; stage.Build(StageTheme.AmberFoundry);
        Assert.IsNull(FoundryStageMotion.Ensure(stage));
    }

    [Test] public void NoIndependentUpdateOrColliders()
    {
        Build();
        Assert.IsEmpty(motion.GetComponentsInChildren<Collider>());
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (string name in new[] { "Update", "LateUpdate", "FixedUpdate" })
            Assert.IsNull(typeof(FoundryStageMotion).GetMethod(name, flags));
    }

    [Test] public void RotatingGeometryAlwaysLeavesCorridorClear()
    {
        Build();
        for (int step = 0; step < 12; step++)
        {
            motion.Tick(step * .61);
            foreach (var filter in motion.GetComponentsInChildren<MeshFilter>())
            {
                Assert.AreEqual(filter.sharedMesh.vertexCount, filter.sharedMesh.normals.Length);
                foreach (var point in filter.sharedMesh.vertices)
                {
                    Vector3 v = stage.transform.InverseTransformPoint(filter.transform.TransformPoint(point));
                    Assert.IsFalse(float.IsNaN(v.sqrMagnitude) || float.IsInfinity(v.sqrMagnitude));
                    Assert.Greater(Mathf.Abs(v.x), FloorRenderer.ClearCorridorHalfWidth, filter.name);
                }
            }
        }
    }

    [Test] public void SharesGeometryAndKeepsParticleBudgetSmall()
    {
        Build();
        var filters = motion.GetComponentsInChildren<MeshFilter>();
        Assert.AreEqual(7, filters.Length);
        Assert.AreEqual(4, filters.Select(f => f.sharedMesh).Distinct().Count());
        Assert.Less(filters.Select(f => f.sharedMesh).Distinct().Sum(m => m.vertexCount), 30000);
        var systems = motion.GetComponentsInChildren<ParticleSystem>();
        Assert.AreEqual(4, systems.Length);
        foreach (var ps in systems)
        {
            Assert.AreEqual(24, ps.main.maxParticles);
            Assert.IsFalse(ps.main.playOnAwake);
            Assert.IsFalse(ps.emission.enabled);
            Assert.IsTrue(ps.isPaused);
        }
        for (int i = 0; i < 100; i++)
        {
            motion.Tick(i * .17);
            Assert.LessOrEqual(motion.LiveParticleCount, 96);
            Assert.AreEqual(motion.LiveParticleCount, systems.Sum(ps => ps.particleCount));
        }
    }

    [Test] public void ClockIsDeterministicAcrossFrameRatesAndRewind()
    {
        Build(); motion.Tick(2.3);
        var rotor = motion.transform.Find("VentilationUnit0/Rotor");
        var rotation = rotor.localRotation;
        var ps = motion.GetComponentsInChildren<ParticleSystem>()[0];
        var expected = new ParticleSystem.Particle[24];
        int count = ps.GetParticles(expected);
        Assert.Greater(count, 0);
        for (int i = 0; i < 100; i++) motion.Tick(i * .033);
        motion.Tick(2.3);
        Assert.Less(Quaternion.Angle(rotation, rotor.localRotation), .001f);
        var actual = new ParticleSystem.Particle[24];
        Assert.AreEqual(count, ps.GetParticles(actual));
        for (int i = 0; i < count; i++) Assert.AreEqual(expected[i].position, actual[i].position);
        motion.Tick(20000); motion.Tick(2.3);
        Assert.Less(Quaternion.Angle(rotation, rotor.localRotation), .001f);
    }

    [Test] public void BadClockCannotCorruptTransforms()
    {
        Build(); motion.Tick(1);
        motion.Tick(double.NaN); motion.Tick(double.PositiveInfinity); motion.Tick(double.NegativeInfinity);
        Assert.AreEqual(1, motion.LastTickSeconds);
        motion.Tick(-2); Assert.AreEqual(0, motion.LastTickSeconds);
    }

    [Test] public void DisableClearsAndNextTickRestoresSteam()
    {
        Build(); motion.Tick(1); Assert.Greater(motion.LiveParticleCount, 0);
        motion.enabled = false;
        Assert.AreEqual(0, motion.LiveParticleCount);
        Assert.IsTrue(motion.GetComponentsInChildren<ParticleSystem>().All(p => p.particleCount == 0));
        motion.Tick(2); Assert.AreEqual(1, motion.LastTickSeconds);
        motion.enabled = true; motion.Tick(1); Assert.Greater(motion.LiveParticleCount, 0);
    }

    [Test] public void VentSteamIntermittentlyStopsAndStaysOutsideLane()
    {
        Build();
        var ps = motion.GetComponentsInChildren<ParticleSystem>()[0];
        var buffer = new ParticleSystem.Particle[24];
        motion.Tick(1); Assert.Greater(ps.GetParticles(buffer), 0);
        foreach (var vent in motion.GetComponentsInChildren<ParticleSystem>())
        {
            int count = vent.GetParticles(buffer);
            for (int i = 0; i < count; i++)
                Assert.Greater(Mathf.Abs(vent.transform.TransformPoint(buffer[i].position).x), 6.7f);
        }
        motion.Tick(4.1); Assert.AreEqual(0, ps.particleCount);
        motion.Tick(6.6); Assert.Greater(ps.particleCount, 0);
    }

    [Test] public void ShaderSupportsCurrentRendererWithoutErrors()
    {
        Build();
        var shader = Resources.Load<Shader>("Stage/FoundrySteam");
        Assert.IsNotNull(shader); Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
        var material = motion.GetComponentInChildren<ParticleSystemRenderer>().sharedMaterial;
        Assert.AreSame(shader, material.shader);
        Assert.GreaterOrEqual(material.FindPass("Steam2DRenderer"), 0);
        Assert.GreaterOrEqual(material.FindPass("SteamForwardRenderer"), 0);
    }

    [Test] public void DestroyReleasesOnlyItsOwnedResources()
    {
        Build();
        var meshes = motion.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).Distinct().ToArray();
        var mats = motion.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial).Distinct().ToArray();
        var floorMat = stage.transform.Find("FloorPanels").GetComponent<MeshRenderer>().sharedMaterial;
        Object.DestroyImmediate(motion.gameObject);
        Assert.IsTrue(meshes.All(m => m == null)); Assert.IsTrue(mats.All(m => m == null));
        Assert.IsFalse(floorMat == null);
    }
}
