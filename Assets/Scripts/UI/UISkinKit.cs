using UnityEngine;
using UnityEngine.UI;
using TMPro;

// 実行時生成 UI の共通部品キット。
// シーンを編集せずに「角丸カード + ネオン」の統一スタイルを作るための土台。
// - 角丸 / 枠線 / グロー / ビネットの各スプライトを手続き生成して静的キャッシュ
// - TMP テキスト・ネオンボタンのファクトリ
// 注意：日本語文字列は TMP 既定フォント(LiberationSans SDF)にグリフが無いので legacy Text を使うこと。
public static class UISkinKit
{
    const int RoundedTexSize = 64;
    const float RoundedCornerRadius = 16f;
    const float RoundedAA = 1.6f;
    const float FrameLineWidth = 3f;

    static Sprite roundedSprite;
    static Sprite roundedFrameSprite;
    static Sprite glowSprite;
    static Sprite vignetteSprite;
    static TMP_FontAsset logoFontAsset;
    static bool logoFontLoadAttempted;
    static readonly System.Collections.Generic.Dictionary<string, TMP_FontAsset> fontAssetCache =
        new System.Collections.Generic.Dictionary<string, TMP_FontAsset>();

    // ---- スプライト(手続き生成・共有キャッシュ) ----

    // 角丸の白スプライト(9スライス)。Image.type = Sliced で任意サイズに伸ばす。
    public static Sprite RoundedRect()
    {
        if (roundedSprite == null) roundedSprite = BuildRoundedSprite(filled: true);
        return roundedSprite;
    }

    // 角丸の枠線だけのスプライト(9スライス)。
    public static Sprite RoundedFrame()
    {
        if (roundedFrameSprite == null) roundedFrameSprite = BuildRoundedSprite(filled: false);
        return roundedFrameSprite;
    }

    // 中心が明るく端に向かって減衰する放射グロー。ボタンの後光・ソフトシャドウ用。
    public static Sprite SoftGlow()
    {
        if (glowSprite == null) glowSprite = BuildGlowSprite();
        return glowSprite;
    }

    // 中心が透明で四隅が暗いビネット。Image.color に黒を入れて使う。
    public static Sprite Vignette()
    {
        if (vignetteSprite == null) vignetteSprite = BuildVignetteSprite();
        return vignetteSprite;
    }

    static Sprite BuildRoundedSprite(bool filled)
    {
        int n = RoundedTexSize;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        float half = n * 0.5f;
        float bHalf = half - 2f; // 端 2px はマージン(AA がテクスチャ境界で切れないように)
        float r = RoundedCornerRadius;
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                // 角丸矩形の符号付き距離
                float qx = Mathf.Abs(x + 0.5f - half) - (bHalf - r);
                float qy = Mathf.Abs(y + 0.5f - half) - (bHalf - r);
                float ox = Mathf.Max(qx, 0f);
                float oy = Mathf.Max(qy, 0f);
                float d = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                float a;
                if (filled)
                {
                    a = Mathf.Clamp01(0.5f - d / RoundedAA);
                }
                else
                {
                    float fill = Mathf.Clamp01(0.5f - d / RoundedAA);
                    float inner = Mathf.Clamp01(0.5f - (d + FrameLineWidth) / RoundedAA);
                    a = Mathf.Clamp01(fill - inner);
                }
                px[y * n + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        float border = RoundedCornerRadius + 6f;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    static Sprite BuildGlowSprite()
    {
        int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        float half = n * 0.5f;
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f - half) / (half - 1f);
                float dy = (y + 0.5f - half) / (half - 1f);
                float r01 = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Pow(Mathf.Clamp01(1f - r01), 2.4f);
                px[y * n + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    static Sprite BuildVignetteSprite()
    {
        int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        float half = n * 0.5f;
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy); // 中心 0、四隅 ~1.41
                float t = Mathf.Clamp01((r - 0.55f) / 0.85f);
                float a = t * t * (3f - 2f * t); // smoothstep
                px[y * n + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    // ---- テキスト ----

    // ロゴ用フォント(Chakra Petch Bold Italic / OFL)。Resources の TTF からランタイム生成して共有する。
    // TTF が無い環境では null を返す(呼び出し側は TMP 既定フォント+擬似イタリックでフォールバック)。
    public static TMP_FontAsset LogoFontAsset()
    {
        if (logoFontAsset != null) return logoFontAsset;
        if (logoFontLoadAttempted) return null;
        logoFontLoadAttempted = true;
        var ttf = Resources.Load<Font>("Fonts/ChakraPetch-BoldItalic");
        if (ttf == null)
        {
            Debug.LogWarning("UISkinKit: ロゴフォント(ChakraPetch-BoldItalic)が見つかりません。既定フォントで代替します");
            return null;
        }
        logoFontAsset = TMP_FontAsset.CreateFontAsset(ttf);
        if (logoFontAsset != null) logoFontAsset.hideFlags = HideFlags.HideAndDontSave;
        return logoFontAsset;
    }

    // Resources/Fonts/<name>.ttf から TMP フォントアセットを生成して共有キャッシュする。
    // 無い場合は警告を出して null(呼び出し側は既定フォントのまま進める)。null もキャッシュして再試行しない。
    public static TMP_FontAsset FontAsset(string name)
    {
        if (fontAssetCache.TryGetValue(name, out var cached)) return cached;
        TMP_FontAsset asset = null;
        var ttf = Resources.Load<Font>("Fonts/" + name);
        if (ttf == null)
        {
            Debug.LogWarning($"UISkinKit: フォント(Fonts/{name})が見つかりません。既定フォントで代替します");
        }
        else
        {
            asset = TMP_FontAsset.CreateFontAsset(ttf);
            if (asset != null) asset.hideFlags = HideFlags.HideAndDontSave;
        }
        fontAssetCache[name] = asset;
        return asset;
    }

    // legacy Text 用の TTF フォント。無ければ組み込みフォントで代替する。
    public static Font LegacyFont(string name)
    {
        var ttf = Resources.Load<Font>("Fonts/" + name);
        if (ttf != null) return ttf;
        Debug.LogWarning($"UISkinKit: フォント(Fonts/{name})が見つかりません。既定フォントで代替します");
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    static readonly System.Collections.Generic.Dictionary<string, Sprite> spriteCache =
        new System.Collections.Generic.Dictionary<string, Sprite>();

    // Resources からスプライトを読む(共有キャッシュ)。Sprite 取込でない環境では
    // Texture2D からの実行時生成にフォールバック。見つからなければ警告して null。
    public static Sprite LoadSprite(string resourcePath)
    {
        if (spriteCache.TryGetValue(resourcePath, out var cached)) return cached;
        var sp = Resources.Load<Sprite>(resourcePath);
        if (sp == null)
        {
            var tex = Resources.Load<Texture2D>(resourcePath);
            if (tex != null)
            {
                sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                sp.hideFlags = HideFlags.HideAndDontSave;
            }
        }
        if (sp == null)
        {
            Debug.LogWarning($"UISkinKit: スプライト({resourcePath})が見つかりません");
        }
        spriteCache[resourcePath] = sp; // null もキャッシュして再試行しない
        return sp;
    }

    // ロゴフォント(Chakra Petch)は ASCII のみ。日本語等が混ざる文字列に使うと□になるため、
    // 可変文字列(曲名など)に適用してよいかの判定に使う。
    public static bool IsAsciiOnly(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        foreach (char c in s)
        {
            if (c > 127) return false;
        }
        return true;
    }

    public static TextMeshProUGUI MakeTMP(Transform parent, string name, string text, float fontSize,
        Color color, TextAlignmentOptions align, Vector2 anchoredPos, Vector2 size,
        FontStyles style = FontStyles.Bold, float characterSpacing = 0f, TMP_FontAsset font = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;
        var t = go.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.text = text;
        t.fontSize = fontSize;
        t.color = color;
        t.alignment = align;
        t.fontStyle = style;
        t.characterSpacing = characterSpacing;
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        return t;
    }

    // ---- ボタン ----

    public struct NeonButtonParts
    {
        public Button button;
        public Image fill;
        public Image frame;
        public Image glow;
        public TextMeshProUGUI label;
        public UIHoverEffect hover;
    }

    // 新規にネオンボタンを作る。anchor 系は呼び出し側で調整する(既定は中央アンカー)。
    public static NeonButtonParts MakeNeonButton(Transform parent, string name, string label,
        Vector2 anchoredPos, Vector2 size, Color accent,
        UnityEngine.Events.UnityAction onClick, float labelSize = 26f)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;
        var parts = StyleButtonObject(go.GetComponent<Button>(), label, accent, labelSize);
        if (onClick != null) parts.button.onClick.AddListener(onClick);
        return parts;
    }

    // 既存ボタンをネオンスタイルに整える。ラベルは TMP に差し替え、元の legacy Text は無効化する。
    public static NeonButtonParts RestyleButton(Button btn, Color accent, float labelSize = 26f, string labelOverride = null)
    {
        string label = labelOverride;
        var oldText = btn.GetComponentInChildren<Text>();
        if (label == null && oldText != null) label = oldText.text;
        if (oldText != null) oldText.gameObject.SetActive(false);
        return StyleButtonObject(btn, label ?? "", accent, labelSize);
    }

    static NeonButtonParts StyleButtonObject(Button btn, string label, Color accent, float labelSize)
    {
        var go = btn.gameObject;
        var parts = new NeonButtonParts { button = btn };

        // 旧 Outline(枠の代用)は廃止
        var legacyOutline = go.GetComponent<Outline>();
        if (legacyOutline != null) SafeDestroy(legacyOutline);

        // 塗り
        var fill = go.GetComponent<Image>();
        if (fill == null) fill = go.AddComponent<Image>();
        fill.sprite = RoundedRect();
        fill.type = Image.Type.Sliced;
        fill.color = DarkFill(accent);
        parts.fill = fill;

        // 後光(ホバーでフェードイン)。最背面に置く。
        var glowGo = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glowGo.transform.SetParent(go.transform, false);
        glowGo.transform.SetAsFirstSibling();
        var grt = glowGo.GetComponent<RectTransform>();
        grt.anchorMin = Vector2.zero;
        grt.anchorMax = Vector2.one;
        grt.sizeDelta = new Vector2(70f, 70f);
        var glow = glowGo.GetComponent<Image>();
        glow.sprite = SoftGlow();
        glow.color = new Color(accent.r, accent.g, accent.b, 0f);
        glow.raycastTarget = false;
        parts.glow = glow;

        // 枠線
        var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image));
        frameGo.transform.SetParent(go.transform, false);
        var frt = frameGo.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.sizeDelta = Vector2.zero;
        var frame = frameGo.GetComponent<Image>();
        frame.sprite = RoundedFrame();
        frame.type = Image.Type.Sliced;
        // ミニマル路線:枠線は主張しすぎない繊細な明るさに。強調はホバー時のグローが担う。
        frame.color = new Color(accent.r, accent.g, accent.b, 0.55f);
        frame.raycastTarget = false;
        parts.frame = frame;

        // ラベル(TMP)。ボタンラベルは ASCII 前提なのでロゴフォントで世界観を揃える。
        var labelFont = IsAsciiOnly(label) ? LogoFontAsset() : null;
        parts.label = MakeTMP(go.transform, "LabelTMP", label, labelSize,
            Color.Lerp(accent, Color.white, 0.35f), TextAlignmentOptions.Center,
            Vector2.zero, Vector2.zero, labelFont != null ? FontStyles.Normal : FontStyles.Bold, 2f, labelFont);
        var lrt = parts.label.rectTransform;
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;

        // ホバー演出。Selectable 標準のティントは使わない。
        btn.transition = Selectable.Transition.None;
        var hover = go.GetComponent<UIHoverEffect>();
        if (hover == null) hover = go.AddComponent<UIHoverEffect>();
        hover.glow = glow;
        parts.hover = hover;

        return parts;
    }

    public static Color DarkFill(Color accent)
    {
        return new Color(accent.r * 0.10f, accent.g * 0.10f, accent.b * 0.16f, 0.88f);
    }

    public static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Object.Destroy(o);
        else Object.DestroyImmediate(o);
    }
}
