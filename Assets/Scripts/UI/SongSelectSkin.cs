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
    TextMeshProUGUI sourceCredit, composerName, timer, timerNotice, achievementDifficulty, libraryPosition, startHint, holdHint;
    // 残り時間の音(盤5): 30秒で1回の知らせ、10秒から1秒ごとの刻み。鳴らすのは MenuSfx。
    int lastBeepSecond = -1, previousSecond = -1;
    // ためている間の表示(盤2): 難易度の円は円周、判定調整・タイトルへのボタンは下の線が満ちる。
    readonly SongSelectDiscGraphic[] difficultyCharges = new SongSelectDiscGraphic[3];
    readonly SongSelectDiscTarget[] difficultyTargets = new SongSelectDiscTarget[3];
    readonly List<(SongSelectDiscTarget target, RectTransform bar, float width)> buttonCharges = new List<(SongSelectDiscTarget, RectTransform, float)>();
    // スタートでジャケットを斬る演出(盤3)。startAge は決めてからの秒(決める前は負)。
    SongSelectStartCut startShards, startLights;
    float startAge = -1;
    public float StartAge => startAge;
    SongSelectRubyText songTitle, highScore;
    SongSelectCorridor corridor;
    RushESecret secretView;
    RectTransform secretDifficulty;
    SongSelectDiscGraphic difficultyTray;
    public bool DiscsSettled => animation >= 1;
    public Rect SelectedDiscScreenRect => ctl != null && ctl.startButton != null
        ? ctl.startButton.GetComponent<SongSelectDiscTarget>().ScreenRect() : default;
    MenuSignalNoise hardNoise;
    RectTransform layout, holdHintRoot;
    float animation = 1;
    bool built;
    int displayedSecond = -1, noticeSecond = -1, shownIndex = -1;
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
        // 消音ボタンのない従来の画面へ戻すため、以前保存された試聴の消音も解除する。
        if (ctl.ChartPreview != null && ctl.ChartPreview.Muted) ctl.ChartPreview.SetMuted(false);
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
        BuildHeader(); BuildDiscs(); BuildTitle(); BuildDifficulties(); BuildSecretDifficulty(); BuildAchievements(); BuildActions();
        // 選曲の音は幕の裏で作っておき、最初の操作で処理落ちしないようにする。
        MenuSfx.PrepareSongSelect();
        hardNoise = MenuSignalNoise.BuildScreenOverlay(canvas, layout);
        secretView = RushESecret.Build(ctl, this, layout);
        ctl.OnSelectionChanged += SelectionChanged; ctl.OnDifficultyChanged += DifficultyChanged; ctl.OnStartProgress += AnimateStart;
        SelectionChanged(ctl.SelectedIndex); animation = 1; AnimateDiscs();
        var guide = SongSelectIdleGuide.Build(ctl, layout);
        SongSelectAimPointer.Build(ctl, canvas, null, guide);
        countdown.Reset(GameSession.CalibrationSelectionSeconds ?? 100);
        GameSession.CalibrationSelectionSeconds = null;
        RefreshCountdownDisplay(timer, countdown.Remaining, ref displayedSecond);
        lastTick = Time.realtimeSinceStartupAsDouble; built = true;
    }

    public static Color DifficultyColor(int index) => SongSelectVisuals.Difficulty[Mathf.Clamp(index, 0, 2)];
    public static string DifficultyDisplayName(int index, string sourceName) => index == 2 ? "HARD" : string.IsNullOrEmpty(sourceName) ? $"CHART {index + 1}" : sourceName.ToUpperInvariant();
    public static int MeterSegmentsForLevel(int level) => Mathf.Clamp(level, 0, 10);
    public static string FormatDifficultyLevel(int level) => level > 0 ? $"LEVEL {Mathf.Clamp(level, 0, 99):00}" : "LEVEL --";
    public static string FormatDifficultyCardLevel(int level) => level > 0 ? $"LV {Mathf.Clamp(level, 0, 99):00}" : "LV --";
    // 結果画面の HI-SCORE と同じ記録(Load は高い順)の1位。まだ遊ばれていない譜面は 0点 と区別して「—」にする。
    public static string FormatHighScore(HighScoreTable table) => table != null && table.entries != null && table.entries.Count > 0
        ? table.entries[0].score.ToString("N0") + "<size=24>点</size>" : "—";
    public static void ApplyNeon(Button button, Color accent, float fillAlpha) => UISkinKit.RestyleButton(button, accent);
    public static void EnterCalibration()
    {
        if (!ScreenTransition.Load("Game", ScreenTransition.Style.Calibration)) return;
        MenuSfx.Calibration();
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
        timer = BuildCountdownDisplay(layout);
        timerNotice = Label(layout, "TimerConsequence", "この曲が自動で始まるまで", 25, Position(1600, 171), new Vector2(560, 40), UISkinKit.JapaneseFallbackFontAsset(), White);
    }
    // 選曲と判定調整で同じ位置・図形・文字を使い、表示仕様を一か所で管理する。
    public static TextMeshProUGUI BuildCountdownDisplay(Transform parent)
    {
        var clock = Graphic(parent, "TimeTab", Position(1680, 92), new Vector2(360, 112), SongSelectDiscGraphic.Shape.Tab, Panel);
        clock.Width = 6; clock.Edge = new Color(.25f, .43f, 1); clock.EdgeRight = Cyan; clock.Bottom = Ink;
        var icon = Graphic(clock.transform, "ClockIcon", new Vector2(-89, 0), new Vector2(56, 56), SongSelectDiscGraphic.Shape.Clock, White); icon.Edge = Ink;
        return Label(clock.transform, "TimeRemaining", "100", 86, new Vector2(42, 0), new Vector2(180, 106), UISkinKit.LogoFontAsset());
    }
    public static void RefreshCountdownDisplay(TextMeshProUGUI timer, double remaining, ref int displayedSecond)
    {
        int seconds = Mathf.CeilToInt((float)remaining);
        if (displayedSecond != seconds) { timer.text = seconds.ToString(); displayedSecond = seconds; }
        timer.color = seconds <= 10 ? Gold : White;
        timer.rectTransform.localScale = Vector3.one * (seconds <= 10 ? 1 + .055f * Mathf.Exp(-((100 - (float)remaining) % 1) * 8) : 1);
    }
    void BuildDiscs()
    {
        libraryPosition = Label(layout, "LibraryPosition", "", 24, Position(295, 743), new Vector2(430, 34), color: SongSelectVisuals.Muted);
        if (ctl.SongCount == 0)
        {
            Label(layout, "EmptyLibrary", "遊べる譜面がまだありません", 44, Position(960, 432), new Vector2(1200, 70), UISkinKit.JapaneseFallbackFontAsset());
            Label(layout, "EmptyLibraryHint", "ノーツ入りの譜面を曲フォルダーへ保存してから、選曲画面を開き直してください。", 25,
                Position(960, 507), new Vector2(1400, 80), UISkinKit.JapaneseFallbackFontAsset(), SongSelectVisuals.Muted);
        }
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
            d.target.ShootOverride = () =>
            {
                if (ScreenTransition.IsBusy || ctl.RushESecret.Transitioning) return false;
                if (d.index == ctl.SelectedIndex) ctl.StartGame(); else if (Mathf.Abs(d.offset) <= 2) StepTo(d);
                return true;
            };
            d.button.onClick.AddListener(() =>
            {
                if (ScreenTransition.IsBusy) return;
                if (d.index == ctl.SelectedIndex)
                {
                    if (ctl.SongIdAt(d.index) == RushESecretState.SongId && !ctl.RushESecret.Unlocked) secretView.Click();
                    else ctl.StartGame();
                }
                else if (Mathf.Abs(d.offset) <= 2) StepTo(d);
            });
            discs.Add(d);
        }
        holdHintRoot = PanelAt("HoldStartPanel", 960, 580, 660, 68, new Color(.012f,.024f,.047f,.94f), 0, 12).rectTransform;
        holdHint = Label(holdHintRoot, "HoldStartHint", "そのまま かざすと スタート", 36, Vector2.zero, new Vector2(640, 58), UISkinKit.JapaneseFallbackFontAsset(), White);
        holdHintRoot.gameObject.SetActive(false);
        startHint = Label(layout, "StartHint", "中央の曲に２秒かざすと スタート", 26, Position(960, 743), new Vector2(750, 36), UISkinKit.JapaneseFallbackFontAsset(), White);
    }
    // 隣・2曲先の盤で曲を送る。送った向き(左は低め、右は高め)の音を鳴らす(盤1)。
    void StepTo(Disc d)
    {
        bool right = d.offset > 0;
        ctl.Select(d.index);
        if (ctl.SelectedIndex == d.index) MenuSfx.DiscMove(right);
    }
    void BuildTitle()
    {
        PanelAt("TitleShadow", 960, 1006, 716, 130, Ink, 5, 30);
        var panel = PanelAt("SongTitlePanel", 960, 1006, 700, 116, Cyan, 4);
        var t = Label(panel.transform, "PanelSongTitle", "", 50, new Vector2(0, 22), new Vector2(660, 62), UISkinKit.FontAsset("Oxanium-ExtraBold"));
        t.enableAutoSizing = true; t.fontSizeMin = 24; t.fontSizeMax = 50; songTitle = Ruby(t, "");
        // 曲名のすぐ下をクレジット欄にする。装飾は曲選択や難易度の入力を遮らない。
        Graphic(panel.transform, "ComposerRule", new Vector2(0, -18), new Vector2(626, 1), SongSelectDiscGraphic.Shape.Panel,
            new Color(Cyan.r, Cyan.g, Cyan.b, .22f)).Width = 0;
        Label(panel.transform, "ComposerLabel", "作曲者", 22, new Vector2(-257, -37), new Vector2(96, 30),
            UISkinKit.JapaneseFallbackFontAsset(), SongSelectVisuals.Muted);
        composerName = Label(panel.transform, "ComposerName", "???", 26, new Vector2(49, -37), new Vector2(500, 32));
        composerName.alignment = TextAlignmentOptions.MidlineLeft;
        composerName.enableAutoSizing = true; composerName.fontSizeMin = 18; composerName.fontSizeMax = 26;
        composerName.overflowMode = TextOverflowModes.Ellipsis;
        composerName.richText = false; composerName.parseCtrlCharacters = false;
        // 出典がある曲だけ、判定調整ボタンの下へ作者・元動画のURLを表示する。
        sourceCredit = Label(layout, "SongSourceCredit", "", 17, Position(1630, 1052), new Vector2(470, 48));
        sourceCredit.alignment = TextAlignmentOptions.MidlineLeft;
        sourceCredit.enableAutoSizing = true; sourceCredit.fontSizeMin = 12; sourceCredit.fontSizeMax = 17;
        sourceCredit.overflowMode = TextOverflowModes.Ellipsis;
        sourceCredit.richText = false; sourceCredit.parseCtrlCharacters = false;
    }

    void BuildDifficulties()
    {
        var tray = PanelAt("DifficultyTray", 960, 828, 720, 124, new Color(.09f, .19f, .29f), 4, 62); tray.color = new Color(.024f, .043f, .082f);
        difficultyTray = tray;
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
            // 円と難易度名を含め、上の開始案内は操作対象から外す。
            difficultyCharges[i] = Graphic(root, "ChargeRing", new Vector2(0, 2), new Vector2(146, 146), SongSelectDiscGraphic.Shape.Ring, DifficultyColor(i));
            difficultyCharges[i].Width = 6; difficultyCharges[i].Progress = 0; difficultyCharges[i].gameObject.SetActive(false);
            var hit = Graphic(root, "HitArea", new Vector2(0, -17), new Vector2(190, 162), SongSelectDiscGraphic.Shape.Panel, Color.clear); hit.Width = 0;
            ctl.difficultyButtons[i] = ButtonOn(root, hit, 1, false);
            difficultyTargets[i] = root.GetComponent<SongSelectDiscTarget>();
            difficultyTargets[i].HitArea = hit.rectTransform;
            ctl.difficultyButtons[i].onClick.AddListener(() => ctl.ChooseDifficulty(index));
        }
    }
    void BuildSecretDifficulty()
    {
        secretDifficulty = Rect("RushEOnlyDifficulty", 960, 832, 200, 190);
        var glow = Graphic(secretDifficulty, "Glow", Vector2.zero, new Vector2(192, 192), SongSelectDiscGraphic.Shape.Ring, new Color(.62f,.17f,1,.25f)); glow.Width = 20;
        var face = Graphic(secretDifficulty, "Face", Vector2.zero, new Vector2(158, 158), SongSelectDiscGraphic.Shape.Cover, new Color(.12f,.03f,.24f));
        var ring = Graphic(secretDifficulty, "Ring", Vector2.zero, new Vector2(164, 164), SongSelectDiscGraphic.Shape.Ring, RushESecretState.Purple); ring.Width = 6;
        Label(secretDifficulty, "Letter", "E", 112, new Vector2(0, 2), new Vector2(140, 150), UISkinKit.LogoFontAsset(), RushESecretState.Purple);
        secretDifficulty.gameObject.SetActive(false);
    }
    void BuildAchievements()
    {
        // 最下段に HI-SCORE を足した。行の間隔72はそのままで上へ16(曲数の表示も16上げた)・下へ24伸ばし、下端を曲名パネル(1064)にそろえる。
        var panel = PanelAt("Achievements", 295, 919, 470, 290, SongSelectVisuals.Edge, 3, 22);
        Ruby(Label(panel.transform, "AchievementHeading", "", 24, new Vector2(-163, 108), new Vector2(94, 45), UISkinKit.JapaneseFallbackFontAsset(), SongSelectVisuals.Accent), "<ruby=きろく>記録</ruby>");
        achievementDifficulty = Label(panel.transform, "AchievementDifficulty", "EASY", 24, new Vector2(-49, 106), new Vector2(152, 36), color: SongSelectVisuals.Muted);
        string[] ranks = { "S↑", "S<color=#ff6e7a>+</color>", "FC", "AP" };
        for (int i = 0; i < 4; i++)
        {
            float x = i % 2 == 0 ? -192 : 32, y = i < 2 ? 49 : -23;
            var rank = Label(panel.transform, "Rank" + i, ranks[i], 46, new Vector2(x + 35, y), new Vector2(100, 65), UISkinKit.LogoFontAsset(), Gold);
            rank.alignment = TextAlignmentOptions.MidlineLeft;
            if (i == 3) { rank.color = Color.white; rank.enableVertexGradient = true; rank.colorGradient = new VertexGradient(new Color(1, .5f, .5f), new Color(.65f, .5f, 1), new Color(1, .85f, .45f), new Color(.3f, .9f, 1)); }
            var number = Label(panel.transform, "Count" + i, "", 36, new Vector2(x + 137, y - 2), new Vector2(106, 55), UISkinKit.LogoFontAsset());
            number.enableAutoSizing = true; number.fontSizeMin = 16; number.fontSizeMax = 36;
            counts[i] = Ruby(number, "0<size=24>回</size>");
            var line = Graphic(panel.transform, "Rule", new Vector2(x + 94, y - 31), new Vector2(196, 2), SongSelectDiscGraphic.Shape.Panel, new Color(.38f, .81f, .83f, .2f)); line.Width = 0;
        }
        // 「HI-SCORE」と6桁の点は片側の枠に入らないので2列ぶんを使い、桁数が変わっても点の右端をそろえる。
        var best = Label(panel.transform, "HighScoreLabel", "HI-SCORE", 46, new Vector2(-97, -95), new Vector2(220, 65), UISkinKit.LogoFontAsset(), Gold);
        best.alignment = TextAlignmentOptions.MidlineLeft;
        var score = Label(panel.transform, "HighScore", "", 36, new Vector2(119, -97), new Vector2(206, 55), UISkinKit.LogoFontAsset());
        score.alignment = TextAlignmentOptions.MidlineRight;
        score.enableAutoSizing = true; score.fontSizeMin = 16; score.fontSizeMax = 36;
        highScore = Ruby(score, FormatHighScore(null));
        Graphic(panel.transform, "HighScoreRule", new Vector2(14, -126), new Vector2(420, 2), SongSelectDiscGraphic.Shape.Panel, new Color(.38f, .81f, .83f, .2f)).Width = 0;
    }
    void BuildActions()
    {
        var cal = PanelAt("CalibrationButton", 1630, 930, 460, 180, SongSelectVisuals.Edge, 4);
        ButtonOn(cal.rectTransform, cal, 2, false).onClick.AddListener(() => { EnterCalibration(); if (ScreenTransition.IsBusy) ctl.StopPreview(); });
        AddChargeBar(cal, 460, 180);
        var t = Label(cal.transform, "CalibrationLabel", "", 52, new Vector2(0, -8), new Vector2(410, 106), UISkinKit.JapaneseFallbackFontAsset()); t.fontStyle = FontStyles.Bold;
        Ruby(t, "<ruby=はんてい>判定</ruby><ruby=ちょうせい>調整</ruby>");
        var back = PanelAt("BackToTitle", 1220, 92, 340, 96, SongSelectVisuals.Edge, 2, 16);
        ButtonOn(back.rectTransform, back, 2, false).onClick.AddListener(ctl.ReturnToTitle);
        Label(back.transform, "Label", "タイトルへ", 36, Vector2.zero, new Vector2(300, 76), UISkinKit.JapaneseFallbackFontAsset());
        AddChargeBar(back, 340, 96);
    }
    // ボタンの下端に、ためている間だけ左から満ちる線を置く(盤2)。
    void AddChargeBar(SongSelectDiscGraphic panel, float width, float height)
    {
        float length = width - 36;
        var bar = Graphic(panel.transform, "ChargeBar", Vector2.zero, new Vector2(length, 6), SongSelectDiscGraphic.Shape.Panel, Gold);
        bar.Width = 0; bar.Radius = 3;
        var rect = bar.rectTransform; rect.pivot = new Vector2(0, .5f); rect.anchoredPosition = new Vector2(-length * .5f, -height * .5f + 11);
        bar.gameObject.SetActive(false);
        buttonCharges.Add((panel.GetComponent<SongSelectDiscTarget>(), rect, length));
    }
    void RefreshChargeDisplays()
    {
        for (int i = 0; i < 3; i++)
        {
            if (difficultyCharges[i] == null || difficultyTargets[i] == null) continue;
            float progress = difficultyTargets[i].Progress;
            bool show = progress > 0 && startAge < 0;
            if (difficultyCharges[i].gameObject.activeSelf != show) difficultyCharges[i].gameObject.SetActive(show);
            if (show && difficultyCharges[i].Progress != progress) { difficultyCharges[i].Progress = progress; difficultyCharges[i].Refresh(); }
        }
        foreach (var (target, bar, width) in buttonCharges)
        {
            float progress = target != null ? target.Progress : 0;
            bool show = progress > 0 && startAge < 0;
            if (bar.gameObject.activeSelf != show) bar.gameObject.SetActive(show);
            if (show) bar.sizeDelta = new Vector2(Mathf.Max(6, width * progress), bar.sizeDelta.y);
        }
    }
    void SelectionChanged(int index)
    {
        // 送った向きと量(隣なら±1、2曲先なら±2)。盤の位置と同じく半周を超えたら逆回りに数える。
        int step = built && shownIndex >= 0 ? WrapOffset(index - shownIndex, discs.Count) : 0;
        shownIndex = index;
        libraryPosition.text = ctl.SongCount > 0 ? (index + 1) + " / " + ctl.SongCount + " 曲" : "0 曲";
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
            d.target.BottomInset = offset == 0 ? 30 : 0;
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
        // ふりがなは stage.json の displayReading から(曲ごとのコードの決め打ちをしない)。
        var info = SongDisplayInfo.Get(id);
        string reading = info.Reading;
        composerName.text = info.Composer;
        sourceCredit.text = string.IsNullOrEmpty(info.SourceUrl) ? string.Empty
            : (string.IsNullOrEmpty(info.Artist) ? string.Empty : info.Artist + "\n") + info.SourceUrl;
        bool japanese = !string.IsNullOrEmpty(reading);
        string markup = japanese ? $"<ruby={reading}>{title}</ruby>" : title;
        var titleText = songTitle.GetComponent<TextMeshProUGUI>();
        // フォントは固定し、日本語は既存のフォールバックで描く。切替時に別アトラスの材質を残さない。
        titleText.fontStyle = japanese ? FontStyles.Bold : FontStyles.Normal;
        titleText.fontSizeMax = japanese ? 44 : 50;
        titleText.rectTransform.anchoredPosition = new Vector2(0, japanese ? 14 : 22);
        songTitle.Set(markup); DifficultyChanged(ctl.SelectedDifficultyIndex);
        corridor.Select(id);
    }
    void DifficultyChanged(int selected)
    {
        bool secret = ctl.RushESecret.Unlocked;
        secretDifficulty.gameObject.SetActive(secret);
        difficultyTray.gameObject.SetActive(!secret);
        corridor.SetSecretTheme(secret);
        hardNoise.SetHardSelected(!secret && string.Equals(ctl.CurrentDifficultyName, "Hard", System.StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < 3; i++)
        {
            ctl.difficultyButtons[i].gameObject.SetActive(!secret);
            bool active = i == selected; int level = ctl.DifficultyDisplayLevelAt(i);
            difficultyNumbers[i].text = level > 0 ? level.ToString() : "—";
            difficultyNumbers[i].color = active ? Ink : DifficultyColor(i);
            difficultyNumbers[i].rectTransform.localScale = Vector3.one * (active ? 1.14f : 1);
            difficultyFaces[i].color = active ? DifficultyColor(i) : Ink;
            difficultyFaces[i].rectTransform.localScale = Vector3.one * (active ? 1.14f : 1);
            difficultyRings[i].gameObject.SetActive(active);
            difficultyLabels[i].color = active ? White : new Color(.56f, .61f, .66f);
            difficultyLabels[i].text = level > 0 ? DifficultyDisplayName(i, ctl.difficultyNames[i]) : "未制作";
            ctl.difficultyButtons[i].interactable = !ctl.RushESecret.Transitioning && ctl.DifficultyLevelAt(i) > 0;
        }
        foreach (var d in discs) d.button.interactable = d.offset == 0 ? !ctl.RushESecret.Transitioning && !ctl.SelectedSongLocked && ctl.CurrentDifficultyLevel() > 0 : Mathf.Abs(d.offset) <= 2;
        achievementDifficulty.text = secret ? "E" : DifficultyDisplayName(selected, ctl.CurrentDifficultyName);
        achievementDifficulty.color = secret ? RushESecretState.Purple : SongSelectVisuals.Muted;
        var value = SongAchievementStore.Load(ctl.SongIdAt(ctl.SelectedIndex), ctl.CurrentDifficultyName);
        int[] values = { value.s, value.sPlus, value.fc, value.ap };
        for (int i = 0; i < 4; i++) counts[i].Set(values[i].ToString() + "<size=24>回</size>");
        highScore.Set(FormatHighScore(HighScoreStore.Load(ctl.SongIdAt(ctl.SelectedIndex), ctl.CurrentDifficultyName)));
    }
    void Update()
    {
        if (!built) return;
        animation = Mathf.Min(1, animation + Time.unscaledDeltaTime / .5f); AnimateDiscs();
        double now = Time.realtimeSinceStartupAsDouble;
        TickCountdown(now - lastTick); lastTick = now;
        RefreshCountdownDisplay(timer, countdown.Remaining, ref displayedSecond);
        bool charging = startAge < 0 && ctl.startButton != null && ctl.startButton.GetComponent<SongSelectDiscTarget>().Progress > 0;
        holdHintRoot.gameObject.SetActive(charging);
        RefreshChargeDisplays();
        bool urgent = displayedSecond > 0 && displayedSecond <= 10;
        startHint.text = urgent ? "あと " + displayedSecond + "秒で この曲が はじまるよ" : "中央の曲に２秒かざすと スタート";
        startHint.color = urgent ? Gold : White;
        bool audible = !ScreenTransition.IsBusy && (Application.isFocused || Application.isBatchMode);
        if (urgent && lastBeepSecond != displayedSecond && audible)
        {
            lastBeepSecond = displayedSecond;
            MenuSfx.TimerTick(displayedSecond);
        }
        // 残り30秒の知らせは、31秒から30秒へ変わったときに1回だけ(判定調整から30秒以下で戻ったときは鳴らさない)。
        if (displayedSecond == 30 && previousSecond == 31 && audible) MenuSfx.TimerChime();
        previousSecond = displayedSecond;
        if (noticeSecond != displayedSecond)
        {
            noticeSecond = displayedSecond;
            timerNotice.text = displayedSecond <= 20 ? "あと " + displayedSecond + " 秒で自動スタート" : "この曲が自動で始まるまで";
            timerNotice.color = displayedSecond <= 20 ? Gold : White;
        }
    }
    public void TickCountdown(double deltaSeconds)
    {
        if (built && countdown.Tick(deltaSeconds, !ScreenTransition.IsBusy && !ctl.RushESecret.Transitioning
            && (Application.isFocused || Application.isBatchMode) && ctl.SongCount > 0 && ctl.CurrentDifficultyLevel() > 0)) ctl.StartGame();
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
            // スタートを決めた盤は 0.2 秒で 1.25 倍へ寄る(盤3)。
            float zoom = d.offset == 0 && startAge >= 0 ? 1 + .25f * Ease(Mathf.Clamp01(startAge / .2f)) : 1;
            d.root.localScale = Vector3.one * Mathf.LerpUnclamped(d.fromScale, d.toScale, t) * bounce * zoom;
            float brightness = d.offset == 0 ? 1 : d.target.Hovered && Mathf.Abs(d.offset) <= 2 ? .8f : Mathf.Abs(d.offset) == 1 ? .5f : .36f;
            // 割れたあとは元のジャケットを消し、4つの片だけを見せる。
            float art = d.offset == 0 && SongSelectStartCut.IsSplit(startAge) ? 0 : 1;
            d.art.color = d.art.Artwork != null ? new Color(brightness, brightness, brightness, art) : new Color(.07f * brightness, .10f * brightness, .20f * brightness, art);
            if (d.fallback != null) d.fallback.color = new Color(brightness, brightness, brightness, art);
            if (d.offset == 0)
            {
                Color accent = ctl.RushESecret.Unlocked ? RushESecretState.Purple : Cyan;
                float pulse = corridor != null ? corridor.CenterPulse : 0;
                d.rim.color = new Color(accent.r, accent.g, accent.b, .35f + .55f * pulse); d.rim.Width = 3 + 4 * pulse; d.rim.Refresh();
                float spread = 24 + 40 * pulse;
                d.halo.Width = spread; d.halo.rectTransform.sizeDelta = Vector2.one * (540 + spread * 2);
                d.halo.color = new Color(accent.r, accent.g, accent.b, .04f + .08f * pulse);
                d.progress.color = Gold; d.progress.Progress = startAge >= 0 ? 0 : d.target.Progress; d.progress.Refresh();
            }
            else if (Mathf.Abs(d.offset) <= 2)
            {
                // 隣と2曲先の盤も、ためている間は外周の弧が満ちる(盤2)。
                bool show = d.target.Progress > 0 && startAge < 0;
                if (d.progress.gameObject.activeSelf != show) d.progress.gameObject.SetActive(show);
                if (show) { d.progress.color = Cyan; d.progress.Progress = d.target.Progress; d.progress.Refresh(); }
            }
            if (animation >= 1 && Mathf.Abs(d.offset) > 2) d.root.gameObject.SetActive(false);
        }
    }
    // スタートを決めてからの秒で、中央の盤のジャケットを斬って割る(盤3)。幕は0.3秒後から閉じる。
    void AnimateStart(float seconds)
    {
        var d = discs.Find(x => x.offset == 0);
        if (d == null) return;
        if (startShards == null)
        {
            float radius = d.art.rectTransform.rect.width * .5f;
            Color tint = d.art.Artwork != null ? Color.white : new Color(.07f, .10f, .20f);
            startShards = SongSelectStartCut.Create(d.root, SongSelectStartCut.Layer.Shards, d.art.Artwork, tint, radius);
            startLights = SongSelectStartCut.Create(d.root, SongSelectStartCut.Layer.Lights, null, Color.white, radius);
        }
        startAge = Mathf.Max(0, seconds);
        startShards.SetAge(startAge); startLights.SetAge(startAge);
        holdHintRoot.gameObject.SetActive(false);
        AnimateDiscs();
    }
    static float Ease(float x)
    {
        float low = 0, high = 1, u = x;
        for (int i = 0; i < 12; i++) { u = (low + high) * .5f; float t = 3 * (1 - u) * (1 - u) * u * .3f + 3 * (1 - u) * u * u * .55f + u * u * u; if (t < x) low = u; else high = u; }
        return 3 * (1 - u) * (1 - u) * u * 1.35f + 3 * (1 - u) * u * u + u * u * u;
    }
    void OnDestroy()
    {
        if (ctl != null) { ctl.OnSelectionChanged -= SelectionChanged; ctl.OnDifficultyChanged -= DifficultyChanged; ctl.OnStartProgress -= AnimateStart; }
    }
    Sprite CoverSprite(int index) => ctl != null ? ctl.CoverSpriteAt(index) : null;
}
