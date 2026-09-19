using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実シーンの操作経路を通り、暗転中の入力・時計・次画面の復帰を検証する。
public class ScreenTransitionPlayTests
{
    readonly Dictionary<FieldInfo, object> savedSession = new Dictionary<FieldInfo, object>();
    string scoreTestId;

    [SetUp]
    public void SaveSession()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) savedSession[field] = field.GetValue(null);
        scoreTestId = "__ScreenTransition_" + Guid.NewGuid().ToString("N");
    }

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        Time.timeScale = 1;
        yield return WaitForTransition();
        GameSession.IsCalibrationMode = false;
        var scene = SceneManager.GetActiveScene();
        var empty = SceneManager.CreateScene("TransitionCleanup");
        SceneManager.SetActiveScene(empty);
        if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        var transition = Object.FindFirstObjectByType<ScreenTransition>(FindObjectsInactive.Include);
        if (transition != null) Object.Destroy(transition.gameObject);
        yield return null;
        HighScoreStore.Clear(scoreTestId, "Normal");
        foreach (var entry in savedSession) entry.Key.SetValue(null, entry.Value);
        savedSession.Clear();
    }

    static IEnumerator Open(string scene)
    {
        yield return SceneManager.LoadSceneAsync(scene);
        yield return null;
        yield return null;
        // 実機の外部入力をテストへ混ぜない。
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.autonomous = false;
    }

    public static IEnumerator WaitForTransition()
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.False(ScreenTransition.IsBusy, "画面の暗転や入力ロックが残らない");
    }

    static void AssertReady(string scene)
    {
        Assert.AreEqual(scene, SceneManager.GetActiveScene().name);
        Assert.False(ScreenTransition.IsBusy);
        Assert.NotNull(EventSystem.current);
        Assert.True(EventSystem.current.enabled);
        var curtain = Object.FindFirstObjectByType<ScreenTransitionGraphic>(FindObjectsInactive.Include);
        Assert.NotNull(curtain);
        Assert.NotNull(curtain.GetComponent<CanvasRenderer>(), "幕の描画コンポーネントが存在する");
        Assert.False(curtain.gameObject.activeInHierarchy);
    }

    [UnityTest]
    public IEnumerator AllFourTitlesDepartAndReturnThroughRealButtons()
    {
        for (int concept = 0; concept < TitleConceptSelection.Count; concept++)
        {
            TitleConceptSelection.Select(concept);
            yield return Open("Title");
            Assert.AreEqual(concept, TitleConceptSelection.Current);
            var ctl = Object.FindFirstObjectByType<TitleMenuController>();
            ctl.OnStartButton();
            Assert.True(ScreenTransition.IsBusy);
            Assert.AreEqual("Title", SceneManager.GetActiveScene().name, "退出演出の前にシーンを破棄しない");
            ctl.OnStartButton();
            Assert.False(ScreenTransition.Load("Result"), "二重遷移を受け付けない");
            var motion = Object.FindFirstObjectByType<TitlePresentationMotion>();
            Assert.NotNull(motion);
            while (motion != null && motion.Departure < .2f) yield return null;
            Assert.NotNull(motion);
            Assert.Greater(motion.Departure, .1f);
            Capture("title-" + concept + "-departure");
            yield return WaitForTransition();
            AssertReady("SongSelect");
            var back = GameObject.Find("BackToTitle").GetComponent<Button>();
            if (concept % 2 == 0)
            {
                yield return new WaitForSecondsRealtime(.6f);
                var action = back.GetComponent<MenuNoteAction>();
                action.Sync();
                Assert.False(action.Note.IsJudgeable);
                Assert.True(action.TryShoot());
                double deadline = Time.realtimeSinceStartupAsDouble + 2;
                while (!ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            }
            else back.onClick.Invoke();
            Assert.True(ScreenTransition.IsBusy);
            yield return new WaitForSecondsRealtime(.21f);
            Capture("select-back-" + concept);
            yield return WaitForTransition();
            AssertReady("Title");
        }
    }

    [UnityTest]
    public IEnumerator CalibrationEntrySaveAndDiscardAllUseTheCurtain()
    {
        yield return Open("SongSelect");
        for (int save = 0; save < 2; save++)
        {
            SongSelectSkin.EnterCalibration();
            Assert.True(ScreenTransition.IsBusy);
            Assert.True(GameSession.IsCalibrationMode);
            yield return new WaitForSecondsRealtime(.21f);
            Capture("calibration-entry-" + save);
            yield return WaitForTransition();
            AssertReady("Game");
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            Assert.NotNull(manager.Calibration);
            if (save == 0) manager.Calibration.DiscardAndExit();
            else manager.Calibration.SaveAndExit();
            Assert.True(ScreenTransition.IsBusy);
            SongSelectSkin.EnterCalibration();
            Assert.False(GameSession.IsCalibrationMode, "連打で遷移先のモードを上書きしない");
            yield return WaitForTransition();
            AssertReady("SongSelect");
        }
    }

    [UnityTest]
    public IEnumerator PlayCountdownWaitsUntilVisibleThenFinishAndResultBackAnimate()
    {
        yield return Open("SongSelect");
        var selection = Object.FindFirstObjectByType<SongSelectController>();
        for (int i = 0; i < selection.SongCount; i++)
        {
            selection.Select(i);
            selection.SetDifficulty(0);
            if (selection.startButton.interactable) break;
        }
        Assert.True(selection.startButton.interactable);
        selection.StartGame();
        Assert.True(ScreenTransition.IsBusy);
        string selectedSong = GameSession.SelectedSongId;
        SongSelectSkin.EnterCalibration();
        Assert.False(GameSession.IsCalibrationMode);
        Assert.AreEqual(selectedSong, GameSession.SelectedSongId);
        yield return new WaitForSecondsRealtime(.21f);
        Capture("play-entry");
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        bool sawGameUnderCurtain = false;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < deadline)
        {
            var active = Object.FindFirstObjectByType<GamePlayManager>();
            if (active != null && SceneManager.GetActiveScene().name == "Game")
            {
                sawGameUnderCurtain = true;
                Assert.False(active.songPlayer.IsScheduled, "暗転中には曲もカウントダウンも開始しない");
            }
            yield return null;
        }
        Assert.True(sawGameUnderCurtain);
        AssertReady("Game");
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        deadline = Time.realtimeSinceStartupAsDouble + 20;
        while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.True(manager.songPlayer.IsScheduled);
        // 曲を待ち切る代わりに本番の終了処理を通す。譜面や保存済みスコアは書き換えない。
        GameSession.SelectedSongId = scoreTestId;
        GameSession.SelectedDifficulty = "Normal";
        typeof(GamePlayManager).GetField("finished", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, true);
        typeof(GamePlayManager).GetMethod("FinishGame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
        Assert.True(ScreenTransition.IsBusy);
        yield return new WaitForSecondsRealtime(.21f);
        Capture("play-result");
        yield return WaitForTransition();
        AssertReady("Result");
        Assert.NotNull(EventSystem.current.currentSelectedGameObject, "暗転後もキーボードの戻る先が選択される");
        var result = Object.FindFirstObjectByType<ResultController>();
        string returnScene = result.titleSceneName;
        result.OnBackButton();
        Assert.True(ScreenTransition.IsBusy);
        yield return new WaitForSecondsRealtime(.21f);
        Capture("result-back");
        yield return WaitForTransition();
        AssertReady(returnScene);
    }

    [UnityTest]
    public IEnumerator PausedClockAndSameSceneReloadStillFinishAndRestoreInput()
    {
        yield return Open("Title");
        Time.timeScale = 0;
        TitleConceptSelection.Select(2);
        Assert.True(ScreenTransition.Load("Title"));
        var eventSystem = Object.FindFirstObjectByType<EventSystem>();
        Assert.False(eventSystem.enabled);
        yield return WaitForTransition();
        AssertReady("Title");
        Assert.AreEqual(2, TitleConceptSelection.Current);
        Assert.AreEqual(0, Time.timeScale, "ゲームの一時停止設定を勝手に変更しない");
    }

    [UnityTest]
    public IEnumerator InvalidDestinationLeavesTheCurrentScreenUsable()
    {
        yield return Open("SongSelect");
        LogAssert.Expect(LogType.Warning, "画面を読み込めません: MissingTransitionScene");
        Assert.False(ScreenTransition.Load("MissingTransitionScene"));
        Assert.False(ScreenTransition.IsBusy);
        Assert.True(EventSystem.current.enabled);
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
    }

    [UnityTest]
    public IEnumerator InterruptedDepartureRestoresOnlyPreviouslyEnabledInput()
    {
        yield return Open("Title");
        var activeInput = EventSystem.current;
        var unused = new GameObject("DisabledInput");
        unused.SetActive(false);
        var disabledInput = unused.AddComponent<EventSystem>();
        disabledInput.enabled = false;
        unused.SetActive(true);
        Assert.True(ScreenTransition.Load("SongSelect"));
        Assert.False(activeInput.enabled);
        yield return null;
        Object.Destroy(Object.FindFirstObjectByType<ScreenTransition>().gameObject);
        yield return null;
        Assert.False(ScreenTransition.IsBusy);
        Assert.True(activeInput.enabled);
        Assert.False(disabledInput.enabled);
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
    }

    static void Capture(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-transitionCapture");
        if (index < 0 || index + 1 >= args.Length) return;
        Directory.CreateDirectory(args[index + 1]);
        // バッチではScreenCaptureが保存しないため、実カメラとUIを描画先へ一時的に接続する。
        var camera = Camera.main;
        Assert.NotNull(camera);
        var curtain = Object.FindFirstObjectByType<ScreenTransitionGraphic>();
        Assert.NotNull(curtain.GetComponent<CanvasRenderer>());
        var overlays = new List<Canvas>();
        var cameras = new List<Camera>();
        var distances = new List<float>();
        var oldTarget = camera.targetTexture;
        float oldAspect = camera.aspect;
        var oldActive = RenderTexture.active;
        var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        try
        {
            target.Create();
            camera.targetTexture = target;
            camera.aspect = 1280f / 720f;
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                overlays.Add(canvas); cameras.Add(canvas.worldCamera); distances.Add(canvas.planeDistance);
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = camera.nearClipPlane + .1f;
            }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(args[index + 1], name + ".png"), image.EncodeToPNG());
        }
        finally
        {
            for (int i = 0; i < overlays.Count; i++)
            {
                overlays[i].renderMode = RenderMode.ScreenSpaceOverlay;
                overlays[i].worldCamera = cameras[i];
                overlays[i].planeDistance = distances[i];
            }
            camera.targetTexture = oldTarget;
            camera.aspect = oldAspect;
            RenderTexture.active = oldActive;
            target.Release();
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(image);
        }
    }
}
