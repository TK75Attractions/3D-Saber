using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実シーンで暗転の最低時間・文字・終了時の入力復帰を検証する。
public sealed class TransitionTipsPlayTests
{
    readonly Dictionary<FieldInfo, object> savedSession = new Dictionary<FieldInfo, object>();
    bool savedEnabled;
    float savedTimeScale;

    [SetUp]
    public void SaveState()
    {
        savedEnabled = TransitionTips.Enabled;
        savedTimeScale = Time.timeScale;
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) savedSession[field] = field.GetValue(null);

        TransitionTips.Enabled = true;
        TransitionTips.Shared.ClearSeen();
        Time.timeScale = 1;
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.TutorialRangeOnly = false;
    }

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        try
        {
            Time.timeScale = 1;
            yield return ScreenTransitionPlayTests.WaitForTransition();
            var transition = Object.FindFirstObjectByType<ScreenTransition>(FindObjectsInactive.Include);
            if (transition != null) Object.Destroy(transition.gameObject);
            yield return null;

            var scene = SceneManager.GetActiveScene();
            var empty = SceneManager.CreateScene("TransitionTipsCleanup_" + Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(empty);
            if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
        finally
        {
            var transition = Object.FindFirstObjectByType<ScreenTransition>(FindObjectsInactive.Include);
            if (transition != null) Object.Destroy(transition.gameObject);
            TransitionTips.Shared.ClearSeen();
            TransitionTips.Enabled = savedEnabled;
            Time.timeScale = savedTimeScale;
            foreach (var entry in savedSession) entry.Key.SetValue(null, entry.Value);
            savedSession.Clear();
        }
    }

    static IEnumerator OpenTitle()
    {
        yield return SceneManager.LoadSceneAsync("Title");
        yield return null;
        yield return null;
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.autonomous = false;
    }

    [UnityTest]
    public IEnumerator TitleToSelectionShowsTipHoldsAndRestoresInput()
    {
        yield return OpenTitle();
        yield return MeasureTransition("SongSelect", true);
    }

    [UnityTest]
    public IEnumerator SameSceneReloadHasNeitherTipNorAdditionalHold()
    {
        yield return OpenTitle();
        yield return MeasureTransition("Title", false);
    }

    [UnityTest]
    public IEnumerator PausedClockStillShowsTipAndFinishes()
    {
        yield return OpenTitle();
        Time.timeScale = 0;
        yield return MeasureTransition("SongSelect", true);
        Assert.AreEqual(0, Time.timeScale, "ゲーム側の一時停止設定は変更しない");
    }

    static IEnumerator MeasureTransition(string destination, bool expectTip)
    {
        double loadedAt = -1, closedAt = -1, openedAt = -1;
        double maxFrame = 0;
        string shown = null;
        float requiredHold = 0;
        bool capturedClosing = false;
        UnityAction<Scene, LoadSceneMode> onLoaded = (scene, mode) =>
        {
            if (scene.name == destination) loadedAt = Time.realtimeSinceStartupAsDouble;
        };
        SceneManager.sceneLoaded += onLoaded;
        try
        {
            var input = EventSystem.current;
            Assert.NotNull(input);
            Assert.True(ScreenTransition.Load(destination));
            Assert.False(input.enabled);
            Assert.IsNull(ScreenTransition.CurrentTip, "Load の呼び出し元が Session を書くまで抽選しない");
            Assert.False(ScreenTransition.Load("Result"), "tips の待機中も二重遷移を受け付けない");

            var transition = Object.FindFirstObjectByType<ScreenTransition>(FindObjectsInactive.Include);
            var curtain = transition.GetComponentInChildren<ScreenTransitionGraphic>(true);
            Assert.NotNull(curtain);
            double previousFrame = Time.realtimeSinceStartupAsDouble;
            double deadline = previousFrame + 20;

            while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < deadline)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                maxFrame = Math.Max(maxFrame, now - previousFrame);
                previousFrame = now;

                if (!expectTip)
                {
                    Assert.IsNull(ScreenTransition.CurrentTip);
                    Assert.AreEqual(0f, ScreenTransition.CurrentTipAlpha);
                    var absent = transition.transform.Find("TransitionTip");
                    Assert.True(absent == null || !absent.gameObject.activeInHierarchy);
                }

                // 見た目の確認用(-transitionCapture <dir> のときだけ撮る): 閉じる途中と真っ暗の間。
                if (expectTip && !capturedClosing && closedAt < 0 && curtain.Progress >= .65f && curtain.Progress < 1f)
                {
                    capturedClosing = true;
                    ScreenTransitionPlayTests.Capture("tip-closing-" + destination);
                }
                if (curtain.Progress >= 1f)
                {
                    if (closedAt < 0) closedAt = now;
                    if (expectTip)
                    {
                        Assert.IsNotEmpty(ScreenTransition.CurrentTip);
                        Assert.GreaterOrEqual(ScreenTransition.CurrentTipAlpha, .99f);
                        if (shown == null)
                        {
                            shown = ScreenTransition.CurrentTip;
                            requiredHold = TransitionTips.HoldSeconds(shown);
                            ScreenTransitionPlayTests.Capture("tip-dark-" + destination);
                        }
                        Assert.AreEqual(shown, ScreenTransition.CurrentTip);
                        var tip = transition.transform.Find("TransitionTip");
                        Assert.NotNull(tip);
                        Assert.True(tip.gameObject.activeInHierarchy);
                        foreach (var graphic in tip.GetComponentsInChildren<Graphic>(true))
                            Assert.False(graphic.raycastTarget);
                    }
                }
                else if (closedAt >= 0 && openedAt < 0)
                {
                    openedAt = now;
                }
                yield return null;
            }

            Assert.False(ScreenTransition.IsBusy, "20秒以内に暗転と入力ロックが終わる");
            Assert.GreaterOrEqual(closedAt, 0);
            Assert.GreaterOrEqual(openedAt, closedAt);
            Assert.GreaterOrEqual(loadedAt, 0);
            double darkSeconds = openedAt - closedAt;
            double loadingSeconds = Math.Max(0, loadedAt - closedAt);
            // 観測するコルーチンの順番と、読込中の長いフレームを許容する。
            double lowerTolerance = .04 + maxFrame;
            double upperTolerance = .10 + 6 * maxFrame;
            if (expectTip)
                Assert.GreaterOrEqual(darkSeconds + lowerTolerance, requiredHold,
                    "閉じきってから本文を読む最低時間を確保する");
            Assert.LessOrEqual(darkSeconds, Math.Max(requiredHold, loadingSeconds) + upperTolerance,
                "読込時間に最低時間を丸ごと足さず、同じシーンには待ち足しもしない");

            Assert.AreEqual(destination, SceneManager.GetActiveScene().name);
            Assert.IsNull(ScreenTransition.CurrentTip);
            Assert.AreEqual(0f, ScreenTransition.CurrentTipAlpha);
            Assert.False(curtain.gameObject.activeInHierarchy);
            Assert.False(transition.gameObject.activeSelf);
            var hiddenTip = transition.transform.Find("TransitionTip");
            if (expectTip) Assert.NotNull(hiddenTip);
            if (hiddenTip != null)
            {
                Assert.False(hiddenTip.gameObject.activeSelf);
                Assert.AreEqual(0f, hiddenTip.GetComponent<CanvasGroup>().alpha);
            }
            Assert.NotNull(EventSystem.current);
            Assert.True(EventSystem.current.enabled);
        }
        finally
        {
            SceneManager.sceneLoaded -= onLoaded;
        }
    }
}
