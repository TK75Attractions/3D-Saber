using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 選曲の「この曲で はじめる？」(2026-10-30 ユーザー「③はすべて実装」)。中央の盤の2秒・クリック・Enter・Space と時間切れは、
// すぐ始めずにここを開く。右の「スタート！」に 1.5 秒(クリック・Enter・Space)で始め、左の「えらびなおす」に 1 秒(Esc・ハイフン)で選曲へ戻る。
// 的は手の位置(まん中)から左右に離し、開いた直後の 0.5 秒は受けない(照準は SongSelectAimPointer.BeginArrival で一度はずしてから)。
// 自分で開いたときは選曲の時計を止め、10 秒なにもなければ選曲へ戻る(始めない)。時間切れで開いたときは 8 秒後に始める。
// 戻ったときは時計を最低 15 秒にする(すぐ時間切れにしない)。
public sealed class SongSelectConfirm : MonoBehaviour
{
    public const float StartHoldSeconds = 1.5f, BackHoldSeconds = 1f;
    public const double InputDelaySeconds = .5, IdleCloseSeconds = 10, AutoStartSeconds = 8, MinimumSecondsAfterBack = 15;
    // 1920×1080 の左上原点。的は 400×240(Kinect の目安 208px 以上)、内側の縁はまん中(960)から 280px。
    public static readonly Vector2 BackCenter = new Vector2(480, 600), StartCenter = new Vector2(1440, 600), TargetSize = new Vector2(400, 240);
    public const string Question = "この曲で はじめる？";
    public const string Hint = "右の「スタート！」に 1.5秒 かざす ／ 左で えらびなおし";
    public static string Footer(bool automatic, int seconds) => automatic
        ? "あと " + seconds + " 秒で はじまるよ" : seconds + "秒 なにもしないと 選曲に もどるよ";

    static SongSelectConfirm instance;
    public static SongSelectConfirm Instance => instance;
    public static bool IsOpen => instance != null && instance.open;

    SongSelectController ctl;
    SongSelectSkin skin;
    SongSelectAimPointer aim;
    RectTransform root;
    TextMeshProUGUI songTitle, difficulty, footer;
    SongSelectRubyText songRuby;
    SongSelectDiscGraphic difficultyChip, jacket;
    RectTransform startBar, backBar;
    bool open, automatic;
    double age;
    int shownSeconds = -1;
    public bool Automatic => automatic;
    public double Age => age;
    public SongSelectDiscTarget StartTarget { get; private set; }
    public SongSelectDiscTarget BackTarget { get; private set; }
    public string SongTitleMarkup { get; private set; } = "";
    public string DifficultyText => difficulty != null ? difficulty.text : "";
    public string FooterText => footer != null ? footer.text : "";

    static Vector2 Position(float x, float y) => new Vector2(x - 960, 540 - y);

    public static SongSelectConfirm Build(SongSelectController ctl, SongSelectSkin skin, SongSelectAimPointer aim, Transform layout)
    {
        var rect = SongSelectVisuals.Rect(layout, "StartConfirm", Vector2.zero, new Vector2(1920, 1080));
        var confirm = rect.gameObject.AddComponent<SongSelectConfirm>();
        confirm.ctl = ctl; confirm.skin = skin; confirm.aim = aim; confirm.root = rect;
        confirm.BuildView();
        rect.SetAsLastSibling();
        rect.gameObject.SetActive(false);
        instance = confirm;
        return confirm;
    }

    void BuildView()
    {
        // 暗幕は後ろの盤・○・ボタンへの照準とクリックを止める(SongSelectAimPointer.Resolve は前景の UI で遮られる)。
        var dim = SongSelectSkin.Graphic(root, "Dim", Vector2.zero, new Vector2(1920, 1080), SongSelectDiscGraphic.Shape.Panel, new Color(0, 0, 0, .62f));
        dim.Width = 0; dim.Radius = 0; dim.raycastTarget = true;
        var panel = SongSelectSkin.Graphic(root, "Panel", Position(960, 480), new Vector2(1500, 600), SongSelectDiscGraphic.Shape.Panel, new Color(.059f, .102f, .173f, .97f));
        panel.Edge = SongSelectSkin.Cyan; panel.Width = 3; panel.Radius = 28; panel.Bottom = new Color(.025f, .045f, .085f, .97f);
        Label("Question", Question, 48, Position(960, 245), new Vector2(1200, 70), Color.white, true);
        songTitle = Label("Song", "", 60, Position(960, 330), new Vector2(1300, 84), Color.white, false);
        songTitle.font = UISkinKit.FontAsset("Oxanium-ExtraBold");
        songTitle.enableAutoSizing = true; songTitle.fontSizeMin = 30; songTitle.fontSizeMax = 60;
        songRuby = songTitle.gameObject.AddComponent<SongSelectRubyText>();
        difficultyChip = SongSelectSkin.Graphic(root, "DifficultyChip", Position(960, 405), new Vector2(300, 56), SongSelectDiscGraphic.Shape.Panel, SongSelectSkin.DifficultyColor(0));
        difficultyChip.Width = 0; difficultyChip.Radius = 28;
        difficulty = Label("Difficulty", "", 34, Position(960, 405), new Vector2(290, 52), SongSelectSkin.Ink, false);
        difficulty.font = UISkinKit.LogoFontAsset();
        jacket = SongSelectSkin.Graphic(root, "Jacket", Position(960, 600), new Vector2(240, 240), SongSelectDiscGraphic.Shape.Cover, Color.white);
        var ring = SongSelectSkin.Graphic(root, "JacketRing", Position(960, 600), new Vector2(252, 252), SongSelectDiscGraphic.Shape.Ring, SongSelectSkin.Cyan);
        ring.Width = 3;
        BackTarget = Target("Back", BackCenter, "えらびなおす", "1秒", new Color(.55f, .65f, .78f), new Color(.075f, .137f, .227f), 46, BackHoldSeconds, out backBar, () => Back("click"));
        StartTarget = Target("Start", StartCenter, "スタート！", "1.5秒", SongSelectSkin.Gold, new Color(.165f, .141f, .063f), 56, StartHoldSeconds, out startBar, () => Confirm("click"));
        // 照準でためきったときはクリックと分けて記録する。
        StartTarget.ShootOverride = () => { Confirm("aim"); return true; };
        BackTarget.ShootOverride = () => { Back("aim"); return true; };
        Label("Hint", Hint, 32, Position(960, 818), new Vector2(1500, 50), Color.white, true);
        footer = Label("Footer", "", 28, Position(960, 868), new Vector2(1200, 44), new Color(.62f, .74f, .84f), false);
        Label("StaffHint", "スタッフ: クリック・Enter でも決められます(Esc で えらびなおし)", 22, Position(1500, 1046), new Vector2(820, 36), new Color(.49f, .6f, .7f), false);
    }

    TextMeshProUGUI Label(string name, string text, float size, Vector2 position, Vector2 dimensions, Color color, bool bold)
    {
        var t = UISkinKit.MakeTMP(root, name, text, size, color, TextAlignmentOptions.Center, position, dimensions,
            bold ? FontStyles.Bold : FontStyles.Normal, 0, UISkinKit.JapaneseFallbackFontAsset());
        t.raycastTarget = false; t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Overflow;
        return t;
    }

    SongSelectDiscTarget Target(string name, Vector2 center, string text, string seconds, Color edge, Color fill, float size,
        float holdSeconds, out RectTransform bar, UnityEngine.Events.UnityAction onClick)
    {
        var face = SongSelectSkin.Graphic(root, name, Position(center.x, center.y), TargetSize, SongSelectDiscGraphic.Shape.Panel, fill);
        face.Edge = edge; face.Width = 5; face.Radius = 24;
        var button = face.gameObject.AddComponent<Button>();
        button.targetGraphic = face; face.raycastTarget = true; button.transition = Selectable.Transition.None;
        button.onClick.AddListener(onClick);
        var target = SongSelectDiscTarget.Attach(button, holdSeconds, false);
        var label = UISkinKit.MakeTMP(face.transform, name + "Label", text, size, edge, TextAlignmentOptions.Center, new Vector2(0, 12),
            new Vector2(380, 90), FontStyles.Bold, 0, UISkinKit.JapaneseFallbackFontAsset());
        label.raycastTarget = false;
        var sub = UISkinKit.MakeTMP(face.transform, name + "Seconds", seconds, 26, new Color(.62f, .74f, .84f), TextAlignmentOptions.Center,
            new Vector2(0, -52), new Vector2(380, 36), FontStyles.Normal, 0, UISkinKit.JapaneseFallbackFontAsset());
        sub.raycastTarget = false;
        // ためている間、下の線が左から満ちる(選曲のボタンと同じ見せ方)。
        float length = TargetSize.x - 48;
        var fillBar = SongSelectSkin.Graphic(face.transform, name + "Charge", Vector2.zero, new Vector2(length, 8), SongSelectDiscGraphic.Shape.Panel, edge);
        fillBar.Width = 0; fillBar.Radius = 4;
        bar = fillBar.rectTransform; bar.pivot = new Vector2(0, .5f); bar.anchoredPosition = new Vector2(-length * .5f, -TargetSize.y * .5f + 16);
        bar.sizeDelta = new Vector2(0, 8);
        return target;
    }

    // 開く。始められないとき(画面遷移中・裏譜面の切り替え中・譜面が無い)は開かず false。
    public bool Open(bool automaticStart, string how)
    {
        if (open || ctl == null || ScreenTransition.IsBusy || ctl.RushESecret.Transitioning || !ctl.CanStartSelected) return open;
        open = true; automatic = automaticStart; age = 0;
        shownSeconds = (int)(automatic ? AutoStartSeconds : IdleCloseSeconds);
        footer.text = Footer(automatic, shownSeconds);
        SetBar(startBar, null); SetBar(backBar, null);
        Refresh();
        root.gameObject.SetActive(true);
        root.SetAsLastSibling();
        SetInteractable(false);
        // 前にクリックした難易度・判定調整のボタンが選ばれたまま残っていると、確認での Enter がそのボタンを押してしまう。選びを外す。
        var events = EventSystem.current;
        if (events != null) events.SetSelectedGameObject(null);
        if (aim != null) aim.BeginArrival();
        if (!automatic) MenuSfx.Fire();
        Record("open", how);
        return true;
    }

    // 「スタート！」。曲を始める(始められなければ選曲へ戻す)。
    public void Confirm(string how)
    {
        if (!open || age < InputDelaySeconds && how != "auto") return;
        bool wasAutomatic = automatic;
        Close();
        Record("start", how);
        ctl.StartConfirmed(wasAutomatic);
        if (!ctl.StartAccepted && skin != null) skin.EnsureRemainingSeconds(MinimumSecondsAfterBack);
    }

    // 「えらびなおす」(と、なにもしないで閉じたとき)。始めずに選曲へ戻し、時計を最低15秒にする。
    public void Back(string how)
    {
        if (!open || age < InputDelaySeconds && how != "timeout") return;
        Close();
        Record(how == "timeout" ? "timeout" : "back", how);
        MenuSfx.Difficulty(0);
        if (skin != null) skin.EnsureRemainingSeconds(MinimumSecondsAfterBack);
    }

    void Close()
    {
        open = false;
        root.gameObject.SetActive(false);
        // まん中の盤に乗ったままの照準は、一度はずしてからでないとためない。
        if (aim != null) aim.BeginArrival();
    }

    void Refresh()
    {
        string id = ctl.SongIdAt(ctl.SelectedIndex);
        string title = ResultSkin.SongIdToDisplayTitle(id);
        var info = SongDisplayInfo.Get(id);
        SongTitleMarkup = string.IsNullOrEmpty(info.Reading) ? title : SongSelectSkin.TitleRubyMarkup(title, info.Reading);
        songRuby.Set(SongTitleMarkup);
        var secret = ctl.UnlockedSecret;
        int index = ctl.SelectedDifficultyIndex;
        int level = ctl.DifficultyDisplayLevelAt(index);
        string name = secret != null ? secret.Difficulty : SongSelectSkin.DifficultyDisplayName(index, ctl.CurrentDifficultyName);
        difficulty.text = secret != null || level <= 0 ? name : name + "  LV " + level;
        difficultyChip.color = secret != null ? secret.Accent : SongSelectSkin.DifficultyColor(index);
        jacket.Artwork = ctl.CoverSpriteAt(ctl.SelectedIndex);
        jacket.color = jacket.Artwork != null ? Color.white : new Color(.07f, .10f, .20f);
        jacket.Refresh();
    }

    void SetInteractable(bool value)
    {
        if (StartTarget != null) StartTarget.Button.interactable = value;
        if (BackTarget != null) BackTarget.Button.interactable = value;
    }

    void Update()
    {
        if (!open) return;
        // スタッフ用の説明を重ねている間は、確認の時間もキーも止める。
        if (SongSelectStaffGuide.IsOpen || SongSelectStaffGuide.ChangedThisFrame) return;
        Tick(Time.unscaledDeltaTime);
        var kb = Keyboard.current;
        if (kb == null || age < InputDelaySeconds || SongSelectController.OperatorChordHeld(kb)) return;
        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame) Confirm("key");
        else if (InputManager.BackKeyPressedThisFrame(kb)) Back("key");
    }

    // 時間を進める(実入力とテストの共通の入口)。0.5 秒たったら的を受け付け、自分で開いたら 10 秒で閉じ、時間切れなら 8 秒で始める。
    public void Tick(double dt)
    {
        if (!open || ScreenTransition.IsBusy) return;
        if (!double.IsNaN(dt) && dt > 0 && dt < 30) age += dt;
        if (age >= InputDelaySeconds && StartTarget != null && !StartTarget.Button.interactable) SetInteractable(true);
        double limit = automatic ? AutoStartSeconds : IdleCloseSeconds;
        int seconds = Mathf.Max(0, Mathf.CeilToInt((float)(limit - age)));
        if (seconds != shownSeconds) { shownSeconds = seconds; footer.text = Footer(automatic, seconds); }
        SetBar(startBar, StartTarget); SetBar(backBar, BackTarget);
        if (age < limit) return;
        if (automatic) Confirm("auto"); else Back("timeout");
    }

    static void SetBar(RectTransform bar, SongSelectDiscTarget target)
    {
        if (bar == null) return;
        bar.sizeDelta = new Vector2((TargetSize.x - 48) * (target != null ? Mathf.Clamp01(target.Progress) : 0), bar.sizeDelta.y);
    }

    void Record(string kind, string how)
    {
        string id = ctl != null ? ctl.SongIdAt(ctl.SelectedIndex) : "";
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "select-confirm",
            kind + " how=" + how + " song=" + id + " diff=" + (ctl != null ? ctl.CurrentDifficultyName : "") + " auto=" + (automatic ? 1 : 0));
    }

    void OnDisable() { open = false; }

    void OnDestroy() { if (instance == this) instance = null; }
}
