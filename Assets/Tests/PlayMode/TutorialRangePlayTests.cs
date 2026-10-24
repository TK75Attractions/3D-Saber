using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 2026-10-24: スマホの座標を受けているときは、チュートリアルの最初に振る範囲を合わせる。
// 本物の指示は送らず、スマホの返事だけを差し替えて、待つ → 学習中(残り秒・範囲の枠・お手本) → 結果 → 持ち方の説明、を本物の部品で確かめる。
public class TutorialRangePlayTests
{
    const int CommandId = 4242;
    bool savedCalibration;
    System.Func<bool> savedActive;
    System.Func<int> savedSend;
    System.Func<int, PhoneSaberAcknowledgement[]> savedRead;
    System.Func<Rect, bool> savedApply;
    Rect? appliedLearned;
    readonly List<PhoneSaberAcknowledgement> replies = new List<PhoneSaberAcknowledgement>();
    int sent;

    [UnitySetUp] public IEnumerator Setup()
    {
        savedCalibration = GameSession.IsCalibrationMode; GameSession.IsCalibrationMode = false;
        savedActive = TutorialController.PhoneInputActive; savedSend = TutorialController.SendLearnArea; savedRead = TutorialController.ReadLearnArea;
        savedApply = TutorialController.ApplyWidenedRange;
        replies.Clear(); sent = 0; appliedLearned = null;
        // 本物の位置補正(PlayerPrefs)には保存しない。渡された枠だけを覚える。
        TutorialController.ApplyWidenedRange = learned => { appliedLearned = learned; return true; };
        TutorialController.PhoneInputActive = () => true;
        TutorialController.SendLearnArea = () => { sent++; return CommandId; };
        TutorialController.ReadLearnArea = id => id == CommandId ? replies.ToArray() : new PhoneSaberAcknowledgement[0];
        GameSession.TutorialPending = true;
        yield return SceneManager.LoadSceneAsync("Game");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<GamePlayManager>()?.Tutorial == null; i++) yield return null;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        TutorialController.PhoneInputActive = savedActive; TutorialController.SendLearnArea = savedSend; TutorialController.ReadLearnArea = savedRead;
        TutorialController.ApplyWidenedRange = savedApply;
        GameSession.TutorialPending = false; GameSession.IsCalibrationMode = savedCalibration;
        var empty = SceneManager.CreateScene("TutorialRangeCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    static PhoneSaberAcknowledgement Reply(string result, string detail) =>
        new PhoneSaberAcknowledgement(CommandId, "none", "ios", result, detail, "phone");

    [UnityTest] public IEnumerator RangeStepCountsDownFromThePhoneAndHandsOverToTheGrip()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        Assert.NotNull(t);
        yield return new WaitForSecondsRealtime(.6f);
        EnsureClockAdvances(m.songPlayer);
        t.Tick(.016f);
        Assert.NotNull(t.Step); Assert.AreEqual(TutorialProgram.RangeStepId, t.Step.Id, "最初は範囲を合わせる段");
        Assert.AreEqual(TutorialProgram.CommonFlow.Length + 1, t.StepCount);
        Assert.AreEqual(1, sent, "範囲を覚える指示を1回だけ送る");
        Assert.IsFalse(t.Overlay.HandsVisible);
        Assert.IsFalse(t.Overlay.SkipButton.gameObject.activeSelf, "画面の下まで振るので、やめるの的は隠す");
        StringAssert.Contains("振りやすい", t.Overlay.TitleText, "限界までではなく振りやすい範囲で");
        Assert.AreEqual("あと 10 秒", t.Overlay.TimerText, "スマホが学習を始めるまでは10秒のまま");
        Assert.IsFalse(t.Overlay.RangeCoverVisible, "学習が始まるまでは枠を出さない");
        Assert.AreEqual(0, m.noteSpawner.TotalNoteCount, "範囲を合わせる間はノーツを出さない");

        replies.Add(Reply("learning", "10s"));
        yield return null; yield return null;
        Assert.AreEqual(TutorialRangeLearning.Phase.Learning, t.Range.State);
        var frames = new HashSet<int>();
        float until = Time.realtimeSinceStartup + 1.2f;
        while (Time.realtimeSinceStartup < until)
        {
            frames.Add(t.Overlay.Guide.LeftFrame);
            Assert.AreEqual(t.Overlay.Guide.LeftFrame, t.Overlay.Guide.RightFrame, "お手本は両腕をそろえて振る");
            yield return null;
        }
        Assert.Greater(frames.Count, 4, "お手本が腕を上げ下げする");
        Assert.That(t.Overlay.TimerText, Is.EqualTo("あと 9 秒").Or.EqualTo("あと 8 秒"));
        Assert.IsTrue(t.HasRangeCover, "学習中は剣の届いた範囲を数える");
        Assert.IsTrue(t.Overlay.RangeCoverVisible, "届いた範囲の枠が出る");
        Assert.GreaterOrEqual(t.Overlay.RangeCoverRect.sizeDelta.x, TutorialOverlay.RangeCoverMinimumSize);

        replies.Clear(); replies.Add(Reply("learned", "samples300-area0.200000_0.100000_0.800000_0.900000"));
        yield return null; yield return null;
        Assert.IsTrue(t.StepDone);
        Assert.AreEqual(TutorialProgram.RangeLearnedTitle, t.Overlay.TitleText);
        Assert.AreEqual(Rect.MinMaxRect(.2f, .1f, .8f, .9f), appliedLearned, "完了の返事の枠を渡す");
        Assert.IsTrue(t.AppliedRange.HasValue, "広げた枠で保存した");
        Assert.AreEqual(.05f, t.AppliedRange.Value.xMin, 1e-5f); Assert.AreEqual(1.1f, t.AppliedRange.Value.yMax, 1e-5f);
        Assert.IsFalse(t.Overlay.RangeCoverVisible, "終わったら枠を消す");
        Assert.AreEqual("", t.Overlay.TimerText);
        Assert.AreEqual(TutorialProgram.RangeStepId, t.Records[0].Id); Assert.AreEqual("ok", t.Records[0].Reason);
        float deadline = Time.realtimeSinceStartup + (float)TutorialProgram.RangeEndSeconds + 2f;
        while (Time.realtimeSinceStartup < deadline && t.Step.Id != "hands") yield return null;
        Assert.AreEqual("hands", t.Step.Id, "結果を見せたら持ち方の説明へ");
        Assert.IsTrue(t.Overlay.HandsVisible);
        Assert.IsTrue(t.Overlay.SkipButton.gameObject.activeSelf, "持ち方の説明からは、やめるの的を戻す");
        Assert.AreEqual(1, sent, "次の段では送らない");
    }

    [UnityTest] public IEnumerator WithoutAnAnswerTheTutorialKeepsTheCurrentRangeAndMovesOn()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        yield return new WaitForSecondsRealtime(.6f);
        EnsureClockAdvances(m.songPlayer);
        float deadline = Time.realtimeSinceStartup + (float)TutorialProgram.RangeAnswerSeconds + 2f;
        while (Time.realtimeSinceStartup < deadline && !t.StepDone) yield return null;
        Assert.IsTrue(t.StepDone, "返事がなければ3秒で終える");
        Assert.AreEqual(TutorialProgram.RangeKeptTitle, t.Overlay.TitleText);
        Assert.AreEqual("noanswer", t.Records[0].Reason);
        Assert.IsFalse(t.HasRangeCover, "学習が始まらなければ範囲は数えない");
        deadline = Time.realtimeSinceStartup + (float)TutorialProgram.RangeEndSeconds + 2f;
        while (Time.realtimeSinceStartup < deadline && t.Step.Id != "hands") yield return null;
        Assert.AreEqual("hands", t.Step.Id);
    }

    // バッチ実行で音声機器が無く sample 位置が進まない環境でも、DSP 時計で進行を確かめられるようにする(TutorialFlowPlayTests と同じ)。
    static void EnsureClockAdvances(SongPlayer player)
    {
        if (player.SongTime > 0) return;
        typeof(SongPlayer).GetField("clockSynchronized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, true);
        typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, AudioSettings.dspTime - .05);
    }
}
