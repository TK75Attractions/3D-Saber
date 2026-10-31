using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public class TutorialGuidePlayTests
{
    GamePlayManager manager;
    TutorialController tutorial;
    bool calibration;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [UnitySetUp] public IEnumerator Setup()
    {
        calibration = GameSession.IsCalibrationMode;
        GameSession.IsCalibrationMode = false; GameSession.TutorialPending = true;
        yield return SceneManager.LoadSceneAsync("Game");
        float deadline = Time.realtimeSinceStartup + 15;
        while (Time.realtimeSinceStartup < deadline)
        {
            manager = Object.FindFirstObjectByType<GamePlayManager>();
            tutorial = manager != null ? manager.Tutorial : null;
            if (tutorial != null && tutorial.Step != null) break;
            yield return null;
        }
        Assert.NotNull(tutorial?.Step);
        manager.enabled = false;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        GameSession.TutorialPending = false; GameSession.IsCalibrationMode = calibration;
        var empty = SceneManager.CreateScene("TutorialGuideCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    [UnityTest] public IEnumerator GuideUsesTransparentUIAndFreezesWithTheSongClock()
    {
        var guide = tutorial.Overlay.Guide;
        Assert.NotNull(guide); Assert.True(guide.IsReady); Assert.False(guide.raycastTarget);
        Assert.AreEqual(2048, guide.mainTexture.width); Assert.AreEqual(2048, guide.mainTexture.height);
        int cameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Length;
        var notes = new[] { new NoteData { time = 1000, color = "red", direction = "up" } };
        guide.Show(notes, .96); int first = guide.RightFrame;
        guide.Show(notes, 1.04);
        Assert.Greater(guide.RightFrame, first); Assert.AreEqual(8, guide.LeftFrame);
        Assert.AreEqual(cameras, Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Length);
        manager.songPlayer.Stop();
        tutorial.Overlay.Tick(.016f);
        int left = guide.LeftFrame, right = guide.RightFrame;
        yield return new WaitForSecondsRealtime(.1f);
        tutorial.Overlay.Tick(.1f);
        Assert.AreEqual(left, guide.LeftFrame); Assert.AreEqual(right, guide.RightFrame);
    }

    // 2026-10-31 夜から流れに入れていない段(ロング・赤・練習してみよう)も、部品の振る舞いは確かめる。段の並びを差し替えて使う。
    void UseSteps(params string[] ids) => typeof(TutorialController).GetField("steps", Private).SetValue(tutorial, TutorialProgram.Build(ids, true));
    void Begin(int index, double at) => typeof(TutorialController).GetMethod("BeginStep", Private).Invoke(tutorial, new object[] { index, at });
    string LastReason() => tutorial.Records[tutorial.Records.Count - 1].Reason;

    [UnityTest] public IEnumerator PartialLongsAndDuplicateCountsCannotPassThePractice()
    {
        UseSteps("long");
        Begin(0, tutorial.DemonstrationTime);
        manager.scoreManager.songPlayer = null; // 同期試験では既存の無時計 Perfect 経路を使う。
        CutLong(2, 1);
        Assert.AreEqual(0, tutorial.Successes); Assert.False(tutorial.StepDone);
        StringAssert.Contains("数字", tutorial.Hint);
        CutLong(2, 2); Assert.AreEqual(1, tutorial.Successes);
        CutLong(2, 2); Assert.AreEqual(1, tutorial.Successes, "2回だけ繰り返しても通過しない");
        CutLong(3, 2); Assert.AreEqual(1, tutorial.Successes);
        CutLong(3, 3); Assert.AreEqual(2, tutorial.Successes, "2回と3回の2種類で合格(2026-10-31 から。前は4回も)");
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone); Assert.AreEqual("ok", tutorial.Records[tutorial.Records.Count - 1].Reason);
        yield return null;
    }

    void CutLong(int required, int cuts)
    {
        var chart = new ChartData { bpm = 120, notes = new List<NoteData>
        { new NoteData { time = 1000, color = "red", type = "long", count = required, lengthMs = (required - 1) * 500 } } };
        manager.noteSpawner.SetChart(chart);
        manager.noteSpawner.Tick(1 + manager.noteSpawner.TotalOffsetSeconds);
        var note = manager.noteSpawner.LiveNotes[0];
        for (int i = 0; i < cuts; i++) note.Cut(note.transform.position, Vector3.down * 5, CutDirection.None, SaberHand.Right);
        if (cuts < required) note.MarkMiss();
    }

    // 2026-10-31「クリアしたらそのまま次いって、今のはあくまで最大」「左右交互に流れるやつだけ長めにとって他全部1周」、
    // 同日夜「左右交互は5周流し、3れんぞくgreat以上まで終わらない」(5周で続かなければ終える)。
    // 左右交互は GREAT 以上が3回続いたらすぐ次へ。途切れたら数え直し、5周の20個の判定が出たら待たずに次へ。赤は1周で次へ。
    [UnityTest] public IEnumerator LoopLimitedStepsMoveOnRightAfterTheLastJudgment()
    {
        manager.scoreManager.songPlayer = null; // 切った時刻は届く時刻ちょうど(PERFECT)にする
        int mix = Array.IndexOf(TutorialProgram.CommonFlow, "tapMix");
        Begin(mix, tutorial.DemonstrationTime);
        Tap(true); Tap(true); Tap(false);
        Assert.AreEqual(0, tutorial.Successes, "見逃したら数え直し");
        Tap(true); Tap(true);
        tutorial.Tick(.016f);
        Assert.False(tutorial.StepDone, "合わせて4回でも、続けて3回でなければ合格しない");
        Tap(true);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone, "3回続いたらすぐ次へ"); Assert.AreEqual("ok", LastReason());

        Begin(mix, tutorial.DemonstrationTime);
        for (int i = 0; i < 19; i++) Tap(i % 3 != 2); // 2回ずつ続いては途切れる
        tutorial.Tick(.016f);
        Assert.False(tutorial.StepDone, "5周目の最後の1個がまだ");
        Tap(false);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone, "5周の20個の判定が出たら次へ");
        Assert.AreEqual("timeout", LastReason());
        Assert.AreEqual("次に進むよ", tutorial.BannerTitle);

        UseSteps("tapRed");
        Begin(0, tutorial.DemonstrationTime);
        Tap(true);
        tutorial.Tick(.016f);
        Assert.False(tutorial.StepDone);
        Tap(false);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone, "1周の判定が出たら、合格しなくても待たずに次へ");
        Assert.AreEqual("timeout", LastReason());
        yield return null;
    }

    // 2026-10-31 夜からの流れ: 左右交互で合格しなくても、次は「曲を選びに行こう」(準備OKの段)で終わる。
    [UnityTest] public IEnumerator TheAlternatingPracticeTimingOutEndsWithReady()
    {
        double now = tutorial.DemonstrationTime;
        Begin(Array.IndexOf(TutorialProgram.CommonFlow, "tapMix"), now - TutorialProgram.MixPracticeTimeoutSeconds - .1);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone); Assert.AreEqual("timeout", LastReason());
        typeof(TutorialController).GetField("stepEndAt", Private).SetValue(tutorial, now - .1);
        tutorial.Tick(.016f);
        Assert.AreEqual("ready", tutorial.Step.Id, "左右交互のあとはすぐ準備OK");
        tutorial.Overlay.Tick(.016f);
        Assert.AreEqual("曲を選びに行こう", tutorial.Overlay.TitleText);
        yield return null;
    }

    void Tap(bool cut)
    {
        var chart = new ChartData { bpm = 120, notes = new List<NoteData> { new NoteData { time = 1000, color = "red", type = "tap" } } };
        manager.noteSpawner.SetChart(chart);
        manager.noteSpawner.Tick(1 + manager.noteSpawner.TotalOffsetSeconds);
        var note = manager.noteSpawner.LiveNotes[0];
        if (cut) Assert.True(note.CutAtSongTime(note.transform.position, Vector3.down * 5, CutDirection.None, SaberHand.Right, note.HitTime));
        else note.MarkMiss();
    }

    [UnityTest] public IEnumerator TimingOutALongIsNotReportedAsSuccessOrReady()
    {
        double now = tutorial.DemonstrationTime;
        UseSteps("long", TutorialProgram.AllStepId, "ready");
        Begin(0, now - TutorialProgram.LongPracticeTimeoutSeconds - .1);
        Assert.AreEqual("long", tutorial.Step.Id);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone); Assert.AreEqual(0, tutorial.Successes);
        Assert.AreEqual("timeout", LastReason());
        Assert.AreNotEqual("OK!", tutorial.BannerTitle);
        // 終了表示の待ちだけを省き、Tick の次段処理で「練習してみよう」へ進む。
        typeof(TutorialController).GetField("stepEndAt", Private).SetValue(tutorial, now - .1);
        tutorial.Tick(.016f);
        Assert.AreEqual(1, tutorial.StepIndex); Assert.AreEqual("all", tutorial.Step.Id);
        Assert.False(tutorial.StepDone);
        tutorial.Overlay.Tick(.016f);
        StringAssert.Contains("練習してみよう", tutorial.Overlay.TitleText);
        // 練習してみようも時間では次へ進み、準備へ。
        Begin(1, now - TutorialProgram.AllPracticeTimeoutSeconds - .1);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone); Assert.AreEqual("timeout", LastReason());
        typeof(TutorialController).GetField("stepEndAt", Private).SetValue(tutorial, now - .1);
        tutorial.Tick(.016f);
        Assert.AreEqual(2, tutorial.StepIndex); Assert.AreEqual("ready", tutorial.Step.Id);
        Assert.False(tutorial.StepDone);
        tutorial.Overlay.Tick(.016f);
        Assert.AreEqual("曲を選びに行こう", tutorial.Overlay.TitleText);
        yield return null;
    }
}
