using System.Collections.Generic;
using UnityEngine;

// ノーツの「見た目」を実行時に組み立てるコンポーネント。
// CuttableNote の RequiredDirection / RequiredCutCount を読んで、Tap / Direction / Long で見た目を分岐する。
// 旧プレハブが持っている装飾子（Front/Back/Target/Dot*/Stripe* など）は剥がして統一スタイルに置き換える。
// 各ノーツは独自の Material を生成するので、脈動アニメは他ノーツに影響しない。
public class NoteVisuals : MonoBehaviour
{
    public enum NoteKind { Tap, Direction, Long }

    [Header("Kind (auto from CuttableNote if present)")]
    public NoteKind kind = NoteKind.Tap;
    public int longSegments = 1; // Long の場合に分割表示する数
    public int maxVisualSegments = 8; // 表示するセグメント数の上限（count=50 等で発散しないように）

    [Header("Look")]
    public Color baseColor = new Color(1f, 0.25f, 0.3f);
    public bool inheritColorFromMainRenderer = true;
    public float baseEmissionStrength = 1.4f;

    [Header("Pulse")]
    public float pulseAmplitude = 0.6f;
    public float pulseSpeedHz = 1.6f;

    [Header("Composition")]
    public bool stripLegacyDecorations = true;

    // 旧 Builder が貼り付けていた装飾の名前一覧。NoteSpawner が追加する Arrow / CountLabel は保持。
    private static readonly HashSet<string> LegacyDecorationNames = new HashSet<string>
    {
        "Front", "Back", "Right", "Left", "Top", "Bottom",
        "Target", "DotTL", "DotTR", "DotBL", "DotBR",
        "StripeTop-1", "StripeTop0", "StripeTop1",
        "StripeBot-1", "StripeBot0", "StripeBot1",
        "StripeR-1", "StripeR0", "StripeR1",
        "StripeL-1", "StripeL0", "StripeL1"
    };

    private Material runtimeBodyMat;
    private readonly List<Material> ownedSubMaterials = new List<Material>();
    private float age;
    private float emissionBoost = 1f;
    private float lastAppliedStrength = -1f;

    void Awake()
    {
        // CuttableNote が同居していれば、種別を自動判定。
        var note = GetComponent<CuttableNote>();
        if (note != null)
        {
            if (note.RequiredCutCount > 1)
            {
                kind = NoteKind.Long;
                longSegments = Mathf.Max(1, note.RequiredCutCount);
            }
            else if (note.RequiredDirection != CutDirection.None)
            {
                kind = NoteKind.Direction;
            }
            else
            {
                kind = NoteKind.Tap;
            }
        }

        // 色決定の優先順位：
        // 1. 金ノーツ（Gold）はチャートの色より優先
        // 2. Long / Direction は kind 固有色を使う（既存の red/blue より見分けやすい）
        // 3. Tap は譜面のマテリアル色（red / blue / default）をそのまま引き継ぐ
        bool overrideColor = false;
        Color overrideC = Color.white;
        if (note != null && note.IsGold)
        {
            overrideColor = true;
            overrideC = UISkinPalette.NoteGold;
        }
        else if (kind == NoteKind.Long)
        {
            overrideColor = true;
            overrideC = UISkinPalette.NoteLong;
        }
        else if (kind == NoteKind.Direction)
        {
            overrideColor = true;
            overrideC = UISkinPalette.NoteFlick;
        }

        if (overrideColor)
        {
            baseColor = overrideC;
            inheritColorFromMainRenderer = false;
        }
        else if (inheritColorFromMainRenderer)
        {
            var sourceMr = GetComponent<MeshRenderer>();
            if (sourceMr != null && sourceMr.sharedMaterial != null)
            {
                baseColor = ReadBaseColor(sourceMr.sharedMaterial);
            }
        }

        if (stripLegacyDecorations) StripLegacyDecorations();
        BuildVisuals();
    }

    void OnDestroy()
    {
        // ランタイム生成 Material のリーク回避
        SafeDestroyMat(runtimeBodyMat);
        foreach (var m in ownedSubMaterials) SafeDestroyMat(m);
        ownedSubMaterials.Clear();
    }

    void Update()
    {
        float pulse = 1f;
        if (pulseAmplitude > 0f && pulseSpeedHz > 0f)
        {
            age += Time.deltaTime;
            pulse = 1f + pulseAmplitude * Mathf.Sin(age * pulseSpeedHz * 2f * Mathf.PI);
        }
        float combined = pulse * emissionBoost;
        float strength = baseEmissionStrength * combined;
        // 脈動もブーストも変化していなければ SetColor を呼ばない
        if (Mathf.Approximately(strength, lastAppliedStrength)) return;
        lastAppliedStrength = strength;
        // ボディ
        ApplyEmissionStrength(runtimeBodyMat, strength);
        // サブマテリアル群（コア・レール・ハロー）。色強度に応じた相対倍率は生成時に焼き込んである。
        foreach (var m in ownedSubMaterials)
        {
            ApplyEmissionStrengthRelative(m, combined);
        }
    }

    // 判定窓内ハイライト等、外部からの発光倍率(1 = 等倍)。NoteTimingCue が毎フレーム設定する。
    public void SetEmissionBoost(float boost)
    {
        emissionBoost = Mathf.Max(0f, boost);
    }

    // ---- public ヘルパー（テストから検査するため） ----

    public int LegacyDecorationCount()
    {
        int count = 0;
        foreach (Transform child in transform)
        {
            if (LegacyDecorationNames.Contains(child.name)) count++;
        }
        return count;
    }

    public Transform FindChildByName(string name)
    {
        foreach (Transform child in transform)
        {
            if (child.name == name) return child;
        }
        return null;
    }

    // ---- 実装 ----

    private void StripLegacyDecorations()
    {
        var toRemove = new List<Transform>();
        foreach (Transform child in transform)
        {
            if (LegacyDecorationNames.Contains(child.name)) toRemove.Add(child);
        }
        foreach (var t in toRemove)
        {
            SafeDestroyGo(t.gameObject);
        }
    }

    private void BuildVisuals()
    {
        ReplaceBodyMaterial();
        switch (kind)
        {
            case NoteKind.Tap: BuildTap(); break;
            case NoteKind.Direction: BuildDirection(); break;
            case NoteKind.Long: BuildLong(); break;
        }
    }

    private void ReplaceBodyMaterial()
    {
        var mr = GetComponent<MeshRenderer>();
        if (mr == null) return;
        // 半透明ボディ：内部が透けて結晶らしく見える。_ZWrite=1 で深度を残し前後関係を安定させる。
        runtimeBodyMat = MakeTranslucentLit(baseColor, baseEmissionStrength, alpha: 0.55f);
        if (runtimeBodyMat.HasProperty("_Smoothness")) runtimeBodyMat.SetFloat("_Smoothness", 0.7f);
        if (runtimeBodyMat.HasProperty("_Metallic")) runtimeBodyMat.SetFloat("_Metallic", 0.1f);
        mr.sharedMaterial = runtimeBodyMat;
    }

    // ---- 種別ごとのレイアウト ----

    private void BuildTap()
    {
        AddInnerCoreAtZ(0f, 0.45f, Quaternion.Euler(35f, 25f, 0f), 2.5f);
        AddEdgeRailsFront(emissionScale: 2.2f);
        AddFrontHalo(emissionScale: 1.5f, alpha: 0.6f);
    }

    private void BuildDirection()
    {
        // 矢印が中央を占めるので InnerCore は無し。代わりに前面に「縁取り光」を程よく、
        // 後ろに薄いバックライトを置いて方向ノーツらしく光が抜ける感じに。
        AddEdgeRailsFront(emissionScale: 3.0f);
        AddFrontHalo(emissionScale: 2.0f, alpha: 0.7f);
        AddBackBackLight(emissionScale: 0.9f, alpha: 0.40f);
    }

    private void BuildLong()
    {
        // Long は NoteSpawner で Z 方向に scale.z が伸ばされる（上限あり）。
        // 表示セグメント数も maxVisualSegments でキャップして、count=50 などでも安全に。
        int requested = Mathf.Max(1, longSegments);
        int n = Mathf.Min(requested, Mathf.Max(1, maxVisualSegments));
        for (int k = 0; k < n; k++)
        {
            float zCenter = -0.5f + (2f * k + 1f) / (2f * n);
            float coreSize = Mathf.Min(0.4f, 0.6f / n + 0.1f);
            AddInnerCoreAtZ(zCenter, coreSize, Quaternion.Euler(35f, 25f + k * 15f, 0f), 2.0f);
        }
        for (int k = 1; k < n; k++)
        {
            float zEdge = -0.5f + (float)k / n;
            AddSegmentDivider(zEdge);
        }
        AddEdgeRailsFront(emissionScale: 2.0f);
        AddFrontHalo(emissionScale: 1.4f, alpha: 0.55f);
    }

    // ---- 部品 ----

    private void AddInnerCoreAtZ(float localZ, float size, Quaternion rot, float emissionScale)
    {
        var core = MakeChild("InnerCore" + (localZ == 0f ? "" : $"_{localZ:F2}"),
            PrimitiveType.Cube,
            new Vector3(0f, 0f, localZ),
            new Vector3(size, size, size),
            rot);
        var coreMat = MakeLit(baseColor, baseEmissionStrength * emissionScale);
        StampRelativeFactor(coreMat, emissionScale);
        core.GetComponent<Renderer>().sharedMaterial = coreMat;
        ownedSubMaterials.Add(coreMat);
    }

    private void AddEdgeRailsFront(float emissionScale)
    {
        // 枠は「太い」と認識されると見栄えが落ちるので、髪の毛より一段太いだけのレベルに抑える。
        // 発光で存在感を担保し、物理サイズは控えめに。
        Color railColor = Color.Lerp(baseColor, Color.white, 0.45f);
        var railMat = MakeLit(railColor, baseEmissionStrength * emissionScale);
        StampRelativeFactor(railMat, emissionScale);

        const float front = -0.51f;
        const float half = 0.5f;
        const float thick = 0.022f;       // 0.05 → 0.022
        const float length = 0.92f;       // 角を少しだけ開けて窓枠的にする
        MakeChildWithMaterial("EdgeTop", PrimitiveType.Cube, new Vector3(0f, half, front), new Vector3(length, thick, thick), Quaternion.identity, railMat);
        MakeChildWithMaterial("EdgeBot", PrimitiveType.Cube, new Vector3(0f, -half, front), new Vector3(length, thick, thick), Quaternion.identity, railMat);
        MakeChildWithMaterial("EdgeLft", PrimitiveType.Cube, new Vector3(-half, 0f, front), new Vector3(thick, length, thick), Quaternion.identity, railMat);
        MakeChildWithMaterial("EdgeRgt", PrimitiveType.Cube, new Vector3(half, 0f, front), new Vector3(thick, length, thick), Quaternion.identity, railMat);
        ownedSubMaterials.Add(railMat);
    }

    private void AddFrontHalo(float emissionScale, float alpha)
    {
        var halo = MakeChild("FrontHalo", PrimitiveType.Quad,
            new Vector3(0f, 0f, -0.515f),
            new Vector3(1.0f, 1.0f, 1.0f),
            Quaternion.Euler(0f, 180f, 0f));
        var haloMat = MakeTransparentLit(baseColor, baseEmissionStrength * emissionScale, alpha);
        StampRelativeFactor(haloMat, emissionScale);
        halo.GetComponent<Renderer>().sharedMaterial = haloMat;
        ownedSubMaterials.Add(haloMat);
    }

    private void AddBackBackLight(float emissionScale, float alpha)
    {
        var back = MakeChild("BackBackLight", PrimitiveType.Quad,
            new Vector3(0f, 0f, 0.515f),
            new Vector3(1.4f, 1.4f, 1.0f),
            Quaternion.identity);
        var mat = MakeTransparentLit(baseColor, baseEmissionStrength * emissionScale, alpha);
        StampRelativeFactor(mat, emissionScale);
        back.GetComponent<Renderer>().sharedMaterial = mat;
        ownedSubMaterials.Add(mat);
    }

    private void AddSegmentDivider(float localZ)
    {
        // 区切りも太いと「線の集合」感が出るので、薄い帯にして発光で見せる。
        var div = MakeChild("Divider", PrimitiveType.Cube,
            new Vector3(0f, 0f, localZ),
            new Vector3(1.01f, 1.01f, 0.008f),
            Quaternion.identity);
        Color dividerColor = Color.Lerp(baseColor, Color.white, 0.6f);
        var mat = MakeLit(dividerColor, baseEmissionStrength * 1.2f);
        StampRelativeFactor(mat, 1.2f);
        div.GetComponent<Renderer>().sharedMaterial = mat;
        ownedSubMaterials.Add(mat);
    }

    // ---- マテリアル生成 ----

    private static Material MakeLit(Color color, float emissionStrength)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        SetBaseColor(m, color);
        SetEmission(m, color, emissionStrength);
        return m;
    }

    // ハロー用：加算ブレンド＋深度書かない
    private static Material MakeTransparentLit(Color color, float emissionStrength, float alpha)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        m.renderQueue = 3000;
        Color c = color;
        c.a = alpha;
        SetBaseColor(m, c);
        SetEmission(m, color, emissionStrength);
        return m;
    }

    // ボディ用：半透明だが深度を残す。ガラス的に中身が透ける。
    private static Material MakeTranslucentLit(Color color, float emissionStrength, float alpha)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
        m.renderQueue = 2450;
        Color c = color;
        c.a = alpha;
        SetBaseColor(m, c);
        SetEmission(m, color, emissionStrength);
        return m;
    }

    private static void SetBaseColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else if (m.HasProperty("_Color")) m.color = c;
    }

    // _BaseColor も _Color も無いマテリアル(2D URP の Mesh2D-Lit-Default 等)では
    // m.color がエラーログを出すため、必ず HasProperty でガードする。
    private static Color ReadBaseColor(Material m)
    {
        if (m == null) return Color.white;
        if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
        if (m.HasProperty("_Color")) return m.color;
        return Color.white;
    }

    private static void SetEmission(Material m, Color color, float strength)
    {
        if (!m.HasProperty("_EmissionColor")) return;
        m.EnableKeyword("_EMISSION");
        Color c = color;
        c.a = 1f;
        m.SetColor("_EmissionColor", c * strength);
    }

    // 脈動更新のため、サブマテリアルが「ボディの何倍の emission を持つか」を覚えておく。
    private const string RelativeFactorTag = "_NoteVisualsEmissionFactor";
    private static void StampRelativeFactor(Material m, float factor)
    {
        // Shader のプロパティに値を覚えさせる手はないので、Material.name に焼き込む。
        m.name = $"NoteVisualsSub:{factor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static float ReadRelativeFactor(Material m)
    {
        if (m == null || string.IsNullOrEmpty(m.name)) return 1f;
        if (!m.name.StartsWith("NoteVisualsSub:")) return 1f;
        string s = m.name.Substring("NoteVisualsSub:".Length);
        if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) return f;
        return 1f;
    }

    private void ApplyEmissionStrength(Material m, float strength)
    {
        if (m == null) return;
        if (!m.HasProperty("_EmissionColor")) return;
        Color baseC = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
        baseC.a = 1f;
        m.SetColor("_EmissionColor", baseC * strength);
    }

    private void ApplyEmissionStrengthRelative(Material m, float pulse)
    {
        float relative = ReadRelativeFactor(m);
        ApplyEmissionStrength(m, baseEmissionStrength * relative * pulse);
    }

    // ---- GameObject 構築ヘルパー ----

    private GameObject MakeChild(string name, PrimitiveType type, Vector3 localPos, Vector3 localScale, Quaternion localRot)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = localScale;
        var col = go.GetComponent<Collider>();
        if (col != null) SafeDestroy(col);
        return go;
    }

    private void MakeChildWithMaterial(string name, PrimitiveType type, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat)
    {
        var go = MakeChild(name, type, localPos, localScale, localRot);
        var r = go.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = mat;
    }

    private static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    private static void SafeDestroyGo(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    private static void SafeDestroyMat(Material m)
    {
        if (m == null) return;
        if (Application.isPlaying) Destroy(m);
        else DestroyImmediate(m);
    }
}
