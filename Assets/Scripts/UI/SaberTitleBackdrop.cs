using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Beat Saber 系の「暗い空間に浮くステージ」をタイトル専用に組み立てる背景。
// 外部画像に依存せず、星・消失点グリッド・左右のネオンレールを UI 図形だけで生成する。
public class SaberTitleBackdrop : MonoBehaviour
{
    static readonly Color Red = new Color(1f, 0.08f, 0.20f, 1f);
    static readonly Color Blue = new Color(0.04f, 0.58f, 1f, 1f);
    static readonly Color Green = new Color(0.16f, 1f, 0.46f, 1f);

    readonly List<Image> pulsingImages = new List<Image>();
    readonly List<float> pulsingBaseAlpha = new List<float>();
    readonly List<float> pulsingPhase = new List<float>();

    Texture2D gradientTexture;
    RectTransform[] stars;
    Vector2[] starBasePositions;
    float[] starSpeeds;
    float[] starPhases;
    float age;

    public static SaberTitleBackdrop Ensure(Canvas canvas)
    {
        if (canvas == null) return null;

        var existing = canvas.GetComponentInChildren<SaberTitleBackdrop>(true);
        if (existing != null) return existing;

        var go = new GameObject("SaberTitleBackdrop", typeof(RectTransform));
        go.transform.SetParent(canvas.transform, false);
        StretchFull(go.GetComponent<RectTransform>());
        go.transform.SetAsFirstSibling();

        var backdrop = go.AddComponent<SaberTitleBackdrop>();
        backdrop.Build();
        return backdrop;
    }

    void Build()
    {
        BuildGradient();
        BuildAmbientGlows();
        BuildStars();
        BuildPerspectiveStage();
        BuildArenaRails();
        BuildVignette();
    }

    void BuildGradient()
    {
        gradientTexture = new Texture2D(1, 64, TextureFormat.RGBA32, false)
        {
            name = "TitleSpaceGradient",
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color bottom = new Color(0.022f, 0.004f, 0.018f, 1f);
        Color middle = new Color(0.009f, 0.014f, 0.042f, 1f);
        Color top = new Color(0.002f, 0.003f, 0.012f, 1f);
        for (int y = 0; y < gradientTexture.height; y++)
        {
            float t = y / (gradientTexture.height - 1f);
            Color c = t < 0.45f
                ? Color.Lerp(bottom, middle, t / 0.45f)
                : Color.Lerp(middle, top, (t - 0.45f) / 0.55f);
            gradientTexture.SetPixel(0, y, c);
        }
        gradientTexture.Apply();

        var go = new GameObject("Gradient", typeof(RectTransform), typeof(RawImage));
        go.transform.SetParent(transform, false);
        StretchFull(go.GetComponent<RectTransform>());
        var image = go.GetComponent<RawImage>();
        image.texture = gradientTexture;
        image.raycastTarget = false;
    }

    void BuildAmbientGlows()
    {
        var root = MakeContainer("AmbientGlows");
        MakeGlow(root, "RedWash", new Vector2(-720f, 70f), new Vector2(1650f, 1450f),
            new Color(Red.r, Red.g, Red.b, 0.075f));
        MakeGlow(root, "BlueWash", new Vector2(720f, 70f), new Vector2(1650f, 1450f),
            new Color(Blue.r, Blue.g, Blue.b, 0.095f));
        MakeGlow(root, "CenterBloom", new Vector2(0f, 185f), new Vector2(1180f, 860f),
            new Color(Green.r, Green.g, Green.b, 0.050f));
    }

    void BuildStars()
    {
        const int count = 52;
        var root = MakeContainer("StarField");
        var random = new System.Random(3187);
        stars = new RectTransform[count];
        starBasePositions = new Vector2[count];
        starSpeeds = new float[count];
        starPhases = new float[count];

        for (int i = 0; i < count; i++)
        {
            var go = new GameObject($"Star_{i:00}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root, false);
            var rt = go.GetComponent<RectTransform>();
            float size = Mathf.Lerp(1.5f, 5.5f, (float)random.NextDouble());
            rt.sizeDelta = Vector2.one * size;

            float x = Mathf.Lerp(-1010f, 1010f, (float)random.NextDouble());
            float y = Mathf.Lerp(-510f, 540f, (float)random.NextDouble());
            starBasePositions[i] = new Vector2(x, y);
            rt.anchoredPosition = starBasePositions[i];

            float colorPick = (float)random.NextDouble();
            Color c = colorPick < 0.10f ? Red
                : (colorPick > 0.90f ? Green : (colorPick > 0.78f ? Blue : Color.white));
            c.a = Mathf.Lerp(0.12f, 0.62f, (float)random.NextDouble());
            var image = go.GetComponent<Image>();
            image.sprite = UISkinKit.SoftGlow();
            image.color = c;
            image.raycastTarget = false;

            stars[i] = rt;
            starSpeeds[i] = Mathf.Lerp(0.6f, 2.8f, (float)random.NextDouble());
            starPhases[i] = Mathf.Lerp(0f, Mathf.PI * 2f, (float)random.NextDouble());
        }
    }

    void BuildPerspectiveStage()
    {
        var root = MakeContainer("PerspectiveStage");
        var grid = new GameObject("Grid", typeof(RectTransform));
        grid.transform.SetParent(root, false);
        StretchFull(grid.GetComponent<RectTransform>());

        Vector2 vanishingPoint = new Vector2(0f, -62f);
        Color gridColor = new Color(0.22f, 0.72f, 0.66f, 0.090f);

        for (int i = -6; i <= 6; i++)
        {
            float x = i * 218f;
            MakeFlatLine(grid.transform, $"Ray_{i + 6:00}", vanishingPoint,
                new Vector2(x, -625f), 1.5f, gridColor);
        }

        for (int i = 1; i <= 9; i++)
        {
            float t = i / 9f;
            float depth = t * t;
            float y = Mathf.Lerp(vanishingPoint.y - 5f, -620f, depth);
            float width = Mathf.Lerp(180f, 2600f, depth);
            Color c = gridColor;
            c.a *= Mathf.Lerp(0.35f, 1f, t);
            MakeFlatLine(grid.transform, $"Depth_{i:00}", new Vector2(-width * 0.5f, y),
                new Vector2(width * 0.5f, y), 1.35f, c);
        }

        MakeNeonLine(root, "Horizon", new Vector2(-880f, -62f), new Vector2(880f, -62f),
            new Color(Green.r, Green.g, Green.b, 0.38f), 2f, 54f, 0.2f);
    }

    void BuildArenaRails()
    {
        var root = MakeContainer("ArenaRails");

        MakeNeonLine(root, "RedLowerRail", new Vector2(-1110f, -610f), new Vector2(-520f, 220f),
            new Color(Red.r, Red.g, Red.b, 0.68f), 3.2f, 76f, 0f);
        MakeNeonLine(root, "RedUpperRail", new Vector2(-520f, 220f), new Vector2(-245f, 525f),
            new Color(Red.r, Red.g, Red.b, 0.52f), 2.4f, 58f, 0.5f);
        MakeNeonLine(root, "BlueLowerRail", new Vector2(1110f, -610f), new Vector2(520f, 220f),
            new Color(Blue.r, Blue.g, Blue.b, 0.74f), 3.2f, 76f, 1.2f);
        MakeNeonLine(root, "BlueUpperRail", new Vector2(520f, 220f), new Vector2(245f, 525f),
            new Color(Blue.r, Blue.g, Blue.b, 0.56f), 2.4f, 58f, 1.8f);

        // ロゴの背後を横切る細いライト。中央は空けて文字の可読性を保つ。
        MakeNeonLine(root, "RedHeader", new Vector2(-960f, 410f), new Vector2(-390f, 410f),
            new Color(Red.r, Red.g, Red.b, 0.42f), 2f, 38f, 0.7f);
        MakeNeonLine(root, "BlueHeader", new Vector2(390f, 410f), new Vector2(960f, 410f),
            new Color(Blue.r, Blue.g, Blue.b, 0.46f), 2f, 38f, 1.4f);

        Color frame = new Color(0.52f, 0.68f, 1f, 0.11f);
        MakeFlatLine(root, "LeftFrame", new Vector2(-735f, -445f), new Vector2(-735f, 330f), 1.5f, frame);
        MakeFlatLine(root, "RightFrame", new Vector2(735f, -445f), new Vector2(735f, 330f), 1.5f, frame);
    }

    void BuildVignette()
    {
        var go = new GameObject("Vignette", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        StretchFull(go.GetComponent<RectTransform>());
        var image = go.GetComponent<Image>();
        image.sprite = UISkinKit.Vignette();
        image.color = new Color(0f, 0f, 0f, 0.74f);
        image.raycastTarget = false;
    }

    Transform MakeContainer(string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(transform, false);
        StretchFull(go.GetComponent<RectTransform>());
        return go.transform;
    }

    static void MakeGlow(Transform parent, string name, Vector2 position, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = position;
        var image = go.GetComponent<Image>();
        image.sprite = UISkinKit.SoftGlow();
        image.color = color;
        image.raycastTarget = false;
    }

    void MakeNeonLine(Transform parent, string name, Vector2 from, Vector2 to, Color color,
        float coreThickness, float glowThickness, float phase)
    {
        Color glowColor = color;
        glowColor.a *= 0.30f;
        var glow = MakeLine(parent, name + "Glow", from, to, glowThickness, glowColor, UISkinKit.SoftGlow());
        pulsingImages.Add(glow);
        pulsingBaseAlpha.Add(glowColor.a);
        pulsingPhase.Add(phase);

        MakeLine(parent, name + "Core", from, to, coreThickness, color, null);
    }

    static Image MakeFlatLine(Transform parent, string name, Vector2 from, Vector2 to,
        float thickness, Color color)
    {
        return MakeLine(parent, name, from, to, thickness, color, null);
    }

    static Image MakeLine(Transform parent, string name, Vector2 from, Vector2 to,
        float thickness, Color color, Sprite sprite)
    {
        Vector2 delta = to - from;
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(delta.magnitude, thickness);
        rt.anchoredPosition = (from + to) * 0.5f;
        rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    void Update()
    {
        age += Time.unscaledDeltaTime;

        for (int i = 0; i < pulsingImages.Count; i++)
        {
            if (pulsingImages[i] == null) continue;
            Color c = pulsingImages[i].color;
            float pulse = 0.78f + 0.22f * Mathf.Sin(age * 1.15f + pulsingPhase[i]);
            c.a = pulsingBaseAlpha[i] * pulse;
            pulsingImages[i].color = c;
        }

        if (stars == null) return;
        for (int i = 0; i < stars.Length; i++)
        {
            if (stars[i] == null) continue;
            Vector2 p = starBasePositions[i];
            p.y = Mathf.Repeat(p.y + age * starSpeeds[i] + 540f, 1080f) - 540f;
            p.x += Mathf.Sin(age * 0.10f + starPhases[i]) * 4f;
            stars[i].anchoredPosition = p;
        }
    }

    void OnDestroy()
    {
        if (gradientTexture == null) return;
        if (Application.isPlaying) Destroy(gradientTexture);
        else DestroyImmediate(gradientTexture);
        gradientTexture = null;
    }
}
