using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 2026-10-30 ユーザー「④もすべてやる」: 開く操作は中クリック・Hだけ。閉じる的は来場者の照準でも使える。
[DefaultExecutionOrder(-1200)]
public sealed class SongSelectStaffGuide : MonoBehaviour
{
    static SongSelectStaffGuide instance;
    static int changedFrame = -1;
    RectTransform veil, panel;
    SongSelectAimPointer aim;

    public static bool IsOpen => instance != null && instance.Opened;
    public static bool ChangedThisFrame => changedFrame == Time.frameCount;
    public bool Opened => veil != null && veil.gameObject.activeSelf;
    public SongSelectDiscTarget CloseTarget { get; private set; }

    public static SongSelectStaffGuide Build(Canvas canvas, SongSelectAimPointer aim)
    {
        var root = SongSelectVisuals.Rect(canvas.transform, "SongSelectStaffGuide", Vector2.zero, Vector2.zero);
        SongSelectVisuals.Stretch(root);
        var front = root.gameObject.AddComponent<Canvas>();
        front.overrideSorting = true; front.sortingOrder = 800;
        root.gameObject.AddComponent<GraphicRaycaster>();
        var help = root.gameObject.AddComponent<SongSelectStaffGuide>();
        help.aim = aim;
        help.BuildView();
        instance = help; changedFrame = -1;
        return help;
    }

    void BuildView()
    {
        veil = SongSelectVisuals.Rect(transform, "HelpVeil", Vector2.zero, Vector2.zero);
        SongSelectVisuals.Stretch(veil);
        var shade = veil.gameObject.AddComponent<Image>();
        shade.color = new Color(0, 0, 0, .76f); shade.raycastTarget = true;

        var face = SongSelectSkin.Graphic(veil, "HelpPanel", Vector2.zero, new Vector2(1640, 900),
            SongSelectDiscGraphic.Shape.Panel, new Color(.059f, .102f, .173f, .99f));
        face.Edge = SongSelectSkin.Cyan; face.Width = 3; face.Radius = 28;
        face.Bottom = new Color(.025f, .045f, .085f, .99f);
        panel = face.rectTransform;
        Text(panel, "HelpHeading", "選曲画面の あそびかた", 44,
            new Vector2(0, 375), new Vector2(1500, 64), Color.white);

        Ring("LeftDisc", -650, 110, 140, SongSelectSkin.Cyan);
        Ring("CenterDisc", -470, 110, 220, SongSelectSkin.Gold);
        Ring("RightDisc", -290, 110, 140, SongSelectSkin.Cyan);
        for (int i = 0; i < 3; i++) Ring("Difficulty" + i, -580 + 110 * i, -100, 70, SongSelectSkin.DifficultyColor(i));
        Line("DiscLine", new Vector2(-470, -25), new Vector2(500, 3));
        Line("TitleLine", new Vector2(-470, -190), new Vector2(400, 3));
        Badge("SongNumber", "①", -750, 110);
        Badge("DifficultyNumber", "②", -690, -100);
        Badge("CenterNumber", "③", -470, 255);

        Text(panel, "HelpSongs", "① 左右の盤 = 曲をかえる（1秒）", 30,
            new Vector2(320, 190), new Vector2(880, 110), Color.white, TextAlignmentOptions.MidlineLeft);
        Text(panel, "HelpDifficulty", "② 下の○ = むずかしさ（1秒）", 30,
            new Vector2(320, 50), new Vector2(880, 110), Color.white, TextAlignmentOptions.MidlineLeft);
        Text(panel, "HelpStart", "③ まん中 = 2秒で「この曲で はじめる？」\n    → 右の「スタート！」に 1.5秒", 30,
            new Vector2(320, -110), new Vector2(880, 180), Color.white, TextAlignmentOptions.MidlineLeft);

        var close = SongSelectSkin.Graphic(panel, "HelpClose", new Vector2(0, -320), new Vector2(400, 160),
            SongSelectDiscGraphic.Shape.Panel, SongSelectVisuals.Raised);
        close.Edge = SongSelectSkin.Cyan; close.Width = 4; close.Radius = 24; close.raycastTarget = true;
        var button = close.gameObject.AddComponent<Button>();
        button.targetGraphic = close; button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        CloseTarget = SongSelectDiscTarget.Attach(button, 1, false);
        button.onClick.AddListener(Close);
        Text(close.transform, "CloseLabel", "とじる", 36, new Vector2(0, 22), new Vector2(370, 56), Color.white);
        Text(close.transform, "CloseSeconds", "1秒かざす / クリック", 24, new Vector2(0, -35), new Vector2(370, 42), SongSelectVisuals.Muted);
        Text(panel, "HelpStaffHint", "スタッフ: 中クリック / H でも閉じる", 24,
            new Vector2(0, -425), new Vector2(1300, 32), SongSelectVisuals.Muted);
        veil.gameObject.SetActive(false);
    }

    void Ring(string name, float x, float y, float diameter, Color color)
    {
        var ring = SongSelectSkin.Graphic(panel, name, new Vector2(x, y), Vector2.one * diameter,
            SongSelectDiscGraphic.Shape.Ring, color);
        ring.Width = 4;
    }

    void Line(string name, Vector2 point, Vector2 size)
    {
        var line = SongSelectSkin.Graphic(panel, name, point, size, SongSelectDiscGraphic.Shape.Panel, SongSelectVisuals.Edge);
        line.Width = 0;
    }

    void Badge(string name, string value, float x, float y)
    {
        Text(panel, name, value, 36, new Vector2(x, y), new Vector2(62, 62), SongSelectSkin.Gold);
    }

    static void Text(Transform parent, string name, string value, float size, Vector2 point, Vector2 dimensions,
        Color color, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        var text = UISkinKit.MakeTMP(parent, name, value, size, color, alignment, point, dimensions,
            FontStyles.Bold, 0, UISkinKit.JapaneseFallbackFontAsset());
        text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.NoWrap;
    }

    void Update()
    {
        if (Opened && (ScreenTransition.IsBusy || SongSelectGhostGuide.ConfirmationOpen)) { Close(); return; }
        if (!Application.isFocused && !Application.isBatchMode) return;
        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        bool pressed = mouse != null && mouse.middleButton.wasPressedThisFrame
            || keyboard != null && !SongSelectController.OperatorChordHeld(keyboard) && keyboard.hKey.wasPressedThisFrame;
        if (!pressed) return;
        if (Opened) Close(); else Open();
    }

    public void Open()
    {
        if (Opened || !isActiveAndEnabled || ScreenTransition.IsBusy || SongSelectGhostGuide.ConfirmationOpen) return;
        veil.gameObject.SetActive(true);
        changedFrame = Time.frameCount;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        if (aim != null) aim.BeginArrival();
    }

    public void Close()
    {
        if (!Opened) return;
        veil.gameObject.SetActive(false);
        changedFrame = Time.frameCount;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        if (aim != null) aim.BeginArrival();
    }

    void OnDisable() { Close(); }
    void OnDestroy() { if (instance == this) instance = null; }
}
