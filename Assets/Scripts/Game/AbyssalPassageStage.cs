using System;
using UnityEngine;
using UnityEngine.Rendering;

// 海底の二本の柱の間を一体だけ通過する。時刻と形だけを受け、判定やBPMを参照しない。
[ExecuteAlways]
public sealed class AbyssalPassageStage : MonoBehaviour
{
    public const float Duration = 14f;
    const int FinEdges = 7, FinVertices = FinEdges * 12, FinCount = 4;
    const float MiddleSpeed = (.51f - .40f) / 8f;
    static readonly Vector3 Start = new Vector3(-8.2f * 16f / 12f, 0, 9f);
    static readonly Vector3 End = new Vector3(-8.2f * 33f / 23f, 0, 26f);
    static readonly Vector3 StartDirection = new Vector3(Start.x, 0, Start.z + 7f).normalized;
    static readonly Vector3 EndDirection = new Vector3(End.x, 0, End.z + 7f).normalized;
    static readonly Vector3 Control1 = Start + StartDirection;
    static readonly Vector3 Control2 = End - EndDirection;
    Vector3[] vertices, normals;
    Mesh passageMesh;
    Material passageMaterial;
    MeshRenderer passageRenderer;
    MeshFilter passageFilter;
    float floor;
    bool built, lastReduced;
    int write;

    public bool IsVisible => built && passageRenderer != null && passageRenderer.enabled && isActiveAndEnabled;
    public float PassAge { get; private set; } = -1;
    public float FoldOpening { get; private set; }
    public Vector3 WorldCenter => transform.position;
    public Vector3 LocalCenter => transform.localPosition;
    public int VertexCount => vertices == null ? 0 : vertices.Length;
    public int BodyVertexCount { get; private set; }

    public static AbyssalPassageStage Create(Transform parent, float floor)
    {
        if (parent == null || !Finite(floor)) return null;
        var existing = parent.GetComponentInChildren<AbyssalPassageStage>(true);
        if (existing != null) return existing;
        var shader = Resources.Load<Shader>("Stage/ScenicSurface");
        if (shader == null) return null;
        var go = new GameObject("AbyssalPassage");
        go.transform.SetParent(parent, false);
        var stage = go.AddComponent<AbyssalPassageStage>();
        stage.floor = floor;
        stage.Build(shader);
        return stage;
    }

    void Build(Shader shader)
    {
        // 甲羅、腹、丸い頭、首、短い尾。閉じた厚みを持ち、点灯する目や模様を付けない。
        var body = new StageGeometry();
        body.Ellipsoid(new Vector3(0, .08f, -.14f), new Vector3(.49f, .29f, .90f), 16, 8);
        body.Ellipsoid(new Vector3(0, -.07f, -.14f), new Vector3(.46f, .20f, .88f), 12, 6);
        body.Ellipsoid(new Vector3(0, .06f, 1.03f), new Vector3(.22f, .17f, .31f), 10, 6);
        body.Ellipsoid(new Vector3(0, .015f, .80f), new Vector3(.15f, .13f, .27f), 8, 4);
        body.Ellipsoid(new Vector3(0, -.065f, -1.05f), new Vector3(.07f, .05f, .22f), 6, 4);
        passageMesh = body.CreateMesh("AbyssalPassage/Creature");
        passageMesh.hideFlags = HideFlags.DontSave;
        passageMesh.MarkDynamic();
        BodyVertexCount = body.VertexCount;
        vertices = new Vector3[BodyVertexCount + FinCount * FinVertices];
        normals = new Vector3[vertices.Length];
        Array.Copy(passageMesh.vertices, vertices, BodyVertexCount);
        Array.Copy(passageMesh.normals, normals, BodyVertexCount);
        int[] bodyIndices = passageMesh.triangles;
        var indices = new int[bodyIndices.Length + FinCount * FinVertices];
        Array.Copy(bodyIndices, indices, bodyIndices.Length);
        for (int i = 0; i < FinCount * FinVertices; i++) indices[bodyIndices.Length + i] = BodyVertexCount + i;

        passageMaterial = new Material(shader) { name = "AbyssalPassage/MatteShell", hideFlags = HideFlags.DontSave };
        passageMaterial.SetColor("_BaseColor", new Color(.18f, .28f, .265f, 1));
        passageMaterial.SetColor("_HazeColor", new Color(.018f, .075f, .105f, 1));
        passageMaterial.SetColor("_AccentColor", Color.black);
        passageMaterial.SetFloat("_Emission", 0);
        passageMaterial.SetFloat("_Mode", 0);
        passageMaterial.SetFloat("_Sway", 0);
        passageMaterial.SetFloat("_Caustics", 0);
        passageFilter = gameObject.AddComponent<MeshFilter>();
        passageFilter.sharedMesh = passageMesh;
        passageRenderer = gameObject.AddComponent<MeshRenderer>();
        passageRenderer.sharedMaterial = passageMaterial;
        passageRenderer.shadowCastingMode = ShadowCastingMode.Off;
        passageRenderer.receiveShadows = false;
        passageRenderer.lightProbeUsage = LightProbeUsage.Off;
        passageRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        built = true;
        DrawFins(0, 0);
        passageMesh.SetTriangles(indices, 0, false);
        passageMesh.bounds = new Bounds(Vector3.zero, new Vector3(2.50f, 1.0f, 2.80f));
        Clear();
    }

    public void SetPass(float ageSeconds)
    {
        if (!built) return;
        if (!isActiveAndEnabled || !Finite(ageSeconds) || ageSeconds < 0 || ageSeconds >= Duration)
        { Clear(); return; }
        bool reduced = DisplaySettings.ReducedEffects;
        if (PassAge == ageSeconds && lastReduced == reduced && passageRenderer.enabled) return;
        PassAge = ageSeconds;
        lastReduced = reduced;
        FoldOpening = EvaluateFold(ageSeconds);
        transform.localPosition = EvaluateCenter(ageSeconds, floor);
        // 鰭の面を見せる主姿勢。LOWでも保ち、畳んだ両端では元の姿勢に戻す。
        transform.localRotation = Quaternion.LookRotation(EvaluateForward(ageSeconds), Vector3.up)
            * Quaternion.AngleAxis(-18f * FoldOpening, Vector3.forward);
        // 遮蔽に必要な航路と折畳みはLOWでも同じ。小さな遊泳だけを抑える。
        float stroke = 6f * Mathf.Sin(ageSeconds * (Mathf.PI * 2f / 4.6f)) * FoldOpening * (reduced ? .3f : 1f);
        DrawFins(FoldOpening, stroke);
        passageRenderer.enabled = true;
    }

    public void Clear()
    {
        // 非活動区間のTickで同じ閉形を毎回アップロードしない。
        if (built && PassAge < 0 && passageRenderer != null && !passageRenderer.enabled) return;
        PassAge = -1;
        FoldOpening = 0;
        if (!built) return;
        transform.localPosition = EvaluateCenter(0, floor);
        transform.localRotation = Quaternion.LookRotation(StartDirection, Vector3.up);
        DrawFins(0, 0);
        passageRenderer.enabled = false;
    }

    public static float EvaluateFold(float ageSeconds)
    {
        if (!Finite(ageSeconds) || ageSeconds < 0 || ageSeconds >= Duration) return 0;
        return Mathf.Min(Ease(ageSeconds / 3f), Ease((Duration - ageSeconds) / 3f));
    }

    public static float EvaluateTravel(float ageSeconds)
    {
        if (!Finite(ageSeconds)) return 0;
        float age = Mathf.Clamp(ageSeconds, 0, Duration);
        // 中央の狭い可視航路へ8秒を配分。両境界のdu/d秒を揃え、途中で止めない。
        if (age < 3f) return Hermite(0, .40f, 0, MiddleSpeed * 3f, age / 3f);
        if (age <= 11f) return .40f + (age - 3f) * MiddleSpeed;
        return Hermite(.51f, 1, MiddleSpeed * 3f, 0, (age - 11f) / 3f);
    }

    public static Vector3 EvaluateCenter(float ageSeconds, float floorY)
    {
        if (!Finite(floorY)) return Vector3.zero;
        float u = EvaluateTravel(ageSeconds), v = 1 - u;
        Vector3 center = v * v * v * Start + 3f * v * v * u * Control1
            + 3f * v * u * u * Control2 + u * u * u * End;
        center.y = floorY + 2.6f;
        return center;
    }

    public static Vector3 EvaluateForward(float ageSeconds)
    {
        float u = EvaluateTravel(ageSeconds), v = 1 - u;
        return (3f * v * v * (Control1 - Start) + 6f * v * u * (Control2 - Control1)
            + 3f * u * u * (End - Control2)).normalized;
    }

    static float Hermite(float start, float end, float startTangent, float endTangent, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * start + (t3 - 2 * t2 + t) * startTangent
            + (-2 * t3 + 3 * t2) * end + (t3 - t2) * endTangent;
    }

    static float Ease(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3 - 2 * t);
    }

    void DrawFins(float opening, float stroke)
    {
        write = BodyVertexCount;
        for (int fin = 0; fin < FinCount; fin++)
        {
            float side = fin < 2 ? -1 : 1;
            bool front = fin % 2 == 0;
            Vector3 root = new Vector3(side * (front ? .38f : .33f), front ? -.11f : -.12f, front ? .35f : -.63f);
            float length = front ? 1 : .55f;
            Quaternion turn = Quaternion.Euler(0, side * 90f * (1 - opening), 0)
                * Quaternion.AngleAxis(side * stroke, Vector3.forward);
            Vector3 top = root + turn * new Vector3(side * .30f * length, .045f, -.02f * length);
            Vector3 bottom = root + turn * new Vector3(side * .30f * length, -.045f, -.02f * length);
            for (int edge = 0; edge < FinEdges; edge++)
            {
                Vector2 a = FinOutline(edge) * length, b = FinOutline((edge + 1) % FinEdges) * length;
                Vector3 at = root + turn * new Vector3(side * a.x, .025f, a.y);
                Vector3 ab = root + turn * new Vector3(side * a.x, -.025f, a.y);
                Vector3 bt = root + turn * new Vector3(side * b.x, .025f, b.y);
                Vector3 bb = root + turn * new Vector3(side * b.x, -.025f, b.y);
                Triangle(top, at, bt, turn * Vector3.up);
                Triangle(bottom, bb, ab, turn * Vector3.down);
                Vector3 sideNormal = Vector3.Cross(bt - at, ab - at).normalized;
                Vector3 away = ((at + bt) * .5f - root - turn * new Vector3(side * .30f * length, 0, -.02f * length)).normalized;
                if (Vector3.Dot(sideNormal, away) < 0) sideNormal = -sideNormal;
                Triangle(at, ab, bb, sideNormal);
                Triangle(at, bb, bt, sideNormal);
            }
        }
        passageMesh.SetVertices(vertices);
        passageMesh.SetNormals(normals);
    }

    static Vector2 FinOutline(int index)
    {
        switch (index)
        {
            case 0: return new Vector2(0, 0);
            case 1: return new Vector2(.18f, .14f);
            case 2: return new Vector2(.55f, .14f);
            case 3: return new Vector2(.78f, .02f);
            case 4: return new Vector2(.72f, -.10f);
            case 5: return new Vector2(.38f, -.18f);
            default: return new Vector2(.08f, -.12f);
        }
    }

    void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
        if (Vector3.Dot(normal, outward) < 0) { Vector3 swap = b; b = c; c = swap; normal = -normal; }
        vertices[write] = a; vertices[write + 1] = b; vertices[write + 2] = c;
        normals[write] = normals[write + 1] = normals[write + 2] = normal;
        write += 3;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    void OnDisable() { Clear(); }
    void OnDestroy()
    {
        if (passageFilter != null) passageFilter.sharedMesh = null;
        if (passageRenderer != null) passageRenderer.sharedMaterial = null;
        Release(passageMesh); Release(passageMaterial);
        passageMesh = null; passageMaterial = null;
        vertices = normals = null;
        BodyVertexCount = 0; built = false;
    }
    static void Release(UnityEngine.Object item)
    {
        if (item == null) return;
        if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
    }
}
