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
                ", calibration="+GameSession.IsCalibrationMode+", leaving="+typeof(GamePlayManager).GetField("leaving",flags).GetValue(manager));
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
        // 来場者の出口(次の曲へ)と照準を出すのは次の曲がある回だけ(2026-10-31 ユーザー「来場者が自分でタイトルへ戻れる的を消す」)。
        // 出口の決まりは、2曲モードの1曲目の結果で見る。
        VisitorPlays.SetSongsPerVisitorForTest(2); VisitorPlays.BeginVisitor();
        try
        {
            // 曲名は選曲と同じ表示名(2026-10-29 から「校歌@音楽祭ver」。古い「校歌」のままだと名前が違うので2行目の和名が出ていた)。
            GameSession.SelectedSongId="Epilogue";GameSession.SelectedSongTitle=SongSelectController.DisplaySongTitle("Epilogue");
            GameSession.FinalScore=0;GameSession.FinalMaxCombo=0;GameSession.FinalComboBonus=0;
            GameSession.FinalHit=GameSession.FinalMiss=GameSession.FinalPerfect=GameSession.FinalGreat=GameSession.FinalGood=GameSession.FinalBad=0;
            GameSession.FinalDailyRanking=null;
            yield return SceneManager.LoadSceneAsync("Result");
            yield return new WaitForSecondsRealtime(.5f);
            var pointer = Object.FindFirstObjectByType<SaberUIPointer>();
            Assert.NotNull(pointer, "次の曲がある回は照準で「次の曲へ」を指す");
            var guard = (SaberUIArrivalGuard)typeof(SaberUIPointer).GetField("arrival",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pointer);
            for (int i=0;i<7;i++) guard.Tick(.1f);
            Assert.False(guard.Tick(.1f));
            Object.FindFirstObjectByType<ResultReveal>().Tick(999);
            Assert.True(guard.Tick(.1f)); Assert.True(guard.CaptureTarget, "出口の受付開始時にも初回対象を保護する");
            Assert.IsNull(GameObject.Find("TitleJp"));
            var dwell=Object.FindFirstObjectByType<SaberDwellTarget>();Assert.NotNull(dwell);Assert.AreEqual(1.5f,dwell.dwellSeconds);
            Capture("result");
        }
        finally { VisitorPlays.BeginVisitor(); VisitorPlays.SetSongsPerVisitorForTest(1); }
    }
    [UnityTest] public IEnumerator SelectionHitAreasLeaveTheStartInstructionUntargeted()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        while (Object.FindFirstObjectByType<SongSelectSkin>()?.IsReady != true && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        var controller = Object.FindFirstObjectByType<SongSelectController>();
        var guide = Object.FindFirstObjectByType<SongSelectIdleGuide>(); guide.RegisterActivity();
        var aim = Object.FindFirstObjectByType<SongSelectAimPointer>(); aim.enabled = false;
        yield return ScreenTransitionPlayTests.WaitForTransition();
        yield return new WaitForSecondsRealtime(.6f);
        var layout = (RectTransform)GameObject.Find("DiscSelectLayout").transform;
        var canvas = layout.GetComponentInParent<Canvas>();
        System.Func<float, float, Vector2> point = (x,y) => RectTransformUtility.WorldToScreenPoint(canvas.worldCamera,
            layout.TransformPoint(new Vector3(x - 960, 540 - y)));
        Canvas.ForceUpdateCanvases();
        // 2026-10-30「④もすべてやる」: まん中の下の一行は y=710(大きくした難易度の当たりは y=740 から)。その帯に当たりが無いこと。
        foreach (var p in new[] { point(960,700), point(960,725), point(1130,712) })
        {
            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
            var es = UnityEngine.EventSystems.EventSystem.current;
            es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = p }, hits);
            Assert.False(hits.Exists(h => h.gameObject.GetComponentInParent<SongSelectDiscTarget>() != null));
        }
        var normal = controller.difficultyButtons[1].GetComponent<SongSelectDiscTarget>();
        Assert.True(normal.IsRaycastLocationValid(point(960,780), canvas.worldCamera));
        aim.BeginArrival(); aim.TickAt(normal.ScreenRect().center,.1f);
        for (int i=0;i<30;i++) aim.TickAt(normal.ScreenRect().center,.1f);
        Assert.Zero(aim.ShotCount, "入場時の難易度ボタンも一度外すまで決定しない");
        for (int i=0;i<2;i++) aim.TickAt(point(960,725),.1f);
        for (int i=0;i<10;i++) aim.TickAt(normal.ScreenRect().center,.1f);
        Assert.AreEqual(1,aim.ShotCount);
        var status=Object.FindFirstObjectByType<MenuSaberStatus>(); Assert.NotNull(status);
        var input=InputPoint.Instance; Assert.NotNull(input);
        double red=input.LastReceivedTime, blue=input.LastReceivedTime2;
        try
        {
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,Time.realtimeSinceStartupAsDouble);
            typeof(InputPoint).GetProperty("LastReceivedTime2").SetValue(input,-1000d);
            status.SendMessage("Update");
            var label=status.GetComponentInChildren<TMPro.TMP_Text>();
            StringAssert.Contains("赤 ●",label.text); StringAssert.Contains("青 ○",label.text);
            Capture("selection-reconsidered");
        }
        finally
        {
            typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,red);
            typeof(InputPoint).GetProperty("LastReceivedTime2").SetValue(input,blue);
        }
        aim.TickAt(point(960,743),.1f); aim.TickAt(point(960,743),.1f);
        for(int i=0;i<6;i++) aim.TickAt(point(960,452),.1f);
        var skin=Object.FindFirstObjectByType<SongSelectSkin>(); skin.SendMessage("Update");
        Capture("selection-holding");
        skin.TickCountdown(skin.RemainingSeconds-8.5); skin.SendMessage("Update");
        Capture("selection-countdown");
    }

    [UnityTest] public IEnumerator CalibrationArrivalOverExitCannotBounceBackToSelection()
    {
        GameSession.IsCalibrationMode = true;
        yield return SceneManager.LoadSceneAsync("Game");
        yield return ScreenTransitionPlayTests.WaitForTransition();
        yield return new WaitForSecondsRealtime(.5f);
        var pointer=Object.FindFirstObjectByType<SaberUIPointer>(); pointer.enabled=false;
        var overlay=Object.FindFirstObjectByType<CalibrationOverlay>();
        var calibration=Object.FindFirstObjectByType<GamePlayManager>().Calibration;
        calibration.ChangeOffset(calibration.Draft.OffsetMs < 990 ? 10 : -10);
        var target=overlay.transform.Find("Controls/Back") as RectTransform;
        Assert.NotNull(target);
        Vector2 point=SongSelectAimPointer.RectOnScreen(target).center;
        pointer.BeginArrival();
        for(int i=0;i<50;i++) pointer.TickAimAt(point,.1f);
        Assert.False(overlay.IsExitDialogOpen); Assert.Zero(pointer.AimProgress01);
        pointer.TickAimAt(new Vector2(Screen.width*.5f,Screen.height*.5f),.1f);
        pointer.TickAimAt(new Vector2(Screen.width*.5f,Screen.height*.5f),.1f);
        for(int i=0;i<16;i++) pointer.TickAimAt(point,.1f);
        Assert.True(overlay.IsExitDialogOpen);
    }

    [UnityTest] public IEnumerator EarlyRankStaysEmptyAndComboUsesTheMaximumComboPalette()
    {
        GameSession.SelectedSongId="Morning"; GameSession.SelectedDifficulty="normal";
        GameSession.TutorialPending=false; GameSession.IsCalibrationMode=false;
        yield return SceneManager.LoadSceneAsync("Game");
        yield return ScreenTransitionPlayTests.WaitForTransition();
        yield return new WaitForSecondsRealtime(.5f);
        var hud=Object.FindFirstObjectByType<GameHUDSkin>(); Assert.NotNull(hud);
        var score=Object.FindFirstObjectByType<ScoreManager>(); score.Reset();
        hud.SendMessage("Update");
        Assert.AreEqual("",hud.transform.Find("RankBadge/RankLetter").GetComponent<TMPro.TMP_Text>().text);
        Assert.IsNull(Object.FindFirstObjectByType<MenuSaberStatus>(), "通常プレイへ状態表示を持ち込まない");
        typeof(ScoreManager).GetProperty("Combo").SetValue(score, 100);
        typeof(ScoreManager).GetProperty("MaxCombo").SetValue(score, 100);
        hud.SendMessage("Update");
        var combo=hud.transform.Find("ComboValue").GetComponent<TMPro.TMP_Text>();
        var max=hud.transform.Find("MaxComboValue").GetComponent<TMPro.TMP_Text>();
        Assert.AreEqual(max.colorGradient.bottomLeft,combo.colorGradient.bottomLeft);
        Capture("game-reconsidered");
        DisplaySettings.SetReducedEffectsForTest(true); yield return null;
        Capture("game-reconsidered-low");
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
