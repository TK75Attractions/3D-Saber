using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 暗い空間の奥行きと、赤青の光路を持つタイトル専用背景。
// 奥から手前へ重なるレールの角度・線幅を揃え、ロゴと開始ノーツの周囲は空ける。
public class SaberTitleBackdrop : MonoBehaviour, ITitlePresentationLayer
{
    static readonly Color Red = new Color(1f, 0.08f, 0.20f, 1f);
    static readonly Color Blue = new Color(0.04f, 0.58f, 1f, 1f);
    static readonly Color Green = new Color(0.16f, 1f, 0.46f, 1f);

    sealed class LinePresentation
    {
        public Image image;
        public Vector2 from, to;
        public float thickness, delay, phase;
        public Color tint;
        public bool reverse, centered, glow;
    }

    readonly List<LinePresentation> animatedLines = new List<LinePresentation>();
    CanvasGroup atmosphere;
    CanvasGroup starGroup;
    readonly Image[] floorFlow = new Image[2];

    Texture2D gradientTexture;
    RectTransform[] stars;
    Vector2[] starBasePositions;
    float[] starSpeeds;
    float[] starPhases;

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
        SetPresentationTime(0f, 0f);
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
        atmosphere = root.gameObject.AddComponent<CanvasGroup>();
        atmosphere.blocksRaycasts = false;
        atmosphere.interactable = false;
        MakeGlow(root, "RedWash", new Vector2(-720f, 70f), new Vector2(1650f, 1450f),
            new Color(Red.r, Red.g, Red.b, 0.055f));
        MakeGlow(root, "BlueWash", new Vector2(720f, 70f), new Vector2(1650f, 1450f),
            new Color(Blue.r, Blue.g, Blue.b, 0.080f));
        MakeGlow(root, "FloorBloom", new Vector2(0f, -245f), new Vector2(920f, 280f),
            new Color(.04f, .55f, .85f, .09f));
    }

    void BuildStars()
    {
        const int count = 26;
        var root = MakeContainer("StarField");
        starGroup = root.gameObject.AddComponent<CanvasGroup>();
        starGroup.blocksRaycasts = false;
        starGroup.interactable = false;
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
            float size = Mathf.Lerp(1.1f, 3.0f, (float)random.NextDouble());
            rt.sizeDelta = Vector2.one * size;

            float x = Mathf.Lerp(-1010f, 1010f, (float)random.NextDouble());
            float y = Mathf.Lerp(-510f, 540f, (float)random.NextDouble());
            starBasePositions[i] = new Vector2(x, y);
            rt.anchoredPosition = starBasePositions[i];

            float colorPick = (float)random.NextDouble();
            Color c = colorPick < 0.10f ? Red
                : (colorPick > 0.90f ? Green : (colorPick > 0.78f ? Blue : Color.white));
            c.a = Mathf.Lerp(0.09f, 0.32f, (float)random.NextDouble());
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

        Vector2 vanishingPoint = new Vector2(0f, -72f);
        Color gridColor = new Color(.10f, .52f, .68f, .13f);

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

        // 水平線を画面の端から端まで引かず、空間の奥に短い光を置く。
        MakeNeonLine(root, "HorizonLeft", new Vector2(-530f, -72f), new Vector2(-155f, -72f),
            new Color(.08f, .64f, .9f, .38f), 1.5f, 24f, .2f);
        MakeNeonLine(root, "HorizonRight", new Vector2(155f, -72f), new Vector2(530f, -72f),
            new Color(.08f, .64f, .9f, .38f), 1.5f, 24f, .8f);
        MakeNeonLine(root, "LeftRunway", new Vector2(-100f, -88f), new Vector2(-390f, -620f),
            new Color(.08f, .64f, .9f, .32f), 1.5f, 18f, .5f);
        MakeNeonLine(root, "RightRunway", new Vector2(100f, -88f), new Vector2(390f, -620f),
            new Color(.08f, .64f, .9f, .32f), 1.5f, 18f, 1.1f);

        // 待機中の流れは開始ノーツより下に限定し、入力の予告とは区別する。
        for (int side = 0; side < 2; side++)
            floorFlow[side] = MakeLine(root, "FloorTravel", Vector2.zero, Vector2.right * 40f, 4f,
                new Color(.09f, .68f, .9f, .19f), UISkinKit.SoftGlow());
    }

    void BuildArenaRails()
    {
        var root = MakeContainer("ArenaRails");

        for (int side = -1; side <= 1; side += 2)
        {
            Color accent = side < 0 ? Red : Blue;
            // 同じ角度の輪郭を奥へ反復させ、単なる斜線ではなく構造物として見せる。
            for (int depth = 0; depth < 3; depth++)
            {
                float inset = depth * 58f;
                Color rib = accent;
                rib.a = depth == 0 ? .68f : .17f - depth * .035f;
                Vector2 a = new Vector2(side * (1040f - inset), -590f);
                Vector2 b = new Vector2(side * (710f - inset), -220f + depth * 28f);
                Vector2 c = new Vector2(side * (570f - inset), 280f - depth * 30f);
                Vector2 d = new Vector2(side * (390f - inset), 475f - depth * 30f);
                MakeNeonLine(root, "LowerRail", a, b, rib, depth == 0 ? 2.8f : 1.2f, 24f, depth * .4f);
                MakeNeonLine(root, "MidRail", b, c, rib, depth == 0 ? 2.8f : 1.2f, 24f, depth * .4f + .4f);
                MakeNeonLine(root, "UpperRail", c, d, rib, depth == 0 ? 2.8f : 1.2f, 24f, depth * .4f + .8f);
            }

            Color edge = new Color(accent.r, accent.g, accent.b, .42f);
            MakeNeonLine(root, "Header", new Vector2(side * 990f, 425f), new Vector2(side * 700f, 425f),
                edge, 1.7f, 22f, side + 1f);
            MakeFlatLine(root, "HeaderReturn", new Vector2(side * 700f, 425f), new Vector2(side * 660f, 385f), 1.7f, edge);

            // 短い目盛りはレールの接合部だけに置き、読めない装飾文字は使わない。
            for (int mark = 0; mark < 5; mark++)
            {
                float y = -55f + mark * 14f;
                float width = mark == 2 ? 35f : 18f;
                MakeFlatLine(root, "RailNotch", new Vector2(side * 838f, y),
                    new Vector2(side * (838f - width), y), 2f,
                    new Color(accent.r, accent.g, accent.b, mark == 2 ? .58f : .23f));
            }
        }
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
        MakeLine(parent, name + "Glow", from, to, glowThickness, glowColor, UISkinKit.SoftGlow(), true, phase);

        MakeLine(parent, name + "Core", from, to, coreThickness, color, null);
    }

    Image MakeFlatLine(Transform parent, string name, Vector2 from, Vector2 to,
        float thickness, Color color)
    {
        return MakeLine(parent, name, from, to, thickness, color, null);
    }

    Image MakeLine(Transform parent, string name, Vector2 from, Vector2 to,
        float thickness, Color color, Sprite sprite, bool glow = false, float phase = 0f)
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
        if (name != "FloorTravel")
        {
            // 上部のレールから手前の床へ順番に組み立てる。元の完成形の座標は保持する。
            float depth = Mathf.InverseLerp(475f, -625f, (from.y + to.y) * .5f);
            animatedLines.Add(new LinePresentation {
                image = image, from = from, to = to, thickness = thickness, tint = color,
                delay = .10f + depth * .55f, phase = phase, glow = glow,
                reverse = name.Contains("Rail"),
                centered = name.StartsWith("Depth_") || name.StartsWith("Horizon")
            });
        }
        return image;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    public void SetPresentationTime(float age, float departure)
    {
        age = Mathf.Max(0f, age);
        departure = Mathf.Clamp01(departure);
        float exitFade = 1f - .7f * departure;
        if (atmosphere != null) atmosphere.alpha = Mathf.SmoothStep(0f, 1f, age / 1.25f) * exitFade;
        if (starGroup != null) starGroup.alpha = Mathf.SmoothStep(0f, 1f, (age - .55f) / 1f) * exitFade;

        foreach (var line in animatedLines)
        {
            if (line.image == null) continue;
            float reveal = Mathf.SmoothStep(0f, 1f, (age - line.delay) / .72f);
            Vector2 from = line.from;
            Vector2 to = line.to;
            if (line.centered)
            {
                Vector2 center = (from + to) * .5f;
                from = Vector2.Lerp(center, from, reveal);
                to = Vector2.Lerp(center, to, reveal);
            }
            else if (line.reverse) from = Vector2.Lerp(to, from, reveal);
            else to = Vector2.Lerp(from, to, reveal);
            // 開始時は左右へわずかに抜け、ノーツとロゴの中心を横断しない。
            from.x *= 1f + .07f * departure;
            to.x *= 1f + .07f * departure;
            PositionLine(line.image.rectTransform, from, to, line.thickness);
            Color tint = line.tint;
            float pulse = line.glow ? .88f + .12f * Mathf.Sin(age * .72f + line.phase) : 1f;
            tint.a *= reveal * pulse * exitFade;
            line.image.color = tint;
        }

        for (int i = 0; i < floorFlow.Length; i++)
        {
            if (floorFlow[i] == null) continue;
            float progress = Mathf.Repeat(age / 8f + i * .5f, 1f);
            float head = Mathf.Lerp(progress, 1f, departure);
            float side = i == 0 ? -1f : 1f;
            Vector2 from = new Vector2(side * 228f, -325f);
            Vector2 to = new Vector2(side * 379f, -600f);
            PositionLine(floorFlow[i].rectTransform, Vector2.Lerp(from, to, Mathf.Max(0f, head - .18f)),
                Vector2.Lerp(from, to, head), 4f);
            Color tint = new Color(.09f, .68f, .9f,
                .24f * Mathf.Sin(head * Mathf.PI) * Mathf.SmoothStep(0f, 1f, (age - .7f) / .7f) * (1f - departure));
            floorFlow[i].color = tint;
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

    static void PositionLine(RectTransform rect, Vector2 from, Vector2 to, float thickness)
    {
        Vector2 delta = to - from;
        rect.anchoredPosition = (from + to) * .5f;
        rect.sizeDelta = new Vector2(delta.magnitude, thickness);
        rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    void OnDestroy()
    {
        if (gradientTexture == null) return;
        if (Application.isPlaying) Destroy(gradientTexture);
        else DestroyImmediate(gradientTexture);
        gradientTexture = null;
    }
}
