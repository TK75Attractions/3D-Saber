using UnityEngine;
using UnityEngine.UI;

// リザルト画面下部の「Tron 風パース床グリッド」を描く Graphic(画像アセット不使用)。
// CSS の perspective(700px) rotateX(62deg) グリッド + 上端フェードマスクの視覚的近似。
//   - 横線: 下ほど間隔が広く、上(地平線側)ほど圧縮される
//   - 縦線: 下端から消失点方向(中央上)へ収束する
//   - 上端 55% 区間でフェードアウト(mask-image 相当)
public class ResultFloorGridGraphic : MaskableGraphic
{
    public int horizontalLines = 10;
    public float verticalSpacing = 150f;   // 下端での縦線間隔(px)
    public float lineWidth = 2f;
    [Range(0f, 1f)] public float convergence = 0.78f; // 上端で中央へ寄る率
    public float horizonPower = 2.2f;                  // 横線の遠近圧縮の強さ
    public Color verticalColor = new Color(69f / 255f, 1f, 247f / 255f, 0.08f);
    public Color horizontalColor = new Color(69f / 255f, 1f, 247f / 255f, 0.06f);

    // 上端フェード(mask-image: transparent → black 55%)。u=0(下端)→1(上端)。純関数。
    public static float FadeAlpha(float u)
    {
        return Mathf.Clamp01((1f - Mathf.Clamp01(u)) / 0.55f);
    }

    // i 本目(0..n)の横線の正規化高さ u。下ほど疎、上ほど密になる遠近圧縮。純関数。
    public static float HorizontalLineU(int i, int n, float power)
    {
        if (n <= 0) return 0f;
        float t = Mathf.Clamp01((float)i / n);
        return 1f - Mathf.Pow(1f - t, Mathf.Max(1f, power));
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();
        float w = r.width;
        float h = r.height;
        if (w <= 0f || h <= 0f) return;

        // 横線
        for (int i = 0; i <= horizontalLines; i++)
        {
            float u = HorizontalLineU(i, horizontalLines, horizonPower);
            float y = r.yMin + u * h;
            float a = FadeAlpha(u);
            if (a <= 0.001f) continue;
            Color c = horizontalColor;
            c.a *= a;
            AddQuad(vh,
                new Vector2(r.xMin, y), new Vector2(r.xMax, y),
                new Vector2(r.xMin, y + lineWidth), new Vector2(r.xMax, y + lineWidth),
                c, c);
        }

        // 縦線(中央から左右対称、上端で消失点方向へ収束)
        int count = Mathf.FloorToInt(w * 0.5f / Mathf.Max(1f, verticalSpacing));
        for (int k = -count; k <= count; k++)
        {
            float xBottom = r.center.x + k * verticalSpacing;
            float xTop = r.center.x + (xBottom - r.center.x) * (1f - convergence);
            Color cBottom = verticalColor;
            Color cTop = verticalColor;
            cTop.a *= FadeAlpha(1f);
            AddQuad(vh,
                new Vector2(xBottom, r.yMin), new Vector2(xBottom + lineWidth, r.yMin),
                new Vector2(xTop, r.yMax), new Vector2(xTop + lineWidth, r.yMax),
                cBottom, cTop);
        }
    }

    // 下辺(b0-b1)→上辺(t0-t1)の台形クアッドを追加。上下で色(フェード)を変えられる。
    private static void AddQuad(VertexHelper vh, Vector2 b0, Vector2 b1, Vector2 t0, Vector2 t1,
        Color bottomColor, Color topColor)
    {
        UIVertex v = UIVertex.simpleVert;
        int start = vh.currentVertCount;
        v.position = b0; v.color = bottomColor; vh.AddVert(v);
        v.position = b1; v.color = bottomColor; vh.AddVert(v);
        v.position = t1; v.color = topColor; vh.AddVert(v);
        v.position = t0; v.color = topColor; vh.AddVert(v);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
