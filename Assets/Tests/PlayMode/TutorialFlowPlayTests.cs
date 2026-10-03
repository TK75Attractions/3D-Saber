using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Game シーンをチュートリアルで開き、説明 → 練習 → 選曲へ、の流れが本物の部品で通ることを確かめる。
public class TutorialFlowPlayTests
{
    bool savedCalibration;

    [UnitySetUp] public IEnumerator Setup()
    {
        savedCalibration = GameSession.IsCalibrationMode; GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = true;
        yield return SceneManager.LoadSceneAsync("Game");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<GamePlayManager>()?.Tutorial == null; i++) yield return null;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        GameSession.TutorialPending = false; GameSession.IsCalibrationMode = savedCalibration;
        var empty = SceneManager.CreateScene("TutorialCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    [UnityTest] public IEnumerator OpensOnTheHandsStepWithASilentClockAndBottomOnlyPointer()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        Assert.NotNull(t, "GamePlayManager がチュートリアルを始める");
        Assert.IsFalse(GameSession.TutorialPending, "フラグは読んだら消す");
        Assert.NotNull(t.Overlay); Assert.NotNull(t.Overlay.Controls); Assert.NotNull(t.Overlay.SkipButton);
        var pointer = Object.FindFirstObjectByType<SaberUIPointer>();
        Assert.NotNull(pointer); Assert.IsTrue(pointer.BottomControlsOnly, "的は画面下の帯だけ");
        yield return new WaitForSecondsRealtime(.6f);
        Assert.IsTrue(m.songPlayer.IsPlaying, "無音の時計が回る"); Assert.NotNull(m.songPlayer.Clip);
        EnsureClockAdvances(m.songPlayer);
        t.Tick(.016f);
        Assert.NotNull(t.Step); Assert.AreEqual("hands", t.Step.Id);
        Assert.IsTrue(t.Overlay.HandsVisible, "持ち方の絵が出る");
        StringAssert.Contains("右手", t.Overlay.TitleText);
        Assert.AreEqual(0, m.noteSpawner.TotalNoteCount, "説明の間はノーツを出さない");
        Assert.AreEqual(TutorialProgram.CommonFlow.Length, t.StepCount);
    }

    [UnityTest] public IEnumerator HandsStepTimesOutIntoPracticeAndSpawnsRedNotesOnTheRight()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        yield return new WaitForSecondsRealtime(.6f);
        EnsureClockAdvances(m.songPlayer);
        float deadline = Time.realtimeSinceStartup + TutorialProgram.HandsTimeoutSeconds + 4f;
        while (Time.realtimeSinceStartup < deadline && (t.Step == null || t.Step.Id != "tapRed")) yield return null;
        Assert.NotNull(t.Step); Assert.AreEqual("tapRed", t.Step.Id, "振らなくても 6 秒で練習へ");
        Assert.IsFalse(t.Overlay.HandsVisible);
        Assert.AreEqual(1, t.Records.Count); Assert.AreEqual("hands", t.Records[0].Id); Assert.AreEqual("ok", t.Records[0].Reason);
        deadline = Time.realtimeSinceStartup + 4f;
        while (Time.realtimeSinceStartup < deadline && m.noteSpawner.AliveCount == 0) yield return null;
        Assert.Greater(m.noteSpawner.AliveCount, 0, "型のノーツが流れる");
        var note = m.noteSpawner.LiveNotes[0];
        Assert.AreEqual(SaberHand.Right, note.RequiredHand, "最初の型は赤(右手)");
        Assert.Greater(note.transform.position.x, 0f, "赤は画面の右");
        Assert.AreEqual(1, note.RequiredCutCount);
    }

    [UnityTest] public IEnumerator SkipFinishesAndLeavesForSongSelect()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        yield return new WaitForSecondsRealtime(.6f);
        t.Skip();
        Assert.IsTrue(t.IsFinished); Assert.AreEqual("skipped", t.FinishReason);
        Assert.IsFalse(t.Overlay.SkipButton.gameObject.activeSelf, "終わったら的を消す");
        Assert.IsTrue(ScreenTransition.IsBusy, "選曲への幕が始まる");
        t.Skip();
        Assert.AreEqual("skipped", t.FinishReason, "二度目は何もしない");
        float deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline && ScreenTransition.IsBusy) yield return null;
        Assert.IsFalse(ScreenTransition.IsBusy);
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Assert.IsFalse(GameSession.TutorialPending);
    }

    // バッチ実行で音声機器が無く sample 位置が進まない環境でも、DSP 時計で進行を確かめられるようにする。
    static void EnsureClockAdvances(SongPlayer player)
    {
        if (player.SongTime > 0) return;
        typeof(SongPlayer).GetField("clockSynchronized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, true);
        typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, AudioSettings.dspTime - .05);
    }
}
