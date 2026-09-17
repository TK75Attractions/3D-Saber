using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 暗い中央通路を囲む、奥行きのある照明バンク。曲時計からのみ駆動する。
// 梁・デッキを結合し、光も一枚の動的メッシュにまとめる。
[ExecuteAlways]
public sealed class PulseArrayStage : MonoBehaviour
{
    public const int BankCount = 8;
    public const int VertexBudget = 1800;
    readonly List<Vector3> vertices = new List<Vector3>(VertexBudget);
    readonly List<Color> colors = new List<Color>(VertexBudget);
    readonly List<Vector2> uvs = new List<Vector2>(VertexBudget);
    readonly List<int> triangles = new List<int>(VertexBudget * 2);
    readonly List<Mesh> meshes = new List<Mesh>();
    readonly List<Material> materials = new List<Material>();
    Mesh lightMesh;
    StageLightCues cues = new StageLightCues(null);
    float floor;
    public double LastTickSeconds { get; private set; }
    public int CueCount => cues.Count;

    public static PulseArrayStage Create(FloorRenderer owner)
    {
        var go = new GameObject("PulseArray");
        go.transform.SetParent(owner.transform, false);
        var stage = go.AddComponent<PulseArrayStage>();
        stage.Build(owner.floorY);
        return stage;
    }

    void Build(float floorY)
    {
        floor = floorY;
        var metal = new Material(Resources.Load<Shader>("Stage/ObsidianMetal")) { name = "PulseArray/Graphite", hideFlags = HideFlags.DontSave };
        metal.SetColor("_BaseColor", new Color(.024f, .032f, .048f));
        metal.SetColor("_EmissionColor", Color.black);
        metal.SetFloat("_Metallic", .4f);
        metal.SetFloat("_Smoothness", .55f);
        materials.Add(metal);
        var structure = new StageGeometry();
        structure.Box(new Vector3(0, floor - .2f, 25), new Vector3(11.8f, .3f, 66));
        for (int bank = 0; bank < BankCount; bank++)
        {
            float z = 2 + bank * 7;
            for (int side = -1; side <= 1; side += 2)
            {
                structure.Beam(new Vector3(side * 7, floor, z), new Vector3(side * 9, 6.8f, z), .22f, .35f);
                structure.Beam(new Vector3(side * 9, 6.8f, z), new Vector3(side * 3.4f, 8.5f, z + 1.6f), .18f, .3f);
                structure.Box(new Vector3(side * 7.4f, floor + .2f, z), new Vector3(2.4f, .5f, 1.8f));
                structure.Beam(new Vector3(side * 7.6f, floor + .6f, z), new Vector3(side * 9.4f, 3.8f, z + 3), .1f, .12f);
            }
        }
        var mesh = structure.CreateMesh("PulseArray/Structure"); meshes.Add(mesh);
        Emit("GraphiteStructure", mesh, metal);
        var light = new Material(Resources.Load<Shader>("Effects/GameplayCutAccent")) { name = "PulseArray/Light", hideFlags = HideFlags.DontSave };
        materials.Add(light);
        lightMesh = new Mesh { name = "PulseArray/Lights", hideFlags = HideFlags.DontSave };
        lightMesh.MarkDynamic(); meshes.Add(lightMesh);
        Emit("LightBanks", lightMesh, light);
        Tick(0, 0);
    }

    void Emit(string label, Mesh mesh, Material material)
    {
        var go = new GameObject(label); go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    public void SetRhythm(ChartData chart) { cues = new StageLightCues(chart); }

    public void Tick(double seconds, float chorus)
    {
        if (!isActiveAndEnabled || lightMesh == null || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
        LastTickSeconds = System.Math.Max(0, seconds);
        float time = (float)LastTickSeconds;
        chorus = float.IsNaN(chorus) || float.IsInfinity(chorus) ? 0 : Mathf.Clamp01(chorus);
        vertices.Clear(); colors.Clear(); uvs.Clear(); triangles.Clear();
        Color cool = new Color(.18f, .42f, 1.35f), warm = new Color(1.55f, .48f, .13f);
        float projector = DisplaySettings.ProjectorMode ? .7f : 1;
        for (int bank = 0; bank < BankCount; bank++)
        {
            float z = 2 + bank * 7;
            float pulse = cues.Evaluate(LastTickSeconds - bank * .055);
            float wave = .5f + .5f * Mathf.Sin(time * .85f - bank * .62f);
            Color tint = Color.Lerp(cool, warm, chorus * .85f);
            float strength = (.16f + Mathf.Pow(wave, 3) * .52f + pulse * .28f + chorus * .14f) * projector;
            for (int side = -1; side <= 1; side += 2)
            {
                float alternate = .75f + .25f * Mathf.Sin(time * .55f + bank * .7f + side * 1.1f);
                var basePoint = new Vector3(side * 6.9f, floor + .5f, z);
                var shoulder = new Vector3(side * 8.8f, 6.6f, z);
                var crown = new Vector3(side * 3.4f, 8.3f, z + 1.6f);
                // 光源の細い芯と控えめな周辺光。中央の低い領域を横切らない。
                Tube(basePoint, shoulder, .075f, tint, strength * alternate);
                Tube(shoulder, crown, .055f, tint, strength * .65f);
                float sweep = Mathf.Sin(time * .32f - bank * .26f + side * .45f);
                for (int ray = 0; ray < 2; ray++)
                {
                    Vector3 tip = new Vector3(side * (10 + ray * 3 + sweep * 1.4f + chorus * 3), 7.3f + ray * 1.4f, z + 8);
                    Tube(basePoint, tip, .035f, tint, strength * (.36f + chorus * .22f));
                    Stroke(basePoint, tip, .26f, .95f, Alpha(tint, strength * .065f), Alpha(tint, 0));
                }
                // 床の細い光溜まり。実時間反射カメラを追加せず、床上に低輝度で置く。
                Stroke(new Vector3(side * 5.75f, floor + .006f, z - 1.3f), new Vector3(side * 5.75f, floor + .006f, z + 3.8f),
                    .12f, .025f, Alpha(tint, strength * .28f), Alpha(tint, 0), Vector3.right);
            }
        }
        // 遠景の切れ目を残した輪郭。消失点は暗いままにする。
        for (int side = -1; side <= 1; side += 2)
        {
            Tube(new Vector3(side * 5.8f, floor + .04f, -4), new Vector3(side * 5.8f, floor + .04f, 56), .025f, cool, .26f * projector, Vector3.right);
        }
        lightMesh.Clear(); lightMesh.SetVertices(vertices); lightMesh.SetColors(colors); lightMesh.SetUVs(0, uvs);
        lightMesh.SetTriangles(triangles, 0, true);
    }

    void Tube(Vector3 a, Vector3 b, float width, Color tint, float alpha, Vector3 edge = default)
    {
        Stroke(a, b, width * 5, width * 5, Alpha(tint, alpha * .16f), Alpha(tint, alpha * .16f), edge);
        Stroke(a, b, width, width, Alpha(Color.Lerp(tint, Color.white, .12f), alpha), Alpha(tint, alpha), edge);
    }

    void Stroke(Vector3 a, Vector3 b, float startWidth, float endWidth, Color startColor, Color endColor, Vector3 edge = default)
    {
        if (edge == Vector3.zero) edge = Vector3.Cross((b - a).normalized, Vector3.forward).normalized;
        int index = vertices.Count;
        vertices.Add(a - edge * startWidth * .5f); vertices.Add(b - edge * endWidth * .5f);
        vertices.Add(b + edge * endWidth * .5f); vertices.Add(a + edge * startWidth * .5f);
        colors.Add(startColor); colors.Add(endColor); colors.Add(endColor); colors.Add(startColor);
        uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0)); uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
        triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
        triangles.Add(index); triangles.Add(index + 2); triangles.Add(index + 3);
    }

    static Color Alpha(Color color, float alpha) { color.a = alpha; return color; }
    void OnDestroy()
    {
        foreach (var mesh in meshes) UISkinKit.SafeDestroy(mesh);
        foreach (var material in materials) UISkinKit.SafeDestroy(material);
    }
}
