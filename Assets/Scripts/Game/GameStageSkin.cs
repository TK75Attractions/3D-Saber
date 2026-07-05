using UnityEngine;

// プレイ画面(Game シーン)の環境を「Neon Focus」テーマへ実行時に組み直す。
// 設計原則:1つの瞬間に1つの主信号。ノーツが最も明るく、判定ゲートが2番目、環境は暗く沈める。
//   ・判定面:半透明パネル+グローの「面」をやめ、細いネオン枠の「ゲート」1つに集約する
//   ・奥行き:フォグで遠方を背景色に溶かし、ノーツが「奥から浮かび上がる」ようにする
//   ・シーン(.unity)は書き換えず、Play 中のインスタンスだけ変更する(非破壊)
// GamePlayManager.useOverhauledStage が true のとき Start から呼ばれる。
public static class GameStageSkin
{
    // ---- テーマ定数(テストからも参照する) ----

    // 背景・フォグ:ほぼ黒(わずかに青)。彩度の強い紺はプレイ中の視界で主張しすぎた。
    public static readonly Color BackgroundColor = new Color(0.006f, 0.009f, 0.022f, 1f);
    public const float FogDensity = 0.035f; // Exponential。判定面(視距離~7m)で減衰~0.78、スポーン(~27m)で~0.39

    // 判定ゲート:シアンの細枠+白いコーナー。パネル塗りはほぼ消す。
    public static readonly Color GateColor = new Color(0.27f, 1f, 0.97f);   // UISkinPalette.Cyan
    public static readonly Color GateCornerColor = new Color(0.95f, 0.98f, 1f);
    public const float GateBarThickness = 0.06f;
    public const float GateEmission = 2.2f;
    public const float GateCornerEmission = 3.0f;
    public const float GateCornerLength = 0.45f;
    public const float PanelFillAlpha = 0.04f;  // 「面がある」ことがギリ分かる程度

    // 小節線:ゲートより確実に暗く(視覚ヒエラルキー維持)
    public const float BarLineAlpha = 0.12f;
    public const float BarLineThickness = 0.02f;

    // 旧 SimplifyJudgeGuide と同じ:判定面ガイドから剥がす子オブジェクトの名前接頭辞。
    private static readonly string[] GuideStripPrefixes = {
        "GridV", "GridH", "Border", "Corner", "Cross"
    };

    // シーン全体へテーマを適用する(冪等)。
    public static void Apply()
    {
        ApplyCameraAndFog(Camera.main);
        RestyleJudgeGuide(GameObject.Find("JudgeGuide"));
    }

    // ---- カメラ+フォグ ----

    public static void ApplyCameraAndFog(Camera cam)
    {
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BackgroundColor;
        }
        // 奥行きの距離感を作る:遠方が背景色に溶けて、ノーツが接近につれ浮かび上がる。
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = FogDensity;
        RenderSettings.fogColor = BackgroundColor;
    }

    // ---- 判定ゲート ----

    // 旧ガイドの格子・枠を剥がし、パネルをほぼ透明化し、細いネオンゲートを1つ建てる(冪等)。
    public static void RestyleJudgeGuide(GameObject guide)
    {
        if (guide == null) return;

        StripLegacyGuideChildren(guide.transform);

        var panel = guide.transform.Find("JudgePanel");
        if (panel == null) return;

        // 旧テーマの残骸(外周グロー/ビート参照線)はゲートと役割が被るので除去
        RemoveChild(guide.transform, "JudgePanelGlow");
        RemoveChild(guide.transform, "BeatReferenceLine");

        DimPanelFill(panel);
        BuildGate(guide.transform, panel);
    }

    private static void StripLegacyGuideChildren(Transform guide)
    {
        var toRemove = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in guide)
        {
            string n = child.name;
            foreach (var prefix in GuideStripPrefixes)
            {
                if (n.StartsWith(prefix)) { toRemove.Add(child); break; }
            }
        }
        foreach (var t in toRemove) SafeDestroy(t.gameObject);
    }

    private static void RemoveChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) SafeDestroy(t.gameObject);
    }

    // パネルの「面」はほぼ消す。ゲート枠が主役で、面はうっすら領域を示すだけ。
    private static void DimPanelFill(Transform panel)
    {
        var mr = panel.GetComponent<MeshRenderer>();
        if (mr == null || mr.sharedMaterial == null) return;
        // renderer.material は EditMode でエラーログを出すため、複製→sharedMaterial 差し替えで共有を守る。
        var m = new Material(mr.sharedMaterial);
        mr.sharedMaterial = m;
        MakeMaterialTransparent(m);
        Color c = BackgroundColor;
        c.a = PanelFillAlpha;
        SetBaseColor(m, c);
        // 面は発光させない(発光はゲート枠の仕事)
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
    }

    // パネルの外周に細いネオン枠+四隅の白ブラケットを建てる。
    private static void BuildGate(Transform guide, Transform panel)
    {
        if (guide.Find("JudgeGate") != null) return; // 冪等

        var gate = new GameObject("JudgeGate");
        gate.transform.SetParent(guide, false);
        gate.transform.localPosition = panel.localPosition + new Vector3(0f, 0f, -0.01f);

        Vector3 ps = panel.localScale;
        float hw = ps.x * 0.5f;   // 半幅
        float hh = ps.y * 0.5f;   // 半高
        float t = GateBarThickness;

        var barMat = MakeEmissiveTransparent(GateColor, GateEmission, 0.9f);

        // 外枠 4 本
        MakeBar(gate.transform, "GateTop", new Vector3(0f, hh, 0f), new Vector3(ps.x + t, t, t), barMat);
        MakeBar(gate.transform, "GateBottom", new Vector3(0f, -hh, 0f), new Vector3(ps.x + t, t, t), barMat);
        MakeBar(gate.transform, "GateLeft", new Vector3(-hw, 0f, 0f), new Vector3(t, ps.y + t, t), barMat);
        MakeBar(gate.transform, "GateRight", new Vector3(hw, 0f, 0f), new Vector3(t, ps.y + t, t), barMat);

        // 四隅ブラケット(白・明るめ):「ここが切る場所」のアンカー
        var cornerMat = MakeEmissiveTransparent(GateCornerColor, GateCornerEmission, 1f);
        float cl = GateCornerLength;
        float ct = t * 1.6f;
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sy = -1; sy <= 1; sy += 2)
            {
                string tag = (sy > 0 ? "T" : "B") + (sx > 0 ? "R" : "L");
                // 横棒と縦棒で L 字を作る
                MakeBar(gate.transform, $"GateCorner{tag}_h",
                    new Vector3(sx * (hw - cl * 0.5f), sy * hh, -0.005f),
                    new Vector3(cl, ct, ct), cornerMat);
                MakeBar(gate.transform, $"GateCorner{tag}_v",
                    new Vector3(sx * hw, sy * (hh - cl * 0.5f), -0.005f),
                    new Vector3(ct, cl, ct), cornerMat);
            }
        }
    }

    private static void MakeBar(Transform parent, string name, Vector3 localPos, Vector3 localScale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        var col = go.GetComponent<Collider>();
        if (col != null) SafeDestroy(col);
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    // ---- マテリアル ----

    private static Material MakeEmissiveTransparent(Color color, float emission, float alpha)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        MakeMaterialTransparent(m);
        Color c = color;
        c.a = alpha;
        SetBaseColor(m, c);
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            Color e = color; e.a = 1f;
            m.SetColor("_EmissionColor", e * emission);
        }
        return m;
    }

    private static void MakeMaterialTransparent(Material m)
    {
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        m.renderQueue = 3005;
    }

    // _BaseColor も _Color も無いマテリアル(2D URP 既定等)でエラーを出さないよう必ずガードする。
    private static void SetBaseColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else if (m.HasProperty("_Color")) m.color = c;
    }

    private static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Object.Destroy(o);
        else Object.DestroyImmediate(o);
    }
}
