using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// ゲージ・輪・レーザー・大きい光で共有する、所有権の明確な一枚のメッシュ。
public sealed class PlayExcitementMesh : IDisposable
{
    readonly List<Vector3> vertices = new List<Vector3>();
    readonly List<Vector2> uvs = new List<Vector2>();
    readonly List<Color> colors = new List<Color>();
    readonly List<int> indices = new List<int>();
    GameObject root;
    Mesh mesh;
    Material material;
    MeshRenderer renderer;
    public bool Available => mesh != null && material != null;

    public PlayExcitementMesh(Transform parent, string name, int queue, int order, bool ignoreDepth = false)
    {
        var shader = Resources.Load<Shader>("Effects/PlayExcitement");
        if (shader == null) return;
        root = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        root.transform.SetParent(parent, false);
        mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        material = new Material(shader) { name = name, hideFlags = HideFlags.DontSave, renderQueue = queue };
        material.SetFloat("_ZTest", (float)(ignoreDepth ? CompareFunction.Always : CompareFunction.LessEqual));
        root.GetComponent<MeshFilter>().sharedMesh = mesh;
        renderer = root.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingOrder = order;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
    }

    public void Protect(FlashRect rect)
    {
        if (material == null) return;
        material.SetFloat("_ClipGate", 1);
        material.SetVector("_GateRect", new Vector4((float)rect.Left, (float)rect.Bottom, (float)rect.Right, (float)rect.Top));
    }

    public void Clear() { vertices.Clear(); uvs.Clear(); colors.Clear(); indices.Clear(); }

    public void Band(Vector3 a, Vector3 b, float width, Color color)
    {
        if ((b - a).sqrMagnitude < .000001f) return;
        Vector3 normal = Vector3.Cross((b - a).normalized, Vector3.forward).normalized * width * .5f;
        Quad(a - normal, b - normal, b + normal, a + normal, color, true);
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, bool soft)
    {
        if (!Available || vertices.Count + 4 > 16000) return;
        int start = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        for (int i = 0; i < 4; i++) colors.Add(color);
        uvs.Add(new Vector2(.5f, soft ? 0 : .5f));
        uvs.Add(new Vector2(.5f, soft ? 0 : .5f));
        uvs.Add(new Vector2(.5f, soft ? 1 : .5f));
        uvs.Add(new Vector2(.5f, soft ? 1 : .5f));
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    public void Flush()
    {
        if (!Available) return;
        mesh.Clear();
        if (vertices.Count > 0)
        {
            mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0, true);
        }
        renderer.enabled = vertices.Count > 0;
    }

    public void Hide() { if (renderer != null) renderer.enabled = false; }

    public void Dispose()
    {
        UISkinKit.SafeDestroy(root);
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
        root = null; mesh = null; material = null; renderer = null;
    }
}

// 背景専用の材質だけを補正する。毎回保存した色から計算し、倍率を累積させない。
// 背景→大きい光→ノーツ・ゲート・剣の順にするため、背景だけ描画順も一時的に前へ移す。
public sealed class VoltageBackgroundPresentation : IDisposable
{
    sealed class Surface
    {
        public Material material;
        public int queue;
        public readonly List<string> names = new List<string>();
        public readonly List<Color> colors = new List<Color>();
    }

    static readonly string[] colorNames = { "_BaseColor", "_Color", "_EmissionColor", "_HazeColor", "_AccentColor", "_ChorusColor" };
    readonly List<Surface> surfaces = new List<Surface>();
    readonly List<(Renderer renderer, int order)> renderers = new List<(Renderer, int)>();
    int appliedLevel = -1;

    public VoltageBackgroundPresentation(Transform stage)
    {
        if (stage == null) return;
        var seen = new HashSet<Material>();
        foreach (var renderer in stage.GetComponentsInChildren<Renderer>(true))
        {
            renderers.Add((renderer, renderer.sortingOrder));
            renderer.sortingOrder = -200 + Mathf.Clamp(renderer.sortingOrder, -20, 20);
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || !seen.Add(material)) continue;
                var surface = new Surface { material = material, queue = material.renderQueue };
                foreach (string name in colorNames)
                    if (material.HasProperty(name))
                    {
                        surface.names.Add(name);
                        surface.colors.Add(material.GetColor(name));
                    }
                surfaces.Add(surface);
                material.renderQueue = surface.queue <= 2500 ? 1980 : 1990;
            }
        }
    }

    public void Apply(int level)
    {
        level = Mathf.Clamp(level, 0, 4);
        if (level == appliedLevel) return;
        appliedLevel = level;
        float gain = 1 + .06f * level;
        foreach (var surface in surfaces)
        {
            if (surface.material == null) continue;
            for (int i = 0; i < surface.names.Count; i++)
            {
                Color color = surface.colors[i] * gain;
                color.a = surface.colors[i].a;
                surface.material.SetColor(surface.names[i], color);
            }
        }
    }

    public void Dispose()
    {
        Apply(0);
        foreach (var surface in surfaces)
            if (surface.material != null) surface.material.renderQueue = surface.queue;
        foreach (var item in renderers)
            if (item.renderer != null) item.renderer.sortingOrder = item.order;
        surfaces.Clear(); renderers.Clear();
    }
}
