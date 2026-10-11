using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実際の各シーンで、カタログの修正箇所を確認。出力先を指定した実行では通常/LOWを撮影する。
public class UICatalogPlayTests
{
    readonly Dictionary<FieldInfo, object> saved = new Dictionary<FieldInfo, object>();
    [SetUp] public void Setup()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) saved[field] = field.GetValue(null);
        GameSession.IsCalibrationMode = false; GameSession.TutorialPending = false;
        GameSession.CalibrationSelectionSeconds = null;
        DisplaySettings.SetReducedEffectsForTest(false);
    }
    [UnityTearDown] public IEnumerator Cleanup()
    {
        var transition = Object.FindFirstObjectByType<ScreenTransition>();
        if (transition != null) Object.Destroy(transition.gameObject);
        yield return null;
        var old = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("UICatalogCleanup"));
        if (old.IsValid() && old.isLoaded) yield return SceneManager.UnloadSceneAsync(old);
        foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
        saved.Clear(); DisplaySettings.ResetReducedEffectsCacheForTest();
    }
    [UnityTest] public IEnumerator TitlePromptAndTutorialHaveBothHandsAndOneInstruction()
    {
        yield return SceneManager.LoadSceneAsync("Title");
        yield return new WaitForSecondsRealtime(.5f);
        Assert.NotNull(GameObject.Find("TitleSaber")); Assert.NotNull(GameObject.Find("TitleSaberLeft"));
        Capture("title");
        Object.FindFirstObjectByType<TitleSceneSkin>().TryStartPresentation();
        double deadline = Time.realtimeSinceStartupAsDouble + 8;
        TitleTutorialPrompt prompt;
        while (((prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>()) == null || !prompt.CanAnswer) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(prompt); Assert.True(prompt.CanAnswer);
        Capture("title-prompt");
        prompt.Choose(true, "catalog-test");
        deadline = Time.realtimeSinceStartupAsDouble + 15;
        while ((SceneManager.GetActiveScene().name != "Game" || ScreenTransition.IsBusy) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        yield return new WaitForSecondsRealtime(.5f);
        var tutorial = Object.FindFirstObjectByType<TutorialController>();
        Assert.NotNull(tutorial); Assert.NotNull(tutorial.Overlay);
        Capture("tutorial");
        DisplaySettings.SetReducedEffectsForTest(true);
        yield return null; yield return null;
        Capture("tutorial-low");
    }
    [UnityTest] public IEnumerator HardNoiseStaysBehindSelectionControlsAndCalibrationRemainsReadable()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        while (Object.FindFirstObjectByType<SongSelectSkin>()?.IsReady != true && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        var select = Object.FindFirstObjectByType<SongSelectController>();
        Assert.NotNull(select); select.SetDifficulty(2);
        Object.FindFirstObjectByType<SongSelectIdleGuide>().RegisterActivity();
        yield return new WaitForSecondsRealtime(.6f);
        var layout = GameObject.Find("DiscSelectLayout").transform;
        var noise = layout.GetComponentInChildren<MenuSignalNoise>();
        Assert.NotNull(noise); Assert.AreEqual(1, noise.transform.GetSiblingIndex());
        Capture("selection-hard");
        DisplaySettings.SetReducedEffectsForTest(true);
        yield return null; yield return null;
        Capture("selection-hard-low");
        DisplaySettings.SetReducedEffectsForTest(false);
        SongSelectSkin.EnterCalibration();
        yield return ScreenTransitionPlayTests.WaitForTransition();
        yield return new WaitForSecondsRealtime(.5f);
        var overlay = Object.FindFirstObjectByType<CalibrationOverlay>();
        Assert.NotNull(overlay); Assert.AreEqual(3, overlay.SpeedButtonCount);
        Capture("calibration");
        DisplaySettings.SetReducedEffectsForTest(true);
        yield return null; yield return null;
        Capture("calibration-low");
    }
    [UnityTest] public IEnumerator EscapeRequiresTwoDistinctPressesWithinTwoSeconds()
    {
        var oldSettings = UnityEngine.InputSystem.InputSystem.settings;
        var settings = Object.Instantiate(oldSettings);
        UnityEngine.InputSystem.InputSystem.settings = settings;
        settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        settings.editorInputBehaviorInPlayMode = UnityEngine.InputSystem.InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        var keyboard = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Keyboard>();
        try
        {
            GameSession.TutorialPending = true;
            yield return SceneManager.LoadSceneAsync("Game");
            yield return ScreenTransitionPlayTests.WaitForTransition();
            yield return new WaitForSecondsRealtime(.5f);
            Assert.NotNull(Object.FindFirstObjectByType<GamePlayManager>().Tutorial);
            keyboard.MakeCurrent();
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Escape));
            yield return null; yield return null;
            Assert.False(ScreenTransition.IsBusy);
            var manager=Object.FindFirstObjectByType<GamePlayManager>();
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            Assert.NotNull(Object.FindFirstObjectByType<OperatorNotice>(),
                "escape="+keyboard.escapeKey.isPressed+", current="+(keyboard==UnityEngine.InputSystem.Keyboard.current)+
                ", enabled="+keyboard.enabled+", finished="+typeof(GamePlayManager).GetField("finished",flags).GetValue(manager)+
                ", calibration="+GameSession.IsCalibrationMode+", confirmation="+typeof(GamePlayManager).GetField("returnConfirmationUntil",flags).GetValue(manager));
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null; yield return new WaitForSecondsRealtime(2.1f);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Escape));
            yield return null; yield return null; Assert.False(ScreenTransition.IsBusy, "確認の期限後は再び一押し目になる");
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null; yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Escape));
            yield return null; yield return null; Assert.True(ScreenTransition.IsBusy);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return ScreenTransitionPlayTests.WaitForTransition();
            Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        }
        finally
        {
            if(keyboard.added) UnityEngine.InputSystem.InputSystem.RemoveDevice(keyboard);
            UnityEngine.InputSystem.InputSystem.settings=oldSettings; Object.DestroyImmediate(settings);
        }
    }
    [UnityTest] public IEnumerator JapaneseResultTitleIsNotDuplicatedAndExitUsesLongDwell()
    {
        GameSession.SelectedSongId="Epilogue";GameSession.SelectedSongTitle="校歌";
        GameSession.FinalScore=0;GameSession.FinalMaxCombo=0;GameSession.FinalComboBonus=0;
        GameSession.FinalHit=GameSession.FinalMiss=GameSession.FinalPerfect=GameSession.FinalGreat=GameSession.FinalGood=GameSession.FinalBad=0;
        GameSession.FinalDailyRanking=null;
        yield return SceneManager.LoadSceneAsync("Result");
        yield return new WaitForSecondsRealtime(.5f);
        Object.FindFirstObjectByType<ResultReveal>().Tick(999);
        Assert.IsNull(GameObject.Find("TitleJp"));
        var dwell=Object.FindFirstObjectByType<SaberDwellTarget>();Assert.NotNull(dwell);Assert.AreEqual(1.5f,dwell.dwellSeconds);
        Capture("result");
    }
    public static void Capture(string name)
    {
        string output = System.Environment.GetEnvironmentVariable("SABER_UI_CATALOG_CAPTURE_DIR");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var camera = Camera.main; Assert.NotNull(camera);
        var overlays = new List<Canvas>(); var cameras = new List<Camera>(); var distances = new List<float>();
        var oldTarget = camera.targetTexture; float oldAspect = camera.aspect; var oldActive = RenderTexture.active;
        var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        try
        {
            target.Create(); camera.targetTexture = target; camera.aspect = 1280f/720f;
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                overlays.Add(canvas); cameras.Add(canvas.worldCamera); distances.Add(canvas.planeDistance);
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = camera.nearClipPlane + .1f;
            }
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target; image.ReadPixels(new Rect(0,0,1280,720),0,0); image.Apply();
            File.WriteAllBytes(Path.Combine(output,name+".png"), image.EncodeToPNG());
        }
        finally
        {
            for(int i=0;i<overlays.Count;i++) { overlays[i].renderMode=RenderMode.ScreenSpaceOverlay; overlays[i].worldCamera=cameras[i]; overlays[i].planeDistance=distances[i]; }
            camera.targetTexture=oldTarget;camera.aspect=oldAspect;RenderTexture.active=oldActive;
            target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(image);
        }
    }
}
