using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

// 最初の10秒間操作がないときだけ、右手の使い方を選曲UI上に重ねる。
[DefaultExecutionOrder(200)]
public sealed class SongSelectIdleGuide : MonoBehaviour
{
    readonly SongSelectIdleState state = new SongSelectIdleState();
    SongSelectController controller;
    RectTransform card;
    CanvasGroup group;
    SongSelectGuideModel model;
    float age, frameAge;
    public bool IsVisible => card != null && card.gameObject.activeSelf;
    public bool Completed => state.Completed;

    public static SongSelectIdleGuide Build(SongSelectController controller, Transform layout)
    {
        var root = SongSelectVisuals.Rect(layout, "SongSelectIdleGuide", Vector2.zero, Vector2.zero);
        var guide = root.gameObject.AddComponent<SongSelectIdleGuide>();
        guide.controller = controller;
        controller.OnSelectionChanged += guide.SelectionChanged;
        controller.OnDifficultyChanged += guide.SelectionChanged;
        guide.BuildCard();
        return guide;
    }

    void BuildCard()
    {
        card = SongSelectVisuals.Rect(transform, "RightHandGuideCard", new Vector2(0, 25), new Vector2(1160, 540));
        group = card.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false; group.interactable = false;
        var shadow = SongSelectSkin.Graphic(card, "Shadow", new Vector2(0, -10), new Vector2(1182, 560), SongSelectDiscGraphic.Shape.Panel, new Color(0, 0, 0, .65f));
        shadow.Width = 0; shadow.Radius = 32;
        var panel = SongSelectSkin.Graphic(card, "Panel", Vector2.zero, card.sizeDelta, SongSelectDiscGraphic.Shape.Panel, new Color(.035f, .068f, .115f, .985f));
        panel.Bottom = new Color(.015f, .03f, .06f, .985f); panel.Edge = new Color(.3f, .79f, .84f, .8f); panel.Width = 2; panel.Radius = 28;
        var stage = SongSelectSkin.Graphic(card, "ModelStage", new Vector2(-337, -12), new Vector2(420, 454), SongSelectDiscGraphic.Shape.Panel, new Color(.065f, .12f, .17f));
        stage.Bottom = new Color(.025f, .05f, .09f); stage.Width = 0; stage.Radius = 20;
        model = SongSelectVisuals.Rect(card, "RightHandDemonstration", new Vector2(-340, -12), new Vector2(420, 440)).gameObject.AddComponent<SongSelectGuideModel>();
        model.raycastTarget = false;
        Text("GuideEyebrow", "あそびかた", 27, new Vector2(210, 182), new Vector2(610, 40), SongSelectSkin.Cyan);
        Text("GuideMessage", "<color=#FF7387>右手</color>で照準を\n合わせてね！", 60, new Vector2(210, 64), new Vector2(620, 176), Color.white);
        Text("GuideDetail", "赤いセイバーをかざして\nえらびたいところをねらおう", 29, new Vector2(210, -102), new Vector2(610, 90), new Color(.72f, .83f, .9f));
        Text("GuideDismissHint", "操作すると案内は消えるよ", 23, new Vector2(210, -204), new Vector2(610, 38), new Color(.49f, .66f, .73f));
        Text("RightHandLabel", "右手", 25, new Vector2(-210, -213), new Vector2(100, 38), new Color(1, .45f, .54f));
        group.alpha = 0; card.gameObject.SetActive(false);
    }

    void Text(string name, string content, float size, Vector2 position, Vector2 dimensions, Color color)
    {
        var text = UISkinKit.MakeTMP(card, name, content, size, color, TextAlignmentOptions.MidlineLeft,
            position, dimensions, FontStyles.Bold, 0, UISkinKit.JapaneseFallbackFontAsset());
        text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.NoWrap;
    }

    void Update()
    {
        bool active = (Application.isFocused || Application.isBatchMode) && !ScreenTransition.IsBusy;
        if (active && !state.Completed && HasButtonInput()) RegisterActivity();
        Tick(Time.unscaledDeltaTime, active);
    }

    static bool HasButtonInput()
    {
        if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) return true;
        var mouse = Mouse.current;
        if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame
            || mouse.middleButton.wasPressedThisFrame || mouse.scroll.ReadValue().sqrMagnitude > .01f)) return true;
        var pad = Gamepad.current;
        if (pad == null) return false;
        if (pad.leftStick.ReadValue().sqrMagnitude > .16f || pad.rightStick.ReadValue().sqrMagnitude > .16f) return true;
        foreach (var control in pad.allControls)
            if (control is ButtonControl button && button.wasPressedThisFrame) return true;
        return false;
    }

    public void ObservePointer(Vector2 point, bool valid, bool tracked)
    {
        state.ObservePointer(point / Mathf.Max(1, Mathf.Min(Screen.width, Screen.height)), tracked ? 1 : 2, valid);
        if (state.Completed) Hide();
    }
    public void RegisterActivity() { state.RegisterActivity(); Hide(); }
    void SelectionChanged(int index) { RegisterActivity(); }

    // 検証・撮影も同じ時間更新を使う。音や選曲の制限時間には触れない。
    public void Tick(float seconds, bool active)
    {
        state.Tick(seconds, active);
        if (!active || !state.ShouldShow) { Hide(); return; }
        if (!IsVisible) { card.gameObject.SetActive(true); age = frameAge = 0; }
        float dt = float.IsNaN(seconds) || float.IsInfinity(seconds) ? 0 : Mathf.Max(0, seconds);
        age += dt; frameAge += dt;
        group.alpha = Mathf.Clamp01(age / .24f);
        if (frameAge >= 1f / 30f) { model.SetPose(age); frameAge = 0; }
    }
    void Hide() { if (card != null) card.gameObject.SetActive(false); }
    void OnDisable() { Hide(); }
    void OnDestroy()
    {
        if (controller == null) return;
        controller.OnSelectionChanged -= SelectionChanged;
        controller.OnDifficultyChanged -= SelectionChanged;
    }
}
