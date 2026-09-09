using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Amber Foundry の設備だけを動かす。床・壁本体、判定、乱数、カメラには触れない。
// Update は持たず GamePlayManager の曲時計で駆動する。粒子も手動配置で停止・巻戻しに対応。
[ExecuteAlways]
public sealed class FoundryStageMotion : MonoBehaviour
{
    public const int EquipmentCount = 4;
    public const int ParticlesPerVent = 24;
    public const float VentCycleSeconds = 5.6f;
    public const float SteamLifetimeSeconds = 2.15f;
    private const float EmissionSeconds = 1.15f;
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly List<Material> materials = new List<Material>();
    private readonly Transform[] rotors = new Transform[EquipmentCount];
    private readonly ParticleSystem[] vents = new ParticleSystem[EquipmentCount];
    private readonly ParticleSystem.Particle[] particles = new ParticleSystem.Particle[ParticlesPerVent];
    private Material steamMaterial;
    private bool built;

    public int LiveParticleCount { get; private set; }
    public double LastTickSeconds { get; private set; }

    public static FoundryStageMotion Ensure(FloorRenderer stage)
    {
        if (stage == null || stage.ActiveTheme != StageTheme.AmberFoundry || !stage.addSideArchitecture) return null;
        var existing = stage.GetComponentInChildren<FoundryStageMotion>(true);
        if (existing != null) return existing;
        var root = new GameObject("FoundryMovingEquipment");
        root.transform.SetParent(stage.transform, false);
        var motion = root.AddComponent<FoundryStageMotion>();
        motion.Build(stage.floorY);
        return motion;
    }

    private void Build(float floorY)
    {
        if (built) return;
        built = true;
        var metalShader = Resources.Load<Shader>("Stage/ObsidianMetal");
        var steamShader = Resources.Load<Shader>("Stage/FoundrySteam");
        if (metalShader == null || steamShader == null)
        {
            Debug.LogError("FoundryStageMotion: 背景用シェーダーが見つかりません。", this);
            enabled = false;
            return;
        }
        Material housing = Metal(metalShader, "FanHousing", new Color(.18f, .175f, .16f), .03f);
        Material dark = Metal(metalShader, "FanRecess", new Color(.019f, .025f, .030f), 0);
        Material blade = Metal(metalShader, "FanBlade", new Color(.29f, .32f, .33f), .04f);
        Material trim = Metal(metalShader, "FanBrass", new Color(.52f, .29f, .09f), .13f);
        steamMaterial = new Material(steamShader) { name = "Stage/FoundrySteam", hideFlags = HideFlags.DontSave };
        materials.Add(steamMaterial);

        var housingGeometry = new StageGeometry();
        Ring(housingGeometry, 1.02f, .15f, .24f, 0);
        var darkGeometry = new StageGeometry();
        darkGeometry.Panel(new Vector3(0, 0, .18f), new Vector3(2.20f, 2.20f, .16f), Quaternion.identity, .35f);
        var trimGeometry = new StageGeometry();
        Ring(trimGeometry, 1.015f, .028f, .032f, -.137f);
        for (int i = 0; i < 8; i++)
        {
            Quaternion turn = Quaternion.Euler(0, 0, i * 45f);
            trimGeometry.Box(turn * new Vector3(0, 1.105f, -.10f), new Vector3(.075f, .075f, .05f), turn);
        }
        // 回転側の羽根は傾斜と厚みを付け、平面の回転画像にはしない。
        var rotorGeometry = new StageGeometry();
        for (int i = 0; i < 7; i++)
        {
            Quaternion turn = Quaternion.Euler(0, 0, i * (360f / 7));
            rotorGeometry.Panel(turn * new Vector3(0, .56f, -.01f),
                new Vector3(.28f, .69f, .06f), turn * Quaternion.Euler(0, 20, -24), .08f);
        }
        Ring(rotorGeometry, .19f, .13f, .16f, -.08f);
        rotorGeometry.Panel(new Vector3(0, 0, -.14f), new Vector3(.23f, .23f, .08f), Quaternion.identity, .09f);
        var guardGeometry = new StageGeometry();
        Ring(guardGeometry, .36f, .018f, .018f, -.235f);
        for (int i = 0; i < 3; i++)
        {
            Quaternion turn = Quaternion.Euler(0, 0, i * 120f + 15f);
            guardGeometry.Beam(turn * new Vector3(0, .20f, -.235f), turn * new Vector3(0, .965f, -.235f), .028f, .025f);
        }
        Mesh casingMesh = Own(housingGeometry.CreateMesh("FanCasing"));
        Mesh backMesh = Own(darkGeometry.CreateMesh("FanBacking"));
        Mesh trimMesh = Own(trimGeometry.CreateMesh("FanTrim"));
        Mesh rotorMesh = Own(rotorGeometry.CreateMesh("FanRotor"));
        Mesh guardMesh = Own(guardGeometry.CreateMesh("FanGuard"));

        var mounts = new Transform[EquipmentCount];
        for (int i = 0; i < EquipmentCount; i++)
        {
            int side = i % 2 == 0 ? -1 : 1;
            int bay = i / 2;
            var mount = new GameObject("VentilationUnit" + i).transform;
            mount.SetParent(transform, false);
            mount.localPosition = new Vector3(side * 6.88f, floorY + 3.12f, 3.9f + bay * 9.2f);
            mount.localRotation = Quaternion.Euler(0, side * 75f, 0);
            mounts[i] = mount;
            rotors[i] = RenderMesh("Rotor", mount, rotorMesh, blade);
            vents[i] = CreateVent(i, new Vector3(side * 6.85f, floorY + .225f, 3f + bay * 8f));
        }
        // 固定部は四台分を材質別に結合。回転する四つの羽根だけを独立させる。
        CombineFixedParts("FanHousings", new[] { casingMesh, guardMesh }, mounts, housing);
        CombineFixedParts("FanBackings", new[] { backMesh }, mounts, dark);
        CombineFixedParts("FanTrims", new[] { trimMesh }, mounts, trim);
        Tick(0);
    }

    // 時刻から直接復元するので FPS によって噴出回数や回転速度が変わらない。
    public void Tick(double songSeconds)
    {
        if (!built || !isActiveAndEnabled || steamMaterial == null || double.IsNaN(songSeconds) || double.IsInfinity(songSeconds)) return;
        songSeconds = Math.Max(0, songSeconds);
        LastTickSeconds = songSeconds;
        steamMaterial.SetFloat("_MotionTime", (float)(songSeconds % 120));
        LiveParticleCount = 0;
        for (int i = 0; i < EquipmentCount; i++)
        {
            int side = i % 2 == 0 ? -1 : 1;
            // 七枚羽根が読める穏やかな速度。左右で回転方向も変える。
            float angle = (float)((songSeconds * (i < 2 ? 47 : 39) + i * 31) % 360);
            rotors[i].localRotation = Quaternion.Euler(0, 0, side * angle);
            int count = 0;
            double phase = (songSeconds + i * 1.37) % VentCycleSeconds;
            for (int p = 0; p < ParticlesPerVent; p++)
            {
                float age = (float)(phase - p * EmissionSeconds / ParticlesPerVent);
                if (age < 0 || age >= SteamLifetimeSeconds) continue;
                float t = age / SteamLifetimeSeconds;
                float seed = p * 2.39996f + i * .91f;
                float fade = Mathf.SmoothStep(0, 1, t / .14f) * (1 - Mathf.SmoothStep(.45f, 1, t));
                var particle = new ParticleSystem.Particle();
                // 外向きの上昇流。中心の判定領域へ横断させない。
                particle.position = new Vector3(side * (.08f + age * .15f) + Mathf.Sin(seed + age * 2) * .10f * t,
                    age * .88f + Mathf.Sin(seed) * .07f * t,
                    Mathf.Cos(seed) * .20f + Mathf.Sin(age * 1.8f + seed) * .16f * t);
                particle.startSize = .24f + t * .97f;
                particle.startColor = new Color(.62f, .67f, .68f, fade * .26f);
                particle.rotation = seed * Mathf.Rad2Deg + age * (p % 2 == 0 ? 17 : -21);
                particle.startLifetime = 10;
                particle.remainingLifetime = 10;
                particle.randomSeed = (uint)(p + i * ParticlesPerVent + 1);
                particles[count++] = particle;
            }
            vents[i].SetParticles(particles, count);
            LiveParticleCount += count;
        }
    }

    private ParticleSystem CreateVent(int index, Vector3 position)
    {
        var go = new GameObject("SteamVent" + index);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = position;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.maxParticles = ParticlesPerVent;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startSpeed = 0;
        main.startLifetime = 10;
        main.cullingMode = ParticleSystemCullingMode.Pause;
        var emission = ps.emission; emission.enabled = false;
        var shape = ps.shape; shape.enabled = false;
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = steamMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.maxParticleSize = .10f;
        ps.Play(false);
        ps.Pause(false);
        return ps;
    }

    private static void Ring(StageGeometry geometry, float radius, float width, float depth, float z)
    {
        const int segments = 40;
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2 / segments, b = (i + 1) * Mathf.PI * 2 / segments;
            geometry.Beam(new Vector3(Mathf.Sin(a) * radius, Mathf.Cos(a) * radius, z),
                new Vector3(Mathf.Sin(b) * radius, Mathf.Cos(b) * radius, z), width, depth);
        }
    }

    private Material Metal(Shader shader, string name, Color color, float emission)
    {
        var material = new Material(shader) { name = "Stage/" + name, hideFlags = HideFlags.DontSave };
        material.SetColor("_BaseColor", color);
        material.SetColor("_EmissionColor", color * emission);
        material.SetFloat("_Smoothness", .42f);
        material.SetFloat("_Metallic", .32f);
        materials.Add(material);
        return material;
    }

    private Mesh Own(Mesh mesh) { mesh.hideFlags = HideFlags.DontSave; meshes.Add(mesh); return mesh; }

    private void CombineFixedParts(string name, Mesh[] parts, Transform[] mounts, Material material)
    {
        var instances = new CombineInstance[parts.Length * mounts.Length];
        int at = 0;
        foreach (var mount in mounts)
            foreach (var part in parts)
                instances[at++] = new CombineInstance { mesh = part,
                    transform = Matrix4x4.TRS(mount.localPosition, mount.localRotation, Vector3.one) };
        var combined = Own(new Mesh { name = "Stage/" + name });
        combined.CombineMeshes(instances, true, true);
        RenderMesh(name, transform, combined, material);
    }

    private static Transform RenderMesh(string name, Transform parent, Mesh mesh, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return go.transform;
    }

    private void OnDisable()
    {
        foreach (var vent in vents) if (vent != null) vent.Clear(false);
        LiveParticleCount = 0;
    }

    private void OnDestroy()
    {
        foreach (var mesh in meshes) Release(mesh);
        foreach (var material in materials) Release(material);
    }

    private static void Release(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }
}
