using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 六つの独立した立体世界。FloorRenderer が選択し、GamePlayManager が曲時計で駆動する。
[ExecuteAlways]
public sealed partial class ScenicStageWorld : MonoBehaviour
{
    public const int ParticleBudget = 72;
    public StageTheme Theme { get; private set; }
    public double LastTickSeconds { get; private set; }
    public float ChorusIntensity { get; private set; }
    public int MovingObjectCount => movers.Count;
    private readonly List<Material> materials = new List<Material>();
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly Dictionary<Material, StageGeometry> groups = new Dictionary<Material, StageGeometry>();
    private readonly List<Motion> movers = new List<Motion>();
    private readonly ParticleSystem.Particle[] particles = new ParticleSystem.Particle[ParticleBudget];
    private ParticleSystem motes;
    private Material particleMaterial;
    private bool built;
    private float floor;
    private Color haze;
    private Shader surfaceShader;
    private class Motion
    {
        public Transform transform;
        public Vector3 position, amplitude, rotationSpeed, sway;
        public Vector3 chorusOffset, chorusRotation;
        public Quaternion rotation;
        public float frequency, phase;
    }

    public static ScenicStageWorld Ensure(FloorRenderer stage)
    {
        if (stage == null || !StageThemeCatalog.IsScenic(stage.ActiveTheme)) return null;
        var existing = stage.GetComponentInChildren<ScenicStageWorld>(true);
        if (existing != null) return existing;
        var root = new GameObject("ScenicWorld-" + stage.ActiveTheme);
        root.transform.SetParent(stage.transform, false);
        var world = root.AddComponent<ScenicStageWorld>();
        world.Build(stage);
        return world;
    }

    private void Build(FloorRenderer stage)
    {
        if (built) return;
        built = true; Theme = stage.ActiveTheme; floor = stage.floorY;
        surfaceShader = Resources.Load<Shader>("Stage/ScenicSurface");
        if (surfaceShader == null) throw new InvalidOperationException("ScenicSurface が見つかりません。");
        switch (Theme)
        {
            case StageTheme.AbyssalRuins: BuildAbyss(); break;
            case StageTheme.SkySanctuary: BuildSky(); break;
            case StageTheme.MoonlitGarden: BuildGarden(); break;
            case StageTheme.CrystalGrotto: BuildCrystal(); break;
            case StageTheme.AstralOrbit: BuildOrbit(); break;
            case StageTheme.DesertSanctum: BuildDesert(); break;
        }
        foreach (var group in groups) Emit("World-" + group.Key.name, group.Value, group.Key, transform);
        CreateMotes(); Tick(0);
    }

    private Material Surface(string name, Color color, float mode = 0, float emission = 0, Color? accent = null, float sway = 0)
    {
        var mat = new Material(surfaceShader) { name = name, hideFlags = HideFlags.DontSave };
        mat.SetColor("_BaseColor", color); mat.SetColor("_HazeColor", haze); mat.SetColor("_AccentColor", accent ?? color);
        mat.SetFloat("_Mode", mode); mat.SetFloat("_Emission", emission); mat.SetFloat("_Sway", sway);
        mat.SetColor("_ChorusColor", ChorusColor(Theme));
        mat.SetFloat("_AnchorY", sway < 0 ? floor + 6 : floor);
        if (Theme == StageTheme.AbyssalRuins && mode == 0) mat.SetFloat("_Caustics", .7f);
        materials.Add(mat); return mat;
    }

    private StageGeometry G(Material mat)
    {
        if (!groups.TryGetValue(mat, out var geometry)) { geometry = new StageGeometry(); groups.Add(mat, geometry); }
        return geometry;
    }

    private void Sky(Color upper, Color horizon, Color cloud)
    {
        haze = horizon;
        var mat = Surface("Atmosphere", upper, 3, 0, cloud);
        G(mat).Box(new Vector3(0, 45, 145), new Vector3(650,450,.4f));
    }

    private void Path(Material stone, Material edging, int layout)
    {
        var ground = G(stone); var edge = G(edging);
        ground.Box(new Vector3(0,floor - .35f,20), new Vector3(11.6f,.60f,48));
        var flat = Quaternion.Euler(90,0,0);
        for (int i = 0; i < 20; i++)
        {
            float z = -3 + i * 2.4f;
            if (layout == 0)
                for (int lane = 0; lane < 3; lane++) ground.Panel(new Vector3((lane-1)*3.82f,floor - .035f,z),new Vector3(3.72f,2.30f,.12f),flat,.20f);
            else if (layout == 1)
                ground.Panel(new Vector3(0,floor-.025f,z),new Vector3(11.35f,2.28f,.14f),flat,.35f);
            else
                for (int lane = 0; lane < 4; lane++)
                    ground.Panel(new Vector3((lane-1.5f)*2.84f,floor-.022f,z + (lane%2)*.12f),new Vector3(2.75f,2.23f,.16f),flat * Quaternion.Euler(0,0,Mathf.Sin(i*4.1f+lane)*1.4f),.32f);
            foreach (int side in new[] {-1,1})
            {
                edge.Panel(new Vector3(side * 5.87f,floor+.015f,z),new Vector3(.30f,2.28f,.20f),flat,.06f);
                if (i%3 == 0) edge.Box(new Vector3(side*5.24f,floor+.045f,z),new Vector3(.68f,.012f,.028f));
            }
        }
    }

    private Transform Emit(string name, StageGeometry geometry, Material material, Transform parent)
    {
        if (geometry.VertexCount == 0) return null;
        var go = new GameObject(name); go.transform.SetParent(parent,false);
        var mesh = geometry.CreateMesh(name); mesh.hideFlags = HideFlags.DontSave; meshes.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return go.transform;
    }

    private Transform Moving(string name, StageGeometry geometry, Material material, Vector3 position, Vector3 amplitude, Vector3 speed, float frequency = .7f, float phase = 0, Vector3 sway = default, Quaternion rotation = default)
    {
        var t = Emit(name, geometry, material, transform); t.localPosition = position;
        t.localRotation = rotation.Equals(default(Quaternion)) ? Quaternion.identity : rotation;
        float side = Mathf.Sign(position.x);
        Vector3 opening = new Vector3(side*.65f,Theme == StageTheme.CrystalGrotto ? .95f : .50f,0);
        if (name.StartsWith("DriftingCloud")) opening = new Vector3(side*1.2f,-.55f,0);
        if (name.StartsWith("PlanetaryRing")) opening = Vector3.zero;
        Vector3 flourish = Theme == StageTheme.AstralOrbit ? new Vector3(0,18,side*14) : new Vector3(0,side*12,0);
        movers.Add(new Motion { transform = t, position = position, rotation = t.localRotation,
            amplitude = amplitude, rotationSpeed = speed, frequency = frequency, phase = phase, sway = sway,
            chorusOffset = opening, chorusRotation = flourish });
        return t;
    }

    public void Tick(double songSeconds, float chorus = 0)
    {
        if (!built || !isActiveAndEnabled || double.IsNaN(songSeconds) || double.IsInfinity(songSeconds)) return;
        songSeconds = Math.Max(0,songSeconds); LastTickSeconds = songSeconds;
        ChorusIntensity = float.IsNaN(chorus) || float.IsInfinity(chorus) ? 0 : Mathf.Clamp01(chorus);
        foreach (var mat in materials)
        {
            mat.SetFloat("_MotionTime", (float)songSeconds);
            mat.SetFloat("_Chorus", ChorusIntensity);
        }
        foreach (var item in movers)
        {
            float wave = (float)Math.Sin(songSeconds * item.frequency + item.phase);
            // 時刻を強度で掛け算するとサビ入りで回転が飛ぶため、位相は常に曲時計のまま。
            item.transform.localPosition = item.position + item.amplitude * wave + item.chorusOffset * ChorusIntensity;
            Vector3 angle = new Vector3((float)(songSeconds * item.rotationSpeed.x % 360),
                (float)(songSeconds * item.rotationSpeed.y % 360), (float)(songSeconds * item.rotationSpeed.z % 360));
            item.transform.localRotation = item.rotation * Quaternion.Euler(angle + item.sway * wave + item.chorusRotation * ChorusIntensity);
        }
        TickMotes(songSeconds);
    }

    private static Color ChorusColor(StageTheme theme)
    {
        switch (theme)
        {
            case StageTheme.AbyssalRuins: return new Color(.10f,.42f,.46f);
            case StageTheme.SkySanctuary: return new Color(.58f,.46f,.24f);
            case StageTheme.MoonlitGarden: return new Color(.34f,.43f,.26f);
            case StageTheme.CrystalGrotto: return new Color(.43f,.24f,.57f);
            case StageTheme.AstralOrbit: return new Color(.32f,.23f,.57f);
            default: return new Color(.56f,.31f,.12f);
        }
    }

    private void CreateMotes()
    {
        var go = new GameObject("WorldMotes"); go.transform.SetParent(transform,false);
        motes = go.AddComponent<ParticleSystem>(); motes.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = motes.main; main.playOnAwake = false; main.loop = false; main.maxParticles = ParticleBudget;
        main.startSpeed = 0; main.simulationSpace = ParticleSystemSimulationSpace.Local;
        var emission = motes.emission; emission.enabled = false; var shape = motes.shape; shape.enabled = false;
        var shader = Resources.Load<Shader>("Stage/ScenicMotes");
        particleMaterial = new Material(shader) { name = "ScenicMotes", hideFlags = HideFlags.DontSave };
        particleMaterial.SetFloat("_Bubble", Theme == StageTheme.AbyssalRuins ? 1 : 0);
        var renderer = go.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = particleMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.sortMode = ParticleSystemSortMode.Distance; renderer.maxParticleSize = .06f;
        motes.Play(false); motes.Pause(false);
    }

    private void TickMotes(double time)
    {
        if (motes == null) return;
        for (int i = 0; i < ParticleBudget; i++)
        {
            float seed = i * 2.39996f; int side = i%2 == 0 ? -1 : 1;
            float phase = (float)((time * .20 + i * .618034) % 1);
            float fade = Mathf.Sin(phase * Mathf.PI);
            float x = side * (7.2f + (i%5)*.47f);
            float z = 1.5f + (i%9)*3.6f;
            float y = floor + phase * 5;
            float size = .10f; Color color = new Color(.46f,.69f,.72f,.55f*fade);
            if (Theme == StageTheme.SkySanctuary) { y = floor - .5f + phase * 5; size = .07f; color = new Color(.8f,.85f,.85f,.35f*fade); }
            if (Theme == StageTheme.MoonlitGarden) { y = floor + 1.3f + Mathf.Sin((float)time*.65f+seed)*.6f; size = .07f; color = new Color(.63f,.64f,.31f,.42f*fade); }
            if (Theme == StageTheme.CrystalGrotto) { y = floor + 4.7f - phase*4.7f; size = .065f; color = new Color(.48f,.60f,.68f,.50f*fade); }
            if (Theme == StageTheme.AstralOrbit) { y = floor + phase*6; x += side*2; size = .05f; color = new Color(.52f,.56f,.72f,.28f*fade); }
            if (Theme == StageTheme.DesertSanctum) { x = side*(7.35f + Mathf.Sin(seed)*.20f); z = 4+(i%3)*9.5f + Mathf.Cos(seed)*.20f; y = floor + 4.0f - phase*4; size = .065f + (i%3)*.02f; color = new Color(.62f,.46f,.26f,.65f*fade); }
            size *= 1 + ChorusIntensity * .25f;
            color.a *= 1 + ChorusIntensity * .30f;
            particles[i] = new ParticleSystem.Particle { position = new Vector3(x + Mathf.Sin((float)(time*.8)+seed)*.10f,y,z),
                startSize = size, startColor = color, remainingLifetime = 10, startLifetime = 10, randomSeed = (uint)(i+1) };
        }
        motes.SetParticles(particles,ParticleBudget);
    }

    private void OnDisable() { if (motes != null) motes.Clear(false); }
    private void OnDestroy()
    {
        foreach (var mesh in meshes) Release(mesh);
        foreach (var mat in materials) Release(mat);
        Release(particleMaterial);
    }
    private static void Release(UnityEngine.Object item)
    {
        if (item == null) return;
        if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
    }
}
