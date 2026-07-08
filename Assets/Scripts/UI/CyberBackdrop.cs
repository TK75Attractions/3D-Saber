using UnityEngine;
using UnityEngine.UI;

// ScreenSpaceOverlay Canvas の最背面に背景を生成するコンポーネント。
// スタイルは2種類:
//   Minimal(既定): 上品な暗色グラデ + 中央の極薄グロー + ビネットのみ。「ミニマル・高級」路線。
//   Cyber: 従来の盛りだくさん(床グリッド/斜め光線/スキャンライン/浮遊ドット/動く光線)。オプトイン。
// Canvas に1つだけ存在する想定(Ensure() で冪等に追加)。
public enum BackdropStyle
{
    Minimal = 0,
    Cyber = 1,
}

public class CyberBackdrop : MonoBehaviour
{
    public BackdropStyle style = BackdropStyle.Minimal;

    [Header("Gradient (Cyber)")]
    public Color topColor = new Color(0.02f, 0.03f, 0.09f, 1f);
    public Color bottomColor = new Color(0.09f, 0.04f, 0.22f, 1f);

    [Header("Gradient (Minimal)")]
    // ほぼ黒。上端は完全な黒に近く、下端にだけごくわずかな藍を残して奥行きを出す。彩度は極小。
    public Color minimalTopColor = new Color(0.012f, 0.014f, 0.026f, 1f);
    public Color minimalBottomColor = new Color(0.03f, 0.028f, 0.05f, 1f);
    [Header("Center glow (Minimal)")]
    // 画面中央やや上に置く、非常に薄く大きいラジアルグロー1枚。奥行きの主役。
    public Color minimalGlowColor = new Color(0.42f, 0.52f, 0.78f, 0.06f);
    public float minimalVignetteStrength = 0.62f;

    [Header("Scan lines")]
    public Color scanLineColor = new Color(0.30f, 1f, 1f, 0.05f);
    public int scanLineCount = 18;

    [Header("Moving glow line")]
    public Color glowLineColor = new Color(0.30f, 0.95f, 1f, 0.30f);
    public float glowLineSpeed = 0.06f;     // 1秒あたり上下方向の進行率
    public float glowLineThickness = 3f;

    [Header("Horizon grid (一点透視の床)")]
    public bool addHorizonGrid = true;
    [Range(0f, 1f)] public float horizonHeight = 0.38f; // 画面下からの地平線の高さ(正規化)
    public Color gridLineColor = new Color(0.30f, 1f, 1f, 0.07f);
    public Color horizonColor = new Color(0.30f, 1f, 1f, 0.22f);

    [Header("Beams (斜めの光)")]
    public bool addBeams = true;

    [Header("Particles (浮遊ドット)")]
    public bool addParticles = true;
    public int particleCount = 16;

    [Header("Vignette")]
    public bool addVignette = true;
    [Range(0f, 1f)] public float vignetteStrength = 0.55f;

    private Texture2D gradientTexture;
    private RectTransform glowLineRT;

    // パーティクル状態(Update でアロケーションしない)
    private RectTransform particleContainer;
    private RectTransform[] particles;
    private Vector2[] particleBasePos;
    private float[] particleSpeed;
    private float[] particlePhase;

    private RectTransform[] beams;
    private Vector2[] beamBasePos;

    public static CyberBackdrop Ensure(Canvas canvas)
    {
        return Ensure(canvas, BackdropStyle.Minimal);
    }

    public static CyberBackdrop Ensure(Canvas canvas, BackdropStyle style)
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
        b.style = style;
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
        if (style == BackdropStyle.Minimal)
        {
            BuildMinimal();
            return;
        }

        BuildGradient();
        if (addHorizonGrid) BuildHorizonGrid();
        if (addBeams) BuildBeams();
        BuildScanLines();
        if (addParticles) BuildParticles();
        if (addVignette) BuildVignette();
        BuildGlowLine();
    }

    // ミニマル背景:暗色グラデ + 中央の極薄グロー + ビネットだけ。
    // 発光もテンプレ模様も持たず、ノーツや前景 UI を最も引き立てる。
    void BuildMinimal()
    {
        BuildGradient();
        BuildCenterGlow();
        BuildVignetteWithStrength(minimalVignetteStrength);
    }

    void BuildCenterGlow()
    {
        var go = new GameObject("CenterGlow", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        // 中央やや上。画面より大きめに広げて、中心が最も明るく端で自然に消えるようにする。
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, 120f);
        rt.sizeDelta = new Vector2(2600f, 1900f);
        var img = go.GetComponent<Image>();
        img.sprite = UISkinKit.SoftGlow();
        img.color = minimalGlowColor;
        img.raycastTarget = false;
    }

    void BuildGradient()
    {
        Color top = style == BackdropStyle.Minimal ? minimalTopColor : topColor;
        Color bottom = style == BackdropStyle.Minimal ? minimalBottomColor : bottomColor;

        // 縦方向のみ32段の小さなテクスチャで十分(Bilinear 補間で滑らか)
        gradientTexture = new Texture2D(1, 32, TextureFormat.RGBA32, false);
        gradientTexture.filterMode = FilterMode.Bilinear;
        gradientTexture.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < 32; y++)
        {
            float t = y / 31f;
            Color c = Color.Lerp(bottom, top, t);
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

    // 画面下部に一点透視の床グリッド。地平線 + 放射状の縦線 + 地平線に近いほど詰まる横線。
    void BuildHorizonGrid()
    {
        var container = new GameObject("HorizonGrid", typeof(RectTransform));
        container.transform.SetParent(transform, false);
        var crt = container.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0f, 0f);
        crt.anchorMax = new Vector2(1f, horizonHeight);
        crt.offsetMin = Vector2.zero;
        crt.offsetMax = Vector2.zero;

        // 地平線
        var horizon = MakeLine(crt, "Horizon");
        var hrt = horizon.rectTransform;
        hrt.anchorMin = new Vector2(0f, 1f);
        hrt.anchorMax = new Vector2(1f, 1f);
        hrt.sizeDelta = new Vector2(0f, 2f);
        horizon.color = horizonColor;

        // 放射状の縦線(消失点 = 地平線の中央)
        const int fanCount = 11;
        for (int i = 0; i < fanCount; i++)
        {
            var line = MakeLine(crt, $"Fan_{i}");
            var rt = line.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(1.6f, 1500f);
            float t = fanCount <= 1 ? 0.5f : i / (float)(fanCount - 1);
            rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(-64f, 64f, t));
            Color c = gridLineColor;
            // 中央付近をほんの少し明るく
            c.a *= Mathf.Lerp(1.2f, 0.7f, Mathf.Abs(t - 0.5f) * 2f);
            line.color = c;
        }

        // 横線(地平線に近いほど詰まる = 奥行き圧縮)
        const int hCount = 6;
        for (int i = 1; i <= hCount; i++)
        {
            float u = i / (float)hCount;
            float yNorm = 1f - u * u; // 地平線(1)から下へ加速度的に離れる
            var line = MakeLine(crt, $"Depth_{i}");
            var rt = line.rectTransform;
            rt.anchorMin = new Vector2(0f, yNorm);
            rt.anchorMax = new Vector2(1f, yNorm);
            rt.sizeDelta = new Vector2(0f, 1.4f);
            Color c = gridLineColor;
            c.a *= Mathf.Lerp(1.1f, 0.55f, u);
            line.color = c;
        }
    }

    Image MakeLine(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        return img;
    }

    // 画面を横切る大きく薄い光のバンド。ゆっくり漂う。
    void BuildBeams()
    {
        var container = new GameObject("Beams", typeof(RectTransform));
        container.transform.SetParent(transform, false);
        StretchFull(container.GetComponent<RectTransform>());

        var defs = new (float angle, float y, Color color)[]
        {
            (18f, 180f, new Color(0.30f, 1f, 1f, 0.030f)),
            (-12f, -120f, new Color(1f, 0.30f, 0.72f, 0.028f)),
        };
        beams = new RectTransform[defs.Length];
        beamBasePos = new Vector2[defs.Length];
        for (int i = 0; i < defs.Length; i++)
        {
            var go = new GameObject($"Beam_{i}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(container.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(4200f, 320f);
            rt.anchoredPosition = new Vector2(0f, defs[i].y);
            rt.localRotation = Quaternion.Euler(0f, 0f, defs[i].angle);
            var img = go.GetComponent<Image>();
            img.sprite = UISkinKit.SoftGlow();
            img.color = defs[i].color;
            img.raycastTarget = false;
            beams[i] = rt;
            beamBasePos[i] = rt.anchoredPosition;
        }
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

    void BuildParticles()
    {
        var container = new GameObject("Particles", typeof(RectTransform));
        container.transform.SetParent(transform, false);
        StretchFull(container.GetComponent<RectTransform>());
        particleContainer = container.GetComponent<RectTransform>();

        particles = new RectTransform[particleCount];
        particleBasePos = new Vector2[particleCount];
        particleSpeed = new float[particleCount];
        particlePhase = new float[particleCount];

        for (int i = 0; i < particleCount; i++)
        {
            var go = new GameObject($"P_{i}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(container.transform, false);
            var rt = go.GetComponent<RectTransform>();
            float size = Random.Range(4f, 11f);
            rt.sizeDelta = new Vector2(size, size);

            var img = go.GetComponent<Image>();
            img.sprite = UISkinKit.SoftGlow();
            // シアン / マゼンタ / 白を混ぜる
            float pick = Random.value;
            Color c = pick < 0.5f ? UISkinPalette.Cyan : (pick < 0.8f ? UISkinPalette.Magenta : Color.white);
            c.a = Random.Range(0.10f, 0.30f);
            img.color = c;
            img.raycastTarget = false;

            particles[i] = rt;
            // 基準解像度(CanvasScaler 1920x1080)前提のローカル座標
            particleBasePos[i] = new Vector2(Random.Range(-960f, 960f), Random.Range(-540f, 540f));
            particleSpeed[i] = Random.Range(8f, 26f);
            particlePhase[i] = Random.Range(0f, Mathf.PI * 2f);
            rt.anchoredPosition = particleBasePos[i];
        }
    }

    void BuildVignette()
    {
        BuildVignetteWithStrength(vignetteStrength);
    }

    void BuildVignetteWithStrength(float strength)
    {
        var go = new GameObject("Vignette", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        StretchFull(go.GetComponent<RectTransform>());
        var img = go.GetComponent<Image>();
        img.sprite = UISkinKit.Vignette();
        img.color = new Color(0f, 0f, 0f, strength);
        img.raycastTarget = false;
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
        float time = Time.unscaledTime;

        if (glowLineRT != null)
        {
            float t = Mathf.PingPong(time * glowLineSpeed, 1f);
            glowLineRT.anchorMin = new Vector2(0, t);
            glowLineRT.anchorMax = new Vector2(1, t);
        }

        if (particles != null)
        {
            float halfH = particleContainer != null && particleContainer.rect.height > 1f
                ? particleContainer.rect.height * 0.5f : 540f;
            for (int i = 0; i < particles.Length; i++)
            {
                var rt = particles[i];
                if (rt == null) continue;
                Vector2 p = particleBasePos[i];
                p.y += time * particleSpeed[i] % (halfH * 2f + 40f);
                // 上端を越えたら下へラップ
                p.y = Mathf.Repeat(p.y + halfH + 20f, halfH * 2f + 40f) - halfH - 20f;
                p.x += Mathf.Sin(time * 0.35f + particlePhase[i]) * 18f;
                rt.anchoredPosition = p;
            }
        }

        if (beams != null)
        {
            for (int i = 0; i < beams.Length; i++)
            {
                if (beams[i] == null) continue;
                float sway = Mathf.Sin(time * 0.07f + i * 2.1f) * 220f;
                beams[i].anchoredPosition = beamBasePos[i] + new Vector2(sway, 0f);
            }
        }
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
