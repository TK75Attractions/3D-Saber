using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 曲終了専用の獲得演出。GamePlayManager の終了コルーチンから駆動する。
public class ComboBonusPresentation : MonoBehaviour
{
    public const float LandingTime = .42f;
    public float Duration => maximum == 0 ? 1.8f : 3.3f + Tier(maximum) * .15f;
    public static int Tier(int combo) => combo >= 300 ? 4 : combo >= 100 ? 3 : combo >= 30 ? 2 : combo >= 10 ? 1 : 0;
    public static Color Accent(int combo)
    {
        switch (Tier(combo))
        {
            case 4: return new Color(1f, .47f, .91f);
            case 3: return new Color(1f, .76f, .16f);
            case 2: return new Color(1f, .90f, .42f);
            case 1: return new Color(.25f, .92f, 1f);
            default: return new Color(.76f, .85f, 1f);
        }
    }
    public static VertexGradient Gradient(int combo)
    {
        if (Tier(combo) == 4)
            return new VertexGradient(new Color(1f, .94f, .38f), new Color(.35f, 1f, .96f),
                new Color(.40f, .38f, 1f), new Color(1f, .30f, .74f));
        return new VertexGradient(Color.white, Color.white, Accent(combo), Accent(combo));
    }

    int maximum, bonus;
    RectTransform lettering;
    CanvasGroup letteringAlpha;
    ComboBonusBurstGraphic burst;
    Image glow, scrim;
    AudioSource audioSource;
    AudioClip impact;
    bool landed;

    public static ComboBonusPresentation Create(int maxCombo)
    {
        var root = new GameObject("ComboBonusPresentation", typeof(RectTransform));
        var presentation = root.AddComponent<ComboBonusPresentation>();
        presentation.Build(maxCombo);
        return presentation;
    }

    void Build(int maxCombo)
    {
        maximum = Mathf.Max(0, maxCombo);
        bonus = maximum * ScoreManager.ComboBonusPerNote;
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 800;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;

        scrim = Image("Dim", transform, new Vector2(3000, 2200), null, new Color(.012f, .018f, .055f, .86f));
        glow = Image("Halo", transform, new Vector2(1700, 1100), UISkinKit.SoftGlow(), Accent(maximum));
        var rays = new GameObject("MedalBurst", typeof(RectTransform), typeof(CanvasRenderer));
        rays.transform.SetParent(transform, false);
        rays.GetComponent<RectTransform>().sizeDelta = new Vector2(1920, 1080);
        burst = rays.AddComponent<ComboBonusBurstGraphic>();
        burst.raycastTarget = false;

        var group = new GameObject("Lettering", typeof(RectTransform), typeof(CanvasGroup));
        group.transform.SetParent(transform, false);
        lettering = group.GetComponent<RectTransform>();
        lettering.sizeDelta = new Vector2(1500, 750);
        letteringAlpha = group.GetComponent<CanvasGroup>();
        letteringAlpha.blocksRaycasts = false;
        letteringAlpha.interactable = false;

        float level = Tier(maximum);
        PrizeText("Headline", "COMBO BONUS!", 88 + level * 9, 160, new Vector2(1500, 170));
        var font = UISkinKit.FontAsset("Oxanium-Bold");
        var formula = UISkinKit.MakeTMP(lettering, "Formula", $"100 × {maximum:N0}", 65 + level * 3,
            Color.white, TextAlignmentOptions.Center, new Vector2(0, 16), new Vector2(1400, 110), FontStyles.Normal, 2, font);
        formula.outlineColor = new Color32(10, 15, 36, 255);
        formula.outlineWidth = .2f;
        PrizeText("BonusAmount", $"+{bonus:N0}", 140 + level * 18, -140, new Vector2(1500, 220));

        if (Application.isPlaying && maximum > 0)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0;
            impact = BuildImpact();
        }
        Tick(0);
    }

    void PrizeText(string name, string value, float size, float y, Vector2 bounds)
    {
        var font = UISkinKit.LogoFontAsset();
        var shadow = UISkinKit.MakeTMP(lettering, name + "Depth", value, size,
            new Color(.07f, .025f, .13f), TextAlignmentOptions.Center,
            new Vector2(7, y - 12), bounds, FontStyles.Normal, 0, font);
        var text = UISkinKit.MakeTMP(lettering, name, value, size, Color.white,
            TextAlignmentOptions.Center, new Vector2(0, y), bounds, FontStyles.Normal, 0, font);
        // 桁数が多くても画面内に収める。色面・太い暗縁・厚みの三層。
        foreach (var layer in new[] { shadow, text })
        {
            layer.enableAutoSizing = true;
            layer.fontSizeMin = 48;
            layer.fontSizeMax = size;
            layer.outlineWidth = .22f;
            layer.outlineColor = new Color32(10, 12, 29, 255);
        }
        text.enableVertexGradient = true;
        text.colorGradient = Gradient(maximum);
    }

    static Image Image(string name, Transform parent, Vector2 size, Sprite sprite, Color color)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
        root.transform.SetParent(parent, false);
        root.GetComponent<RectTransform>().sizeDelta = size;
        var image = root.GetComponent<UnityEngine.UI.Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    public void Tick(float elapsed)
    {
        bool low = DisplaySettings.ReducedEffects;
        float approach = Mathf.Clamp01(elapsed / LandingTime);
        float after = Mathf.Max(0, elapsed - LandingTime);
        float scale = low ? 1f : Mathf.Lerp(1.85f + .06f * Tier(maximum), 1f, approach * approach);
        if (!low && elapsed >= LandingTime)
            scale = 1f + .11f * Mathf.Sin(Mathf.Clamp01(after / .24f) * Mathf.PI) * Mathf.Clamp01(1f - after / .24f);
        lettering.localScale = Vector3.one * scale;
        // 短い着地振動は文字だけに適用し、カメラを動かさない。
        float shake = !low && maximum > 0 ? (3 + Tier(maximum) * 2) * Mathf.Clamp01(1f - after / .22f) : 0;
        lettering.anchoredPosition = elapsed < LandingTime ? Vector2.zero : new Vector2(0, Mathf.Sin(after * 75) * shake);
        letteringAlpha.alpha = Mathf.Clamp01(elapsed / .14f);
        scrim.color = new Color(.012f, .018f, .055f, Mathf.Lerp(0, .87f, Mathf.Clamp01(elapsed / .18f)));
        var tint = Accent(maximum);
        tint.a = (low ? .15f : .48f) * Mathf.Clamp01(elapsed / .2f);
        glow.color = tint;
        glow.rectTransform.localScale = Vector3.one * (low ? 1 : 1 + .05f * Mathf.Sin(after * 2));
        burst.SetFrame(maximum, after, elapsed >= LandingTime, low);
        if (!landed && elapsed >= LandingTime)
        {
            landed = true;
            if (audioSource != null && impact != null) audioSource.PlayOneShot(impact, .48f);
        }
    }

    // 短い低音の着地音と金属的な倍音を合成。外部音源は使わない。
    static AudioClip BuildImpact()
    {
        const int rate = 44100;
        var samples = new float[(int)(rate * .65f)];
        float phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)rate;
            phase += 2 * Mathf.PI * Mathf.Lerp(135, 55, Mathf.Clamp01(t / .17f)) / rate;
            float bass = Mathf.Sin(phase) * Mathf.Exp(-t * 10);
            float bell = (Mathf.Sin(t * 2 * Mathf.PI * 880) + .4f * Mathf.Sin(t * 2 * Mathf.PI * 1320)) * Mathf.Exp(-t * 8);
            samples[i] = (.65f * bass + .16f * bell) * Mathf.Clamp01(t / .004f) * Mathf.Clamp01((.65f - t) / .04f);
        }
        var clip = AudioClip.Create("ComboBonusImpact", samples.Length, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    void OnDestroy()
    {
        if (impact != null) Destroy(impact);
    }
}
