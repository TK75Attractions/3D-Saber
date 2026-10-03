using System.Collections;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class CalibrationCountdownPlayTests
{
    int savedOffset;
    float savedSpeed;
    string savedSelectionSongId, savedSelectionDifficulty;

    [UnitySetUp] public IEnumerator Open()
    {
        savedOffset=GameSession.JudgmentOffsetMs;savedSpeed=GameSession.NoteApproachTime;
        savedSelectionSongId=GameSession.CalibrationSelectionSongId;
        savedSelectionDifficulty=GameSession.CalibrationSelectionDifficulty;
        GameSession.CalibrationSelectionSongId=null;GameSession.CalibrationSelectionDifficulty=null;
        GameSession.IsCalibrationMode=false;GameSession.CalibrationSelectionSeconds=null;
        yield return SceneManager.LoadSceneAsync("SongSelect");
        yield return WaitForSelection();
    }

    static IEnumerator WaitForSelection()
    {
        double deadline=Time.realtimeSinceStartupAsDouble+20;
        while(Object.FindFirstObjectByType<SongSelectSkin>()?.IsReady!=true && Time.realtimeSinceStartupAsDouble<deadline)yield return null;
        Assert.True(Object.FindFirstObjectByType<SongSelectSkin>()?.IsReady==true);
    }

    static IEnumerator EnterCalibration()
    {
        SongSelectSkin.EnterCalibration();
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game",SceneManager.GetActiveScene().name);
        Assert.NotNull(Object.FindFirstObjectByType<GamePlayManager>().Calibration);
        // 実機からの入力を混ぜず、UIと時計の往復を確認する。
        foreach(var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))judge.autonomous=false;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        Time.timeScale=1;
        yield return ScreenTransitionPlayTests.WaitForTransition();
        GameSession.IsCalibrationMode=false;GameSession.CalibrationSelectionSeconds=null;
        GameSession.JudgmentOffsetMs=savedOffset;GameSession.NoteApproachTime=savedSpeed;
        GameSession.CalibrationSelectionSongId=savedSelectionSongId;
        GameSession.CalibrationSelectionDifficulty=savedSelectionDifficulty;
        var scene=SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("CountdownCleanup"));
        if(scene.IsValid()&&scene.isLoaded)yield return SceneManager.UnloadSceneAsync(scene);
    }

    [UnityTest] public IEnumerator CarriesTimeThroughCalibrationAndVoluntaryReturn()
    {
        var select=Object.FindFirstObjectByType<SongSelectSkin>();select.TickCountdown(30.25);
        double before=select.RemainingSeconds;
        yield return EnterCalibration();
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        Assert.That(c.Overlay.RemainingSeconds,Is.EqualTo(before).Within(1));
        c.Overlay.TickCountdown(12.5);double remaining=c.Overlay.RemainingSeconds;
        c.DiscardAndExit();
        yield return ScreenTransitionPlayTests.WaitForTransition();yield return WaitForSelection();
        Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name);
        Assert.That(Object.FindFirstObjectByType<SongSelectSkin>().RemainingSeconds,Is.EqualTo(remaining).Within(1));
        Assert.IsNull(GameSession.CalibrationSelectionSeconds,"引き継ぎは一度だけ消費する");
    }

    [UnityTest] public IEnumerator BackRestoresSelectedSongDifficultyAndPreview()
    {
        yield return SelectionRoundTrip(false);
    }

    [UnityTest] public IEnumerator SaveAndReturnRestoresSelectedSongDifficultyAndPreview()
    {
        yield return SelectionRoundTrip(true);
    }

    static IEnumerator SelectionRoundTrip(bool save)
    {
        // 別の曲・難易度で二度往復し、最初の保存値が残らないことも確認する。
        for(int difficulty=1;difficulty<=2;difficulty++)
        {
            var controller=Object.FindFirstObjectByType<SongSelectController>();
            Assert.Greater(controller.SongCount,2);
            int index=difficulty==1?controller.SongCount-1:1;
            controller.Select(index);controller.SetDifficulty(difficulty);
            string songId=controller.SongIdAt(index);
            yield return EnterCalibration();
            var calibration=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
            if(save)
            {
                calibration.ChangeOffset(1);calibration.SaveAndExit();
            }
            else
            {
                if(difficulty==2)calibration.ChangeOffset(1);
                calibration.Overlay.OnBackClicked();
                if(difficulty==2)
                {
                    Assert.True(calibration.Overlay.IsExitDialogOpen);
                    GameObject.Find("Discard").GetComponent<Button>().onClick.Invoke();
                }
            }
            yield return ScreenTransitionPlayTests.WaitForTransition();yield return WaitForSelection();
            AssertSelection(songId,difficulty);
            Assert.IsNull(GameSession.CalibrationSelectionSongId,"復元した曲は一度だけ消費する");
            Assert.IsNull(GameSession.CalibrationSelectionDifficulty,"復元した難易度は一度だけ消費する");
        }
        // 次の選曲まで調整前の曲を持ち越さない。
        Object.FindFirstObjectByType<SongSelectController>().ReturnToTitle();
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Object.FindFirstObjectByType<TitleMenuController>().OnStartButton();
        yield return ScreenTransitionPlayTests.WaitForTransition();yield return WaitForSelection();
        var fresh=Object.FindFirstObjectByType<SongSelectController>();
        Assert.AreEqual(0,fresh.SelectedIndex);Assert.AreEqual(0,fresh.SelectedDifficultyIndex);
    }

    static void AssertSelection(string songId,int difficulty)
    {
        var controller=Object.FindFirstObjectByType<SongSelectController>();
        Assert.AreEqual(songId,controller.SongIdAt(controller.SelectedIndex),"調整前に選んでいた曲へ戻る");
        Assert.AreEqual(difficulty,controller.SelectedDifficultyIndex,"難易度も引き継ぐ");
        Assert.NotNull(controller.ChartPreview);
        Assert.AreEqual(songId,controller.ChartPreview.SongId,"試聴も復元した曲を使う");
        Assert.AreEqual(controller.difficultyNames[difficulty],controller.ChartPreview.Difficulty);
    }

    [UnityTest] public IEnumerator MissingSavedSongFallsBackToFirstSong()
    {
        GameSession.CalibrationSelectionSongId="__missing_calibration_song__";
        GameSession.CalibrationSelectionDifficulty="Hard";
        yield return SceneManager.LoadSceneAsync("SongSelect");yield return WaitForSelection();
        var controller=Object.FindFirstObjectByType<SongSelectController>();
        Assert.AreEqual(0,controller.SelectedIndex);Assert.AreEqual(0,controller.SelectedDifficultyIndex);
        Assert.IsNull(GameSession.CalibrationSelectionSongId);
        Assert.IsNull(GameSession.CalibrationSelectionDifficulty);
    }

    [UnityTest] public IEnumerator TimerStaysVisibleAndCountsDuringPracticePauseSettingsAndConfirmation()
    {
        yield return EnterCalibration();
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;var ui=c.Overlay;
        var tab=ui.transform.Find("TimeTab");var text=tab.Find("TimeRemaining").GetComponent<TextMeshProUGUI>();
        Capture("calibration");
        Assert.True(text.gameObject.activeInHierarchy);Assert.AreEqual(ui.transform.childCount-1,tab.GetSiblingIndex());
        foreach(var graphic in tab.GetComponentsInChildren<Graphic>())Assert.False(graphic.raycastTarget);
        c.BeginLivePractice();double before=ui.RemainingSeconds;ui.TickCountdown(2);
        Assert.AreEqual(before-2,ui.RemainingSeconds,.001);
        ui.TogglePractice();Assert.False(c.IsRunning);before=ui.RemainingSeconds;ui.TickCountdown(3);
        Assert.AreEqual(before-3,ui.RemainingSeconds,.001);
        ui.OpenSettings();before=ui.RemainingSeconds;Time.timeScale=0;
        yield return new WaitForSecondsRealtime(.2f);
        Assert.Less(ui.RemainingSeconds,before,"ポーズ中も実時間で進む");Time.timeScale=1;
        ui.TickCountdown(4);Assert.AreEqual(System.Math.Ceiling(ui.RemainingSeconds).ToString(),text.text);
        Assert.Greater(tab.GetSiblingIndex(),ui.transform.Find("AudioSettings").GetSiblingIndex());
        Capture("calibration-settings");
        ui.OnBackClicked();c.ChangeOffset(savedOffset<990?10:-10);ui.OnBackClicked();
        Assert.True(ui.IsExitDialogOpen);before=ui.RemainingSeconds;ui.TickCountdown(5);
        Assert.AreEqual(before-5,ui.RemainingSeconds,.001);
        Assert.Greater(tab.GetSiblingIndex(),ui.transform.Find("UnsavedChanges").GetSiblingIndex());
        Canvas.ForceUpdateCanvases();text.ForceMeshUpdate();Assert.Greater(text.textInfo.characterCount,0);
        Capture("calibration-confirmation");
    }

    [UnityTest] public IEnumerator TimeoutStopsPracticeDiscardsDraftAndReturnsToFreshSelection()
    {
        var controller=Object.FindFirstObjectByType<SongSelectController>();
        controller.Select(controller.SongCount-1);controller.SetDifficulty(2);
        string songId=controller.SongIdAt(controller.SelectedIndex);
        yield return EnterCalibration();
        var manager=Object.FindFirstObjectByType<GamePlayManager>();var c=manager.Calibration;
        c.ChangeOffset(savedOffset<990?10:-10);c.SetSpeedStep(c.SpeedStep==0?1:0);c.BeginLivePractice();
        c.Overlay.TickCountdown(100);
        Assert.True(ScreenTransition.IsBusy);Assert.False(manager.songPlayer.IsPlaying);
        Assert.IsNull(GameSession.CalibrationSelectionSeconds);
        Assert.AreEqual(savedOffset,GameSession.JudgmentOffsetMs);Assert.AreEqual(savedSpeed,GameSession.NoteApproachTime);
        yield return ScreenTransitionPlayTests.WaitForTransition();yield return WaitForSelection();
        Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name);Assert.False(GameSession.IsCalibrationMode);
        AssertSelection(songId,2);
        Assert.That(Object.FindFirstObjectByType<SongSelectSkin>().RemainingSeconds,Is.InRange(95d,100d));
        yield return null;yield return null;
        Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name,"0秒を引き継いで即プレイ開始しない");
    }

    [UnityTest] public IEnumerator TimeoutClosesUnsavedConfirmationWithoutSaving()
    {
        yield return EnterCalibration();
        var c=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        c.ChangeOffset(savedOffset<990?10:-10);c.Overlay.OnBackClicked();Assert.True(c.Overlay.IsExitDialogOpen);
        c.Overlay.TickCountdown(100);
        yield return ScreenTransitionPlayTests.WaitForTransition();yield return WaitForSelection();
        Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name);Assert.AreEqual(savedOffset,GameSession.JudgmentOffsetMs);
    }

    static void Capture(string name)
    {
        string folder=System.Environment.GetEnvironmentVariable("SABER_CALIBRATION_CAPTURE_DIR");
        if(string.IsNullOrEmpty(folder))return;
        System.IO.Directory.CreateDirectory(folder);
        var camera=Camera.main;var canvas=Object.FindFirstObjectByType<CalibrationOverlay>().GetComponent<Canvas>();
        var oldMode=canvas.renderMode;var oldCamera=canvas.worldCamera;float oldDistance=canvas.planeDistance;
        var oldTarget=camera.targetTexture;float oldAspect=camera.aspect;var oldActive=RenderTexture.active;
        var target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);
        var image=new Texture2D(1280,720,TextureFormat.RGB24,false);
        try
        {
            target.Create();camera.targetTexture=target;camera.aspect=1280f/720f;
            canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=camera.nearClipPlane+.1f;
            Canvas.ForceUpdateCanvases();
            foreach(var label in canvas.GetComponentsInChildren<TextMeshProUGUI>())label.ForceMeshUpdate();
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,
                new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1280,720),0,0);image.Apply();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder,name+".png"),image.EncodeToPNG());
        }
        finally
        {
            canvas.renderMode=oldMode;canvas.worldCamera=oldCamera;canvas.planeDistance=oldDistance;
            camera.targetTexture=oldTarget;camera.aspect=oldAspect;RenderTexture.active=oldActive;
            target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(image);Canvas.ForceUpdateCanvases();
        }
    }
}
