using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

// 2026-10-30 ユーザー「④もすべてやる」: 実照準との独立、前景の遮蔽、説明の開閉と時計だけをPlayModeで確かめる。
public class SongSelectHelpPlayTests
{
    SongSelectController ctl;
    SongSelectSkin skin;
    SongSelectAimPointer aim;
    SongSelectIdleGuide guide;
    SongSelectGhostGuide ghost;
    SongSelectStaffGuide help;
    Keyboard keyboard;
    Mouse mouse;
    System.Func<bool> confirmRead;
    bool previousOperated;
    InputSettings originalInputSettings, testInputSettings;

    [UnitySetUp] public IEnumerator Open()
    {
        previousOperated = GameSession.SongSelectOperated;
        // 仮のキーボード・マウスの入力を、ウィンドウに焦点が無い CLI の実行でも受ける(SongSelectKeyboardPlayTests と同じ)。
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        testInputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testInputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        yield return SceneManager.LoadSceneAsync("SongSelect");
        float deadline = Time.realtimeSinceStartup + 15;
        while (Object.FindFirstObjectByType<SongSelectStaffGuide>() == null && Time.realtimeSinceStartup < deadline)
            yield return null;
        help = Object.FindFirstObjectByType<SongSelectStaffGuide>(); Assert.NotNull(help);
        skin = Object.FindFirstObjectByType<SongSelectSkin>();
        ctl = Object.FindFirstObjectByType<SongSelectController>();
        aim = Object.FindFirstObjectByType<SongSelectAimPointer>(); aim.enabled = false;
        guide = Object.FindFirstObjectByType<SongSelectIdleGuide>(); guide.enabled = false;
        // 止める前の1コマで入場カードが出ていると、出たまま固まってお手本が隠れる(実行ごとに結果が揺れた)。操作に数えずに隠す。
        guide.Tick(0, false);
        ghost = Object.FindFirstObjectByType<SongSelectGhostGuide>(); ghost.enabled = false;
        confirmRead = SongSelectGhostGuide.ConfirmOpen;
        yield return ScreenTransitionPlayTests.WaitForTransition();
        ctl.Select(Enumerable.Range(0, ctl.SongCount).Single(i => ctl.SongIdAt(i) == "Epilogue"));
        ctl.SetDifficulty(0);
        yield return new WaitForSecondsRealtime(.6f);
        skin.enabled = false;
        Canvas.ForceUpdateCanvases();
        aim.BeginArrival(); Hold(new Vector2(1, 1), 7);
    }

    [UnityTearDown] public IEnumerator Close()
    {
        SongSelectGhostGuide.ConfirmOpen = confirmRead;
        if (help != null) help.Close();
        if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
        if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("HelpCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        GameSession.SongSelectOperated = previousOperated;
        Assert.False(SongSelectSkin.PauseClock);
    }

    void Hold(Vector2 point, int frames)
    {
        for (int i = 0; i < frames; i++) aim.TickAt(point, .1f);
    }

    static List<RaycastResult> Hits(Vector2 point)
    {
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        return hits;
    }

    static void Click(Vector2 point)
    {
        var hits = Hits(point); Assert.IsNotEmpty(hits);
        ExecuteEvents.ExecuteHierarchy(hits[0].gameObject,
            new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left },
            ExecuteEvents.pointerClickHandler);
    }

    [UnityTest] public IEnumerator GhostDoesNotChangeRealAimChargeOrSelection()
    {
        var targets = ctl.GetComponentInParent<Canvas>().GetComponentsInChildren<SongSelectDiscTarget>(true);
        var progress = targets.Select(t => t.Progress).ToArray();
        int song = ctl.SelectedIndex, difficulty = ctl.SelectedDifficultyIndex, shots = aim.ShotCount;
        Vector2 reticle = aim.Reticle.anchoredPosition;
        bool release = aim.NeedsRelease;
        ghost.Tick(8);
        Assert.True(ghost.IsVisible);
        Assert.AreEqual(.45f, ghost.GetComponent<CanvasGroup>().alpha);
        Assert.False(ghost.GetComponent<CanvasGroup>().blocksRaycasts);
        foreach (var graphic in ghost.GetComponentsInChildren<Graphic>(true)) Assert.False(graphic.raycastTarget);
        for (int i = 0; i < 400; i++) ghost.Tick(.025f);
        Assert.AreEqual(song, ctl.SelectedIndex); Assert.AreEqual(difficulty, ctl.SelectedDifficultyIndex);
        Assert.AreEqual(shots, aim.ShotCount); Assert.AreEqual(reticle, aim.Reticle.anchoredPosition);
        Assert.AreEqual(release, aim.NeedsRelease); Assert.Zero(aim.Progress01);
        CollectionAssert.AreEqual(progress, targets.Select(t => t.Progress).ToArray());

        var normal = ctl.difficultyButtons[1].GetComponent<SongSelectDiscTarget>();
        Vector2 point = normal.ScreenRect().center;
        Assert.AreSame(normal, Hits(point)[0].gameObject.GetComponentInParent<SongSelectDiscTarget>());
        Hold(point, 5);
        float held = aim.Progress01, targetHeld = normal.Progress;
        Assert.Greater(held, 0);
        ghost.Tick(.1f);
        Assert.False(ghost.IsVisible);
        Assert.AreEqual(held, aim.Progress01); Assert.AreEqual(targetHeld, normal.Progress);
        Hold(point, 5);
        Assert.AreEqual(1, ghost.Shots);
        ghost.Tick(11.99f); Assert.False(ghost.IsVisible);
        ghost.Tick(.02f); Assert.True(ghost.IsVisible);
        SongSelectGhostGuide.ConfirmOpen = () => true;
        ghost.Tick(0); Assert.False(ghost.IsVisible);
        SongSelectGhostGuide.ConfirmOpen = confirmRead;
        ghost.Tick(0); Assert.True(ghost.IsVisible);
        ctl.difficultyButtons[2].onClick.Invoke();
        Assert.AreEqual(2, ghost.Shots);
        ghost.Tick(120); Assert.False(ghost.IsVisible);
        Assert.False(help.Opened);
        yield return null;
    }

    [UnityTest] public IEnumerator StaffHotkeysBlockTargetsPauseClockAndRequireReleaseAfterClosing()
    {
        keyboard = InputSystem.AddDevice<Keyboard>();
        mouse = InputSystem.AddDevice<Mouse>();
        GameSession.SongSelectOperated = false;
        InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(MouseButton.Middle));
        yield return null; yield return null;
        Assert.True(help.Opened); Assert.True(SongSelectSkin.PauseClock);
        double remaining = skin.RemainingSeconds;
        skin.TickCountdown(5); Assert.AreEqual(remaining, skin.RemainingSeconds);
        Assert.AreEqual(new Vector2(400, 160), ((RectTransform)help.CloseTarget.transform).sizeDelta);
        Assert.AreEqual(1, help.CloseTarget.HoldSeconds);

        // NORMAL の中心は説明の「とじる」の的(中心 960,860・400×160)と重なるので、重ならない HARD で後ろの的が遮られるかを見る。
        var hard = ctl.difficultyButtons[2].GetComponent<SongSelectDiscTarget>();
        Vector2 behind = hard.ScreenRect().center;
        int difficulty = ctl.SelectedDifficultyIndex, shots = aim.ShotCount;
        Assert.IsNull(Hits(behind)[0].gameObject.GetComponentInParent<SongSelectDiscTarget>());
        Click(behind); Hold(behind, 30);
        Assert.AreEqual(difficulty, ctl.SelectedDifficultyIndex);
        Assert.AreEqual(shots, aim.ShotCount); Assert.Zero(aim.Progress01);
        Assert.False(GameSession.SongSelectOperated);

        InputSystem.QueueStateEvent(mouse, new MouseState());
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.H));
        yield return null; yield return null;
        Assert.False(help.Opened); Assert.False(SongSelectSkin.PauseClock);
        skin.TickCountdown(.5);
        Assert.That(skin.RemainingSeconds, Is.EqualTo(remaining - .5).Within(.00001));
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;

        help.Open(); Canvas.ForceUpdateCanvases();
        Hold(new Vector2(1, 1), 7);
        Hold(help.CloseTarget.ScreenRect().center, 10);
        Assert.False(help.Opened); Assert.Zero(ghost.Shots);
        Assert.False(GameSession.SongSelectOperated);
        var center = ctl.startButton.GetComponent<SongSelectDiscTarget>().ScreenRect().center;
        aim.TickAt(center, .1f);
        Assert.True(aim.NeedsRelease);
        Hold(center, 25);
        Assert.Zero(aim.Progress01); Assert.False(SongSelectConfirm.IsOpen);
        Assert.False(ctl.StartAccepted);

        help.Open(); Canvas.ForceUpdateCanvases();
        Click(help.CloseTarget.ScreenRect().center);
        Assert.False(help.Opened); Assert.False(SongSelectSkin.PauseClock);
        yield return null;
    }

    [UnityTest] public IEnumerator GuideCardContainsTheThreeOperationsAtReadableSizes()
    {
        var texts = guide.GetComponentsInChildren<TextMeshProUGUI>(true).ToDictionary(t => t.name);
        Assert.AreEqual(27, texts["GuideEyebrow"].fontSize);
        Assert.AreEqual("あそびかた", texts["GuideEyebrow"].text);
        Assert.AreEqual(60, texts["GuideMessage"].fontSize);
        StringAssert.Contains("右手の赤い剣で", texts["GuideMessage"].text);
        Assert.AreEqual("となりの曲 … 1秒\nむずかしさ … 1秒\nまん中 … 2秒で かくにん", texts["GuideDetail"].text);
        Assert.AreEqual(29, texts["GuideDetail"].fontSize);
        Assert.AreEqual("操作すると案内は消えるよ", texts["GuideDismissHint"].text);
        foreach (var text in texts.Values) Assert.GreaterOrEqual(text.fontSize, 22);
        yield return null;
    }
}
