using UnityEngine;
using UnityEngine.UI;

// リザルト画面の六角形ランクバッジ用 Graphic(画像アセット不使用)。
// CSS の clip-path: polygon(50% 0, 100% 25%, 100% 75%, 50% 100%, 0 75%, 0 25%) 相当の
// 縦長六角形を、頂点カラーによる縦グラデーション(160deg 近似)付きで描画する。
// flatFill=true にすると topColor の単色塗り(内側六角形用)。
public class ResultHexBadgeGraphic : MaskableGraphic
{
    public Color topColor = Color.white;                              // グラデ上端(ランク色)
    public Color midColor = new Color(0.063f, 0.078f, 0.227f);        // #10143A
    public Color bottomColor = new Color(0.102f, 0.051f, 0.239f);     // #1A0D3D
    [Range(0f, 1f)] public float midStop = 0.55f;
    public bool flatFill = false;

    // 中心原点の rect(w×h)に内接する六角形の頂点(上から時計回り)。純関数。
    public static Vector2[] HexPoints(float w, float h)
    {
        float hw = w * 0.5f;
        float hh = h * 0.5f;
        return new[]
        {
            new Vector2(0f, hh),          // 50% 0
            new Vector2(hw, hh * 0.5f),   // 100% 25%
            new Vector2(hw, -hh * 0.5f),  // 100% 75%
            new Vector2(0f, -hh),         // 50% 100%
            new Vector2(-hw, -hh * 0.5f), // 0 75%
            new Vector2(-hw, hh * 0.5f),  // 0 25%
        };
    }

    // グラデ位置 t(0=上端, 1=下端) → 色。純関数的に切り出してテスト可能。
    public Color EvaluateGradient(float t)
    {
        if (flatFill) return topColor;
        t = Mathf.Clamp01(t);
        if (midStop <= 0f) return Color.Lerp(midColor, bottomColor, t);
        if (t < midStop) return Color.Lerp(topColor, midColor, t / midStop);
        if (midStop >= 1f) return bottomColor;
        return Color.Lerp(midColor, bottomColor, (t - midStop) / (1f - midStop));
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();
        Vector2 c = r.center;
        Vector2[] pts = HexPoints(r.width, r.height);

        UIVertex v = UIVertex.simpleVert;
        v.position = c;
        v.color = (EvaluateGradient(0.5f) * color);
        vh.AddVert(v);

        for (int i = 0; i < pts.Length; i++)
        {
            // 縦位置ベース + 少しだけ横方向に傾ける(CSS 160deg の斜めグラデ近似)
            float t = 0.5f - pts[i].y / Mathf.Max(1f, r.height)
                    + 0.15f * (pts[i].x / Mathf.Max(1f, r.width));
            v.position = c + pts[i];
            v.color = (EvaluateGradient(t) * color);
            vh.AddVert(v);
        }
        for (int i = 0; i < pts.Length; i++)
        {
            vh.AddTriangle(0, 1 + i, 1 + (i + 1) % pts.Length);
        }
    }
}
