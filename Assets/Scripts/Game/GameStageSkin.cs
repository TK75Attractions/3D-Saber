using UnityEngine;

// プレイ画面(Game シーン)の環境を立体的な金属ゲートと暗い環境へ実行時に組み直す。
// 設計原則:1つの瞬間に1つの主信号。ノーツが最も明るく、判定ゲートが2番目、環境は暗く沈める。
//   ・判定面:半透明パネル+グローの「面」をやめ、金属筐体と短い発光インレイの「ゲート」1つに集約する
//   ・奥行き:フォグで遠方を背景色に溶かし、ノーツが「奥から浮かび上がる」ようにする
//   ・シーン(.unity)は書き換えず、Play 中のインスタンスだけ変更する(非破壊)
// GamePlayManager.useOverhauledStage が true のとき Start から呼ばれる。
public static class GameStageSkin
{
    // ---- テーマ定数(テストからも参照する) ----

    // 奥の構造も読める青灰色。背景だけを明るくし、ノーツの発光とスコアは維持する。
    public static readonly Color BackgroundColor = new Color(0.014f, 0.024f, 0.047f, 1f);
    public const float FogDensity = 0.028f;

    // 判定ゲート:暗い面取り筐体と細い氷色のインレイ。パネルの塗りはほぼ消す。
    public static readonly Color GateColor = new Color(0.22f, 0.67f, 0.76f);
    public static readonly Color GateCornerColor = new Color(0.62f, 0.85f, 0.92f);
    public const float GateBarThickness = 0.017f;
    public const float GateEmission = 0.95f;
    public const float GateCornerEmission = 0.8f;
    public const float GateCornerLength = 0.32f;
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
        if (panel == null || guide.transform.Find("JudgeGate") != null) return;

        // 旧テーマの残骸(外周グロー/ビート参照線)はゲートと役割が被るので除去
        RemoveChild(guide.transform, "JudgePanelGlow");
        RemoveChild(guide.transform, "BeatReferenceLine");

        var renderer = panel.GetComponent<MeshRenderer>();
        var original = renderer != null ? renderer.sharedMaterial : null;
        var copy = DimPanelFill(panel);
        var frame = BuildGate(guide.transform, panel);
        frame.OwnPanelMaterial(renderer, original, copy);
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
    private static Material DimPanelFill(Transform panel)
    {
        var mr = panel.GetComponent<MeshRenderer>();
        if (mr == null || mr.sharedMaterial == null) return null;
        // renderer.material は EditMode でエラーログを出すため、複製→sharedMaterial 差し替えで共有を守る。
        var m = new Material(mr.sharedMaterial);
        mr.sharedMaterial = m;
        MakeMaterialTransparent(m);
        Color c = BackgroundColor;
        c.a = PanelFillAlpha;
        SetBaseColor(m, c);
        // 面は発光させない(発光はゲート枠の仕事)
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
        return m;
    }

    // 判定面の位置・大きさはそのまま。見た目の筐体だけを別コンポーネントで構築する。
    private static JudgeGateFrame BuildGate(Transform guide, Transform panel)
    {
        var gate = new GameObject("JudgeGate");
        gate.transform.SetParent(guide, false);
        gate.transform.localPosition = panel.localPosition + new Vector3(0f, 0f, -0.01f);
        var frame = gate.AddComponent<JudgeGateFrame>();
        frame.Build(panel.localScale);
        return frame;
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
