using UnityEngine;
using UnityEngine.UI;

// ScreenSpaceOverlay Canvas の最背面に「サイバーパンク背景」を生成するコンポーネント。
// 縦グラデーション + 等間隔の薄いスキャンライン + ゆっくり上下する強めの光線。
// Canvas に1つだけ存在する想定（Ensure() で冪等に追加）。
public class CyberBackdrop : MonoBehaviour
{
    [Header("Gradient")]
    public Color topColor = new Color(0.03f, 0.04f, 0.10f, 1f);
    public Color bottomColor = new Color(0.10f, 0.05f, 0.24f, 1f);

    [Header("Scan lines")]
    public Color scanLineColor = new Color(0.30f, 1f, 1f, 0.08f);
    public int scanLineCount = 18;

    [Header("Moving glow line")]
    public Color glowLineColor = new Color(0.30f, 0.95f, 1f, 0.45f);
    public float glowLineSpeed = 0.08f;     // 1秒あたり上下方向の進行率
    public float glowLineThickness = 2f;

    private Texture2D gradientTexture;
    private RectTransform glowLineRT;

    public static CyberBackdrop Ensure(Canvas canvas)
    {
        if (canvas == null) return null;
        var existing = canvas.GetComponentInChildren<CyberBackdrop>();
        if (existing != null) return existing;

        var go = new GameObject("CyberBackdrop", typeof(RectTransform));
        go.transform.SetParent(canvas.transform, false);
        StretchFull(go.GetComponent<RectTransform>());
        // 既存 UI の後ろに置く
        go.transform.SetAsFirstSibling();
        var b = go.AddComponent<CyberBackdrop>();
        b.Build();
        return b;
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    void Build()
    {
        BuildGradient();
        BuildScanLines();
        BuildGlowLine();
    }

    void BuildGradient()
    {
        // 縦方向のみ32段の小さなテクスチャで十分（Bilinear 補間で滑らか）
        gradientTexture = new Texture2D(1, 32, TextureFormat.RGBA32, false);
        gradientTexture.filterMode = FilterMode.Bilinear;
        gradientTexture.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < 32; y++)
        {
            float t = y / 31f;
            Color c = Color.Lerp(bottomColor, topColor, t);
            gradientTexture.SetPixel(0, y, c);
        }
        gradientTexture.Apply();

        var bg = new GameObject("Gradient", typeof(RectTransform), typeof(RawImage));
        bg.transform.SetParent(transform, false);
        StretchFull(bg.GetComponent<RectTransform>());
        var raw = bg.GetComponent<RawImage>();
        raw.texture = gradientTexture;
        raw.color = Color.white;
        raw.raycastTarget = false;
    }

    void BuildScanLines()
    {
        var container = new GameObject("ScanLines", typeof(RectTransform));
        container.transform.SetParent(transform, false);
        StretchFull(container.GetComponent<RectTransform>());
        var crt = container.GetComponent<RectTransform>();
        for (int i = 0; i < scanLineCount; i++)
        {
            var ln = new GameObject($"Line_{i}", typeof(RectTransform), typeof(Image));
            ln.transform.SetParent(crt, false);
            var lrt = ln.GetComponent<RectTransform>();
            float yNorm = (i + 0.5f) / scanLineCount;
            lrt.anchorMin = new Vector2(0, yNorm);
            lrt.anchorMax = new Vector2(1, yNorm);
            lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(0, 1f);
            var img = ln.GetComponent<Image>();
            img.color = scanLineColor;
            img.raycastTarget = false;
        }
    }

    void BuildGlowLine()
    {
        var glow = new GameObject("GlowLine", typeof(RectTransform), typeof(Image));
        glow.transform.SetParent(transform, false);
        var rt = glow.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 0.5f);
        rt.anchorMax = new Vector2(1, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(0, glowLineThickness);
        var img = glow.GetComponent<Image>();
        img.color = glowLineColor;
        img.raycastTarget = false;
        glowLineRT = rt;
    }

    void Update()
    {
        if (glowLineRT == null) return;
        float t = Mathf.PingPong(Time.unscaledTime * glowLineSpeed, 1f);
        glowLineRT.anchorMin = new Vector2(0, t);
        glowLineRT.anchorMax = new Vector2(1, t);
    }

    void OnDestroy()
    {
        if (gradientTexture != null)
        {
            if (Application.isPlaying) Destroy(gradientTexture);
            else DestroyImmediate(gradientTexture);
            gradientTexture = null;
        }
    }
}
