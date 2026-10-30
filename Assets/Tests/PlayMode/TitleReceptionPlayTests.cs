using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// タイトルの受付の鍵(2026-10-30 ユーザー「マウスの真ん中のやつのクリックで、タイトルノーツのカギを開け閉めできるようにして」
// 「①の1 2 5をやる」「タイトルノーツでかくして」)。運営モードではタイトルに入ると閉じ、開始ノーツを隠す。
public class TitleReceptionPlayTests
{
    Mouse mouse;
    InputSettings originalInputSettings, testInputSettings;

    [UnitySetUp] public IEnumerator Setup()
    {
        GameSession.TutorialPending = false; GameSession.IsCalibrationMode = false;
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        testInputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testInputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        mouse = InputSystem.AddDevice<Mouse>();
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(5, 5) });
        OperatorMode.OverrideForTest(true);
        yield return LoadTitle();
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        OperatorMode.OverrideForTest(false);
        TitleReception.ResetForTest();
        GameSession.TutorialPending = false;
        yield return ScreenTransitionPlayTests.WaitForTransition();
        if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        var empty = SceneManager.CreateScene("TitleReceptionCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    static IEnumerator LoadTitle()
    {
        yield return SceneManager.LoadSceneAsync("Title");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<TitleSceneSkin>() == null; i++) yield return null;
        yield return null; yield return null;
    }

    IEnumerator MiddleClick()
    {
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(5, 5) }.WithButton(MouseButton.Middle));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(5, 5) });
        yield return null; yield return null;
    }

    [UnityTest]
    public IEnumerator InOperatorModeTheStartNoteStaysHiddenUntilTheMiddleClick()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.NotNull(skin);
        Assert.IsTrue(TitleReception.Locked, "本番(運営モード)はタイトルに入ると閉じる");
        Assert.IsFalse(skin.StartNoteShown, "鍵の間は開始ノーツを隠す");
        Assert.IsNull(Object.FindFirstObjectByType<TitleStartNote>(), "隠したノーツは剣の判定の一覧にも入らない");
        skin.TryStartPresentation(); // Enter・Space・的のクリックと同じ入口
        yield return null; yield return null;
        Assert.IsNull(Object.FindFirstObjectByType<TitleTutorialPrompt>(), "鍵の間は Enter・クリックでも始まらない");
        Assert.IsTrue(skin.AtStart);

        yield return MiddleClick();
        Assert.IsFalse(TitleReception.Locked, "マウスの中ボタンで開く");
        Assert.IsTrue(skin.StartNoteShown, "開けるとノーツが出る");
        yield return MiddleClick();
        Assert.IsTrue(TitleReception.Locked, "もう一度押すと閉じる");
        Assert.IsFalse(skin.StartNoteShown);
        yield return MiddleClick();

        Assert.IsTrue(skin.TryStartPresentation());
        float deadline = Time.realtimeSinceStartup + 4f;
        TitleTutorialPrompt prompt = null;
        while (Time.realtimeSinceStartup < deadline && (prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>()) == null) yield return null;
        Assert.NotNull(prompt, "開けたあとは今までどおり問いかけへ");
        yield return MiddleClick();
        Assert.IsFalse(TitleReception.Locked, "切ったあとは中ボタンで鍵を変えない");
    }

    [UnityTest]
    public IEnumerator ComingBackToTheTitleLocksAgain()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        skin.ToggleReception("test");
        Assert.IsFalse(TitleReception.Locked);
        yield return LoadTitle();
        Assert.IsTrue(TitleReception.Locked, "タイトルに戻ると閉じる(1回の受付=1人)");
        Assert.IsFalse(Object.FindFirstObjectByType<TitleSceneSkin>().StartNoteShown);
    }

    [UnityTest]
    public IEnumerator OutsideOperatorModeTheTitleStartsOpen()
    {
        OperatorMode.OverrideForTest(false);
        yield return LoadTitle();
        Assert.IsFalse(TitleReception.Locked, "ふだん(開発・テスト)は開けて入る");
        Assert.IsTrue(Object.FindFirstObjectByType<TitleSceneSkin>().StartNoteShown);
    }
}
