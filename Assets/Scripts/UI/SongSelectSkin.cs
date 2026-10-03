using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 1920×1080のディスク選曲。選択・開始・音源の所有権はControllerに残す。
public class SongSelectSkin : MonoBehaviour
{
    sealed class Disc
    {
        // offset=選択中から見た位置(±半周)。slot=画面上で向かっている置き場(端を回り込む途中は ±3 より外)。
        public int index, offset, slot;
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
    int displayedSecond = -1, shownIndex = -1;
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
        var guide = SongSelectIdleGuide.Build(ctl, layout);
        SongSelectAimPointer.Build(ctl, canvas, null, guide);
        countdown.Reset(GameSession.CalibrationSelectionSeconds ?? 100);
        GameSession.CalibrationSelectionSeconds = null;
        timer.text = Mathf.CeilToInt((float)countdown.Remaining).ToString();
        lastTick = Time.realtimeSinceStartupAsDouble; built = true;
    }

    public static Color DifficultyColor(int index) => SongSelectVisuals.Difficulty[Mathf.Clamp(index, 0, 2)];
    public static string DifficultyDisplayName(int index, string sourceName) => index == 2 ? "MASTER" : string.IsNullOrEmpty(sourceName) ? $"CHART {index + 1}" : sourceName.ToUpperInvariant();
    public static int MeterSegmentsForLevel(int level) => Mathf.Clamp(level, 0, 10);
    public static string FormatDifficultyLevel(int level) => level > 0 ? $"LEVEL {Mathf.Clamp(level, 0, 99):00}" : "LEVEL --";
    public static string FormatDifficultyCardLevel(int level) => level > 0 ? $"LV {Mathf.Clamp(level, 0, 99):00}" : "LV --";
    public static void ApplyNeon(Button button, Color accent, float fillAlpha) => UISkinKit.RestyleButton(button, accent);
    public static void EnterCalibration()
    {
        if (!ScreenTransition.Load("Game", ScreenTransition.Style.Calibration)) return;
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        var controller = Object.FindFirstObjectByType<SongSelectController>();
        GameSession.CalibrationSelectionSongId = controller != null ? controller.SongIdAt(controller.SelectedIndex) : null;
        GameSession.CalibrationSelectionDifficulty = controller != null && controller.difficultyNames != null
            && controller.SelectedDifficultyIndex >= 0 && controller.SelectedDifficultyIndex < controller.difficultyNames.Length
            ? controller.difficultyNames[controller.SelectedDifficultyIndex] : null;
        GameSession.CalibrationSelectionSeconds = skin != null ? skin.RemainingSeconds : 100;
        GameSession.IsCalibrationMode = true;
    }
    static Vector2 Position(float x, float y) => new Vector2(x - 960, 540 - y);
    // 盤の置き場(1920×1080 基準、画面中央が原点)。0=選択中、±1=隣、±2=2曲先。±3 より外は画面外で待機する。
    // 中央540・隣380 は保ち、盤どうしの隙間を約20pxにそろえて2曲先を画面内に収める。
    public static Vector2 DiscSlotPosition(int slot)
    {
        int distance = Mathf.Abs(slot); float side = slot < 0 ? -1 : 1;
        float x = distance == 0 ? 960 : distance == 1 ? 960 + side * 479 : 960 + side * (790 + 300 * (distance - 2));
        return Position(x, distance == 0 ? 452 : distance == 1 ? 500 : 615);
    }
    public static float DiscSlotDiameter(int slot) => slot == 0 ? 540 : Mathf.Abs(slot) == 1 ? 380 : 240;
    // 選択中から見た盤の位置。半周を超えたら反対側に数える。
    public static int WrapOffset(int offset, int count)
    {
        if (count <= 0) return 0;
        offset = (offset % count + count) % count;
        return offset > count / 2 ? offset - count : offset;
    }
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
                else if (Mathf.Abs(d.offset) <= 2) ctl.Select(d.index);
            });
            discs.Add(d);
        }
        foreach (int side in new[] { -1, 1 })
        {
            // 2曲先の盤(画面端)の縁にかからない高さに置く。
            var arrow = Graphic(layout, "DecorativeArrow", Position(side < 0 ? 52 : 1844, 470), new Vector2(60, 90), SongSelectDiscGraphic.Shape.Arrow, new Color(.38f, .81f, .83f, .35f));
            if (side > 0) arrow.rectTransform.localScale = new Vector3(-1, 1, 1);
        }
    }
    void BuildTitle()
    {
        PanelAt("TitleShadow", 960, 1000, 716, 108, Ink, 5, 30);
        var panel = PanelAt("SongTitlePanel", 960, 1000, 700, 92, Cyan, 5);
        var t = Label(panel.transform, "PanelSongTitle", "", 58, new Vector2(0, -2), new Vector2(660, 74), UISkinKit.FontAsset("Oxanium-ExtraBold"));
        t.enableAutoSizing = true; t.fontSizeMin = 26; t.fontSizeMax = 58; songTitle = Ruby(t, "");
    }
    void BuildDifficulties()
    {
        var tray = PanelAt("DifficultyTray", 960, 828, 720, 124, new Color(.09f, .19f, .29f), 4, 62); tray.color = new Color(.024f, .043f, .082f);
        ctl.difficultyButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var root = Rect("Difficulty" + i, 730 + 230 * i, 830, 190, 196);
            var face = Graphic(root, "Circle", new Vector2(0, 2), new Vector2(124, 124), SongSelectDiscGraphic.Shape.Cover, Ink);
            var ring = Graphic(root, "CircleEdge", new Vector2(0, 2), new Vector2(124, 124), SongSelectDiscGraphic.Shape.Ring, DifficultyColor(i)); ring.Width = 9;
            difficultyFaces[i] = face;
            difficultyRings[i] = Graphic(root, "SelectedRing", new Vector2(0, 2), new Vector2(158, 158), SongSelectDiscGraphic.Shape.Ring, White); difficultyRings[i].Width = 5;
            difficultyNumbers[i] = Label(root, "Level", "", 58, new Vector2(0, 2), new Vector2(110, 100), UISkinKit.LogoFontAsset(), DifficultyColor(i));
            difficultyLabels[i] = Label(root, "DifficultyName", DifficultyDisplayName(i, ctl.difficultyNames[i]), 28, new Vector2(0, -88), new Vector2(210, 40));
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
        achievementDifficulty = Label(panel.transform, "AchievementDifficulty", "EASY", 24, new Vector2(-49, 86), new Vector2(152, 36), color: SongSelectVisuals.Muted);
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
        var back = PanelAt("BackToTitle", 1220, 92, 340, 96, SongSelectVisuals.Edge, 2, 16);
        ButtonOn(back.rectTransform, back, 2, false).onClick.AddListener(ctl.ReturnToTitle);
        Label(back.transform, "Label", "タイトルへ", 36, Vector2.zero, new Vector2(300, 76), UISkinKit.JapaneseFallbackFontAsset());
    }
    void SelectionChanged(int index)
    {
        // 送った向きと量(隣なら±1、2曲先なら±2)。盤の位置と同じく半周を超えたら逆回りに数える。
        int step = built && shownIndex >= 0 ? WrapOffset(index - shownIndex, discs.Count) : 0;
        shownIndex = index;
        foreach (var d in discs)
        {
            int offset = WrapOffset(d.index - index, discs.Count), travel = d.slot - step;
            bool shown = Mathf.Abs(offset) <= 2;
            d.offset = offset; d.from = d.root.anchoredPosition; d.fromScale = d.root.localScale.x;
            // 端を回り込む盤に画面を横切らせない。消える盤は進む向きへ抜け、現れる盤は反対側の画面外から入る。
            if (!built || travel == offset) d.slot = offset;
            else if (shown)
            {
                int entry = step > 0 ? Mathf.Max(offset + step, 3) : Mathf.Min(offset + step, -3);
                d.slot = offset; d.from = DiscSlotPosition(entry); d.fromScale = DiscSlotDiameter(entry) / 540f;
            }
            else d.slot = travel;
            d.to = DiscSlotPosition(d.slot); d.toScale = DiscSlotDiameter(d.slot) / 540f;
            if (!built) { d.from = d.to; d.fromScale = d.toScale; }
            d.target.HoldSeconds = offset == 0 ? 2 : 1;
            // 隣と2曲先は曲送りの的で、乗せたままでも続けて送る。中央(スタート)は一度外れるまで受け付けない。
            d.target.RepeatWhileHeld = offset != 0; d.target.Sliding = built;
            d.halo.gameObject.SetActive(offset == 0); d.rim.gameObject.SetActive(offset == 0); d.progress.gameObject.SetActive(offset == 0);
            if (offset == 0) ctl.startButton = d.button;
            d.root.gameObject.SetActive(shown || d.root.gameObject.activeSelf);
        }
        // 中央ほど手前に描く。滑っている途中で重なっても、手前の盤が照準の当たりを取る。
        int order = int.MaxValue;
        foreach (var d in discs) order = Mathf.Min(order, d.root.GetSiblingIndex());
        foreach (var d in discs.OrderByDescending(x => Mathf.Abs(x.offset))) d.root.SetSiblingIndex(order++);
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
        foreach (var d in discs) d.button.interactable = d.offset == 0 ? !ctl.SelectedSongLocked && ctl.CurrentDifficultyLevel() > 0 : Mathf.Abs(d.offset) <= 2;
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
            // 画面外へ抜けた盤は、次に呼ばれる側の待機位置へ移しておく(非表示のまま移すので見えない)。
            if (animation >= 1 && Mathf.Abs(d.offset) > 2 && d.slot != d.offset)
            {
                d.slot = d.offset; d.from = d.to = DiscSlotPosition(d.offset);
                d.fromScale = d.toScale = DiscSlotDiameter(d.offset) / 540f;
            }
            d.root.anchoredPosition = Vector2.LerpUnclamped(d.from, d.to, t);
            d.target.Sliding = animation < 1;
            float bounce = d.offset == 0 && animation < 1 ? animation < .5f ? Mathf.Lerp(.9f, 1.05f, animation * 2) : Mathf.Lerp(1.05f, 1, animation * 2 - 1) : 1;
            d.root.localScale = Vector3.one * Mathf.LerpUnclamped(d.fromScale, d.toScale, t) * bounce;
            float brightness = d.offset == 0 ? 1 : d.target.Hovered && Mathf.Abs(d.offset) <= 2 ? .8f : Mathf.Abs(d.offset) == 1 ? .5f : .36f;
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
