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
    public const float PrismLifetime = .71f;
    public const float CrystalLifetime = .4f;
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
    bool built, hasTime, dirty, lastProjector, lastReduced;
    public bool ReplacesSideResponse => built;
    public int ActiveResponseCount { get; private set; }
    public int ActiveLaneMask { get; private set; }
    public double LastTickSeconds { get; private set; }

    public static StageThemeResponse Create(Transform parent, StageTheme theme, float floorY)
    {
        if (parent == null || !Finite(floorY) ||
            (theme != StageTheme.AmberFoundry && theme != StageTheme.MoonlitGarden &&
                theme != StageTheme.AzurePrism && theme != StageTheme.CrystalGrotto)) return null;
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
            ? new Color(.28f, .37f, .17f) : theme == StageTheme.AzurePrism
            ? new Color(.16f, .25f, .30f) : theme == StageTheme.CrystalGrotto
            ? new Color(.24f, .19f, .34f) : new Color(.27f, .245f, .19f));
        surfaceMaterial.SetColor("_HazeColor", new Color(.035f, .065f, .08f));
        surfaceMaterial.SetColor("_AccentColor", new Color(.28f, .31f, .18f));
        surfaceMaterial.SetFloat("_Emission", .025f);
        surfaceMaterial.SetFloat("_Mode", 0);
        surfaceMaterial.SetFloat("_Sway", 0);
        accentMaterial = new Material(accent) { name = "ThemeResponse/Details", hideFlags = HideFlags.DontSave };
        surfaceMesh = new Mesh { name = "ThemeResponse/SurfaceMesh", hideFlags = HideFlags.DontSave };
        accentMesh = new Mesh { name = "ThemeResponse/AccentMesh", hideFlags = HideFlags.DontSave };
        if (theme == StageTheme.MoonlitGarden || theme == StageTheme.AzurePrism) surfaceMesh.MarkDynamic();
        accentMesh.MarkDynamic();
        surfaceRenderer = Emit("Surface", surfaceMesh, surfaceMaterial);
        accentRenderer = Emit("Details", accentMesh, accentMaterial);
        if (theme == StageTheme.AmberFoundry)
        {
            for (int lane = 0; lane < LaneCount; lane++) BuildGaugeHousing(lane);
            UploadSurface();
        }
        else if (theme == StageTheme.CrystalGrotto)
        {
            for (int lane = 0; lane < LaneCount; lane++) BuildCrystalHousing(lane);
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
        // 絞りも一周期を完了させる。連打のたびに閉じたまま張り付かせない。
        // 結晶の帯も一度だけ面を渡り、稜線で巻き戻ったり予約再生したりしない。
        if (theme != StageTheme.AmberFoundry && active[lane]) return;
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
        float lifetime = theme == StageTheme.MoonlitGarden ? GardenLifetime
            : theme == StageTheme.AzurePrism ? PrismLifetime
            : theme == StageTheme.CrystalGrotto ? CrystalLifetime : FoundryLifetime;
        for (int lane = 0; lane < LaneCount; lane++)
        {
            if (!active[lane] || delta <= 0) continue;
            // 巨大なシーク値でもfloat化のオーバーフローを起こさない。
            ages[lane] += (float)Math.Min(delta, lifetime);
            if (ages[lane] >= lifetime) { ages[lane] = 0; active[lane] = false; }
            dirty = true;
        }
        CountResponses();
        if (dirty || lastProjector != DisplaySettings.ProjectorMode || lastReduced != DisplaySettings.ReducedEffects) Draw();
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
        if (theme == StageTheme.AzurePrism)
            // 奥の装置も柱より通路側へ置き、羽根の中心を隠さない。全頂点は通路外に保つ。
            return new Vector3(side * (outer ? 6.75f : 6.60f), floor + 1.65f, outer ? 5.6f : 10f);
        if (theme == StageTheme.CrystalGrotto)
            return new Vector3(side * 7.15f, floor + .02f, outer ? 4.1f : 10.3f);
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
        lastReduced = DisplaySettings.ReducedEffects;
        if (theme == StageTheme.MoonlitGarden)
        {
            surfaceVertices.Clear(); normals.Clear(); surfaceIndices.Clear();
            for (int lane = 0; lane < LaneCount; lane++) if (active[lane]) DrawGarden(lane, ages[lane]);
            UploadSurface();
        }
        else if (theme == StageTheme.AzurePrism)
        {
            surfaceVertices.Clear(); normals.Clear(); surfaceIndices.Clear();
            for (int lane = 0; lane < LaneCount; lane++) DrawPrism(lane, active[lane] ? ages[lane] : -1);
            UploadSurface();
        }
        else if (theme == StageTheme.CrystalGrotto)
        {
            for (int lane = 0; lane < LaneCount; lane++) if (active[lane]) DrawCrystal(lane, ages[lane]);
        }
        else for (int lane = 0; lane < LaneCount; lane++) DrawGauge(lane, active[lane] ? ages[lane] : -1);
        if (lastReduced)
            for (int i = 0; i < colors.Count; i++) { var c = colors[i]; c.a *= DisplaySettings.AccentScale; colors[i] = c; }
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

    void DrawPrism(int lane, float age)
    {
        Vector3 center = Anchor(lane);
        Quaternion rotation = Quaternion.Euler(0, lane < 2 ? -25 : 25, 0);
        Vector3 right = rotation * Vector3.right, up = Vector3.up, front = rotation * Vector3.back;
        // 六枚の羽根は光ではなく金属面で読ませ、装置の輪郭と取付座は動かさない。
        for (int i = 0; i < 32; i++)
        {
            Vector3 a = Circle(right, up, i * Mathf.PI * 2 / 32);
            Vector3 b = Circle(right, up, (i + 1) * Mathf.PI * 2 / 32);
            SolidQuad(center + a * .61f, center + b * .61f,
                center + b * .53f, center + a * .53f, front);
        }
        SolidQuad(center - right * .055f - up * .58f, center + right * .055f - up * .58f,
            center + right * .055f - up * 1.5f, center - right * .055f - up * 1.5f, front);
        SolidQuad(center - right * .27f - up * 1.43f, center + right * .27f - up * 1.43f,
            center + right * .27f - up * 1.54f, center - right * .27f - up * 1.54f, front);

        float closure = age < 0 ? 0 : age < .16f ? Mathf.SmoothStep(0, 1, age / .16f)
            : age <= .26f ? 1 : 1 - Mathf.SmoothStep(0, 1, (age - .26f) / .45f);
        if (lastReduced) closure *= .3f;
        float aperture = Mathf.Lerp(.38f, .23f, closure);
        float turn = Mathf.Lerp(8, 29, closure) * Mathf.Deg2Rad;
        Vector3 face = center + front * .012f;
        float gain = lastProjector ? .78f : 1;
        Color seam = new Color(.42f, .66f, .76f, .58f * gain);
        for (int blade = 0; blade < 6; blade++)
        {
            float a = blade * Mathf.PI / 3;
            float b = (blade + 1) * Mathf.PI / 3;
            Vector3 outerA = face + Circle(right, up, a) * .535f;
            Vector3 outerB = face + Circle(right, up, b) * .535f;
            Vector3 innerA = face + Circle(right, up, a + turn) * aperture;
            Vector3 innerB = face + Circle(right, up, b + turn) * aperture;
            SolidQuad(outerA, outerB, innerB, innerA, front);
            Vector3 edge = Vector3.Cross(innerA - outerA, front).normalized;
            Stroke(outerA + front * .007f, innerA + front * .007f, .012f, seam, edge);
        }
        // 成功時もフラッシュは加えず、固定された外周の刻みを残す。
        Ring(center + front * .008f, .575f, .009f,
            new Color(.29f, .49f, .60f, .38f * gain), right, up, 32);
        for (int i = 0; i < 4; i++)
        {
            Vector3 axis = Circle(right, up, i * Mathf.PI * .5f);
            Stroke(center + axis * .555f + front * .010f, center + axis * .60f + front * .010f,
                .018f, seam, Vector3.Cross(axis, front).normalized);
        }
    }

    Vector3 CrystalPoint(int lane, Vector3 local)
    {
        return Anchor(lane) + Quaternion.Euler(0, lane < 2 ? -8 : 8, 0) * local;
    }

    void BuildCrystalHousing(int lane)
    {
        // 小結晶は静止した不透明な面。既存の大結晶の回転や常時shineとは分ける。
        var rotation = Quaternion.Euler(0, lane < 2 ? -8 : 8, 0);
        for (int face = 0; face < 4; face++)
        {
            float a = face * Mathf.PI * .5f, b = (face + 1) * Mathf.PI * .5f;
            Vector3 lowerA = new Vector3(Mathf.Cos(a) * .42f, .18f, Mathf.Sin(a) * .30f);
            Vector3 lowerB = new Vector3(Mathf.Cos(b) * .42f, .18f, Mathf.Sin(b) * .30f);
            Vector3 upperA = lowerA + Vector3.up * 1.10f, upperB = lowerB + Vector3.up * 1.10f;
            Vector3 normal = rotation * new Vector3(Mathf.Cos((a + b) * .5f) / .42f, 0,
                Mathf.Sin((a + b) * .5f) / .30f).normalized;
            SolidQuad(CrystalPoint(lane, lowerA), CrystalPoint(lane, lowerB),
                CrystalPoint(lane, upperB), CrystalPoint(lane, upperA), normal);
            Vector3 top = new Vector3(lane < 2 ? -.08f : .08f, 1.84f, 0);
            Vector3 topNormal = rotation * Vector3.Cross(top - upperA, upperB - upperA).normalized;
            SolidTriangle(CrystalPoint(lane, upperA), CrystalPoint(lane, upperB), CrystalPoint(lane, top), topNormal);
            SolidTriangle(CrystalPoint(lane, lowerB), CrystalPoint(lane, lowerA),
                CrystalPoint(lane, Vector3.zero), (normal + Vector3.down).normalized);
        }
    }

    void DrawCrystal(int lane, float age)
    {
        // 外肩→手前の稜→内肩の二面を一方向に渡る短い帯。面の外へ漏らさない。
        float phase = Mathf.Clamp01(age / CrystalLifetime);
        float head = phase * 2.55f, tail = head - .55f;
        float gain = Mathf.Sin(phase * Mathf.PI) * (lastProjector ? .78f : 1);
        int side = lane < 2 ? -1 : 1;
        for (int face = 0; face < 2; face++)
        {
            float from = Mathf.Max(tail, face), to = Mathf.Min(head, face + 1);
            if (to <= from) continue;
            Vector3 start = face == 0 ? new Vector3(side * .42f, .42f, 0) : new Vector3(0, .96f, -.30f);
            Vector3 end = face == 0 ? new Vector3(0, .96f, -.30f) : new Vector3(-side * .42f, .62f, 0);
            Vector3 normal = Vector3.Cross(end - start, Vector3.up).normalized;
            if (normal.z > 0) normal = -normal;
            Vector3 a = Vector3.Lerp(start, end, from - face) + normal * .006f;
            Vector3 b = Vector3.Lerp(start, end, to - face) + normal * .006f;
            Color tint = face == 0 ? new Color(.67f, .54f, .79f, .65f * gain)
                : new Color(.42f, .70f, .70f, .58f * gain);
            AccentQuad(CrystalPoint(lane, a - Vector3.up * .05f), CrystalPoint(lane, b - Vector3.up * .05f),
                CrystalPoint(lane, b + Vector3.up * .05f), CrystalPoint(lane, a + Vector3.up * .05f), tint, true);
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
