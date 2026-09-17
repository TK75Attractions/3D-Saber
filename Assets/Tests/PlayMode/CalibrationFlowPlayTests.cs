using System.Collections;
using System.Reflection;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class CalibrationFlowPlayTests
{
    // 集計・入力の試験だけ時計を進める。音声同期そのものは別の実再生試験で確認する。
    static void SetSongClock(SongPlayer player, double start)
    {
        typeof(SongPlayer).GetField("clockSynchronized",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(player,true);
        typeof(SongPlayer).GetField("startDspTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(player,start);
    }
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
        SetSongClock(manager.songPlayer,
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
        foreach(int index in new[]{0,4})
        {
            double hit=CalibrationProtocol.NoteTime(index)+manager.noteSpawner.TotalOffsetSeconds;
            SetSongClock(manager.songPlayer,AudioSettings.dspTime-hit-.025);
            manager.noteSpawner.Tick(manager.songPlayer.SongTime);
            var note=Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None).Single(n=>System.Math.Abs(n.HitTime-hit)<.001);
            // ノーツ生成・探索の CPU 時間は、入力した時刻とは分ける。
            SetSongClock(manager.songPlayer,AudioSettings.dspTime-hit-.025);
            note.Cut(note.transform.position,Vector3.right*6,CutDirection.None,SaberHand.Left);
            Assert.AreEqual(index==0?0:1,c.CollectedCount);
            Assert.GreaterOrEqual(c.LastErrorMs,24);Assert.AreEqual(manager.scoreManager.LastErrorMs,c.LastErrorMs,.001);
            StringAssert.Contains("左",c.LastCut);
            note.Cut(note.transform.position,Vector3.right*6,CutDirection.None,SaberHand.Left);
            Assert.AreEqual(index==0?0:1,c.CollectedCount);
        }
        c.Stop();yield return null;Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
    }

    [UnityTest] public IEnumerator LivePracticeContinuesAndOffsetChangesDoNotRestartAudioOrSave()
    {
        var m=Object.FindFirstObjectByType<GamePlayManager>();var c=m.Calibration;c.BeginLivePractice();
        var source=m.songPlayer.GetComponent<AudioSource>();var clip=m.songPlayer.Clip;
        Assert.True(source.loop);Assert.AreEqual(2.4f,clip.length,.0001f);
        SetSongClock(m.songPlayer,
            AudioSettings.dspTime-CalibrationProtocol.EndSeconds-2);
        c.Tick(.016f);yield return null;
        Assert.True(c.IsLive);Assert.IsNull(c.Result,"24ノーツ後に結果画面で止まらない");
        double before=m.songPlayer.SongTime;int delta=saved<990?10:-10;c.ChangeOffset(delta);
        Assert.AreEqual(saved+delta,c.Draft.OffsetMs);Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
        Assert.AreEqual(m.extraOffsetSeconds+(saved+delta)/1000.0,m.noteSpawner.TotalOffsetSeconds,.00001);
        Assert.GreaterOrEqual(m.songPlayer.SongTime,before);Assert.AreSame(clip,m.songPlayer.Clip);Assert.True(source.loop);
        Assert.False(c.HasLastError,"旧設定の判定を残さない");
        c.Stop();Assert.False(source.loop);Assert.False(m.songPlayer.IsPlaying);
    }

    [UnityTest] public IEnumerator LiveCutShowsImmediateFeedbackAndMissClearsOldError()
    {
        var m=Object.FindFirstObjectByType<GamePlayManager>();var c=m.Calibration;c.BeginLivePractice();
        double[] errors={-.030,0,.030};
        for(int index=0;index<3;index++)
        {
            double hit=CalibrationProtocol.NoteTime(index)+m.noteSpawner.TotalOffsetSeconds;
            SetSongClock(m.songPlayer,AudioSettings.dspTime-hit-errors[index]);c.Tick(.016f);
            var note=Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None).Single(n=>System.Math.Abs(n.HitTime-hit)<.001);
            SetSongClock(m.songPlayer,AudioSettings.dspTime-hit-errors[index]);
            note.Cut(note.transform.position,Vector3.right*6,CutDirection.None,note.RequiredHand);c.Overlay.Tick();
            // DSP時計は音声バッファ単位で進むため、入力準備時ではなく実際に確定した誤差を検証する。
            // ±8msの境界そのものはCalibrationLiveTestsで壁時計に依存せず検証する。
            Assert.True(c.HasLastError);Assert.AreEqual(CalibrationProtocol.LiveFeedback(c.LastErrorMs),c.Overlay.FeedbackText);
            Assert.AreEqual(m.scoreManager.LastErrorMs,c.LastErrorMs,.001);Assert.AreEqual(0,c.CollectedCount);
        }
        double nextHit=CalibrationProtocol.NoteTime(3)+m.noteSpawner.TotalOffsetSeconds;
        SetSongClock(m.songPlayer,AudioSettings.dspTime-nextHit);c.Tick(.016f);
        var next=Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None).Single(n=>System.Math.Abs(n.HitTime-nextHit)<.001);
        next.MarkMiss();c.Overlay.Tick();
        Assert.False(c.HasLastError);Assert.AreEqual("見逃し",c.Overlay.FeedbackText);
        c.Stop();yield return null;
    }

    [UnityTest] public IEnumerator SimpleScreenHidesStatisticsAndSettingsPauseLivePractice()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;var ui=c.Overlay;
        Assert.False(ui.IsSettingsOpen);Assert.IsNull(ui.transform.Find("Environment"));
        Assert.IsNull(ui.transform.Find("TimingReadout"));Assert.IsNull(ui.transform.Find("MeasurementResult"));
        Assert.NotNull(ui.transform.Find("LiveFeedback"));Assert.NotNull(ui.transform.Find("Controls"));
        ui.TogglePractice();Assert.True(c.IsLive);ui.OpenSettings();Assert.False(c.IsRunning);Assert.True(ui.IsSettingsOpen);
        Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);ui.OnBackClicked();Assert.False(ui.IsSettingsOpen);
        yield return null;
    }

    [UnityTest] public IEnumerator SettingsDialogBlocksSaberClicksOnTheSaveButtonBehindIt()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        var pointer=Object.FindFirstObjectByType<SaberUIPointer>();
        var raycast=typeof(SaberUIPointer).GetMethod("RaycastButton",BindingFlags.Instance|BindingFlags.NonPublic);
        Canvas.ForceUpdateCanvases();var position=c.Overlay.transform.Find("Controls/Save").position;
        Assert.NotNull(raycast.Invoke(pointer,new object[]{position}));
        c.Overlay.OpenSettings();Canvas.ForceUpdateCanvases();
        Assert.IsNull(raycast.Invoke(pointer,new object[]{position}),"ダイアログ外をかざして背後の保存を押さない");
        yield return null;
        Assert.IsNull(raycast.Invoke(pointer,new object[]{position}),"描画更新後も背後の保存を押さない");
        c.Overlay.OnBackClicked();Canvas.ForceUpdateCanvases();
        Assert.NotNull(raycast.Invoke(pointer,new object[]{position}),"閉じたら元の操作を使える");
    }
}
