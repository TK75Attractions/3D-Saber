using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// タイトルの開始ノーツを切ると、問いかけを出さずに全員 Game シーンの試し切り(練習)へ進む
// (2026-11-01 ユーザー「チュートリアルさ、やっぱり強制にする(はじめての方ははいの画面をなくす)」。前は「はい」「いいえ」を切って選んだ)。
public class TitlePromptPlayTests
{
    [UnitySetUp] public IEnumerator Setup()
    {
        GameSession.TutorialPending = false; GameSession.TutorialRangeOnly = false; GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("Title");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<TitleSceneSkin>() == null; i++) yield return null;
        yield return null; yield return null;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        GameSession.TutorialPending = false; GameSession.TutorialRangeOnly = false;
        var empty = SceneManager.CreateScene("TitlePromptCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    [UnityTest] public IEnumerator SlashingStartGoesStraightToThePracticeWithoutAPrompt()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.NotNull(skin); Assert.IsTrue(skin.AtStart);
        Assert.IsTrue(skin.TryStartPresentation(), "開始ノーツが切れる");
        float deadline = Time.realtimeSinceStartup + 2f;
        while (Time.realtimeSinceStartup < deadline && !ScreenTransition.IsBusy) yield return null;
        Assert.IsTrue(ScreenTransition.IsBusy, "切ったらそのまま前進の幕で練習へ");
        Assert.IsTrue(GameSession.TutorialPending, "全員が練習を通る");
        Assert.IsFalse(GameSession.TutorialRangeOnly, "範囲だけの回は無い");
        Assert.IsFalse(skin.AtStart);
        Assert.IsFalse(skin.ReturnToStart(), "幕の間は戻さない");
        deadline = Time.realtimeSinceStartup + 12f;
        while (Time.realtimeSinceStartup < deadline && (SceneManager.GetActiveScene().name != "Game" || ScreenTransition.IsBusy))
        {
            Assert.IsNull(Object.FindFirstObjectByType<TitleTutorialPrompt>(), "問いかけは出さない");
            yield return null;
        }
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        for (int i = 0; i < 120 && manager.Tutorial == null; i++) yield return null;
        Assert.NotNull(manager.Tutorial, "Game シーンは練習で始まる");
        Assert.IsFalse(manager.Tutorial.RangeOnly);
        Assert.IsFalse(GameSession.TutorialPending, "フラグは読んだら消す");
    }

    // 版の表示(仕様面の点検 14)は、どの画面でも右下に1つだけ。シーンを移っても増えない。
    [UnityTest] public IEnumerator VersionLabelStaysOnceAcrossScenes()
    {
        foreach (string scene in new[] { "SongSelect", "Result", "Title" })
        {
            yield return SceneManager.LoadSceneAsync(scene);
            yield return null;
            var labels = Object.FindObjectsByType<GameVersionLabel>(FindObjectsSortMode.None);
            Assert.AreEqual(1, labels.Length, scene);
            Assert.AreEqual(GameVersion.Label, labels[0].Text.text, scene);
            Assert.True(labels[0].Text.isActiveAndEnabled, scene);
        }
    }
}
