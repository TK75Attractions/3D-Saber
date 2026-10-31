using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実シーンのonClick・Enter・照準・Escを通す。モードも判定設定も保存しない。
public class VisitorPlaysPlayTests
{
    readonly Dictionary<FieldInfo, object> session = new Dictionary<FieldInfo, object>();
    readonly Dictionary<string, int?> prefs = new Dictionary<string, int?>();
    readonly string[] prefKeys =
    {
        VisitorPlays.PreferenceKey, "judgmentOffsetMs", CalibrationDraft.ActiveKey,
        CalibrationDraft.SpeakerKey, CalibrationDraft.HeadphoneKey, "songSelectPreviewMuted"
    };
    float? savedSpeed;
    Keyboard keyboard;
    Mouse mouse;
    InputPoint input;
    InputSettings originalSettings, testSettings;
    ResultReveal reveal;
    Button next;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) session[field] = field.GetValue(null);
        foreach (string key in prefKeys)
            prefs[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) : (int?)null;
        savedSpeed = PlayerPrefs.HasKey("noteApproachTime")
            ? PlayerPrefs.GetFloat("noteApproachTime") : (float?)null;
        VisitorPlays.SetSongsPerVisitorForTest(2);
        VisitorPlays.BeginVisitor();
        GameSession.SetPlayerSettingsForTest(true);
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = GameSession.TutorialRangeOnly = false;
        GameSession.CalibrationSelectionSeconds = null;
        GameSession.CalibrationSelectionSongId = GameSession.CalibrationSelectionDifficulty = null;

        originalSettings = InputSystem.settings;
        testSettings = Object.Instantiate(originalSettings);
        InputSystem.settings = testSettings;
        testSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testSettings.editorInputBehaviorInPlayMode =
            InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        keyboard = InputSystem.AddDevice<Keyboard>();
        mouse = InputSystem.AddDevice<Mouse>();
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None))
            Object.DestroyImmediate(old.gameObject);
        var receiver = new GameObject("VisitorPlaysTestInput");
        receiver.SetActive(false);
        Object.DontDestroyOnLoad(receiver);
        input = receiver.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        receiver.SetActive(true);
        InputPoint.EnsureInstance();
        SceneManager.sceneLoaded += PrepareScene;
        yield return Open("Title");
    }

    // 試聴はこのテストの対象外。Startより先に外し、既存の消音解除による保存も避ける。
    static void PrepareScene(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "SongSelect") return;
        foreach (var root in scene.GetRootGameObjects())
            foreach (var selection in root.GetComponentsInChildren<SongSelectController>(true))
                selection.previewSource = null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        try
        {
            var prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>();
            if (prompt != null) prompt.Cancel();
            yield return WaitForTransition();
            var scene = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(SceneManager.CreateScene("VisitorPlaysCleanup"));
            if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
        finally
        {
            SceneManager.sceneLoaded -= PrepareScene;
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
            if (originalSettings != null) InputSystem.settings = originalSettings;
            if (testSettings != null) Object.DestroyImmediate(testSettings);
            if (input != null) Object.DestroyImmediate(input.gameObject);
            foreach (var value in session) value.Key.SetValue(null, value.Value);
            session.Clear();
            ResultSelectionReturn.Remember(null, null);
            GameSession.SetPlayerSettingsForTest(false);
            VisitorPlays.BeginVisitor();
            VisitorPlays.SetSongsPerVisitorForTest(1);
        }
        foreach (var pref in prefs)
        {
            Assert.AreEqual(pref.Value.HasValue, PlayerPrefs.HasKey(pref.Key), pref.Key);
            if (pref.Value.HasValue) Assert.AreEqual(pref.Value.Value, PlayerPrefs.GetInt(pref.Key), pref.Key);
        }
        Assert.AreEqual(savedSpeed.HasValue, PlayerPrefs.HasKey("noteApproachTime"));
        if (savedSpeed.HasValue) Assert.AreEqual(savedSpeed.Value, PlayerPrefs.GetFloat("noteApproachTime"));
        prefs.Clear();
    }

    static IEnumerator Open(string name)
    {
        yield return SceneManager.LoadSceneAsync(name);
        yield return null;
        yield return null;
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.autonomous = false;
    }

    IEnumerator OpenResult(string song = "Epilogue", string difficulty = "Hard")
    {
        GameSession.SelectedSongId = song;
        GameSession.SelectedSongTitle = SongSelectController.DisplaySongTitle(song);
        GameSession.SelectedDifficulty = difficulty;
        GameSession.ResetResult();
        // 全MISSでも曲数は消費する。実際の成績保存には触れない。
        GameSession.FinalMiss = 1;
        yield return Open("Result");
        FindResult();
    }

    void FindResult()
    {
        reveal = Object.FindFirstObjectByType<ResultReveal>();
        Assert.NotNull(reveal);
        reveal.enabled = false;
        reveal.Tick(0);
        var controller = Object.FindFirstObjectByType<ResultController>();
        Assert.NotNull(controller);
        var buttons = controller.GetComponent<Canvas>().GetComponentsInChildren<Button>(true);
        Assert.AreEqual(1, buttons.Length);
        next = buttons[0];
        Assert.AreEqual(1, next.onClick.GetPersistentEventCount());
        Assert.AreEqual("OnBackButton", next.onClick.GetPersistentMethodName(0));
        Canvas.ForceUpdateCanvases();
    }

    static TMP_Text Text(string name)
    {
        var go = GameObject.Find(name);
        Assert.NotNull(go, name);
        return go.GetComponent<TMP_Text>();
    }

    string ButtonText => next.transform.Find("LabelTMP").GetComponent<TMP_Text>().text;

    void ClickNext()
    {
        reveal.Tick(999);
        Assert.True(next.IsInteractable());
        ExecuteEvents.Execute(next.gameObject, new PointerEventData(EventSystem.current),
            ExecuteEvents.pointerClickHandler);
        Assert.True(ScreenTransition.IsBusy);
    }

    static IEnumerator WaitForTransition()
    {
        double until = Time.realtimeSinceStartupAsDouble + 20;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < until) yield return null;
        Assert.False(ScreenTransition.IsBusy);
        yield return null;
        yield return null;
    }

    static IEnumerator SelectionReady()
    {
        yield return WaitForTransition();
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        double until = Time.realtimeSinceStartupAsDouble + 10;
        SongSelectSkin skin;
        while (((skin = Object.FindFirstObjectByType<SongSelectSkin>()) == null || !skin.IsReady)
            && Time.realtimeSinceStartupAsDouble < until) yield return null;
        Assert.NotNull(skin);
        Assert.True(skin.IsReady);
    }

    IEnumerator Tap(params Key[] keys)
    {
        keyboard.MakeCurrent();
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        yield return null;
    }

    [UnityTest]
    public IEnumerator FirstResultReturnsToTheSameSelectionAndSecondResultEndsTheVisit()
    {
        GameSession.JudgmentOffsetMs = 180;
        GameSession.NoteApproachTime = .5f;
        yield return OpenResult();
        Assert.AreEqual(1, VisitorPlays.CompletedSongs);
        Assert.AreEqual("次の曲へ", ButtonText);
        Assert.AreEqual("あと1曲", Text("NextSongNotice").text);
        Assert.False(next.IsInteractable());
        Assert.Zero(next.GetComponent<CanvasGroup>().alpha);
        ExecuteEvents.Execute(next.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);

        // 同じ結果を作り直しても、2曲目を消費しない。
        yield return Open("Result");
        FindResult();
        Assert.AreEqual(1, VisitorPlays.CompletedSongs);
        ClickNext();
        yield return SelectionReady();
        var selection = Object.FindFirstObjectByType<SongSelectController>();
        Assert.AreEqual("Epilogue", selection.SongIdAt(selection.SelectedIndex));
        Assert.AreEqual("Hard", selection.CurrentDifficultyName);
        Assert.AreEqual(180, GameSession.JudgmentOffsetMs);
        Assert.AreEqual(.5f, GameSession.NoteApproachTime);
        Assert.False(GameSession.SongSelectOperated);
        Assert.AreEqual("2曲目", Text("VisitorProgressText").text);
        Assert.AreEqual("● ●", Text("VisitorProgressDots").text);
        Assert.IsNull(Object.FindFirstObjectByType<TitleTutorialPrompt>());
        Assert.False(GameSession.TutorialPending);

        yield return OpenResult();
        Assert.AreEqual(2, VisitorPlays.CompletedSongs);
        // 2曲目のあとは来場者の出口(タイトルへ)を出さず、スタッフが Enter などで戻す(2026-10-31 ユーザー「来場者が自分でタイトルへ戻れる的を消す」)。
        Assert.False(next.gameObject.activeSelf);
        Assert.IsNull(Object.FindFirstObjectByType<SaberUIPointer>(), "指す的が無いので照準も出さない");
        Assert.IsNull(GameObject.Find("NextSongNotice"));
        reveal.Tick(999);
        yield return Tap(Key.Enter);
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.Zero(VisitorPlays.CompletedSongs);
        Assert.AreEqual(GameSession.JudgmentOffsetDefaultMs, GameSession.JudgmentOffsetMs);
        Assert.AreEqual(GameSession.NoteApproachTimeDefault, GameSession.NoteApproachTime);
    }

    [UnityTest]
    public IEnumerator FirstEnterSkipsAndSecondEnterGoesToTheSecondSelection()
    {
        yield return OpenResult();
        reveal.enabled = true;
        yield return Tap(Key.Enter);
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        Assert.True(next.IsInteractable());
        yield return Tap(Key.Enter);
        yield return SelectionReady();
        Assert.AreEqual(1, VisitorPlays.CompletedSongs);
    }

    [UnityTest]
    public IEnumerator EscapeTwiceEndsTheVisitEvenAfterTheFirstSong()
    {
        yield return OpenResult();
        reveal.enabled = true;
        yield return Tap(Key.Escape);
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        Assert.True(next.IsInteractable());
        Assert.NotNull(Object.FindFirstObjectByType<OperatorNotice>());
        yield return Tap(Key.Escape);
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.Zero(VisitorPlays.CompletedSongs);
    }

    [UnityTest]
    public IEnumerator SecretDifficultyIsRestoredToo()
    {
        yield return OpenResult("RushE", "E");
        ClickNext();
        yield return SelectionReady();
        var selection = Object.FindFirstObjectByType<SongSelectController>();
        Assert.AreEqual("RushE", selection.SongIdAt(selection.SelectedIndex));
        Assert.AreEqual("E", selection.CurrentDifficultyName);
        Assert.True(selection.RushESecret.Unlocked);
        Assert.False(selection.RushESecret.Transitioning);
    }

    [UnityTest]
    public IEnumerator TitleTwoKeysToggleTheModeAndCopyWithoutReloading()
    {
        var title = Object.FindFirstObjectByType<TitleMenuController>();
        int concept = TitleConceptSelection.Current;
        Assert.AreEqual("1回で2曲 あそべる", Text("VisitorModeText").text);
        yield return Tap(Key.Digit2);
        Assert.AreEqual(1, VisitorPlays.SongsPerVisitor);
        Assert.AreEqual("1回で1曲 あそべる", Text("VisitorModeText").text);
        yield return Tap(Key.Numpad2);
        Assert.AreEqual(2, VisitorPlays.SongsPerVisitor);
        Assert.AreEqual("1回で2曲 あそべる", Text("VisitorModeText").text);
        Assert.AreSame(title, Object.FindFirstObjectByType<TitleMenuController>());
        Assert.AreEqual(concept, TitleConceptSelection.Current);
        Assert.NotNull(Object.FindFirstObjectByType<OperatorNotice>());
    }

    [UnityTest]
    public IEnumerator OperatorChordsAndOldBackgroundKeysDoNothing()
    {
        var title = Object.FindFirstObjectByType<TitleMenuController>();
        int concept = TitleConceptSelection.Current;
        foreach (var modifier in new[] { Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt,
            Key.RightAlt, Key.LeftMeta, Key.RightMeta })
        {
            yield return Tap(modifier, Key.Digit2);
            yield return Tap(modifier, Key.Numpad2);
            Assert.AreEqual(2, VisitorPlays.SongsPerVisitor);
        }
        foreach (var key in new[] { Key.Digit0, Key.Digit1, Key.Digit3,
            Key.Numpad0, Key.Numpad1, Key.Numpad3 })
        {
            yield return Tap(key);
            Assert.False(ScreenTransition.IsBusy);
            Assert.AreSame(title, Object.FindFirstObjectByType<TitleMenuController>());
            Assert.AreEqual(concept, TitleConceptSelection.Current);
        }
    }

    [UnityTest]
    public IEnumerator ModeIsLockedAfterSlashingAndWhileThePromptIsOpen()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.True(skin.TryStartPresentation());
        yield return Tap(Key.Digit2);
        Assert.AreEqual(2, VisitorPlays.SongsPerVisitor);
        double until = Time.realtimeSinceStartupAsDouble + 5;
        TitleTutorialPrompt prompt;
        while (((prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>()) == null || !prompt.CanAnswer)
            && Time.realtimeSinceStartupAsDouble < until) yield return null;
        Assert.NotNull(prompt);
        Assert.True(prompt.CanAnswer);
        yield return Tap(Key.Numpad2);
        Assert.AreEqual(2, VisitorPlays.SongsPerVisitor);
        Assert.False(prompt.Answered);
    }

    [UnityTest]
    public IEnumerator CurtainDoesNotAcceptModeChanges()
    {
        Assert.True(ScreenTransition.Load("Title", ScreenTransition.Style.Back));
        yield return Tap(Key.Digit2);
        Assert.AreEqual(2, VisitorPlays.SongsPerVisitor);
        yield return WaitForTransition();
        Assert.AreEqual("1回で2曲 あそべる", Text("VisitorModeText").text);
    }

    [UnityTest]
    public IEnumerator SelectionShowsFirstSongOnlyInTwoSongMode()
    {
        yield return Open("SongSelect");
        yield return SelectionReady();
        Assert.AreEqual("1曲目", Text("VisitorProgressText").text);
        Assert.AreEqual("● ○", Text("VisitorProgressDots").text);
        VisitorPlays.SetSongsPerVisitorForTest(1);
        yield return Open("SongSelect");
        yield return SelectionReady();
        Assert.IsNull(GameObject.Find("VisitorProgress"));
        yield return OpenResult();
        Assert.False(next.gameObject.activeSelf, "1曲モードの結果には来場者の出口を出さない");
        Assert.IsNull(GameObject.Find("NextSongNotice"));
    }

    [UnityTest]
    public IEnumerator AimDwellGoesToTheSecondSelection()
    {
        yield return OpenResult();
        reveal.Tick(999);
        Canvas.ForceUpdateCanvases();
        var pointer = Object.FindFirstObjectByType<SaberUIPointer>();
        Assert.NotNull(pointer);
        Assert.NotNull(pointer.AimReticle);
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, next.transform.position);
        float x = (screen.x / Screen.width * 2 - 1) / input.sensitivity;
        float y = (screen.y / Screen.height * 2 - 1) / input.sensitivity;
        byte[] packet = Encoding.ASCII.GetBytes(x.ToString("R", CultureInfo.InvariantCulture)
            + "," + y.ToString("R", CultureInfo.InvariantCulture));
        bool hovered = false;
        using (var sender = new UdpClient())
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, input.port);
            byte[] center = Encoding.ASCII.GetBytes("0,0");
            sender.Send(center, center.Length, endpoint);
            double until = Time.realtimeSinceStartupAsDouble + 2;
            while (!input.IsRecentlyActive() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.True(input.IsRecentlyActive());
            yield return new WaitForSecondsRealtime(SaberUIPointer.StaleSeconds + .2f);
            until = Time.realtimeSinceStartupAsDouble + 5;
            while (!ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < until)
            {
                sender.Send(packet, packet.Length, endpoint);
                if (pointer != null && pointer.HoveredForTest == next) hovered = true;
                yield return null;
            }
        }
        Assert.True(hovered);
        Assert.True(ScreenTransition.IsBusy);
        yield return SelectionReady();
        Assert.AreEqual(1, VisitorPlays.CompletedSongs);
    }

    static int FreePort()
    {
        using (var receiver = new UdpClient(0))
            return ((IPEndPoint)receiver.Client.LocalEndPoint).Port;
    }
}
