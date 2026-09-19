using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 確定Perfectを背景の素材へ返す。床4列の共通反応は親が維持する。
// 四つの状態と二枚の結合メッシュだけを持ち、曲時計以外では進めない。
// 編集中のプレビューにも生成されるため、解除・破棄の通知を編集時にも受け取る。
[ExecuteAlways]
public sealed class StageThemeResponse : MonoBehaviour
{
    public const int LaneCount = 4;
    public const int VertexBudget = 4096;
    public const float GardenLifetime = 1.2f;
    public const float FoundryLifetime = .65f;
    readonly bool[] active = new bool[LaneCount];
    readonly float[] ages = new float[LaneCount];
    readonly List<Vector3> surfaceVertices = new List<Vector3>(VertexBudget);
    readonly List<Vector3> normals = new List<Vector3>(VertexBudget);
    readonly List<int> surfaceIndices = new List<int>(VertexBudget * 3 / 2);
    readonly List<Vector3> accentVertices = new List<Vector3>(VertexBudget);
    readonly List<Vector3> uvs = new List<Vector3>(VertexBudget);
    readonly List<Color> colors = new List<Color>(VertexBudget);
    readonly List<int> accentIndices = new List<int>(VertexBudget * 3 / 2);
    Mesh surfaceMesh, accentMesh;
    Material surfaceMaterial, accentMaterial;
    MeshRenderer surfaceRenderer, accentRenderer;
    StageTheme theme;
    float floor;
    bool built, hasTime, dirty, lastProjector;
    public bool ReplacesSideResponse => built;
    public int ActiveResponseCount { get; private set; }
    public int ActiveLaneMask { get; private set; }
    public double LastTickSeconds { get; private set; }

    public static StageThemeResponse Create(Transform parent, StageTheme theme, float floorY)
    {
        if (parent == null || !Finite(floorY) ||
            (theme != StageTheme.AmberFoundry && theme != StageTheme.MoonlitGarden)) return null;
        var surface = Resources.Load<Shader>("Stage/ScenicSurface");
        var accent = Resources.Load<Shader>("Effects/GameplayCutAccent");
        // 材質がない場合は側面の既存反応を置き換えない。
        if (surface == null || accent == null) return null;
        var go = new GameObject("StageThemeResponse-" + theme);
        go.transform.SetParent(parent, false);
        var response = go.AddComponent<StageThemeResponse>();
        response.theme = theme; response.floor = floorY;
        response.Build(surface, accent);
        return response;
    }

    void Build(Shader surface, Shader accent)
    {
        surfaceMaterial = new Material(surface) { name = "ThemeResponse/Surface", hideFlags = HideFlags.DontSave };
        surfaceMaterial.SetColor("_BaseColor", theme == StageTheme.MoonlitGarden
            ? new Color(.28f, .37f, .17f) : new Color(.27f, .245f, .19f));
        surfaceMaterial.SetColor("_HazeColor", new Color(.035f, .065f, .08f));
        surfaceMaterial.SetColor("_AccentColor", new Color(.28f, .31f, .18f));
        surfaceMaterial.SetFloat("_Emission", .025f);
        surfaceMaterial.SetFloat("_Mode", 0);
        surfaceMaterial.SetFloat("_Sway", 0);
        accentMaterial = new Material(accent) { name = "ThemeResponse/Details", hideFlags = HideFlags.DontSave };
        surfaceMesh = new Mesh { name = "ThemeResponse/SurfaceMesh", hideFlags = HideFlags.DontSave };
        accentMesh = new Mesh { name = "ThemeResponse/AccentMesh", hideFlags = HideFlags.DontSave };
        if (theme == StageTheme.MoonlitGarden) surfaceMesh.MarkDynamic();
        accentMesh.MarkDynamic();
        surfaceRenderer = Emit("Surface", surfaceMesh, surfaceMaterial);
        accentRenderer = Emit("Details", accentMesh, accentMaterial);
        if (theme == StageTheme.AmberFoundry)
        {
            for (int lane = 0; lane < LaneCount; lane++) BuildGaugeHousing(lane);
            UploadSurface();
        }
        built = true; dirty = true;
        Draw();
    }

    MeshRenderer Emit(string name, Mesh mesh, Material material)
    {
        var go = new GameObject(name); go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return renderer;
    }

    // 親は方向降格後のPerfectのみを一度通知する。手色は受け取らない。
    public void OnPerfect(int lane)
    {
        if (!built || !isActiveAndEnabled || lane < 0 || lane >= LaneCount) return;
        // 同列の高速連打でも葉を着水させ、最初の寿命を延長しない。
        // この間の各Perfectは親の床反応へ任せ、別列の葉は独立して開始できる。
        if (theme == StageTheme.MoonlitGarden && active[lane]) return;
        active[lane] = true; ages[lane] = 0; dirty = true;
        CountResponses();
    }

    public void Tick(double songSeconds)
    {
        if (!built || !isActiveAndEnabled || !Finite(songSeconds)) return;
        songSeconds = Math.Max(0, songSeconds);
        double delta = hasTime ? songSeconds - LastTickSeconds : 0;
        if (delta < -.001)
        {
            ResetResponses();
            delta = 0;
        }
        hasTime = true; LastTickSeconds = songSeconds;
        float lifetime = theme == StageTheme.MoonlitGarden ? GardenLifetime : FoundryLifetime;
        for (int lane = 0; lane < LaneCount; lane++)
        {
            if (!active[lane] || delta <= 0) continue;
            // 巨大なシーク値でもfloat化のオーバーフローを起こさない。
            ages[lane] += (float)Math.Min(delta, lifetime);
            if (ages[lane] >= lifetime) { ages[lane] = 0; active[lane] = false; }
            dirty = true;
        }
        CountResponses();
        if (dirty || lastProjector != DisplaySettings.ProjectorMode) Draw();
    }

    public void Clear()
    {
        ResetResponses(); hasTime = false; LastTickSeconds = 0;
        if (built && isActiveAndEnabled) Draw();
    }

    void ResetResponses()
    {
        Array.Clear(active, 0, active.Length); Array.Clear(ages, 0, ages.Length);
        ActiveResponseCount = 0; ActiveLaneMask = 0; dirty = true;
    }

    void CountResponses()
    {
        ActiveResponseCount = 0; ActiveLaneMask = 0;
        for (int lane = 0; lane < LaneCount; lane++) if (active[lane])
        { ActiveResponseCount++; ActiveLaneMask |= 1 << lane; }
    }

    // 外側列は近景、内側列は奥に分け、同じ半分の同時Perfectを混ぜない。
    Vector3 Anchor(int lane)
    {
        int side = lane < 2 ? -1 : 1;
        bool outer = lane == 0 || lane == 3;
        return theme == StageTheme.MoonlitGarden
            ? new Vector3(side * 7.75f, floor - .075f, outer ? 5.8f : 10.5f)
            : new Vector3(side * 6.55f, floor + 1.65f, outer ? 5.5f : 14.7f);
    }

    Quaternion GaugeRotation(int lane) => Quaternion.Euler(0, lane < 2 ? -35 : 35, 0);

    void BuildGaugeHousing(int lane)
    {
        Vector3 c = Anchor(lane);
        Quaternion q = GaugeRotation(lane);
        Vector3 right = q * Vector3.right, up = Vector3.up, front = q * Vector3.back;
        const int segments = 32;
        for (int i = 0; i < segments; i++)
        {
            Vector3 a = Circle(right, up, i * Mathf.PI * 2 / segments);
            Vector3 b = Circle(right, up, (i + 1) * Mathf.PI * 2 / segments);
            SolidQuad(c + a * .54f, c + b * .54f, c + b * .445f, c + a * .445f, front);
        }
        // 細い支柱と取付座。ファンや蒸気の既存部品は変更しない。
        SolidQuad(c - right * .07f - up * .49f, c + right * .07f - up * .49f,
            c + right * .07f - up * 1.2f, c - right * .07f - up * 1.2f, front);
        SolidQuad(c - right * .30f - up * 1.16f, c + right * .30f - up * 1.16f,
            c + right * .30f - up * 1.25f, c - right * .30f - up * 1.25f, front);
    }

    void Draw()
    {
        if (!built || accentMesh == null) return;
        accentVertices.Clear(); uvs.Clear(); colors.Clear(); accentIndices.Clear();
        lastProjector = DisplaySettings.ProjectorMode;
        if (theme == StageTheme.MoonlitGarden)
        {
            surfaceVertices.Clear(); normals.Clear(); surfaceIndices.Clear();
            for (int lane = 0; lane < LaneCount; lane++) if (active[lane]) DrawGarden(lane, ages[lane]);
            UploadSurface();
        }
        else for (int lane = 0; lane < LaneCount; lane++) DrawGauge(lane, active[lane] ? ages[lane] : -1);
        accentMesh.Clear(); accentMesh.SetVertices(accentVertices); accentMesh.SetColors(colors);
        accentMesh.SetUVs(0, uvs); accentMesh.SetTriangles(accentIndices, 0, true);
        accentRenderer.enabled = isActiveAndEnabled && accentVertices.Count > 0;
        surfaceRenderer.enabled = isActiveAndEnabled && surfaceMesh.vertexCount > 0;
        dirty = false;
    }

    void DrawGarden(int lane, float age)
    {
        Vector3 c = Anchor(lane);
        int side = lane < 2 ? -1 : 1;
        float settle = Mathf.Clamp01(age / .15f);
        float drift = Mathf.Clamp01((age - .15f) / 1.05f);
        Vector3 leaf = c + new Vector3(side * drift * .34f, (1 - settle) * .24f, drift * .28f);
        float size = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.94f, GardenLifetime, age));
        var turn = Quaternion.Euler(0, side * (24 + drift * 18), 0);
        Vector3 tip = turn * new Vector3(0, 0, .32f) * size;
        Vector3 tail = turn * new Vector3(0, 0, -.24f) * size;
        Vector3 flank = turn * new Vector3(.15f, 0, 0) * size;
        SolidTriangle(leaf + tip, leaf + flank, leaf + tail, Vector3.up);
        SolidTriangle(leaf + tip, leaf + tail, leaf - flank, Vector3.up);
        var vein = new Color(.58f, .59f, .30f, .50f * size);
        Stroke(leaf + tail + Vector3.up * .008f, leaf + tip + Vector3.up * .008f, .025f * size, vein, Vector3.right);
        if (age < .15f) return;
        // 池の上面はfloor-.12。既存の水面頂点変形より少し上で重なりを避ける。
        for (int ring = 0; ring < 2; ring++)
        {
            float t = age - .15f - ring * .12f;
            if (t < 0 || t > .90f) continue;
            float alpha = .40f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / .90f));
            if (lastProjector) alpha *= .78f;
            Ring(c, .10f + t * .93f, .045f, new Color(.43f, .61f, .61f, alpha), Vector3.right, Vector3.forward, 32);
        }
    }

    void DrawGauge(int lane, float age)
    {
        Quaternion q = GaugeRotation(lane);
        Vector3 right = q * Vector3.right, up = Vector3.up, front = q * Vector3.back;
        Vector3 c = Anchor(lane) + front * .012f;
        Color face = new Color(.028f, .038f, .042f, 1);
        for (int i = 0; i < 32; i++)
            FlatTriangle(c, c + Circle(right, up, i * Mathf.PI * 2 / 32) * .443f,
                c + Circle(right, up, (i + 1) * Mathf.PI * 2 / 32) * .443f, face);
        c += front * .012f;
        float gain = lastProjector ? .78f : 1;
        Color mark = new Color(.63f, .62f, .47f, .65f * gain);
        for (int i = 0; i < 9; i++)
        {
            float angle = Mathf.Lerp(-.25f, 1.25f, i / 8f) * Mathf.PI;
            Vector3 axis = Circle(right, up, angle);
            Stroke(c + axis * .33f, c + axis * .40f, .023f, mark, Vector3.Cross(axis, front).normalized);
        }
        float response = age < 0 ? 0 : age < .1f ? Mathf.SmoothStep(0, 1, age / .1f)
            : Mathf.Exp(-(age - .1f) * 9) * Mathf.Cos((age - .1f) * 12);
        float needleAngle = (145 - response * 88) * Mathf.Deg2Rad;
        Vector3 needle = Circle(right, up, needleAngle);
        Stroke(c - needle * .055f, c + needle * .32f, .048f,
            new Color(.88f, .62f, .24f, .95f * gain), Vector3.Cross(needle, front).normalized);
        // 弁の小さい遅れ。輪は固定し、内側のスポークだけを一度ひねる。
        Vector3 valve = c - up * .80f;
        Ring(valve, .19f, .045f, mark, right, up, 24);
        float valveKick = age < .1f ? 0 : Mathf.Sin((age - .1f) * 12) * Mathf.Exp(-(age - .1f) * 7) * .22f;
        for (int spoke = 0; spoke < 3; spoke++)
        {
            Vector3 axis = Circle(right, up, spoke * Mathf.PI * 2 / 3 + valveKick);
            Stroke(valve, valve + axis * .17f, .043f, mark, Vector3.Cross(axis, front).normalized);
        }
    }

    static Vector3 Circle(Vector3 right, Vector3 up, float angle) => right * Mathf.Cos(angle) + up * Mathf.Sin(angle);

    void Ring(Vector3 center, float radius, float width, Color tint, Vector3 right, Vector3 up, int segments)
    {
        for (int i = 0; i < segments; i++)
        {
            Vector3 a = Circle(right, up, i * Mathf.PI * 2 / segments);
            Vector3 b = Circle(right, up, (i + 1) * Mathf.PI * 2 / segments);
            // UV.xを中央に固定し、円周の各継ぎ目が点線にならないようにする。
            AccentQuad(center + a * (radius - width), center + b * (radius - width),
                center + b * (radius + width), center + a * (radius + width), tint, false);
        }
    }

    void Stroke(Vector3 a, Vector3 b, float width, Color tint, Vector3 edge)
    {
        AccentQuad(a - edge * width, b - edge * width, b + edge * width, a + edge * width, tint, false);
    }

    void AccentQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color tint, bool solid)
    {
        int n = accentVertices.Count;
        accentVertices.Add(a); accentVertices.Add(b); accentVertices.Add(c); accentVertices.Add(d);
        float z = solid ? 1 : 0;
        uvs.Add(new Vector3(.5f, 0, z)); uvs.Add(new Vector3(.5f, 0, z));
        uvs.Add(new Vector3(.5f, 1, z)); uvs.Add(new Vector3(.5f, 1, z));
        for (int i = 0; i < 4; i++) colors.Add(tint);
        accentIndices.Add(n); accentIndices.Add(n + 1); accentIndices.Add(n + 2);
        accentIndices.Add(n); accentIndices.Add(n + 2); accentIndices.Add(n + 3);
    }

    void FlatTriangle(Vector3 a, Vector3 b, Vector3 c, Color tint)
    {
        int n = accentVertices.Count;
        accentVertices.Add(a); accentVertices.Add(b); accentVertices.Add(c);
        for (int i = 0; i < 3; i++) { colors.Add(tint); uvs.Add(new Vector3(.5f, .5f, 1)); accentIndices.Add(n + i); }
    }

    void SolidQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    { SolidTriangle(a, b, c, normal); SolidTriangle(a, c, d, normal); }

    void SolidTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0) { Vector3 swap = b; b = c; c = swap; }
        int n = surfaceVertices.Count;
        surfaceVertices.Add(a); surfaceVertices.Add(b); surfaceVertices.Add(c);
        for (int i = 0; i < 3; i++) { normals.Add(normal); surfaceIndices.Add(n + i); }
    }

    void UploadSurface()
    {
        surfaceMesh.Clear(); surfaceMesh.SetVertices(surfaceVertices); surfaceMesh.SetNormals(normals);
        surfaceMesh.SetTriangles(surfaceIndices, 0, true);
    }

    void OnEnable() { if (built) { dirty = true; Draw(); } }
    void OnDisable()
    {
        Clear();
        if (surfaceRenderer != null) surfaceRenderer.enabled = false;
        if (accentRenderer != null) accentRenderer.enabled = false;
    }
    void OnDestroy()
    {
        // Rendererの参照を外してから、自分が作成した二組だけを破棄する。
        if (surfaceRenderer != null) surfaceRenderer.sharedMaterial = null;
        if (accentRenderer != null) accentRenderer.sharedMaterial = null;
        foreach (var filter in GetComponentsInChildren<MeshFilter>()) filter.sharedMesh = null;
        Release(surfaceMesh); Release(accentMesh); Release(surfaceMaterial); Release(accentMaterial);
        built = false;
    }
    static void Release(UnityEngine.Object item)
    {
        if (item == null) return;
        if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
    }
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
