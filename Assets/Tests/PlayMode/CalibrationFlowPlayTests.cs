using System.Collections;
using System.Reflection;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class CalibrationFlowPlayTests
{
    int saved; Scene gameScene;
    [UnitySetUp] public IEnumerator Setup()
    {
        saved=GameSession.JudgmentOffsetMs; GameSession.IsCalibrationMode=true;
        yield return SceneManager.LoadSceneAsync("Game");gameScene=SceneManager.GetActiveScene();
        for(int i=0;i<120&&Object.FindFirstObjectByType<GamePlayManager>()?.Calibration==null;i++) yield return null;
    }
    [UnityTearDown] public IEnumerator Cleanup()
    {
        GameSession.IsCalibrationMode=false;GameSession.JudgmentOffsetMs=saved;
        var empty=SceneManager.CreateScene("CalibrationCleanup");SceneManager.SetActiveScene(empty);
        yield return SceneManager.UnloadSceneAsync(gameScene);
    }
    [UnityTest] public IEnumerator OpensIdleAndPracticeUsesUnsavedOffsetWithoutChangingGame()
    {
        var manager=Object.FindFirstObjectByType<GamePlayManager>();var c=manager.Calibration;
        Assert.NotNull(c);Assert.AreEqual(CalibrationRunMode.Idle,c.Mode);Assert.False(manager.songPlayer.IsPlaying);
        int delta=saved<990?10:-10;c.ChangeOffset(delta);Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
        c.Begin(CalibrationRunMode.Practice);Assert.True(c.IsRunning);
        Assert.AreEqual(manager.extraOffsetSeconds+(saved+delta)/1000.0,manager.noteSpawner.TotalOffsetSeconds,.00001);
        Assert.AreEqual(manager.saberBladeRadiusV2,manager.cutJudge.bladeRadius);
        Assert.AreEqual(manager.saberNoteHitRadiusXYV2,manager.cutJudge.noteHitRadiusXY);
        c.ChangeOffset(10);Assert.AreEqual(saved+delta,c.Draft.OffsetMs);
        yield return new WaitForSecondsRealtime(.45f);Assert.True(manager.songPlayer.IsPlaying);Assert.NotNull(manager.songPlayer.Clip);
        c.Stop();Assert.False(manager.songPlayer.IsPlaying);Assert.AreEqual(0,manager.noteSpawner.TotalNoteCount);
        Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
        c.Overlay.OnBackClicked();Assert.True(c.Overlay.IsExitDialogOpen);
    }
    [UnityTest] public IEnumerator NoInputDoesNotGenerateARecommendedOffset()
    {
        var manager=Object.FindFirstObjectByType<GamePlayManager>();var c=manager.Calibration;c.Begin(CalibrationRunMode.Practice);
        typeof(SongPlayer).GetField("startDspTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(manager.songPlayer,
            AudioSettings.dspTime-CalibrationProtocol.EndSeconds-manager.extraOffsetSeconds-.1);
        c.Tick(.016f);yield return null;
        Assert.AreEqual(CalibrationRunMode.Result,c.Mode);Assert.NotNull(c.Result);Assert.False(c.Result.CanRecommend);
        Assert.AreEqual(24,c.Result.Missing);Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
    }
    [UnityTest] public IEnumerator CalibrationKeepsPrismCutAndMissSoundsMuted()
    {
        var sounds=Object.FindObjectsByType<JudgmentSfx>(FindObjectsSortMode.None);
        Assert.Greater(sounds.Length,0);
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        foreach(var sfx in sounds)
        {
            Assert.AreEqual(0f,sfx.volume,"待機中も通常の判定音を鳴らさない");
            Assert.AreSame(Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut"),sfx.ClipFor(JudgmentTier.Perfect));
            Assert.AreSame(Resources.Load<AudioClip>("Audio/SFX/Saber_Miss"),sfx.ClipFor(JudgmentTier.Miss));
        }
        c.Begin(CalibrationRunMode.Practice);yield return null;
        foreach(var sfx in sounds) Assert.AreEqual(0f,sfx.volume,"試し切り中は基準クリックだけを聴く");
        c.Stop();
        foreach(var sfx in sounds) Assert.AreEqual(0f,sfx.volume,"中止後も調整画面内では無音を維持");
        Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
    }
    [UnityTest] public IEnumerator FocusLossStopsAudioAndClearsTrial()
    {
        var manager=Object.FindFirstObjectByType<GamePlayManager>();var c=manager.Calibration;c.Begin(CalibrationRunMode.Practice);
        c.SendMessage("OnApplicationFocus",false);yield return null;
        Assert.False(c.IsRunning);Assert.False(manager.songPlayer.IsPlaying);Assert.AreEqual(0,manager.noteSpawner.TotalNoteCount);
        Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
    }
    [UnityTest] public IEnumerator ActualNoteCutExcludesWarmupAndRecordsHandAndTiming()
    {
        var manager=Object.FindFirstObjectByType<GamePlayManager>();var c=manager.Calibration;c.Begin(CalibrationRunMode.Practice);
        // 既知の時刻を与え、通常の CuttableNote.OnCut 経路を使う。実機遅延の測定を代替するテストではない。
        var clock=typeof(SongPlayer).GetField("startDspTime",BindingFlags.Instance|BindingFlags.NonPublic);
        foreach(int index in new[]{0,4})
        {
            double hit=CalibrationProtocol.NoteTime(index)+manager.noteSpawner.TotalOffsetSeconds;
            clock.SetValue(manager.songPlayer,AudioSettings.dspTime-hit-.025);
            manager.noteSpawner.Tick(manager.songPlayer.SongTime);
            var note=Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None).Single(n=>System.Math.Abs(n.HitTime-hit)<.001);
            // ノーツ生成・探索の CPU 時間は、入力した時刻とは分ける。
            clock.SetValue(manager.songPlayer,AudioSettings.dspTime-hit-.025);
            note.Cut(note.transform.position,Vector3.right*6,CutDirection.None,SaberHand.Left);
            Assert.AreEqual(index==0?0:1,c.CollectedCount);
            Assert.GreaterOrEqual(c.LastErrorMs,24);Assert.AreEqual(manager.scoreManager.LastErrorMs,c.LastErrorMs,.001);
            StringAssert.Contains("左",c.LastCut);
            note.Cut(note.transform.position,Vector3.right*6,CutDirection.None,SaberHand.Left);
            Assert.AreEqual(index==0?0:1,c.CollectedCount);
        }
        c.Stop();yield return null;Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
    }
}
