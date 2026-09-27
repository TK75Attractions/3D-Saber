using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 1920×1080のディスク選曲。選択・開始・音源の所有権はControllerに残す。
public class SongSelectSkin : MonoBehaviour
{
    sealed class Disc
    {
        public int index, offset;
        public RectTransform root;
        public Button button;
        public SongSelectDiscTarget target;
        public SongSelectDiscGraphic art, halo, rim, progress;
        public TextMeshProUGUI fallback;
        public Vector2 from, to;
        public float fromScale, toScale;
    }
    SongSelectController ctl;
    readonly List<Disc> discs = new List<Disc>();
    readonly SongSelectCountdown countdown = new SongSelectCountdown();
    readonly SongSelectDiscGraphic[] difficultyFaces = new SongSelectDiscGraphic[3];
    readonly SongSelectDiscGraphic[] difficultyRings = new SongSelectDiscGraphic[3];
    readonly TextMeshProUGUI[] difficultyNumbers = new TextMeshProUGUI[3];
    readonly TextMeshProUGUI[] difficultyLabels = new TextMeshProUGUI[3];
    readonly SongSelectRubyText[] counts = new SongSelectRubyText[4];
    TextMeshProUGUI timer, achievementDifficulty;
    SongSelectRubyText songTitle;
    SongSelectCorridor corridor;
    RectTransform layout;
    float animation = 1;
    bool built;
    int displayedSecond = -1;
    double lastTick;
    public bool IsReady => built;
    public double RemainingSeconds => countdown.Remaining;
    public SongSelectCorridor Background => corridor;
    public static readonly Color Cyan = new Color(.27f, 1, .97f);
    public static readonly Color Ink = new Color(.012f, .024f, .047f);
    public static readonly Color Gold = new Color(1, .847f, .302f);
    static readonly Color Panel = new Color(.059f, .102f, .173f, .94f);
    static readonly Color White = new Color(.918f, .965f, 1);

    IEnumerator Start()
    {
        yield return null;
        ctl = Object.FindFirstObjectByType<SongSelectController>();
        if (ctl == null) yield break;
        var canvas = ctl.GetComponent<Canvas>() ?? ctl.GetComponentInParent<Canvas>();
        if (canvas == null) yield break;
        canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = Camera.main; canvas.planeDistance = 20;
        var scaler = canvas.GetComponent<CanvasScaler>() ?? canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        foreach (Transform child in canvas.transform)
        {
            // PreviewAudioは元のCanvasの子。表示の交換で音源まで止めない。
            if (ctl.previewSource != null && (child == ctl.previewSource.transform || ctl.previewSource.transform.IsChildOf(child))) continue;
            child.gameObject.SetActive(false);
        }
        layout = SongSelectVisuals.Rect(canvas.transform, "DiscSelectLayout", Vector2.zero, new Vector2(1920, 1080));
        ctl.suppressDefaultDifficultyTint = true;
        corridor = SongSelectCorridor.Build(layout, ctl);
        BuildHeader(); BuildDiscs(); BuildTitle(); BuildDifficulties(); BuildAchievements(); BuildActions();
        ctl.OnSelectionChanged += SelectionChanged; ctl.OnDifficultyChanged += DifficultyChanged;
        SelectionChanged(ctl.SelectedIndex); animation = 1; AnimateDiscs();
        SongSelectAimPointer.Build(ctl, canvas, null);
        countdown.Reset(); lastTick = Time.realtimeSinceStartupAsDouble; built = true;
    }

    public static Color DifficultyColor(int index) => SongSelectVisuals.Difficulty[Mathf.Clamp(index, 0, 2)];
    public static string DifficultyDisplayName(int index, string sourceName) => index == 2 ? "MASTER" : string.IsNullOrEmpty(sourceName) ? $"CHART {index + 1}" : sourceName.ToUpperInvariant();
    public static int MeterSegmentsForLevel(int level) => Mathf.Clamp(level, 0, 10);
    public static string FormatDifficultyLevel(int level) => level > 0 ? $"LEVEL {Mathf.Clamp(level, 0, 99):00}" : "LEVEL --";
    public static string FormatDifficultyCardLevel(int level) => level > 0 ? $"LV {Mathf.Clamp(level, 0, 99):00}" : "LV --";
    public static void ApplyNeon(Button button, Color accent, float fillAlpha) => UISkinKit.RestyleButton(button, accent);
    public static void EnterCalibration()
    {
        if (ScreenTransition.Load("Game", ScreenTransition.Style.Calibration)) GameSession.IsCalibrationMode = true;
    }
    static Vector2 Position(float x, float y) => new Vector2(x - 960, 540 - y);
    RectTransform Rect(string name, float x, float y, float w, float h) => SongSelectVisuals.Rect(layout, name, Position(x, y), new Vector2(w, h));
    public static SongSelectDiscGraphic Graphic(Transform parent, string name, Vector2 position, Vector2 size, SongSelectDiscGraphic.Shape form, Color color)
    {
        var g = SongSelectVisuals.Rect(parent, name, position, size).gameObject.AddComponent<SongSelectDiscGraphic>();
        g.Form = form; g.color = color; g.raycastTarget = false; return g;
    }
    static TextMeshProUGUI Label(Transform parent, string name, string text, float size, Vector2 position, Vector2 dimensions, TMP_FontAsset font = null, Color? color = null)
    {
        var t = UISkinKit.MakeTMP(parent, name, text, size, color ?? White, TextAlignmentOptions.Center,
            position, dimensions, FontStyles.Normal, 0, font ?? UISkinKit.FontAsset("Oxanium-Bold"));
        t.raycastTarget = false; t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Overflow;
        if (font == UISkinKit.JapaneseFallbackFontAsset())
        {
            t.fontStyle = FontStyles.Bold;
            t.fontMaterial.SetFloat(ShaderUtilities.ID_FaceDilate, .1f);
        }
        return t;
    }
    static SongSelectRubyText Ruby(TextMeshProUGUI text, string markup)
    {
        var ruby = text.gameObject.AddComponent<SongSelectRubyText>(); ruby.Set(markup); return ruby;
    }
    SongSelectDiscGraphic PanelAt(string name, float x, float y, float w, float h, Color edge, float border = 3, float radius = 24)
    {
        var g = Graphic(layout, name, Position(x, y), new Vector2(w, h), SongSelectDiscGraphic.Shape.Panel, Panel);
        g.Edge = edge; g.Width = border; g.Radius = radius; g.Bottom = new Color(.025f, .045f, .085f, .94f); return g;
    }
    static Button ButtonOn(RectTransform rect, Graphic graphic, float seconds, bool circle)
    {
        var b = rect.gameObject.AddComponent<Button>(); b.targetGraphic = graphic; graphic.raycastTarget = true;
        b.transition = Selectable.Transition.None; SongSelectDiscTarget.Attach(b, seconds, circle); return b;
    }

    void BuildHeader()
    {
        var header = Graphic(layout, "InstructionTab", Position(490, 92), new Vector2(860, 112), SongSelectDiscGraphic.Shape.Tab, Panel);
        header.Width = 6; header.Edge = new Color(1, .18f, .30f); header.EdgeRight = new Color(.25f, .43f, 1); header.Bottom = Ink;
        var title = Label(header.transform, "Instruction", "", 48, new Vector2(0, -7), new Vector2(825, 80), UISkinKit.JapaneseFallbackFontAsset());
        title.fontStyle = FontStyles.Bold;
        Ruby(title, "<ruby=なんいど>難易度</ruby>と<ruby=がっきょく>楽曲</ruby>を<ruby=えら>選</ruby>んでね");
        var clock = Graphic(layout, "TimeTab", Position(1680, 92), new Vector2(360, 112), SongSelectDiscGraphic.Shape.Tab, Panel);
        clock.Width = 6; clock.Edge = new Color(.25f, .43f, 1); clock.EdgeRight = Cyan; clock.Bottom = Ink;
        var icon = Graphic(clock.transform, "ClockIcon", new Vector2(-89, 0), new Vector2(56, 56), SongSelectDiscGraphic.Shape.Clock, White); icon.Edge = Ink;
        timer = Label(clock.transform, "TimeRemaining", "100", 86, new Vector2(42, 0), new Vector2(180, 106), UISkinKit.LogoFontAsset());
    }
    void BuildDiscs()
    {
        for (int i = 0; i < ctl.SongCount; i++)
        {
            var d = new Disc { index = i };
            d.root = Rect("SongDisc_" + i, 960, 452, 540, 540);
            d.halo = Graphic(d.root, "Halo", Vector2.zero, new Vector2(664, 664), SongSelectDiscGraphic.Shape.Halo, new Color(Cyan.r, Cyan.g, Cyan.b, .16f)); d.halo.Width = 64;
            var face = Graphic(d.root, "RecordGrooves", Vector2.zero, new Vector2(540, 540), SongSelectDiscGraphic.Shape.Disc, Color.white);
            d.art = Graphic(d.root, "Artwork", Vector2.zero, new Vector2(454, 454), SongSelectDiscGraphic.Shape.Cover, Color.white);
            d.art.Artwork = CoverSprite(i);
            if (d.art.Artwork == null)
            {
                d.art.color = new Color(.07f, .10f, .20f);
                var stripes = Graphic(d.root, "FallbackRings", Vector2.zero, new Vector2(370, 370), SongSelectDiscGraphic.Shape.Ring, new Color(.3f, .65f, .8f, .6f)); stripes.Width = 7;
                Graphic(d.root, "FallbackInner", Vector2.zero, new Vector2(295, 295), SongSelectDiscGraphic.Shape.Ring, new Color(1, .2f, .4f, .65f)).Width = 3;
                d.fallback = Label(d.root, "FallbackTitle", ResultSkin.SongIdToDisplayTitle(ctl.SongIdAt(i)), 42, Vector2.zero, new Vector2(370, 130), UISkinKit.FontAsset("Oxanium-ExtraBold"));
                d.fallback.enableAutoSizing = true; d.fallback.fontSizeMin = 20; d.fallback.fontSizeMax = 42;
            }
            d.rim = Graphic(d.root, "DiscRim", Vector2.zero, new Vector2(544, 544), SongSelectDiscGraphic.Shape.Ring, Cyan); d.rim.Width = 3;
            d.progress = Graphic(d.root, "StartProgress", Vector2.zero, new Vector2(588, 588), SongSelectDiscGraphic.Shape.Ring, Gold); d.progress.Width = 12; d.progress.Progress = 0;
            d.button = ButtonOn(d.root, face, 2, true); d.target = d.root.GetComponent<SongSelectDiscTarget>();
            d.button.onClick.AddListener(() =>
            {
                if (ScreenTransition.IsBusy) return;
                if (d.index == ctl.SelectedIndex) ctl.StartGame();
                else if (Mathf.Abs(d.offset) == 1) ctl.Select(d.index);
            });
            discs.Add(d);
        }
        foreach (int side in new[] { -1, 1 })
        {
            var arrow = Graphic(layout, "DecorativeArrow", Position(side < 0 ? 52 : 1844, 500), new Vector2(60, 90), SongSelectDiscGraphic.Shape.Arrow, new Color(.38f, .81f, .83f, .35f));
            if (side > 0) arrow.rectTransform.localScale = new Vector3(-1, 1, 1);
        }
    }
    void BuildTitle()
    {
        PanelAt("TitleShadow", 960, 788, 716, 108, Ink, 5, 30);
        var panel = PanelAt("SongTitlePanel", 960, 788, 700, 92, Cyan, 5);
        var t = Label(panel.transform, "PanelSongTitle", "", 58, new Vector2(0, -2), new Vector2(660, 74), UISkinKit.FontAsset("Oxanium-ExtraBold"));
        t.enableAutoSizing = true; t.fontSizeMin = 26; t.fontSizeMax = 58; songTitle = Ruby(t, "");
    }
    void BuildDifficulties()
    {
        var tray = PanelAt("DifficultyTray", 960, 952, 720, 124, new Color(.09f, .19f, .29f), 4, 62); tray.color = new Color(.024f, .043f, .082f);
        ctl.difficultyButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var root = Rect("Difficulty" + i, 730 + 230 * i, 954, 190, 196);
            var face = Graphic(root, "Circle", new Vector2(0, 2), new Vector2(124, 124), SongSelectDiscGraphic.Shape.Cover, Ink);
            var ring = Graphic(root, "CircleEdge", new Vector2(0, 2), new Vector2(124, 124), SongSelectDiscGraphic.Shape.Ring, DifficultyColor(i)); ring.Width = 9;
            difficultyFaces[i] = face;
            difficultyRings[i] = Graphic(root, "SelectedRing", new Vector2(0, 2), new Vector2(158, 158), SongSelectDiscGraphic.Shape.Ring, White); difficultyRings[i].Width = 5;
            difficultyNumbers[i] = Label(root, "Level", "", 58, new Vector2(0, 2), new Vector2(110, 100), UISkinKit.LogoFontAsset(), DifficultyColor(i));
            difficultyLabels[i] = Label(root, "DifficultyName", DifficultyDisplayName(i, ctl.difficultyNames[i]), 19, new Vector2(0, -88), new Vector2(190, 30));
            // 資料の190×196の受付範囲。円と下の難易度名を一つの的にする。
            var hit = Graphic(root, "HitArea", Vector2.zero, new Vector2(190, 196), SongSelectDiscGraphic.Shape.Panel, Color.clear); hit.Width = 0;
            ctl.difficultyButtons[i] = ButtonOn(root, hit, 1, false);
            ctl.difficultyButtons[i].onClick.AddListener(() => ctl.SetDifficulty(index));
        }
    }
    void BuildAchievements()
    {
        var panel = PanelAt("Achievements", 295, 915, 470, 250, SongSelectVisuals.Edge, 3, 22);
        Ruby(Label(panel.transform, "AchievementHeading", "", 24, new Vector2(-163, 88), new Vector2(94, 45), UISkinKit.JapaneseFallbackFontAsset(), SongSelectVisuals.Accent), "<ruby=じっせき>実績</ruby>");
        achievementDifficulty = Label(panel.transform, "AchievementDifficulty", "EASY", 16, new Vector2(-73, 86), new Vector2(110, 28), color: SongSelectVisuals.Muted);
        string[] ranks = { "S", "S<color=#ff6e7a>+</color>", "FC", "AP" };
        for (int i = 0; i < 4; i++)
        {
            float x = i % 2 == 0 ? -192 : 32, y = i < 2 ? 29 : -43;
            var rank = Label(panel.transform, "Rank" + i, ranks[i], 46, new Vector2(x + 35, y), new Vector2(100, 65), UISkinKit.LogoFontAsset(), Gold);
            rank.alignment = TextAlignmentOptions.MidlineLeft;
            if (i == 3) { rank.color = Color.white; rank.enableVertexGradient = true; rank.colorGradient = new VertexGradient(new Color(1, .5f, .5f), new Color(.65f, .5f, 1), new Color(1, .85f, .45f), new Color(.3f, .9f, 1)); }
            var number = Label(panel.transform, "Count" + i, "", 36, new Vector2(x + 137, y - 2), new Vector2(106, 55), UISkinKit.LogoFontAsset());
            number.enableAutoSizing = true; number.fontSizeMin = 16; number.fontSizeMax = 36;
            counts[i] = Ruby(number, "0<size=20><ruby=にん>人</ruby></size>");
            var line = Graphic(panel.transform, "Rule", new Vector2(x + 94, y - 31), new Vector2(196, 2), SongSelectDiscGraphic.Shape.Panel, new Color(.38f, .81f, .83f, .2f)); line.Width = 0;
        }
    }
    void BuildActions()
    {
        var cal = PanelAt("CalibrationButton", 1630, 930, 460, 180, SongSelectVisuals.Edge, 4);
        ButtonOn(cal.rectTransform, cal, 2, false).onClick.AddListener(() => { EnterCalibration(); if (ScreenTransition.IsBusy) ctl.StopPreview(); });
        var t = Label(cal.transform, "CalibrationLabel", "", 52, new Vector2(0, -8), new Vector2(410, 106), UISkinKit.JapaneseFallbackFontAsset()); t.fontStyle = FontStyles.Bold;
        Ruby(t, "<ruby=はんてい>判定</ruby><ruby=ちょうせい>調整</ruby>");
        var back = PanelAt("BackToTitle", 1240, 88, 244, 64, SongSelectVisuals.Edge, 2, 16);
        ButtonOn(back.rectTransform, back, 2, false).onClick.AddListener(ctl.ReturnToTitle);
        Label(back.transform, "Label", "タイトルへ", 25, Vector2.zero, new Vector2(220, 52), UISkinKit.JapaneseFallbackFontAsset());
    }
    void SelectionChanged(int index)
    {
        foreach (var d in discs)
        {
            int offset = (d.index - index + discs.Count) % discs.Count;
            if (offset > discs.Count / 2) offset -= discs.Count;
            d.offset = offset; d.from = d.root.anchoredPosition; d.fromScale = d.root.localScale.x;
            float x = offset == 0 ? 960 : offset == -1 ? 320 : offset == 1 ? 1600 : offset < 0 ? -100 - 600 * Mathf.Max(0, -offset - 2) : 2020 + 600 * Mathf.Max(0, offset - 2);
            d.to = Position(x, offset == 0 ? 452 : Mathf.Abs(offset) == 1 ? 500 : 600);
            d.toScale = (offset == 0 ? 540 : Mathf.Abs(offset) == 1 ? 380 : 280) / 540f;
            if (!built) { d.from = d.to; d.fromScale = d.toScale; }
            d.target.HoldSeconds = offset == 0 ? 2 : 1;
            d.halo.gameObject.SetActive(offset == 0); d.rim.gameObject.SetActive(offset == 0); d.progress.gameObject.SetActive(offset == 0);
            if (offset == 0) ctl.startButton = d.button;
            d.root.gameObject.SetActive(Mathf.Abs(offset) <= 2 || Mathf.Abs(d.from.x) < 1400);
        }
        animation = 0;
        string id = ctl.SongIdAt(index), title = ResultSkin.SongIdToDisplayTitle(id);
        string markup = id == "Epilogue" ? "<ruby=こうか>校歌</ruby>" : id == "揺籠" ? "<ruby=ゆりかご>揺籠</ruby>" : title;
        var titleText = songTitle.GetComponent<TextMeshProUGUI>();
        bool japanese = id == "Epilogue" || id == "揺籠";
        // フォントは固定し、日本語は既存のフォールバックで描く。切替時に別アトラスの材質を残さない。
        titleText.fontStyle = japanese ? FontStyles.Bold : FontStyles.Normal;
        titleText.fontSizeMax = japanese ? 48 : 58;
        titleText.rectTransform.anchoredPosition = new Vector2(0, japanese ? -11 : -2);
        songTitle.Set(markup); DifficultyChanged(ctl.SelectedDifficultyIndex);
        corridor.Select(id);
    }
    void DifficultyChanged(int selected)
    {
        for (int i = 0; i < 3; i++)
        {
            bool active = i == selected; int level = ctl.DifficultyDisplayLevelAt(i);
            difficultyNumbers[i].text = level > 0 ? level.ToString() : "—";
            difficultyNumbers[i].color = active ? Ink : DifficultyColor(i);
            difficultyNumbers[i].rectTransform.localScale = Vector3.one * (active ? 1.14f : 1);
            difficultyFaces[i].color = active ? DifficultyColor(i) : Ink;
            difficultyFaces[i].rectTransform.localScale = Vector3.one * (active ? 1.14f : 1);
            difficultyRings[i].gameObject.SetActive(active);
            difficultyLabels[i].color = active ? White : new Color(.56f, .61f, .66f);
            ctl.difficultyButtons[i].interactable = ctl.DifficultyLevelAt(i) > 0;
        }
        foreach (var d in discs) d.button.interactable = d.offset == 0 ? !ctl.SelectedSongLocked && ctl.CurrentDifficultyLevel() > 0 : Mathf.Abs(d.offset) == 1;
        achievementDifficulty.text = DifficultyDisplayName(selected, ctl.difficultyNames[selected]);
        var value = SongAchievementStore.Load(ctl.SongIdAt(ctl.SelectedIndex), ctl.difficultyNames[selected]);
        int[] values = { value.s, value.sPlus, value.fc, value.ap };
        for (int i = 0; i < 4; i++) counts[i].Set(values[i].ToString() + "<size=20><ruby=にん>人</ruby></size>");
    }
    void Update()
    {
        if (!built) return;
        animation = Mathf.Min(1, animation + Time.unscaledDeltaTime / .5f); AnimateDiscs();
        double now = Time.realtimeSinceStartupAsDouble;
        TickCountdown(now - lastTick); lastTick = now;
        int seconds = Mathf.CeilToInt((float)countdown.Remaining);
        if (displayedSecond != seconds) { timer.text = seconds.ToString(); displayedSecond = seconds; }
        timer.color = seconds <= 10 ? Gold : White;
        timer.rectTransform.localScale = Vector3.one * (seconds <= 10 ? 1 + .055f * Mathf.Exp(-((100 - (float)countdown.Remaining) % 1) * 8) : 1);
    }
    public void TickCountdown(double deltaSeconds)
    {
        if (built && countdown.Tick(deltaSeconds, !ScreenTransition.IsBusy)) ctl.StartGame();
    }
    void AnimateDiscs()
    {
        float t = Ease(Mathf.Clamp01(animation * .5f / .46f));
        foreach (var d in discs)
        {
            d.root.anchoredPosition = Vector2.LerpUnclamped(d.from, d.to, t);
            float bounce = d.offset == 0 && animation < 1 ? animation < .5f ? Mathf.Lerp(.9f, 1.05f, animation * 2) : Mathf.Lerp(1.05f, 1, animation * 2 - 1) : 1;
            d.root.localScale = Vector3.one * Mathf.LerpUnclamped(d.fromScale, d.toScale, t) * bounce;
            float brightness = d.offset == 0 ? 1 : d.target.Hovered && Mathf.Abs(d.offset) == 1 ? .8f : Mathf.Abs(d.offset) == 1 ? .5f : .36f;
            d.art.color = d.art.Artwork != null ? new Color(brightness, brightness, brightness) : new Color(.07f * brightness, .10f * brightness, .20f * brightness);
            if (d.fallback != null) d.fallback.color = new Color(brightness, brightness, brightness);
            if (d.offset == 0)
            {
                float pulse = corridor != null ? corridor.CenterPulse : 0;
                d.rim.color = new Color(Cyan.r, Cyan.g, Cyan.b, .35f + .55f * pulse); d.rim.Width = 3 + 4 * pulse; d.rim.Refresh();
                float spread = 24 + 40 * pulse;
                d.halo.Width = spread; d.halo.rectTransform.sizeDelta = Vector2.one * (540 + spread * 2);
                d.halo.color = new Color(Cyan.r, Cyan.g, Cyan.b, .04f + .08f * pulse);
                d.progress.Progress = d.target.Progress; d.progress.Refresh();
            }
            if (animation >= 1 && Mathf.Abs(d.offset) > 2) d.root.gameObject.SetActive(false);
        }
    }
    static float Ease(float x)
    {
        float low = 0, high = 1, u = x;
        for (int i = 0; i < 12; i++) { u = (low + high) * .5f; float t = 3 * (1 - u) * (1 - u) * u * .3f + 3 * (1 - u) * u * u * .55f + u * u * u; if (t < x) low = u; else high = u; }
        return 3 * (1 - u) * (1 - u) * u * 1.35f + 3 * (1 - u) * u * u + u * u * u;
    }
    void OnDestroy()
    {
        if (ctl != null) { ctl.OnSelectionChanged -= SelectionChanged; ctl.OnDifficultyChanged -= DifficultyChanged; }
    }
    Sprite CoverSprite(int index) => ctl != null ? ctl.CoverSpriteAt(index) : null;
}
