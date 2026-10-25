using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// チュートリアルで「いいえ」を選んでも、スマホで遊ぶときは振る範囲を合わせる段だけ通ってから選曲へ進む
// (2026-10-26 ユーザー「チュートリアルがなくても範囲検査は行うようにして」)。スマホへの指示は差し替えて本物を送らない。
public class TutorialRangeOnlyPlayTests
{
    const int CommandId = 4242;
    System.Func<bool> savedActive; System.Func<int> savedSend;
    System.Func<int, PhoneSaberAcknowledgement[]> savedRead; System.Func<Rect, bool> savedApply;
    int sent;

    [UnitySetUp] public IEnumerator Setup()
    {
        savedActive = TutorialController.PhoneInputActive; savedSend = TutorialController.SendLearnArea;
        savedRead = TutorialController.ReadLearnArea; savedApply = TutorialController.ApplyWidenedRange;
        sent = 0;
        TutorialController.PhoneInputActive = () => true;
        TutorialController.SendLearnArea = () => { sent++; return CommandId; };
        // 返事が来ないので、3秒で「今の範囲のまま進むよ」になって選曲へ進む。
        TutorialController.ReadLearnArea = id => new PhoneSaberAcknowledgement[0];
        TutorialController.ApplyWidenedRange = learned => true;
        GameSession.TutorialPending = false; GameSession.TutorialRangeOnly = false; GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("Title");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<TitleSceneSkin>() == null; i++) yield return null;
        yield return null; yield return null;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        TutorialController.PhoneInputActive = savedActive; TutorialController.SendLearnArea = savedSend;
        TutorialController.ReadLearnArea = savedRead; TutorialController.ApplyWidenedRange = savedApply;
        GameSession.TutorialPending = false; GameSession.TutorialRangeOnly = false;
        var empty = SceneManager.CreateScene("TutorialRangeOnlyCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    [UnityTest] public IEnumerator NoWithThePhoneSetsOnlyTheSwingRangeThenGoesToSongSelect()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.NotNull(skin);
        Assert.IsTrue(skin.TryStartPresentation(), "開始ノーツが切れる");
        float deadline = Time.realtimeSinceStartup + 4f;
        TitleTutorialPrompt prompt = null;
        while (Time.realtimeSinceStartup < deadline && ((prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>()) == null || !prompt.IsOpen)) yield return null;
        Assert.NotNull(prompt);
        yield return new WaitForSecondsRealtime(TitleTutorialPrompt.InputDelaySeconds + .05f);
        prompt.Choose(false, "test");
        Assert.IsTrue(prompt.Answered); Assert.IsFalse(prompt.AnswerIsTutorial);

        deadline = Time.realtimeSinceStartup + 12f;
        while (Time.realtimeSinceStartup < deadline && (SceneManager.GetActiveScene().name != "Game" || ScreenTransition.IsBusy)) yield return null;
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name, "いいえでも、範囲を合わせるため Game シーンへ");
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        for (int i = 0; i < 120 && manager.Tutorial == null; i++) yield return null;
        var tutorial = manager.Tutorial;
        Assert.NotNull(tutorial);
        Assert.IsTrue(tutorial.RangeOnly);
        Assert.AreEqual(1, tutorial.StepCount, "範囲の段だけ");
        Assert.IsFalse(GameSession.TutorialPending, "フラグは読んだら消す");
        Assert.IsFalse(GameSession.TutorialRangeOnly, "フラグは読んだら消す");

        deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline && (tutorial.Step == null || tutorial.Step.Id != TutorialProgram.RangeStepId)) yield return null;
        Assert.AreEqual(TutorialProgram.RangeStepId, tutorial.Step.Id);
        for (int i = 0; i < 30 && sent == 0; i++) yield return null;
        Assert.AreEqual(1, sent, "範囲を覚える指示を1回送る");

        deadline = Time.realtimeSinceStartup + 25f;
        while (Time.realtimeSinceStartup < deadline && (SceneManager.GetActiveScene().name != "SongSelect" || ScreenTransition.IsBusy)) yield return null;
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name, "範囲の段が終わったら選曲へ");
    }
}
