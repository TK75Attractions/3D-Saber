using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 選曲の専用キーと、クリック後に残るボタン選択の二重動作を実シーンで確認する。
public class SongSelectKeyboardPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    SongSelectController controller;
    Keyboard keyboard;
    Mouse mouse;
    Gamepad gamepad;
    InputPoint input;
    InputSettings originalInputSettings, testInputSettings;
    bool hadProjectorPreference, savedProjectorMode;
    int savedProjectorPreference;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        hadProjectorPreference = PlayerPrefs.HasKey("displayProjectorMode");
        savedProjectorPreference = PlayerPrefs.GetInt("displayProjectorMode");
        savedProjectorMode = DisplaySettings.ProjectorMode;
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        testInputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testInputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        keyboard = InputSystem.AddDevice<Keyboard>();
        mouse = InputSystem.AddDevice<Mouse>();
        gamepad = InputSystem.AddDevice<Gamepad>();
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None))
            Object.DestroyImmediate(old.gameObject);
        // OnEnableで受信が始まるので、テスト専用ポートを先に設定する。
        var receiver = new GameObject("SongSelectKeyboardInput");
        receiver.SetActive(false);
        Object.DontDestroyOnLoad(receiver);
        input = receiver.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        receiver.SetActive(true);
        InputPoint.EnsureInstance();
        GameSession.IsCalibrationMode = false;
        yield return SceneManager.LoadSceneAsync("SongSelect");
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (GameObject.Find(ProjectorModeToggleUI.ButtonName) == null && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.NotNull(GameObject.Find(ProjectorModeToggleUI.ButtonName));
        controller = Object.FindFirstObjectByType<SongSelectController>();
        controller.Select(Enumerable.Range(0, controller.SongCount).Single(i => controller.SongIdAt(i) == "Epilogue"));
        controller.SetDifficulty(1);
        Canvas.ForceUpdateCanvases();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        yield return ScreenTransitionPlayTests.WaitForTransition();
        if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
        if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
        if (gamepad != null && gamepad.added) InputSystem.RemoveDevice(gamepad);
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("SongSelectKeyboardCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        if (input != null) Object.DestroyImmediate(input.gameObject);
        foreach (var entry in sessionValues) entry.Key.SetValue(null, entry.Value);
        sessionValues.Clear();
        if (hadProjectorPreference) PlayerPrefs.SetInt("displayProjectorMode", savedProjectorPreference);
        else PlayerPrefs.DeleteKey("displayProjectorMode");
        PlayerPrefs.Save();
        DisplaySettings.SetProjectorModeForTest(savedProjectorMode);
        ProjectorModeHotkey.ApplyNow();
    }

    IEnumerator Press(Key key)
    {
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        yield return null;
        yield return ScreenTransitionPlayTests.WaitForTransition();
    }

    [UnityTest]
    public IEnumerator EnterAfterProjectorClickStartsGameWithoutChangingTheSettingAgain()
    {
        var button = GameObject.Find(ProjectorModeToggleUI.ButtonName).GetComponent<Button>();
        yield return Click(button);
        Assert.AreSame(button.gameObject, EventSystem.current.currentSelectedGameObject);
        bool mode = DisplaySettings.ProjectorMode;
        int extraClicks = 0;
        button.onClick.AddListener(() => extraClicks++);
        yield return Press(Key.Enter);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(0, extraClicks, "Enterはプレイ開始だけを行い、前にクリックした設定を再実行しない");
        Assert.AreEqual(mode, DisplaySettings.ProjectorMode);
        Assert.False(GameSession.IsCalibrationMode);
    }

    [UnityTest]
    public IEnumerator ArrowKeysChangeSongAndDifficultyWithoutMovingButtonFocus()
    {
        var button = GameObject.Find("CalibrationButton");
        EventSystem.current.SetSelectedGameObject(button);
        int song = controller.SelectedIndex;
        yield return Press(Key.DownArrow);
        Assert.AreEqual((song + 1) % controller.SongCount, controller.SelectedIndex);
        Assert.AreSame(button, EventSystem.current.currentSelectedGameObject, "曲送りと別のUI移動を同時に実行しない");
        yield return Press(Key.RightArrow);
        Assert.AreEqual(2, controller.SelectedDifficultyIndex);
        Assert.AreSame(button, EventSystem.current.currentSelectedGameObject);
    }

    [UnityTest]
    public IEnumerator EnterWithCalibrationFocusedStillStartsTheSelectedChart()
    {
        var button = GameObject.Find("CalibrationButton").GetComponent<Button>();
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        int calibrationClicks = 0;
        button.onClick.AddListener(() => calibrationClicks++);
        yield return Press(Key.Enter);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(0, calibrationClicks);
        Assert.False(GameSession.IsCalibrationMode, "画面で案内しているEnterの意味は選択譜面のプレイ開始");
        Assert.AreEqual("Epilogue", GameSession.SelectedSongId);
        Assert.AreEqual("Normal", GameSession.SelectedDifficulty);
    }

    [UnityTest]
    public IEnumerator PointerCanStillOpenCalibration()
    {
        yield return Click(GameObject.Find("CalibrationButton").GetComponent<Button>());
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.True(GameSession.IsCalibrationMode);
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (Object.FindFirstObjectByType<CalibrationController>() == null && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.NotNull(Object.FindFirstObjectByType<CalibrationController>());
    }

    [UnityTest]
    public IEnumerator KeyboardHandlingRestoresTheOriginalNavigationSetting()
    {
        var events = EventSystem.current;
        Assert.True(events.sendNavigationEvents);
        yield return Press(Key.W);
        Assert.True(events.sendNavigationEvents, "専用キーを離した後に標準ナビゲーションを無効のまま残さない");
        events.sendNavigationEvents = false;
        yield return Press(Key.A);
        controller.enabled = false;
        Assert.False(events.sendNavigationEvents, "元から無効なら無効のまま戻す");
        yield return null;
    }

    [UnityTest]
    public IEnumerator GamepadStillSubmitsFocusedSettingsWhenNoShortcutIsPressed()
    {
        var button = GameObject.Find(ProjectorModeToggleUI.ButtonName).GetComponent<Button>();
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        bool mode = DisplaySettings.ProjectorMode;
        InputSystem.QueueStateEvent(gamepad, new GamepadState().WithButton(GamepadButton.South));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(gamepad, new GamepadState());
        yield return null;
        yield return null;
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Assert.AreEqual(!mode, DisplaySettings.ProjectorMode);
    }

    [UnityTest]
    public IEnumerator HeldArrowDoesNotStartASeparateButtonRepeat()
    {
        var button = GameObject.Find("CalibrationButton");
        EventSystem.current.SetSelectedGameObject(button);
        int song = controller.SelectedIndex;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.DownArrow));
        yield return new WaitForSecondsRealtime(.8f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        yield return null;
        Assert.AreEqual((song + 1) % controller.SongCount, controller.SelectedIndex, "従来の1押下1曲を維持");
        Assert.AreSame(button, EventSystem.current.currentSelectedGameObject);
        Assert.True(EventSystem.current.sendNavigationEvents);
    }

    [UnityTest]
    public IEnumerator SpaceStartsGameWithoutSubmittingFocusedSettings()
    {
        var button = GameObject.Find(ProjectorModeToggleUI.ButtonName).GetComponent<Button>();
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        bool mode = DisplaySettings.ProjectorMode;
        yield return Press(Key.Space);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(mode, DisplaySettings.ProjectorMode);
    }

    [UnityTest]
    public IEnumerator NumpadEnterStartsGameWithoutSubmittingFocusedSettings()
    {
        var button = GameObject.Find(ProjectorModeToggleUI.ButtonName).GetComponent<Button>();
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        bool mode = DisplaySettings.ProjectorMode;
        yield return Press(Key.NumpadEnter);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(mode, DisplaySettings.ProjectorMode);
    }

    IEnumerator Click(Button button)
    {
        Canvas.ForceUpdateCanvases();
        var canvas = button.GetComponentInParent<Canvas>();
        var position = RectTransformUtility.WorldToScreenPoint(canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, button.transform.position);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = position });
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = position }.WithButton(MouseButton.Left));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = position });
        yield return null;
        yield return null;
    }

    static int FreePort()
    {
        using (var receiver = new UdpClient(0)) return ((IPEndPoint)receiver.Client.LocalEndPoint).Port;
    }
}
