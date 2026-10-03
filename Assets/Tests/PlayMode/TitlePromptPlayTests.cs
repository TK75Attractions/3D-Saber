using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// タイトルの開始ノーツを切ると幕の中で問いかけが出て、「はい」で Game シーンの練習へ、「いいえ」で選曲へ進む。
public class TitlePromptPlayTests
{
    [UnitySetUp] public IEnumerator Setup()
    {
        GameSession.TutorialPending = false; GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("Title");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<TitleSceneSkin>() == null; i++) yield return null;
        yield return null; yield return null;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        GameSession.TutorialPending = false;
        var empty = SceneManager.CreateScene("TitlePromptCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    IEnumerator OpenPrompt()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.NotNull(skin);
        Assert.IsTrue(skin.TryStartPresentation(), "開始ノーツが切れる");
        float deadline = Time.realtimeSinceStartup + 4f;
        TitleTutorialPrompt prompt = null;
        while (Time.realtimeSinceStartup < deadline && ((prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>()) == null || !prompt.IsOpen)) yield return null;
        Assert.NotNull(prompt, "問いかけが作られる");
        Assert.IsTrue(prompt.IsOpen, "暗転の後に問いかけが開く");
        Assert.IsFalse(ScreenTransition.IsBusy, "答えるまで幕は次のシーンへ進まない");
    }

    [UnityTest] public IEnumerator SlashingStartOpensThePromptWithYesOnTheLeftAndNoOnTheRight()
    {
        yield return OpenPrompt();
        var prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>();
        Assert.NotNull(prompt.YesNote); Assert.NotNull(prompt.NoNote);
        Assert.Less(prompt.YesNote.transform.position.x, 0f); Assert.Greater(prompt.NoNote.transform.position.x, 0f);
        Assert.IsTrue(prompt.YesNote.Note.IsJudgeable && prompt.NoNote.Note.IsJudgeable, "どちらも切れる");
        Assert.IsFalse(prompt.Answered); Assert.IsFalse(GameSession.TutorialPending);
        Assert.That(prompt.RemainingSeconds, Is.InRange(0f, TitleTutorialPrompt.DefaultTimeoutSeconds));
    }

    [UnityTest] public IEnumerator YesLeadsToTheTutorialInTheGameScene()
    {
        yield return OpenPrompt();
        var prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>();
        prompt.Choose(true, "test");
        Assert.IsTrue(prompt.Answered); Assert.IsTrue(prompt.AnswerIsTutorial);
        Assert.IsFalse(prompt.NoNote.gameObject.activeSelf, "選ばなかった方は消える");
        float deadline = Time.realtimeSinceStartup + 12f;
        while (Time.realtimeSinceStartup < deadline && (SceneManager.GetActiveScene().name != "Game" || ScreenTransition.IsBusy)) yield return null;
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        for (int i = 0; i < 120 && manager.Tutorial == null; i++) yield return null;
        Assert.NotNull(manager.Tutorial, "Game シーンは練習で始まる");
        Assert.IsFalse(GameSession.TutorialPending, "フラグは読んだら消す");
    }

    [UnityTest] public IEnumerator NoLeadsStraightToSongSelect()
    {
        yield return OpenPrompt();
        var prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>();
        prompt.Choose(false, "test");
        Assert.IsTrue(prompt.Answered); Assert.IsFalse(prompt.AnswerIsTutorial);
        float deadline = Time.realtimeSinceStartup + 12f;
        while (Time.realtimeSinceStartup < deadline && (SceneManager.GetActiveScene().name != "SongSelect" || ScreenTransition.IsBusy)) yield return null;
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Assert.IsFalse(GameSession.TutorialPending);
        prompt.Choose(true, "late");
        Assert.IsFalse(prompt == null ? false : prompt.AnswerIsTutorial, "答えた後は変えられない");
    }
}
