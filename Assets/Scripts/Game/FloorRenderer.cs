using UnityEngine;

// Game シーンに Tron 風の床 + 天井 + レーンガイド + 奥行きグリッドを runtime で生成する。
// 視認性向上：床の depth cue、レーン位置のプレビュー、トンネル感。
public class FloorRenderer : MonoBehaviour
{
    [Header("Floor / Ceiling extents")]
    public float floorY = -2.5f;
    public float ceilingY = 3.0f;
    public float minX = -8f;
    public float maxX = 8f;
    public float minZ = -3f;
    public float maxZ = 22f;

    [Header("Colors")]
    public Color baseColor = new Color(0.012f, 0.02f, 0.05f, 1f);
    public Color lineColor = new Color(0.27f, 1f, 0.97f, 0.7f);
    public Color brightLineColor = new Color(0.5f, 1f, 1f, 1f);

    [Header("Lane lines (vertical strips along Z)")]
    // 両端 + 中央の 3 本だけ。7 本あった旧仕様は縦線が多すぎて視覚ノイズだった。
    public float[] laneXPositions = new float[] { -3f, 0f, 3f };
    public float laneLineThickness = 0.04f;
    public float laneLineEmission = 0.35f;

    [Header("Depth lines (perpendicular strips along X)")]
    public float depthLineSpacing = 4f;
    public float depthLineThickness = 0.04f;
    public float depthLineEmission = 0.3f;
    public float judgeDepthLineEmission = 2.4f;

    [Header("Ceiling")]
    // 既定 OFF。天井グリッドはノーツ軌道の背後で常に光る視覚ノイズだった(true で復活可能)。
    public bool addCeiling = false;
    public float ceilingDimming = 0.7f;

    [Header("Floor base")]
    public bool addFloorBase = true;

    public static FloorRenderer Ensure(Transform parent = null)
    {
        var existing = Object.FindFirstObjectByType<FloorRenderer>();
        if (existing != null) return existing;
        var go = new GameObject("FloorRenderer");
        if (parent != null) go.transform.SetParent(parent, false);
        var r = go.AddComponent<FloorRenderer>();
        r.Build();
        return r;
    }

    void Build()
    {
        if (addFloorBase) BuildBase("FloorBase", floorY);
        BuildLaneLines(floorY, 1f, "FloorLane_");
        BuildDepthLines(floorY, 1f, "FloorDepth_");

        if (addCeiling)
        {
            // 天井は base 無し（背景が見えるよう grid lines のみ）
            BuildLaneLines(ceilingY, ceilingDimming, "CeilLane_");
            BuildDepthLines(ceilingY, ceilingDimming, "CeilDepth_");
        }
    }

    void BuildBase(string name, float y)
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = name;
        floor.transform.SetParent(transform, false);
        float w = maxX - minX;
        float d = maxZ - minZ;
        float cx = (maxX + minX) / 2f;
        float cz = (maxZ + minZ) / 2f;
        floor.transform.localPosition = new Vector3(cx, y - 0.05f, cz);
        floor.transform.localScale = new Vector3(w, 0.05f, d);
        StripCollider(floor);
        floor.GetComponent<MeshRenderer>().sharedMaterial = MakeOpaqueLit(baseColor);
    }

    void BuildLaneLines(float y, float brightnessFactor, string prefix)
    {
        foreach (var x in laneXPositions)
        {
            var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
            line.name = $"{prefix}x{x:F1}";
            line.transform.SetParent(transform, false);
            float d = maxZ - minZ;
            float cz = (maxZ + minZ) / 2f;
            line.transform.localPosition = new Vector3(x, y + 0.005f, cz);
            line.transform.localScale = new Vector3(laneLineThickness, 0.02f, d);
            StripCollider(line);
            bool isCenter = Mathf.Abs(x) < 0.01f;
            float emission = (isCenter ? laneLineEmission * 1.5f : laneLineEmission) * brightnessFactor;
            line.GetComponent<MeshRenderer>().sharedMaterial =
                MakeEmissiveLit(isCenter ? brightLineColor : lineColor, emission);
        }
    }

    void BuildDepthLines(float y, float brightnessFactor, string prefix)
    {
        for (float z = minZ; z <= maxZ + 0.01f; z += depthLineSpacing)
        {
            var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
            line.name = $"{prefix}z{z:F1}";
            line.transform.SetParent(transform, false);
            float w = maxX - minX;
            float cx = (maxX + minX) / 2f;
            line.transform.localPosition = new Vector3(cx, y + 0.005f, z);
            line.transform.localScale = new Vector3(w, 0.02f, depthLineThickness);
            StripCollider(line);
            bool isJudge = Mathf.Abs(z) < 0.5f;
            float emission = (isJudge ? judgeDepthLineEmission : depthLineEmission) * brightnessFactor;
            line.GetComponent<MeshRenderer>().sharedMaterial =
                MakeEmissiveLit(isJudge ? brightLineColor : lineColor, emission);
        }
    }

    static void StripCollider(GameObject go)
    {
        var c = go.GetComponent<Collider>();
        if (c != null)
        {
            if (Application.isPlaying) Destroy(c);
            else DestroyImmediate(c);
        }
    }

    static Material MakeOpaqueLit(Color c)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else m.color = c;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.3f);
        return m;
    }

    static Material MakeEmissiveLit(Color c, float emission)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else m.color = c;
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            Color e = c; e.a = 1f;
            m.SetColor("_EmissionColor", e * emission);
        }
        return m;
    }
}
