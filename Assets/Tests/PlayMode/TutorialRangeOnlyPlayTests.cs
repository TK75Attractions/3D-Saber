using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// タイトルのあとは、スマホで遊ぶときも全員が 範囲合わせ → 左右交互 → 準備OK を通る
// (2026-11-01 ユーザー「チュートリアルさ、やっぱり強制にする(はじめての方ははいの画面をなくす)」)。
// 前は「いいえ」を選ぶと範囲合わせだけ通って選曲へ進んだ(2026-10-26)。範囲だけの回は、もうタイトルからは作らない。
// スマホへの指示は差し替えて本物を送らない。
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
        // 返事が来ないので、3秒で「今の範囲のまま進むよ」になって左右交互の練習へ進む。
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

    [UnityTest] public IEnumerator WithThePhoneEveryoneSetsTheRangeAndThenPractices()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.NotNull(skin);
        Assert.IsTrue(skin.TryStartPresentation(), "開始ノーツが切れる");
        float deadline = Time.realtimeSinceStartup + 12f;
        while (Time.realtimeSinceStartup < deadline && (SceneManager.GetActiveScene().name != "Game" || ScreenTransition.IsBusy))
        {
            Assert.IsNull(Object.FindFirstObjectByType<TitleTutorialPrompt>(), "問いかけは出さない");
            yield return null;
        }
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        for (int i = 0; i < 120 && manager.Tutorial == null; i++) yield return null;
        var tutorial = manager.Tutorial;
        Assert.NotNull(tutorial);
        Assert.IsFalse(tutorial.RangeOnly, "範囲だけの回にはしない");
        Assert.AreEqual(TutorialProgram.CommonFlow.Length + 1, tutorial.StepCount, "範囲の段と、左右交互・準備OK");
        Assert.IsFalse(GameSession.TutorialPending, "フラグは読んだら消す");
        Assert.IsFalse(GameSession.TutorialRangeOnly);

        deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline && (tutorial.Step == null || tutorial.Step.Id != TutorialProgram.RangeStepId)) yield return null;
        Assert.AreEqual(TutorialProgram.RangeStepId, tutorial.Step.Id, "最初は範囲を合わせる段");
        for (int i = 0; i < 30 && sent == 0; i++) yield return null;
        Assert.AreEqual(1, sent, "範囲を覚える指示を1回送る");

        deadline = Time.realtimeSinceStartup + (float)(TutorialProgram.RangeAnswerSeconds + TutorialProgram.RangeEndSeconds) + 4f;
        while (Time.realtimeSinceStartup < deadline && tutorial.Step.Id != "tapMix") yield return null;
        Assert.AreEqual("tapMix", tutorial.Step.Id, "範囲の段のあとは左右交互の練習");
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name, "練習の前に選曲へは行かない");
    }
}
