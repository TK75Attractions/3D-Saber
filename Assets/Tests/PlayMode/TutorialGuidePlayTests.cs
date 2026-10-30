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

    [UnityTest] public IEnumerator PartialLongsAndDuplicateCountsCannotPassThePractice()
    {
        int index = Array.IndexOf(TutorialProgram.CommonFlow, "long");
        typeof(TutorialController).GetMethod("BeginStep", Private).Invoke(tutorial, new object[] { index, tutorial.DemonstrationTime });
        manager.scoreManager.songPlayer = null; // 同期試験では既存の無時計 Perfect 経路を使う。
        CutLong(2, 1);
        Assert.AreEqual(0, tutorial.Successes); Assert.False(tutorial.StepDone);
        StringAssert.Contains("数字", tutorial.Hint);
        CutLong(2, 2); Assert.AreEqual(1, tutorial.Successes);
        CutLong(2, 2); Assert.AreEqual(1, tutorial.Successes, "2回だけ繰り返しても通過しない");
        CutLong(3, 2); Assert.AreEqual(1, tutorial.Successes);
        CutLong(3, 3); Assert.AreEqual(2, tutorial.Successes);
        CutLong(4, 3); Assert.AreEqual(2, tutorial.Successes);
        CutLong(4, 4); Assert.AreEqual(3, tutorial.Successes);
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

    // 2026-10-31「クリアしたらそのまま次いって、今のはあくまで最大」「左右交互に流れるやつだけ長めにとって他全部1周」。
    // 合格したらすぐ次へ。合格しないまま最大の周(左右交互は2周、赤は1周)の判定が全部出たら、待たずに次へ。
    [UnityTest] public IEnumerator LoopLimitedStepsMoveOnRightAfterTheLastJudgment()
    {
        manager.scoreManager.songPlayer = null; // 切った時刻は届く時刻ちょうど(PERFECT)にする
        int mix = Array.IndexOf(TutorialProgram.CommonFlow, "tapMix");
        typeof(TutorialController).GetMethod("BeginStep", Private).Invoke(tutorial, new object[] { mix, tutorial.DemonstrationTime });
        for (int i = 0; i < 3; i++) Tap(true);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone, "1周目で合格したらすぐ次へ"); Assert.AreEqual("ok", tutorial.Records[tutorial.Records.Count - 1].Reason);

        typeof(TutorialController).GetMethod("BeginStep", Private).Invoke(tutorial, new object[] { mix, tutorial.DemonstrationTime });
        for (int i = 0; i < 2; i++) Tap(true);
        for (int i = 2; i < 7; i++) Tap(false);
        tutorial.Tick(.016f);
        Assert.False(tutorial.StepDone, "2周目の最後の1個がまだ");
        Tap(false);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone, "2周の8個の判定が出たら次へ");
        Assert.AreEqual("timeout", tutorial.Records[tutorial.Records.Count - 1].Reason);

        int red = Array.IndexOf(TutorialProgram.CommonFlow, "tapRed");
        typeof(TutorialController).GetMethod("BeginStep", Private).Invoke(tutorial, new object[] { red, tutorial.DemonstrationTime });
        Tap(true);
        tutorial.Tick(.016f);
        Assert.False(tutorial.StepDone);
        Tap(false);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone, "1周の判定が出たら、合格しなくても待たずに次へ");
        Assert.AreEqual("timeout", tutorial.Records[tutorial.Records.Count - 1].Reason);
        Assert.AreEqual("次に進むよ", tutorial.BannerTitle);
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
        int index = Array.IndexOf(TutorialProgram.CommonFlow, "long");
        Assert.AreEqual(7, index);
        typeof(TutorialController).GetMethod("BeginStep", Private).Invoke(tutorial,
            new object[] { index, now - TutorialProgram.LongPracticeTimeoutSeconds - .1 });
        Assert.AreEqual("long", tutorial.Step.Id);
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone); Assert.AreEqual(0, tutorial.Successes);
        Assert.AreEqual("timeout", tutorial.Records[tutorial.Records.Count - 1].Reason);
        Assert.AreNotEqual("OK!", tutorial.BannerTitle);
        // 終了表示の待ちだけを省き、Tick の次段処理で「練習してみよう」へ進む(2026-10-28 から準備の前に入る)。
        typeof(TutorialController).GetField("stepEndAt", Private).SetValue(tutorial, now - .1);
        tutorial.Tick(.016f);
        Assert.AreEqual(8, tutorial.StepIndex); Assert.AreEqual("all", tutorial.Step.Id);
        Assert.False(tutorial.StepDone);
        tutorial.Overlay.Tick(.016f);
        StringAssert.Contains("練習してみよう", tutorial.Overlay.TitleText);
        // 練習してみようも時間では次へ進み、準備へ。
        typeof(TutorialController).GetMethod("BeginStep", Private).Invoke(tutorial,
            new object[] { 8, now - TutorialProgram.AllPracticeTimeoutSeconds - .1 });
        tutorial.Tick(.016f);
        Assert.True(tutorial.StepDone); Assert.AreEqual("timeout", tutorial.Records[tutorial.Records.Count - 1].Reason);
        typeof(TutorialController).GetField("stepEndAt", Private).SetValue(tutorial, now - .1);
        tutorial.Tick(.016f);
        Assert.AreEqual(9, tutorial.StepIndex); Assert.AreEqual("ready", tutorial.Step.Id);
        Assert.False(tutorial.StepDone);
        tutorial.Overlay.Tick(.016f);
        Assert.AreEqual("曲を選びに行こう", tutorial.Overlay.TitleText);
        yield return null;
    }
}
