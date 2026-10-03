using System.Collections;
using System.Reflection;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class CalibrationFlowPlayTests
{
    [UnityTest] public IEnumerator AimHoldAdjustsOnceAndRequiresLeavingBeforeAnotherChange()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        var pointer=Object.FindFirstObjectByType<SaberUIPointer>();
        Assert.NotNull(pointer.AimReticle);
        pointer.enabled=false; c.ChangeOffset(-c.Draft.OffsetMs); c.Overlay.Refresh();
        Vector2 point=AimPoint(c,"Controls/Adjust1");
        HoldAim(pointer,point,9); Assert.AreEqual(0,c.Draft.OffsetMs);
        Assert.AreEqual(.9f,pointer.AimProgress01,.001f);
        Assert.True(pointer.AimReticle.gameObject.activeSelf);
        Assert.Less(Vector2.Distance(point,RectTransformUtility.WorldToScreenPoint(null,pointer.AimReticle.position)),1);
        HoldAim(pointer,point,1); Assert.AreEqual(1,c.Draft.OffsetMs);
        HoldAim(pointer,point,20); Assert.AreEqual(1,c.Draft.OffsetMs,"置いたままで連発しない");
        HoldAim(pointer,new Vector2(Screen.width*.5f,Screen.height*.5f),2);
        HoldAim(pointer,point,10); Assert.AreEqual(2,c.Draft.OffsetMs);
        yield return null;
    }

    [UnityTest] public IEnumerator AimCancelsInterruptedHoldsAndDoesNotRepeatMouseClicks()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        var pointer=Object.FindFirstObjectByType<SaberUIPointer>(); pointer.enabled=false;
        c.ChangeOffset(-c.Draft.OffsetMs); c.Overlay.Refresh();
        Vector2 point=AimPoint(c,"Controls/Adjust1");
        HoldAim(pointer,point,9); pointer.SendMessage("OnApplicationFocus",false);
        Assert.False(pointer.AimReticle.gameObject.activeSelf); Assert.Zero(pointer.AimProgress01);
        HoldAim(pointer,point,9); pointer.TickAimAt(point,.1f,false); Assert.Zero(pointer.AimProgress01);
        HoldAim(pointer,point,9); Assert.Zero(c.Draft.OffsetMs);
        pointer.TickAimAt(point,.3f); Assert.Zero(pointer.AimProgress01);
        HoldAim(pointer,point,9); Assert.Zero(c.Draft.OffsetMs);
        pointer.TickAimAt(point,.01f,true,true);
        c.Overlay.transform.Find("Controls/Adjust1").GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
        HoldAim(pointer,point,20); Assert.AreEqual(1,c.Draft.OffsetMs,"クリック後の置きっぱなしも再実行しない");
        yield return null;
    }

    [UnityTest] public IEnumerator AimCanStartAndPausePracticeWhileOnlyBottomControlsAcceptHolds()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        var pointer=Object.FindFirstObjectByType<SaberUIPointer>(); pointer.enabled=false;
        Vector2 play=AimPoint(c,"Controls/PlayPause");
        HoldAim(pointer,play,10); Assert.True(c.IsLive);
        yield return null;
        HoldAim(pointer,play,20); Assert.True(c.IsLive,"開始した場所に置いても停止しない");
        Vector2 back=AimPoint(c,"Header/Back");
        HoldAim(pointer,back,20); Assert.True(c.IsLive);
        Assert.IsNull(pointer.HoveredForTest); Assert.True(pointer.AimReticle.gameObject.activeSelf);
        Assert.Zero(pointer.AimProgress01,"上端へ照準しても試し切り中は操作を受け付けない");
        HoldAim(pointer,AimPoint(c,"Controls/PlayPause"),10);
        Assert.False(c.IsRunning,$"target={pointer.HoveredForTest?.name}, progress={pointer.AimProgress01}, position={play}, screen={Screen.width}x{Screen.height}");
        yield return null;
    }

    [UnityTest] public IEnumerator AimUsesDialogButtonsAndCannotReachTheControlsBehindThem()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        var pointer=Object.FindFirstObjectByType<SaberUIPointer>(); pointer.enabled=false;
        c.ChangeOffset(-c.Draft.OffsetMs); c.Overlay.OpenSettings();
        yield return null;
        HoldAim(pointer,AimPoint(c,"Controls/Adjust1"),15); Assert.Zero(c.Draft.OffsetMs);
        Assert.Zero(pointer.AimProgress01);
        HoldAim(pointer,AimPoint(c,"AudioSettings/SettingsCard/Close"),10);
        Assert.False(c.Overlay.IsSettingsOpen,$"target={pointer.HoveredForTest?.name}, progress={pointer.AimProgress01}");
        c.ChangeOffset(saved==1?2:1); c.Overlay.OnBackClicked(); Assert.True(c.Overlay.IsExitDialogOpen);
        yield return null;
        HoldAim(pointer,AimPoint(c,"UnsavedChanges/Dialog/Continue"),2);
        HoldAim(pointer,AimPoint(c,"UnsavedChanges/Dialog/Continue"),10); Assert.False(c.Overlay.IsExitDialogOpen);
        yield return null;
    }

    [UnityTest] public IEnumerator AimFollowsMouseAndTrackedCoordinatesAndHidesWhenTrackingExpires()
    {
        var pointer=Object.FindFirstObjectByType<SaberUIPointer>(); pointer.enabled=false;
        var input=InputPoint.Instance;
        double previous=input.LastReceivedTime;
        Vector2 previousPosition=input.NormalizedPosition;
        var originalSettings=UnityEngine.InputSystem.InputSystem.settings;
        var settings=Object.Instantiate(originalSettings);
        UnityEngine.InputSystem.InputSystem.settings=settings;
        settings.backgroundBehavior=UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        settings.editorInputBehaviorInPlayMode=UnityEngine.InputSystem.InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        var mouse=UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Mouse>();
        var update=typeof(SaberUIPointer).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic);
        try
        {
            Vector2 mousePoint=new Vector2(Screen.width*.4f,Screen.height*.5f);
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,-1000d);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse,new UnityEngine.InputSystem.LowLevel.MouseState{position=mousePoint});
            yield return null; update.Invoke(pointer,null);
            Assert.True(pointer.AimReticle.gameObject.activeSelf);
            Assert.Less(Vector2.Distance(mousePoint,RectTransformUtility.WorldToScreenPoint(null,pointer.AimReticle.position)),1);
            Vector2 normalized=new Vector2(.75f,.15f);
            typeof(InputPoint).GetProperty("NormalizedPosition").SetValue(input,normalized);
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,Time.realtimeSinceStartupAsDouble);
            update.Invoke(pointer,null);
            Assert.Less(Vector2.Distance(Vector2.Scale(normalized,new Vector2(Screen.width,Screen.height)),
                RectTransformUtility.WorldToScreenPoint(null,pointer.AimReticle.position)),1,"選曲と同じ画面座標を使う");
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,-1000d);
            update.Invoke(pointer,null); Assert.False(pointer.AimReticle.gameObject.activeSelf);
            mousePoint+=Vector2.right*20;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse,new UnityEngine.InputSystem.LowLevel.MouseState{position=mousePoint});
            yield return null; update.Invoke(pointer,null); Assert.True(pointer.AimReticle.gameObject.activeSelf);
        }
        finally
        {
            UnityEngine.InputSystem.InputSystem.RemoveDevice(mouse);
            UnityEngine.InputSystem.InputSystem.settings=originalSettings; Object.Destroy(settings);
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,previous);
            typeof(InputPoint).GetProperty("NormalizedPosition").SetValue(input,previousPosition);
        }
    }

    static Vector2 AimPoint(CalibrationController controller,string path)
    {
        Canvas.ForceUpdateCanvases();
        return RectTransformUtility.WorldToScreenPoint(null,controller.Overlay.transform.Find(path).position);
    }
    static void HoldAim(SaberUIPointer pointer,Vector2 point,int frames)
    { for(int i=0;i<frames;i++) pointer.TickAimAt(point,.1f); }

    [UnityTest] public IEnumerator ConnectionLabelsFollowTheAssignedHandsInsteadOfPortOrder()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;var input=InputPoint.Instance;
        double previousLeft=input.LastReceivedTime,previousRight=input.LastReceivedTime2;
        try
        {
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,Time.realtimeSinceStartupAsDouble);
            typeof(InputPoint).GetProperty("LastReceivedTime2").SetValue(input,-1000d);
            typeof(CalibrationController).GetField("firstHand",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,SaberHand.Right);
            Assert.False(c.LeftReady);Assert.True(c.RightReady);Assert.False(c.CanMeasure);
            c.Overlay.OpenSettings();
            StringAssert.Contains("左：入力待ち    右：接続中",c.Overlay.transform.Find("AudioSettings/SettingsCard/Connections").GetComponent<TMPro.TextMeshProUGUI>().text);
            typeof(CalibrationController).GetField("firstHand",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,SaberHand.Left);
            Assert.True(c.LeftReady);Assert.False(c.RightReady);
        }
        finally
        {
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,previousLeft);
            typeof(InputPoint).GetProperty("LastReceivedTime2").SetValue(input,previousRight);
        }
        yield return null;
    }
    [UnityTest] public IEnumerator MeasurementProposalCanBeTriedWithoutSavingAndControlsStaySafe()
    {
        var m=Object.FindFirstObjectByType<GamePlayManager>();var c=m.Calibration;var ui=c.Overlay;
        var input=InputPoint.Instance;
        double previousLeft=input.LastReceivedTime, previousRight=input.LastReceivedTime2;
        try
        {
            // 接続状態と既知の測定結果を与え、測定→提案→検証の実際のUI遷移を確認する。
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,Time.realtimeSinceStartupAsDouble);
            typeof(InputPoint).GetProperty("LastReceivedTime2").SetValue(input,Time.realtimeSinceStartupAsDouble);
            c.ChangeOffset(60-c.Draft.OffsetMs);ui.StartMeasurement();
            Assert.AreEqual(CalibrationRunMode.Measure,c.Mode);
            Assert.True(Object.FindFirstObjectByType<SaberUIPointer>().BottomControlsOnly);
            Assert.True(ui.transform.Find("Controls/PlayPause").GetComponent<UnityEngine.UI.Button>().interactable);
            Assert.False(ui.transform.Find("Controls/Save").GetComponent<UnityEngine.UI.Button>().interactable);
            var samples=(System.Collections.Generic.List<CalibrationSample>)typeof(CalibrationController)
                .GetField("samples",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c);
            samples.AddRange(Enumerable.Range(0,24).Select(i=>new CalibrationSample(i,25,i%2==0?SaberHand.Left:SaberHand.Right)));
            SetSongClock(m.songPlayer,AudioSettings.dspTime-CalibrationProtocol.EndSeconds-m.noteSpawner.TotalOffsetSeconds-.2);
            c.Tick(.016f);
            Assert.True(ui.IsResultOpen);Assert.True(c.Result.CanRecommend,c.Result.Message);
            Assert.AreEqual(85,c.Result.ProposedOffsetMs);Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
            ui.transform.Find("MeasurementSummary/TryProposal").GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
            Assert.AreEqual(CalibrationRunMode.Practice,c.Mode);Assert.False(ui.IsResultOpen);
            Assert.AreEqual(85,c.Draft.OffsetMs);Assert.AreEqual(saved,GameSession.JudgmentOffsetMs);
            Assert.False(m.songPlayer.GetComponent<AudioSource>().loop);
            c.Stop();ui.Refresh();
        }
        finally
        {
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,previousLeft);
            typeof(InputPoint).GetProperty("LastReceivedTime2").SetValue(input,previousRight);
        }
        yield return null;
    }
    [UnityTest] public IEnumerator LiveRestoreKeepsAudioAndClearsHistoryFromOldSettings()
    {
        var m=Object.FindFirstObjectByType<GamePlayManager>();var c=m.Calibration;
        c.ChangeOffset(saved<990?10:-10);c.SetSpeedStep(c.SpeedStep==0?1:0);c.BeginLivePractice();
        c.History.Add(30,SaberHand.Left);var clip=m.songPlayer.Clip;
        c.RestoreSaved();
        Assert.True(c.IsLive);Assert.AreSame(clip,m.songPlayer.Clip);
        Assert.True(m.songPlayer.GetComponent<AudioSource>().loop);
        Assert.AreEqual(saved,c.Draft.OffsetMs);Assert.AreEqual(GameSession.NoteApproachTime,c.Draft.ApproachTime);
        Assert.AreEqual(0,c.History.Count);Assert.False(c.Draft.IsDirty);
        c.ChangeOffset(GameSession.JudgmentOffsetMaxMs-c.Draft.OffsetMs);c.Overlay.Refresh();
        Assert.False(c.Overlay.transform.Find("Controls/Adjust1").GetComponent<UnityEngine.UI.Button>().interactable);
        Assert.True(c.Overlay.transform.Find("Controls/Adjust-1").GetComponent<UnityEngine.UI.Button>().interactable);
        c.Stop();yield return null;
    }
    [UnityTest] public IEnumerator MeasurementCannotStartWithTooLittleTimeAndHelpExplainsWhy()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;var ui=c.Overlay;
        ui.TickCountdown(ui.RemainingSeconds-24);ui.Refresh();
        ui.StartMeasurement();Assert.False(c.IsRunning);
        Assert.False(ui.transform.Find("Controls/Measure").GetComponent<UnityEngine.UI.Button>().interactable);
        StringAssert.Contains("25秒",ui.transform.Find("MeasurementStatus").GetComponent<TMPro.TextMeshProUGUI>().text);
        yield return null;
    }
    // 集計・入力の試験だけ時計を進める。音声同期そのものは別の実再生試験で確認する。
    static void SetSongClock(SongPlayer player, double start)
    {
        typeof(SongPlayer).GetField("clockSynchronized",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(player,true);
        typeof(SongPlayer).GetField("startDspTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(player,start);
    }
    int saved; double? savedSelectionSeconds; Scene gameScene;
    [UnitySetUp] public IEnumerator Setup()
    {
        saved=GameSession.JudgmentOffsetMs; GameSession.IsCalibrationMode=true;
        savedSelectionSeconds=GameSession.CalibrationSelectionSeconds;GameSession.CalibrationSelectionSeconds=null;
        yield return SceneManager.LoadSceneAsync("Game");gameScene=SceneManager.GetActiveScene();
        for(int i=0;i<120&&Object.FindFirstObjectByType<GamePlayManager>()?.Calibration==null;i++) yield return null;
    }
    [UnityTearDown] public IEnumerator Cleanup()
    {
        GameSession.IsCalibrationMode=false;GameSession.JudgmentOffsetMs=saved;
        GameSession.CalibrationSelectionSeconds=savedSelectionSeconds;
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
            Assert.AreSame(Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut_Perfect"),sfx.ClipFor(JudgmentTier.Perfect));
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

    [UnityTest] public IEnumerator GuidedScreenKeepsPracticeClearAndSettingsPauseLivePractice()
    {
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;var ui=c.Overlay;
        Assert.False(ui.IsSettingsOpen);Assert.IsNull(ui.transform.Find("Environment"));
        Assert.IsNull(ui.transform.Find("TimingReadout"));Assert.IsNull(ui.transform.Find("MeasurementResult"));
        Assert.NotNull(ui.transform.Find("LiveFeedback"));Assert.NotNull(ui.transform.Find("Controls"));
        Assert.NotNull(ui.transform.Find("RecentTiming/ErrorMeter"));Assert.NotNull(ui.transform.Find("Controls/Measure"));
        ui.TogglePractice();Assert.True(c.IsLive);Assert.False(ui.transform.Find("ReadyGuide").gameObject.activeSelf);
        ui.OpenSettings();Assert.False(c.IsRunning);Assert.True(ui.IsSettingsOpen);
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
