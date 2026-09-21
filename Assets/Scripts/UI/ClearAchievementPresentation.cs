using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 達成文字は TMP、枠と光は UI メッシュ。GamePlayManager の終了処理からのみ駆動する。
public sealed class ClearAchievementPresentation : MonoBehaviour
{
    public const float Duration = 3.1f;
    public const float OpenDuration = .78f;
    public bool IsAllPerfect { get; private set; }
    public float FrameHalfWidth { get; private set; }

    readonly List<Material> ownedMaterials = new List<Material>();
    Texture2D faceRamp;
    CanvasGroup inkAlpha;
    RectTransform reveal, lettering;
    Vector3 fittedScale;
    ClearAchievementGraphic frame;
    Image dim;

    public static ClearAchievementPresentation Create(bool allPerfect)
    {
        var root = new GameObject("ClearAchievementPresentation", typeof(RectTransform));
        var view = root.AddComponent<ClearAchievementPresentation>();
        view.Build(allPerfect);
        return view;
    }

    void Build(bool allPerfect)
    {
        IsAllPerfect = allPerfect;
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 810;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        // 縦長・横長でも文字と左右の枠をすべて画面内に収める。
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

        var dimRoot = new GameObject("Dim", typeof(RectTransform), typeof(Image));
        dimRoot.transform.SetParent(transform, false);
        dim = dimRoot.GetComponent<Image>();
        dim.raycastTarget = false;
        dim.rectTransform.anchorMin = Vector2.zero;
        dim.rectTransform.anchorMax = Vector2.one;
        dim.rectTransform.offsetMin = dim.rectTransform.offsetMax = Vector2.zero;

        var content = Rect("Content", transform, new Vector2(1920, 1080));
        inkAlpha = content.gameObject.AddComponent<CanvasGroup>();
        inkAlpha.blocksRaycasts = false;
        inkAlpha.interactable = false;
        var graphicRoot = Rect("ExpandingFrame", content, new Vector2(1920, 1080));
        graphicRoot.gameObject.AddComponent<CanvasRenderer>();
        frame = graphicRoot.gameObject.AddComponent<ClearAchievementGraphic>();
        frame.raycastTarget = false;

        reveal = Rect("LetterReveal", content, new Vector2(1600, 330));
        reveal.gameObject.AddComponent<RectMask2D>().softness = new Vector2Int(12, 0);
        lettering = Rect("Lettering", reveal, new Vector2(1600, 330));
        string label = allPerfect ? "ALL PERFECT" : "FULL COMBO";
        Color edge = allPerfect ? new Color(.41f, .38f, .84f) : new Color(.61f, .34f, .06f);
        Color rim = allPerfect ? new Color(.85f, .96f, 1f) : new Color(1f, .95f, .69f);
        var layers = new List<TextMeshProUGUI>();
        layers.Add(Text("Depth", label, new Color(.028f, .035f, .09f), .53f, null));
        layers.Add(Text("Edge", label, edge, .43f, null));
        layers.Add(Text("Rim", label, rim, .42f, null));
        layers.Add(Text("Ink", label, new Color(.055f, .045f, .09f), .23f, null));

        // 写真素材ではなく、文字の塗り色だけを計算する小さなグラデーション。
        faceRamp = MakeFaceRamp(allPerfect);
        var face = Text("AchievementText", label, Color.white, .025f, faceRamp);
        layers.Add(face);
        face.ForceMeshUpdate();
        Bounds bounds = InkBounds(face);
        fittedScale = new Vector3(1470f / Mathf.Max(1, bounds.size.x), 182f / Mathf.Max(1, bounds.size.y), 1);
        foreach (var layer in layers) layer.rectTransform.anchoredPosition = -(Vector2)bounds.center;
        layers[0].rectTransform.anchoredPosition += new Vector2(0, -12 / fittedScale.y);
        layers[1].rectTransform.anchoredPosition += new Vector2(0, -7 / fittedScale.y);
        Tick(0);
    }

    TextMeshProUGUI Text(string layer, string label, Color tint, float outline, Texture2D ramp)
    {
        var font = UISkinKit.LogoFontAsset();
        var text = UISkinKit.MakeTMP(lettering, layer, label, 230, Color.white,
            TextAlignmentOptions.Center, Vector2.zero, new Vector2(1800, 320), FontStyles.Normal, 0, font);
        // 共有フォントの材質は変えず、この演出の材質だけを所有する。
        var material = new Material(font.material) { name = "Achievement " + layer };
        if (ramp != null)
        {
            // Resources の明示参照でビルド時にも FaceTex 対応シェーダーを保持する。
            var template = Resources.Load<Material>("ClearAchievementText");
            material.shader = template.shader;
            material.SetTexture("_FaceTex", ramp);
            text.horizontalMapping = TextureMappingOptions.Paragraph;
            text.verticalMapping = TextureMappingOptions.Line;
        }
        material.SetColor("_FaceColor", tint);
        material.SetColor("_OutlineColor", ramp != null ? new Color(1, .98f, .86f) : tint);
        material.SetFloat("_OutlineWidth", outline);
        material.SetFloat("_OutlineSoftness", 0);
        material.SetFloat("_FaceDilate", 0);
        material.EnableKeyword("OUTLINE_ON");
        text.fontSharedMaterial = material;
        text.extraPadding = true;
        text.overflowMode = TextOverflowModes.Overflow;
        ownedMaterials.Add(material);
        return text;
    }

    public void Tick(float elapsed)
    {
        elapsed = Mathf.Max(0, elapsed);
        bool low = DisplaySettings.ReducedEffects;
        float travel = Mathf.Clamp01((elapsed - .10f) / (OpenDuration - .10f));
        float opening = low ? 1 : 1 - Mathf.Pow(1 - travel, 3);
        float exit = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(Duration - .36f, Duration, elapsed));
        // 文字は横へ引き伸ばさず、左右の枠に合わせてマスクだけを開く。
        FrameHalfWidth = Mathf.Lerp(115, 904, opening) + (low ? 0 : 32 * exit);
        reveal.sizeDelta = new Vector2(Mathf.Max(0, (FrameHalfWidth - 112) * 2), 330);
        float impact = low ? 1 : 1 + .055f * Mathf.Sin(Mathf.Clamp01((elapsed - .38f) / .4f) * Mathf.PI);
        lettering.localScale = new Vector3(fittedScale.x * impact, fittedScale.y * impact, 1);
        inkAlpha.alpha = Mathf.Clamp01(elapsed / .12f) * (1 - exit);
        dim.color = new Color(.012f, .018f, .044f, Mathf.Clamp01(elapsed / .14f) * .96f);
        frame.SetFrame(allPerfect: IsAllPerfect, halfWidth: FrameHalfWidth, age: elapsed, reduced: low);
    }

    static RectTransform Rect(string name, Transform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = size;
        return rect;
    }

    // SDF の透明な余白を除いた字形で高さを揃え、輪郭を太くしても文字が小さくならないようにする。
    static Bounds InkBounds(TMP_Text text)
    {
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < text.textInfo.characterCount; i++)
        {
            var ch = text.textInfo.characterInfo[i];
            if (!ch.isVisible) continue;
            var metrics = ch.textElement.glyph.metrics;
            float left = ch.origin + metrics.horizontalBearingX * ch.scale;
            float top = ch.baseLine + metrics.horizontalBearingY * ch.scale;
            min = Vector2.Min(min, new Vector2(left, top - metrics.height * ch.scale));
            max = Vector2.Max(max, new Vector2(left + metrics.width * ch.scale, top));
        }
        return new Bounds((min + max) * .5f, max - min);
    }

    static Texture2D MakeFaceRamp(bool perfect)
    {
        const int width = 256, height = 128;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "Achievement procedural lettering colors",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = new Color[width * height];
        Color[] rainbow = { new Color(1, .96f, .67f), new Color(.56f, .96f, .87f),
            new Color(.48f, .91f, 1), new Color(.52f, .58f, 1), new Color(.89f, .64f, 1),
            new Color(1, .73f, .84f), new Color(1, .95f, .67f) };
        Color[] gold = { new Color(1, .95f, .69f), new Color(1, .87f, .44f),
            new Color(.86f, .54f, .07f), new Color(.56f, .29f, .02f), new Color(1, .84f, .36f),
            new Color(1, .96f, .65f), new Color(1, 1, .94f) };
        float[] goldStops = { 0, .22f, .47f, .505f, .52f, .72f, 1 };
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float v = y / (height - 1f);
            Color color;
            if (perfect)
            {
                float t = Mathf.Clamp01(x / (width - 1f) * .77f + (1 - v) * .28f) * (rainbow.Length - 1);
                int at = Mathf.Min((int)t, rainbow.Length - 2);
                color = Color.Lerp(rainbow[at], rainbow[at + 1], t - at);
                color = Color.Lerp(color, Color.white, v > .96f ? .5f : .10f);
            }
            else
            {
                int at = 0;
                while (at < goldStops.Length - 2 && v > goldStops[at + 1]) at++;
                color = Color.Lerp(gold[at], gold[at + 1], Mathf.InverseLerp(goldStops[at], goldStops[at + 1], v));
            }
            pixels[y * width + x] = color;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return texture;
    }

    void OnDestroy()
    {
        foreach (var material in ownedMaterials) if (material != null) Destroy(material);
        if (faceRamp != null) Destroy(faceRamp);
    }
}
