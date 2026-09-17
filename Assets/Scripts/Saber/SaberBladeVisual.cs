using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 刃の端点をそのまま描き、判定へは値を返さない。Bridgeの更新からのみ駆動する。
// 中点の細い軌跡ではなく、刃全体が通過した面を短く残す。
[ExecuteAlways]
public sealed class SaberBladeVisual : MonoBehaviour
{
    public const int PoseCapacity = 16;
    public const float TrailLifetime = .12f;
    struct Pose { public Vector3 a, b; public float time; }
    readonly Pose[] history = new Pose[PoseCapacity];
    readonly List<Vector3> vertices = new List<Vector3>(PoseCapacity * 4 + 8);
    readonly List<Color> colors = new List<Color>(PoseCapacity * 4 + 8);
    readonly List<Vector3> uvs = new List<Vector3>(PoseCapacity * 4 + 8);
    readonly List<int> indices = new List<int>(PoseCapacity * 6 + 12);
    int count;
    Mesh mesh;
    Material material;
    MeshRenderer visual;

    void EnsureMesh()
    {
        if (mesh != null) return;
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        if (shader == null) return;
        mesh = new Mesh { name = "Saber/BladeLight", hideFlags = HideFlags.DontSave }; mesh.MarkDynamic();
        material = new Material(shader) { name = "Saber/BladeLight", hideFlags = HideFlags.DontSave };
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        visual = gameObject.AddComponent<MeshRenderer>(); visual.sharedMaterial = material;
        visual.shadowCastingMode = ShadowCastingMode.Off; visual.receiveShadows = false;
        visual.lightProbeUsage = LightProbeUsage.Off; visual.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    public void Show(Vector3 a, Vector3 b, float width, Color tint, float time)
    {
        if (!Finite(a) || !Finite(b) || !Finite(time) || !Finite(width)) { Clear(); return; }
        EnsureMesh(); if (mesh == null) return;
        // 通信復帰・ワープ・時計の巻き戻しで画面を横断する帯を作らない。
        if (count > 0 && (time < history[count - 1].time || time - history[count - 1].time > TrailLifetime
            || ((a + b - history[count - 1].a - history[count - 1].b) * .5f).sqrMagnitude > 9)) count = 0;
        if (count > 0)
        {
            var last = history[count - 1];
            // センサーが同じ棒の端点順だけを反転した場合、表示面をねじらない。
            if ((a - last.b).sqrMagnitude + (b - last.a).sqrMagnitude < (a - last.a).sqrMagnitude + (b - last.b).sqrMagnitude)
                (a, b) = (b, a);
        }
        int first = 0;
        while (first < count && time - history[first].time > TrailLifetime) first++;
        if (first > 0) { System.Array.Copy(history, first, history, 0, count - first); count -= first; }
        if (count > 0 && Mathf.Abs(time - history[count - 1].time) < .00001f) count--;
        if (count == PoseCapacity) { System.Array.Copy(history, 1, history, 0, --count); }
        history[count++] = new Pose { a = a, b = b, time = time };
        vertices.Clear(); colors.Clear(); uvs.Clear(); indices.Clear();
        float strength = DisplaySettings.ProjectorMode ? .11f : .2f;
        for (int i = 1; i < count; i++)
        {
            var previous = history[i - 1]; var current = history[i];
            float before = Mathf.Pow(Mathf.Clamp01(1 - (time - previous.time) / TrailLifetime), 2) * strength;
            float after = Mathf.Pow(Mathf.Clamp01(1 - (time - current.time) / TrailLifetime), 2) * strength;
            Quad(previous.a, previous.b, current.b, current.a, Alpha(tint, before), Alpha(tint, after), true);
        }
        Vector3 edge = Vector3.Cross((b - a).normalized, Vector3.forward);
        Vector3 front = Vector3.back * .012f;
        float glow = Mathf.Max(.01f, width) * 1.6f;
        Quad(a - edge * glow + front, b - edge * glow + front, b + edge * glow + front, a + edge * glow + front,
            Alpha(tint, .19f), Alpha(tint, .19f));
        float core = Mathf.Max(.006f, width * .17f);
        Quad(a - edge * core + front, b - edge * core + front, b + edge * core + front, a + edge * core + front,
            Alpha(Color.Lerp(tint, Color.white, .92f), .98f), Alpha(Color.Lerp(tint, Color.white, .92f), .98f));
        mesh.Clear(); mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs); mesh.SetTriangles(indices, 0, true);
        visual.enabled = true;
    }

    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color before, Color after, bool ribbon = false)
    {
        int start = vertices.Count;
        vertices.Add(transform.InverseTransformPoint(a)); vertices.Add(transform.InverseTransformPoint(b));
        vertices.Add(transform.InverseTransformPoint(c)); vertices.Add(transform.InverseTransformPoint(d));
        colors.Add(before); colors.Add(before); colors.Add(after); colors.Add(after);
        float mode = ribbon ? 1 : 0;
        uvs.Add(new Vector3(0, 0, mode)); uvs.Add(new Vector3(1, 0, mode));
        uvs.Add(new Vector3(1, 1, mode)); uvs.Add(new Vector3(0, 1, mode));
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    public void Clear() { count = 0; if (mesh != null) mesh.Clear(); if (visual != null) visual.enabled = false; }
    void OnDisable() { Clear(); }
    void OnDestroy() { UISkinKit.SafeDestroy(mesh); UISkinKit.SafeDestroy(material); }
    static Color Alpha(Color color, float alpha) { color.a = alpha; return color; }
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
}
