using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 遷移の所有者はScreenTransition。ここでは写真・文字・線・モールス音だけを持つ。
public sealed class HardSongIntro : MonoBehaviour
{
    RectTransform stage, nameRow, levelRow;
    CanvasGroup fade, nameAlpha, levelAlpha;
    Image backdrop, titleBackdrop, shade, topBar, bottomBar;
    readonly RawImage[] photos = new RawImage[3];
    Material photoMaterial, noiseMaterial;
    RawImage noise;
    HardIntroLinesGraphic lines;
    AudioSource signal;
    float time;
    public float TimeInIntro => time;
    public bool IsSignalPlaying => signal != null && signal.isPlaying;
    public bool UsesOriginalTitle { get; private set; }
    public string Title { get; private set; }
    public int Level { get; private set; }

    public static HardSongIntro Create(Transform parent, string songId, string title, int level)
    {
        var root = new GameObject("HardSongIntro", typeof(RectTransform), typeof(CanvasGroup));
        root.transform.SetParent(parent, false); Stretch(root.GetComponent<RectTransform>());
        var view = root.AddComponent<HardSongIntro>();
        try { view.Build(songId, title, level); }
        catch { Destroy(root); throw; }
        return view;
    }
    void Build(string songId, string title, int level)
    {
        Title = title; Level = level;
        fade = GetComponent<CanvasGroup>(); fade.blocksRaycasts = true;
        backdrop = ImageAt(transform, "OpaqueBackdrop", new Color(.012f, .016f, .025f, 0)); Stretch(backdrop.rectTransform);
        var stageGo = new GameObject("Frame16x9", typeof(RectTransform), typeof(RectMask2D));
        stageGo.transform.SetParent(transform, false); stage = stageGo.GetComponent<RectTransform>(); stage.sizeDelta = new Vector2(1280, 720);
        titleBackdrop = ImageAt(stage, "TitleBackground", new Color(.035f, .05f, .075f)); Stretch(titleBackdrop.rectTransform);
        var glow = ImageAt(titleBackdrop.transform, "SoftTitleLight", new Color(.05f, .08f, .11f, .7f));
        glow.sprite = UISkinKit.SoftGlow(); glow.rectTransform.sizeDelta = new Vector2(1350, 1100);
        nameRow = Row("SongTitle", out nameAlpha); levelRow = Row("Difficulty", out levelAlpha);
        UsesOriginalTitle = string.Equals(songId, "Epilogue", StringComparison.OrdinalIgnoreCase);
        if (UsesOriginalTitle)
        {
            AddGlyph(1, nameRow, 719, 790, 540, 555, 719, 787, 1128, 573);
            AddGlyph(2, nameRow, 1278, 787, 569, 573, 719, 787, 1128, 573);
        }
        else AddText(nameRow, title, 92);
        if (UsesOriginalTitle && level == 10)
        {
            AddGlyph(3, levelRow, 1855, 780, 311, 575, 1855, 779, 1263, 580);
            AddGlyph(4, levelRow, 2148, 948, 348, 411, 1855, 779, 1263, 580);
            AddGlyph(5, levelRow, 2460, 1232, 109, 120, 1855, 779, 1263, 580);
            AddGlyph(6, levelRow, 2537, 779, 225, 576, 1855, 779, 1263, 580);
            AddGlyph(7, levelRow, 2766, 781, 352, 573, 1855, 779, 1263, 580);
        }
        else AddText(levelRow, "Lv." + level, 68);
        var photoShader = Resources.Load<Shader>("UI/HardIntro/HardIntroPhoto");
        var grainShader = Resources.Load<Shader>("UI/HardIntro/HardIntroNoise");
        if (photoShader == null || grainShader == null || !photoShader.isSupported || !grainShader.isSupported)
            throw new InvalidOperationException("HARD演出のシェーダーを読み込めません");
        photoMaterial = new Material(photoShader) { name = "Hard intro photographs", hideFlags = HideFlags.DontSave };
        noiseMaterial = new Material(grainShader) { name = "Hard intro grain", hideFlags = HideFlags.DontSave };
        for (int i = 0; i < 3; i++)
        {
            photos[i] = RawAt(stage, "Photo" + (i + 1));
            photos[i].texture = Resources.Load<Texture2D>(HardIntroTimeline.PhotoResource(i));
            if (photos[i].texture == null) throw new InvalidOperationException("HARD演出の写真がありません");
            photos[i].material = photoMaterial;
        }
        shade = ImageAt(stage, "PhotographVignette", new Color(.008f, .02f, .035f, .5f));
        shade.sprite = UISkinKit.Vignette(); Stretch(shade.rectTransform);
        noise = RawAt(stage, "SignalGrain"); Stretch(noise.rectTransform); noise.material = noiseMaterial;
        var lineGo = new GameObject("OriginalEightLines", typeof(RectTransform), typeof(HardIntroLinesGraphic));
        lineGo.transform.SetParent(stage, false); lines = lineGo.GetComponent<HardIntroLinesGraphic>();
        Stretch(lines.rectTransform); lines.raycastTarget = false;
        topBar = ImageAt(stage, "TopLetterbox", new Color(.012f, .02f, .028f));
        bottomBar = ImageAt(stage, "BottomLetterbox", new Color(.012f, .02f, .028f));
        signal = gameObject.AddComponent<AudioSource>(); signal.playOnAwake = false; signal.spatialBlend = 0; signal.loop = false;
        signal.clip = Resources.Load<AudioClip>("UI/HardIntro/MorseSignal");
        signal.volume = 1f;
        if (signal.clip == null) throw new InvalidOperationException("HARD演出のモールス音がありません");
        SetTime(0);
    }
    public void PlaySignal(double startDspTime) { if (signal != null) signal.PlayScheduled(startDspTime); }
    public void StopSignal() { if (signal != null) signal.Stop(); }
    public void Hide() { StopSignal(); if (fade != null) fade.alpha = 0; }
    public void SetTime(float elapsed)
    {
        time = Mathf.Max(0, elapsed);
        bool reduced = DisplaySettings.ReducedEffects;
        var r = ((RectTransform)transform).rect;
        float scale = Mathf.Min(r.width / 1280f, r.height / 720f);
        stage.localScale = Vector3.one * Mathf.Max(.001f, scale);
        fade.alpha = 1f - HardIntroTimeline.Smooth(HardIntroTimeline.PlayStart, HardIntroTimeline.PlayStart + .6f, time);
        backdrop.color = new Color(.012f, .016f, .025f, time >= HardIntroTimeline.PhotoStart ? 1 : reduced ? HardIntroTimeline.Smooth(1f, 4.4f, time) : 0);
        titleBackdrop.enabled = time >= HardIntroTimeline.PhotoStart;
        titleBackdrop.gameObject.SetActive(time >= HardIntroTimeline.PhotoStart);
        for (int i = 0; i < 3; i++)
        {
            float begin = HardIntroTimeline.PhotoBegin(i), end = HardIntroTimeline.PhotoEnd(i);
            bool visible = time >= begin && time < end + (i == 2 ? .68f : .48f);
            photos[i].enabled = visible;
            if (!visible) continue;
            float t = reduced ? .4f : Mathf.InverseLerp(begin, end, time);
            float zoom = i == 0 ? Mathf.Lerp(1.24f, 1.31f, t) : i == 1 ? Mathf.Lerp(1.30f, 1.22f, t) : Mathf.Lerp(1.27f, 1.18f, t);
            float x = i == 0 ? Mathf.Lerp(-.052f, .026f, t) : i == 1 ? Mathf.Lerp(.035f, -.035f, t) : Mathf.Lerp(-.055f, .012f, t);
            float y = i == 0 ? Mathf.Lerp(-.025f, .015f, t) : i == 1 ? Mathf.Lerp(.10f, .13f, t) : Mathf.Lerp(.03f, .065f, t);
            // 元のJPEGは横倒し。UVだけを90度回し、ファイルを再圧縮しない。
            float w = photos[i].texture.height, h = photos[i].texture.width;
            float cover = Mathf.Max(1280f / w, 720f / h) * zoom;
            photos[i].rectTransform.sizeDelta = new Vector2(w * cover, h * cover);
            photos[i].rectTransform.anchoredPosition = new Vector2(x * 1280, -y * 720);
            float alpha = i == 0 ? 1 : HardIntroTimeline.Smooth(begin, begin + .48f, time);
            if (i == 2 && time >= end) alpha *= 1f - HardIntroTimeline.Smooth(end, end + .68f, time);
            photos[i].color = new Color(1, 1, 1, alpha);
        }
        SetRow(nameRow, nameAlpha, 284, UsesOriginalTitle ? .39f : 1f,
            HardIntroTimeline.Smooth(HardIntroTimeline.NameStart, HardIntroTimeline.NameEnd, time), reduced);
        SetRow(levelRow, levelAlpha, 456, UsesOriginalTitle && Level == 10 ? .195f : 1f,
            HardIntroTimeline.Smooth(HardIntroTimeline.LevelStart, HardIntroTimeline.LevelEnd, time), reduced);
        shade.enabled = time >= HardIntroTimeline.PhotoStart;
        shade.color = new Color(.008f, .02f, .035f, time < HardIntroTimeline.TitleStart ? .5f : .22f);
        float amount = reduced ? 0 : HardIntroTimeline.NoiseAt(time);
        noise.enabled = amount > 0;
        noiseMaterial.SetFloat("_Strength", amount); noiseMaterial.SetFloat("_Clock", time);
        lines.SetTime(time, reduced);
        float bars = 27 * HardIntroTimeline.Smooth(0, 1.5f, time) * (1f - HardIntroTimeline.Smooth(HardIntroTimeline.PlayStart, HardIntroTimeline.Duration, time));
        topBar.rectTransform.sizeDelta = bottomBar.rectTransform.sizeDelta = new Vector2(1280, bars);
        topBar.rectTransform.anchoredPosition = new Vector2(0, 360 - bars * .5f);
        bottomBar.rectTransform.anchoredPosition = new Vector2(0, -360 + bars * .5f);
    }
    static void SetRow(RectTransform row, CanvasGroup alpha, float y, float scale, float reveal, bool reduced)
    {
        alpha.alpha = reveal;
        row.anchoredPosition = new Vector2(0, 360 - y - (reduced ? 0 : 14 * (1 - reveal)));
        row.localScale = Vector3.one * scale * (reduced ? 1 : 1 + (1 - reveal) * .035f);
    }
    RectTransform Row(string label, out CanvasGroup alpha)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(CanvasGroup)); go.transform.SetParent(stage, false);
        alpha = go.GetComponent<CanvasGroup>(); alpha.blocksRaycasts = false; return go.GetComponent<RectTransform>();
    }
    static void AddGlyph(int index, RectTransform parent, float x, float y, float w, float h, float left, float top, float rowW, float rowH)
    {
        var image = RawAt(parent, "Glyph" + index);
        image.texture = Resources.Load<Texture2D>("UI/HardIntro/glyph-" + index.ToString("D2"));
        if (image.texture == null) throw new InvalidOperationException("Dotの文字素材がありません: " + index);
        image.rectTransform.pivot = new Vector2(0, 1); image.rectTransform.sizeDelta = new Vector2(w, h);
        image.rectTransform.anchoredPosition = new Vector2(x - left - rowW * .5f, rowH * .5f - (y - top));
    }
    static void AddText(RectTransform parent, string value, float size)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(parent, false);
        var text = go.GetComponent<TextMeshProUGUI>(); text.font = UISkinKit.FontAsset("Oxanium-Bold"); text.text = value;
        text.fontSize = size; text.alignment = TextAlignmentOptions.Center; text.color = new Color(.85f, .9f, .93f);
        text.raycastTarget = false; text.enableAutoSizing = true; text.fontSizeMin = 28; text.fontSizeMax = size;
        text.rectTransform.sizeDelta = new Vector2(1080, 180);
    }
    static RawImage RawAt(Transform parent, string label)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(RawImage)); go.transform.SetParent(parent, false);
        var image = go.GetComponent<RawImage>(); image.raycastTarget = false; return image;
    }
    static Image ImageAt(Transform parent, string label, Color color)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
    }
    static void Stretch(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }
    void OnDisable() { StopSignal(); }
    void OnDestroy()
    {
        StopSignal();
        if (photoMaterial != null) Destroy(photoMaterial);
        if (noiseMaterial != null) Destroy(noiseMaterial);
    }
}
