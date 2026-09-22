using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 装飾の強弱に影響されない譜面ガイド。生成済みノーツの位置を同じTick内で床へ写す。
// 一枚の動的メッシュで判定線・横線・方向矢印を描き、ノーツごとの資源を増やさない。
public sealed class FloorTimingGuide : MonoBehaviour
{
    const int MaxMarkers = 256;
    static readonly Color Ink = new Color(.018f, .023f, .05f, 1);
    readonly List<Vector3> vertices = new List<Vector3>(8192);
    readonly List<Color> colors = new List<Color>(8192);
    readonly List<int> triangles = new List<int>(16384);
    Mesh mesh;
    Material material;
    MeshRenderer display;
    float surfaceY;
    public int MarkerCount { get; private set; }
    public float JudgmentZ { get; private set; }

    public static FloorTimingGuide Create(Transform parent, float floorY)
    {
        var go = new GameObject("FloorTimingGuide");
        go.transform.SetParent(parent, false);
        var guide = go.AddComponent<FloorTimingGuide>();
        guide.surfaceY = floorY + .14f;
        guide.mesh = new Mesh { name = "FloorTimingGuides", hideFlags = HideFlags.DontSave };
        guide.mesh.MarkDynamic();
        guide.material = new Material(Resources.Load<Shader>("Effects/NoteGuide")) { hideFlags = HideFlags.DontSave };
        go.AddComponent<MeshFilter>().sharedMesh = guide.mesh;
        guide.display = go.AddComponent<MeshRenderer>();
        guide.display.sharedMaterial = guide.material;
        guide.display.shadowCastingMode = ShadowCastingMode.Off;
        guide.display.receiveShadows = false;
        guide.display.lightProbeUsage = LightProbeUsage.Off;
        guide.display.reflectionProbeUsage = ReflectionProbeUsage.Off;
        guide.Clear();
        return guide;
    }

    public void Tick(NoteSpawner spawner, double songTime)
    {
        if (!isActiveAndEnabled || spawner == null || !spawner.isActiveAndEnabled ||
            double.IsNaN(songTime) || double.IsInfinity(songTime)) { Clear(); return; }
        vertices.Clear(); colors.Clear(); triangles.Clear(); MarkerCount = 0;
        JudgmentZ = spawner.judgeZ;
        // 中央の白い固定線と暗い下敷き。中心がノーツの判定面と一致する。
        Bar(0, JudgmentZ, 7.2f, .19f, Ink, 0);
        Bar(0, JudgmentZ, 7.2f, .065f, new Color(.83f, .93f, 1), .003f);
        foreach (var note in spawner.LiveNotes)
        {
            if (note == null || note.IsCut || note.IsMissed || note.IsFinalized) continue;
            double remaining = note.HitTime - songTime;
            // 到着後は短く消す。ロングの開始時刻も同じ基準で示す。
            if (remaining < -.13 || MarkerCount >= MaxMarkers) continue;
            var position = note.transform.position;
            float alpha = Mathf.Clamp01((float)((spawner.approachTime - remaining) / .12));
            if (remaining < 0) alpha *= Mathf.Clamp01(1 + (float)remaining / .13f);
            if (alpha <= 0) continue;
            var visuals = note.GetComponent<NoteVisuals>();
            Color tint = visuals != null ? visuals.baseColor : UISkinPalette.NoteFlick;
            tint = Color.Lerp(tint, Color.white, .22f); tint.a = alpha;
            Color ink = Ink; ink.a = alpha;
            float size = Mathf.Clamp(Mathf.Abs(note.transform.lossyScale.x), .65f, 1.05f);
            if (note.RequiredDirection == CutDirection.None)
            {
                Bar(position.x, position.z, size * 1.22f, .24f, ink, .006f);
                Bar(position.x, position.z, size * 1.12f, .12f, tint, .009f);
                Bar(position.x, position.z, size * .68f, .035f, new Color(1, 1, 1, alpha), .012f);
            }
            else
            {
                Arrow(position, note.RequiredDirection, size, ink, .006f, true);
                Arrow(position, note.RequiredDirection, size, Color.Lerp(tint, new Color(1, 1, 1, alpha), .75f), .009f);
            }
            MarkerCount++;
        }
        mesh.Clear(); mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles, 0, true);
        display.enabled = true;
    }

    void Bar(float x, float z, float width, float depth, Color color, float bias)
    {
        int start = vertices.Count;
        Vertex(new Vector3(x - width / 2, surfaceY + bias, z - depth / 2), color);
        Vertex(new Vector3(x + width / 2, surfaceY + bias, z - depth / 2), color);
        Vertex(new Vector3(x + width / 2, surfaceY + bias, z + depth / 2), color);
        Vertex(new Vector3(x - width / 2, surfaceY + bias, z + depth / 2), color);
        triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
        triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
    }

    void Arrow(Vector3 position, CutDirection direction, float size, Color color, float bias, bool outline = false)
    {
        int start = vertices.Count;
        var rotation = Quaternion.Euler(0, 0, CutDirectionHelper.ToZRotationDegrees(direction));
        var points = outline ? FlickArrowShape.OutlinePoints : FlickArrowShape.Points;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 p = rotation * (Vector3)(points[i] * size);
            // 床の遠近で潰れないよう、前後の長さだけ広げる。上矢印は画面奥へ向く。
            Vertex(new Vector3(position.x + p.x, surfaceY + bias, position.z + p.y * 2.2f), outline ? color : color * FlickArrowShape.VertexColor(i));
        }
        foreach (int index in FlickArrowShape.Triangles) triangles.Add(start + index);
    }

    void Vertex(Vector3 world, Color color) { vertices.Add(transform.InverseTransformPoint(world)); colors.Add(color); }
    public void Clear()
    {
        MarkerCount = 0;
        if (mesh != null) mesh.Clear();
        if (display != null) display.enabled = false;
    }
    void OnDisable() { Clear(); }
    void OnDestroy() { UISkinKit.SafeDestroy(mesh); UISkinKit.SafeDestroy(material); }
}
