using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 勉強の裏譜面 Q の試験用紙と、黒板の線・玉・的・輪を描く1枚のメッシュ(毎コマ作り直す)。
// 色は頂点色だけ(Effects/PlayExcitement の材質。霧・光・発光が無いので、黒は 0 のまま黒く、プロジェクターの露出でも浮かない)。
// 深さは見ない。前後は sortingOrder(2D レンダラーでは材質の queue より先に効く)と、同じメッシュの中では足した順で決まる。
public sealed class QuizMesh : IDisposable
{
    const int MaxVertices = 60000;
    readonly List<Vector3> vertices = new List<Vector3>();
    readonly List<Vector2> uvs = new List<Vector2>();
    readonly List<Color> colors = new List<Color>();
    readonly List<int> indices = new List<int>();
    readonly bool world;
    GameObject root;
    Mesh mesh;
    Material material;
    MeshRenderer renderer;

    public MeshRenderer Renderer => renderer;
    public Mesh Mesh => mesh;
    public bool Available => mesh != null && material != null && root != null;
    public int VertexCount => vertices.Count;

    // world: 頂点をワールド座標で受け取り、このメッシュの中の座標へ直す(false なら parent の中の座標のまま)。
    public QuizMesh(Transform parent, string name, int sortingOrder, bool world)
    {
        this.world = world;
        var shader = Resources.Load<Shader>("Effects/PlayExcitement");
        if (shader == null) shader = Resources.Load<Shader>("Effects/NoteGuide");
        if (shader == null) return;
        root = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        root.transform.SetParent(parent, false);
        mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        material = new Material(shader) { name = name, hideFlags = HideFlags.DontSave, renderQueue = 3050 };
        if (material.HasProperty("_ZTest")) material.SetFloat("_ZTest", (float)CompareFunction.Always);
        if (material.HasProperty("_ClipGate")) material.SetFloat("_ClipGate", 0f);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        root.GetComponent<MeshFilter>().sharedMesh = mesh;
        renderer = root.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = 0;
        renderer.sortingOrder = sortingOrder;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
    }

    public void Clear() { vertices.Clear(); uvs.Clear(); colors.Clear(); indices.Clear(); }

    Vector3 Local(Vector3 point) => world ? root.transform.InverseTransformPoint(point) : point;

    bool Room(int count) => Available && vertices.Count + count <= MaxVertices;

    int Add(Vector3 point, Color color)
    {
        vertices.Add(Local(point));
        colors.Add(color);
        // PlayExcitement の材質は uv.y で縁を薄くする。0.5 なら縁まで同じ濃さ。
        uvs.Add(new Vector2(.5f, .5f));
        return vertices.Count - 1;
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        if (color.a <= 0f || !Room(4)) return;
        int i = Add(a, color); Add(b, color); Add(c, color); Add(d, color);
        indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
        indices.Add(i); indices.Add(i + 2); indices.Add(i + 3);
    }

    // 太さ width の線分(z は a と b のまま)。
    public void Line(Vector3 a, Vector3 b, float width, Color color)
    {
        Vector3 along = b - a;
        along.z = 0f;
        if (along.sqrMagnitude < 1e-10f || width <= 0f) return;
        Vector3 side = new Vector3(-along.y, along.x, 0f).normalized * (width * .5f);
        Quad(a - side, b - side, b + side, a + side, color);
    }

    // 塗りつぶした円。
    public void Disc(Vector2 center, float z, float radius, Color color, int segments = 28)
    {
        if (radius <= 0f || color.a <= 0f || segments < 3 || !Room(segments + 1)) return;
        int c = Add(new Vector3(center.x, center.y, z), color);
        for (int s = 0; s < segments; s++)
        {
            float angle = s * Mathf.PI * 2f / segments;
            Add(new Vector3(center.x + Mathf.Cos(angle) * radius, center.y + Mathf.Sin(angle) * radius, z), color);
        }
        for (int s = 0; s < segments; s++)
        {
            indices.Add(c); indices.Add(c + 1 + s); indices.Add(c + 1 + (s + 1) % segments);
        }
    }

    // 縁だけの輪(半径 radius を中心に太さ width)。
    public void Ring(Vector2 center, float z, float radius, float width, Color color, int segments = 48)
    {
        if (radius <= 0f || width <= 0f || color.a <= 0f || segments < 3 || !Room(segments * 2)) return;
        float inner = Mathf.Max(0f, radius - width * .5f), outer = radius + width * .5f;
        int first = vertices.Count;
        for (int s = 0; s < segments; s++)
        {
            float angle = s * Mathf.PI * 2f / segments;
            float x = Mathf.Cos(angle), y = Mathf.Sin(angle);
            Add(new Vector3(center.x + x * inner, center.y + y * inner, z), color);
            Add(new Vector3(center.x + x * outer, center.y + y * outer, z), color);
        }
        for (int s = 0; s < segments; s++)
        {
            int a = first + s * 2, b = first + (s + 1) % segments * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(b + 1);
            indices.Add(a); indices.Add(b + 1); indices.Add(b);
        }
    }

    public void Flush()
    {
        if (!Available) return;
        mesh.Clear();
        if (vertices.Count > 0)
        {
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0, true);
        }
        renderer.enabled = vertices.Count > 0;
    }

    public void Hide()
    {
        Clear();
        if (mesh != null) mesh.Clear();
        if (renderer != null) renderer.enabled = false;
    }

    public void Dispose()
    {
        UISkinKit.SafeDestroy(root);
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
        root = null; mesh = null; material = null; renderer = null;
    }
}
