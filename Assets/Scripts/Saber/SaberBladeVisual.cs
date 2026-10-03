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
    // 切れた瞬間の反応(爽快感カタログ 手8)。刃の太さは変えず、濃さと白さだけを変える。
    public const float CutGlowSeconds = .15f;   // 軌跡を濃くする時間(切った瞬間は約3倍)
    public const float SlashSeconds = .2f;      // 振りの弧に沿った細い斬り跡を残す時間
    public const float CoreFlashSeconds = .06f; // 刃の芯を白くする時間
    // サビの間は軌跡を長く・少し濃くする(山1)。ChorusDrop が本編で毎フレーム設定し、終わったら 0 に戻す。
    public static float ChorusBoost;
    public static float LifetimeFor(float boost) => TrailLifetime * (1f + .6f * Mathf.Clamp01(boost));
    float cutTime = float.NegativeInfinity, slashTime = float.NegativeInfinity;
    readonly Vector3[] slash = new Vector3[PoseCapacity];
    int slashCount;
    public float LastCutTime => cutTime;
    public int SlashPointCount => slashCount;
    public int VertexCount => vertices.Count;
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
        float lifetime = LifetimeFor(ChorusBoost);
        if (count > 0 && (time < history[count - 1].time || time - history[count - 1].time > lifetime
            || ((a + b - history[count - 1].a - history[count - 1].b) * .5f).sqrMagnitude > 9)) { count = 0; slashCount = 0; }
        if (count > 0)
        {
            var last = history[count - 1];
            // センサーが同じ棒の端点順だけを反転した場合、表示面をねじらない。
            if ((a - last.b).sqrMagnitude + (b - last.a).sqrMagnitude < (a - last.a).sqrMagnitude + (b - last.b).sqrMagnitude)
                (a, b) = (b, a);
        }
        int first = 0;
        while (first < count && time - history[first].time > lifetime) first++;
        if (first > 0) { System.Array.Copy(history, first, history, 0, count - first); count -= first; }
        if (count > 0 && Mathf.Abs(time - history[count - 1].time) < .00001f) count--;
        if (count == PoseCapacity) { System.Array.Copy(history, 1, history, 0, --count); }
        history[count++] = new Pose { a = a, b = b, time = time };
        vertices.Clear(); colors.Clear(); uvs.Clear(); indices.Clear();
        float cutGlow = Fade(time - cutTime, CutGlowSeconds);
        float coreFlash = Fade(time - cutTime, CoreFlashSeconds);
        float strength = (DisplaySettings.ProjectorMode ? .11f : .2f) * (1f + .35f * Mathf.Clamp01(ChorusBoost)) * (1f + 2f * cutGlow);
        for (int i = 1; i < count; i++)
        {
            var previous = history[i - 1]; var current = history[i];
            float before = Mathf.Pow(Mathf.Clamp01(1 - (time - previous.time) / lifetime), 2) * strength;
            float after = Mathf.Pow(Mathf.Clamp01(1 - (time - current.time) / lifetime), 2) * strength;
            Quad(previous.a, previous.b, current.b, current.a, Alpha(tint, before), Alpha(tint, after), true);
        }
        Vector3 edge = Vector3.Cross((b - a).normalized, Vector3.forward);
        Vector3 front = Vector3.back * .012f;
        DrawSlash(time, tint, front);
        float glow = Mathf.Max(.01f, width) * 1.6f;
        Quad(a - edge * glow + front, b - edge * glow + front, b + edge * glow + front, a + edge * glow + front,
            Alpha(tint, .19f + .2f * coreFlash), Alpha(tint, .19f + .2f * coreFlash));
        float core = Mathf.Max(.006f, width * .17f);
        Color coreColor = Color.Lerp(Color.Lerp(tint, Color.white, .92f), Color.white, coreFlash);
        Quad(a - edge * core + front, b - edge * core + front, b + edge * core + front, a + edge * core + front,
            Alpha(coreColor, .98f), Alpha(coreColor, .98f));
        mesh.Clear(); mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs); mesh.SetTriangles(indices, 0, true);
        visual.enabled = true;
    }

    // flat: 帯の長さ方向の端を薄くしない(斬り跡を点線にしない)。幅方向の縁は柔らかいまま。
    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color before, Color after, bool ribbon = false, bool flat = false)
    {
        int start = vertices.Count;
        vertices.Add(transform.InverseTransformPoint(a)); vertices.Add(transform.InverseTransformPoint(b));
        vertices.Add(transform.InverseTransformPoint(c)); vertices.Add(transform.InverseTransformPoint(d));
        colors.Add(before); colors.Add(before); colors.Add(after); colors.Add(after);
        float mode = ribbon ? 1 : 0;
        float u0 = flat ? .5f : 0, u1 = flat ? .5f : 1;
        uvs.Add(new Vector3(u0, 0, mode)); uvs.Add(new Vector3(u1, 0, mode));
        uvs.Add(new Vector3(u1, 1, mode)); uvs.Add(new Vector3(u0, 1, mode));
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    // 切れた瞬間に呼ぶ(手8)。直前の振り(軌跡に残っている姿勢)の中心を結んだ弧を、細い斬り跡として残す。
    // 描画は次の Show(入力の更新)で反映する。時刻は最後の姿勢の時刻を使う。
    public void NotifyCut()
    {
        if (count == 0) return;
        float now = history[count - 1].time;
        float lifetime = LifetimeFor(ChorusBoost);
        cutTime = now;
        slashCount = 0;
        for (int i = 0; i < count; i++)
            if (now - history[i].time <= lifetime) slash[slashCount++] = (history[i].a + history[i].b) * .5f;
        slashTime = slashCount >= 2 ? now : float.NegativeInfinity;
        if (slashCount < 2) slashCount = 0;
    }

    void DrawSlash(float time, Color tint, Vector3 front)
    {
        float age = time - slashTime;
        if (slashCount < 2 || age < 0 || age >= SlashSeconds) return;
        float alpha = Mathf.Pow(1 - age / SlashSeconds, 2) * .9f;
        float half = (DisplaySettings.ProjectorMode ? .05f : .035f) * .5f;
        Color color = Alpha(Color.Lerp(tint, Color.white, .55f), alpha);
        for (int i = 1; i < slashCount; i++)
        {
            Vector3 p0 = slash[i - 1], p1 = slash[i];
            Vector3 along = p1 - p0;
            if (along.sqrMagnitude < 1e-6f) continue;
            Vector3 side = Vector3.Cross(along.normalized, Vector3.forward) * half;
            Quad(p0 - side + front, p1 - side + front, p1 + side + front, p0 + side + front, color, color, false, true);
        }
    }

    static float Fade(float age, float seconds) => age >= 0 && age < seconds ? 1 - age / seconds : 0;

    public void Clear()
    {
        count = 0; slashCount = 0; cutTime = slashTime = float.NegativeInfinity;
        if (mesh != null) mesh.Clear(); if (visual != null) visual.enabled = false;
    }
    void OnDisable() { Clear(); }
    void OnDestroy() { UISkinKit.SafeDestroy(mesh); UISkinKit.SafeDestroy(material); }
    static Color Alpha(Color color, float alpha) { color.a = alpha; return color; }
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
}
