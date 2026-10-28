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

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
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
        InputSystem.QueueStateEvent(mouse, new MouseState { position = Vector2.zero });
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
        while (GameObject.Find("CalibrationButton") == null && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.NotNull(GameObject.Find("CalibrationButton"));
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
    }

    // 運営のキー(F キー・修飾キーだけ・Ctrl を押しながらのキー)は「何かキー」に数えない
    // (リザルトの演出を飛ばさない・選曲の入場の案内を閉じない)。F4 は投影の切り替えでほかの試験に響くので、同じ F キーの F12 で見る。
    [UnityTest]
    public IEnumerator OperatorKeysAreNotCountedAsVisitorKeys()
    {
        bool seen = false;
        IEnumerator Hold(params Key[] keys)
        {
            seen = false;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            for (int i = 0; i < 3; i++) { yield return null; seen |= PhoneSaberOperatorKeys.VisitorKeyPressedThisFrame(keyboard); }
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null; yield return null;
        }
        yield return Hold(Key.F12); Assert.False(seen, "F キー");
        yield return Hold(Key.LeftShift); Assert.False(seen, "修飾キーだけ");
        yield return Hold(Key.LeftCtrl, Key.LeftShift, Key.Q); Assert.False(seen, "Ctrl を押しながらのキー");
        yield return Hold(Key.Q); Assert.True(seen, "ほかのキーは来場者の操作として数える");
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
    public IEnumerator EnterAfterDifficultyClickStartsGameWithoutClickingTheButtonAgain()
    {
        var button = controller.difficultyButtons[2];
        yield return Click(button);
        Assert.AreSame(button.gameObject, EventSystem.current.currentSelectedGameObject);
        Assert.AreEqual(2, controller.SelectedDifficultyIndex);
        int extraClicks = 0;
        button.onClick.AddListener(() => extraClicks++);
        yield return Press(Key.Enter);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(0, extraClicks, "Enterはプレイ開始だけを行い、前にクリックした設定を再実行しない");
        Assert.AreEqual("Hard", GameSession.SelectedDifficulty);
        Assert.False(GameSession.IsCalibrationMode);
    }

    [UnityTest]
    public IEnumerator ArrowKeysChangeSongAndDifficultyWithoutMovingButtonFocus()
    {
        var button = GameObject.Find("CalibrationButton");
        EventSystem.current.SetSelectedGameObject(button);
        int song = controller.SelectedIndex;
        yield return Press(Key.RightArrow);
        Assert.AreEqual((song + 1) % controller.SongCount, controller.SelectedIndex);
        Assert.AreSame(button, EventSystem.current.currentSelectedGameObject, "曲送りと別のUI移動を同時に実行しない");
        yield return Press(Key.DownArrow);
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
    public IEnumerator GamepadStillSubmitsFocusedDifficultyWhenNoShortcutIsPressed()
    {
        var button = controller.difficultyButtons[0];
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        Assert.AreEqual(1, controller.SelectedDifficultyIndex);
        InputSystem.QueueStateEvent(gamepad, new GamepadState().WithButton(GamepadButton.South));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(gamepad, new GamepadState());
        yield return null;
        yield return null;
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Assert.AreEqual(0, controller.SelectedDifficultyIndex);
    }

    [UnityTest]
    public IEnumerator HeldArrowDoesNotStartASeparateButtonRepeat()
    {
        var button = GameObject.Find("CalibrationButton");
        EventSystem.current.SetSelectedGameObject(button);
        int song = controller.SelectedIndex;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.RightArrow));
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
        var button = GameObject.Find("CalibrationButton").GetComponent<Button>();
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        yield return Press(Key.Space);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.False(GameSession.IsCalibrationMode);
    }

    [UnityTest]
    public IEnumerator NumpadEnterStartsGameWithoutSubmittingFocusedSettings()
    {
        var button = GameObject.Find("CalibrationButton").GetComponent<Button>();
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        yield return Press(Key.NumpadEnter);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.False(GameSession.IsCalibrationMode);
    }

    [UnityTest]
    public IEnumerator MouseHoverShootsOnceAndClickDoesNotScheduleAnExtraShot()
    {
        var aim = Object.FindFirstObjectByType<SongSelectAimPointer>();
        yield return new WaitForSecondsRealtime(.6f);
        var normal = controller.difficultyButtons[0].GetComponent<SongSelectDiscTarget>();
        InputSystem.QueueStateEvent(mouse, new MouseState { position = normal.ScreenRect().center });
        yield return new WaitForSecondsRealtime(.7f); Assert.AreEqual(0, aim.ShotCount);
        yield return new WaitForSecondsRealtime(.7f); Assert.AreEqual(1, aim.ShotCount);
        Assert.AreEqual(0, controller.SelectedDifficultyIndex);
        yield return new WaitForSecondsRealtime(1.5f); Assert.AreEqual(1, aim.ShotCount);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = Vector2.zero });
        yield return new WaitForSecondsRealtime(.2f);
        yield return Click(controller.difficultyButtons[2]);
        yield return new WaitForSecondsRealtime(1.3f);
        Assert.AreEqual(1, aim.ShotCount, "通常クリック後の置きっぱなしで追加発射しない");
        Assert.AreEqual(2, controller.SelectedDifficultyIndex);
    }

    [UnityTest]
    public IEnumerator TrackingLossCancelsAimAndDoesNotFireAtStaleMousePosition()
    {
        var aim = Object.FindFirstObjectByType<SongSelectAimPointer>();
        yield return new WaitForSecondsRealtime(.6f);
        var action = controller.difficultyButtons[0].GetComponent<SongSelectDiscTarget>();
        Vector2 pixel = action.ScreenRect().center;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = pixel });
        var normalized = typeof(InputPoint).GetProperty("NormalizedPosition");
        var received = typeof(InputPoint).GetProperty("LastReceivedTime");
        // 照準の跳び止めは同じ受信時刻を一度しか処理しない。実際の受信と同じく、受信ごとに単調時刻も進める。
        var stamped = typeof(InputPoint).GetProperty("LastReceivedMonotonicTime");
        IEnumerator Track(Vector2 point, float seconds)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                normalized.SetValue(input, new Vector2(point.x / Screen.width, point.y / Screen.height));
                received.SetValue(input, Time.realtimeSinceStartupAsDouble);
                stamped.SetValue(input, SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
                yield return null;
            }
        }
        // 入力元が変わった直後は中立位置から入る。通常のためを途中で切る試験。
        yield return Track(Vector2.zero, .65f);
        yield return Track(pixel, .5f);
        float before = aim.Progress01;
        Assert.Greater(before, .2f);
        // 1秒を超えて途切れたら、ためは取り消す。マウスは動かすまで使わない(古い位置で発射しない)。
        received.SetValue(input, -1000d);
        yield return new WaitForSecondsRealtime(1.4f);
        Assert.Zero(aim.ShotCount); Assert.Zero(aim.Progress01);
        Assert.False(aim.Reticle.gameObject.activeSelf, "見失ったままなら照準を隠す");
        // 2026-10-21: 戻った位置が同じボタンでもロックしない(以前は一度外すまで溜まらず「カーソルが合っているのに始まらない」)。
        // 古いためは続けず、0から溜め直す。
        yield return Track(pixel, .3f);
        Assert.False(aim.NeedsRelease); Assert.Zero(aim.ShotCount);
        Assert.Greater(aim.Progress01, 0f); Assert.Less(aim.Progress01, before + .25f, "古いためは続けない");
        yield return Track(pixel, 1f);
        Assert.AreEqual(1, aim.ShotCount); Assert.AreEqual(0, controller.SelectedDifficultyIndex);
    }

    // 1秒未満の途切れ(赤を見失った・通信の揺れ)では、ためを止めて保ち、戻ったら続きから溜める。ロックはしない。
    [UnityTest]
    public IEnumerator ShortTrackingGapKeepsTheChargeWithoutLocking()
    {
        var aim = Object.FindFirstObjectByType<SongSelectAimPointer>();
        yield return new WaitForSecondsRealtime(.6f);
        Vector2 pixel = controller.difficultyButtons[1].GetComponent<SongSelectDiscTarget>().ScreenRect().center;
        var normalized = typeof(InputPoint).GetProperty("NormalizedPosition");
        var received = typeof(InputPoint).GetProperty("LastReceivedTime");
        var stamped = typeof(InputPoint).GetProperty("LastReceivedMonotonicTime");
        IEnumerator Track(Vector2 point, float seconds)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                normalized.SetValue(input, new Vector2(point.x / Screen.width, point.y / Screen.height));
                received.SetValue(input, Time.realtimeSinceStartupAsDouble);
                stamped.SetValue(input, SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
                yield return null;
            }
        }
        yield return Track(Vector2.zero, .65f);
        yield return Track(pixel, .5f);
        Assert.Greater(aim.Progress01, .2f);
        yield return new WaitForSecondsRealtime(.6f);
        Assert.Zero(aim.ShotCount); Assert.False(aim.NeedsRelease, "途切れてもロックしない");
        Assert.True(aim.Reticle.gameObject.activeSelf, "1秒までは照準を残す");
        Assert.True(aim.Reticle.GetComponent<SongSelectAimGraphic>().Stale, "見失っている間は照準を薄くする");
        float paused = aim.Progress01;
        yield return new WaitForSecondsRealtime(.2f);
        Assert.AreEqual(paused, aim.Progress01, 1e-4f, "途切れている間はためを進めず、取り消しもしない");
        // 取り消していたら 0.7 秒では溜まりきらない。続きから溜まるので発射する。
        yield return Track(pixel, .7f);
        Assert.AreEqual(1, aim.ShotCount); Assert.AreEqual(1, controller.SelectedDifficultyIndex);
        Assert.False(aim.Reticle.GetComponent<SongSelectAimGraphic>().Stale);
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

    [UnityTest]
    public IEnumerator NumberKeysChooseDifficultyAndEscapeReturnsToTitle()
    {
        yield return Press(Key.Digit3); Assert.AreEqual(2, controller.SelectedDifficultyIndex);
        yield return Press(Key.Digit1); Assert.AreEqual(0, controller.SelectedDifficultyIndex);
        yield return Press(Key.Digit2); Assert.AreEqual(1, controller.SelectedDifficultyIndex);
        // Esc は来場者の画面で共通の「2秒以内に2回」(仕様面の点検 2)。1回目は知らせだけ。
        yield return Press(Key.Escape); Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Assert.NotNull(Object.FindFirstObjectByType<OperatorNotice>());
        yield return Press(Key.Escape); Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
    }

    [UnityTest]
    public IEnumerator EscapeDuringSongReturnsToTitleWithCurtainWithoutSaving()
    {
        yield return EscapeFromGame(false);
    }

    [UnityTest]
    public IEnumerator EscapeDuringCountInCancelsScheduledAudioAndReturnsToTitle()
    {
        yield return EscapeFromGame(true);
    }

    IEnumerator EscapeFromGame(bool duringCountIn)
    {
        string[] keys = { HighScoreStore.Key("Epilogue", "Normal"), SongAchievementStore.Key("Epilogue", "Normal") };
        var stored = keys.ToDictionary(key => key, key => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null);
        var visited = new List<string>();
        void Observe(Scene scene, LoadSceneMode mode) => visited.Add(scene.name);
        SceneManager.sceneLoaded += Observe;
        try
        {
            yield return Press(Key.Enter);
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            Assert.NotNull(manager);
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!(duringCountIn ? manager.songPlayer.IsScheduled : manager.songPlayer.SongTime > .25) &&
                Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.True(manager.songPlayer.IsScheduled);
            if (duringCountIn)
            {
                Assert.Less(manager.songPlayer.SongTime, 0);
                Assert.NotNull(Object.FindFirstObjectByType<GameStartCountdown>());
            }
            else Assert.Greater(manager.songPlayer.SongTime, .25);
            manager.scoreManager.RegisterHit(JudgmentTier.Perfect);

            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
            yield return null; yield return null;
            Assert.False(ScreenTransition.IsBusy);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
            deadline = Time.realtimeSinceStartupAsDouble + 2;
            while (!ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.True(ScreenTransition.IsBusy);
            Assert.AreEqual("Game", SceneManager.GetActiveScene().name, "幕が閉じる前にシーンを飛ばさない");
            Assert.False(manager.songPlayer.IsScheduled);
            Assert.False(manager.songPlayer.GetComponent<AudioSource>().isPlaying);
            int hit = manager.scoreManager.HitCount, miss = manager.scoreManager.MissCount;
            yield return new WaitForSecondsRealtime(.15f);
            var curtain = Object.FindFirstObjectByType<ScreenTransitionGraphic>();
            Assert.NotNull(curtain);
            Assert.That(curtain.Progress, Is.InRange(.01f, .99f), "戻る幕が途中のフレームを描く");
            Assert.AreEqual(hit, manager.scoreManager.HitCount);
            Assert.AreEqual(miss, manager.scoreManager.MissCount, "退出中は追加の判定をしない");
            Assert.IsNull(Object.FindFirstObjectByType<GameStartCountdown>());
            yield return ScreenTransitionPlayTests.WaitForTransition();
            Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
            Assert.True(EventSystem.current.enabled);
            Assert.False(curtain.gameObject.activeInHierarchy);
            yield return new WaitForSecondsRealtime(.3f); // Escを押したままでも再遷移しない。
            Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
            CollectionAssert.AreEqual(new[] { "Game", "Title" }, visited);
            Assert.Zero(GameSession.FinalScore);
            Assert.Zero(GameSession.FinalPerfect);
            foreach (var item in stored)
                Assert.AreEqual(item.Value, PlayerPrefs.HasKey(item.Key) ? PlayerPrefs.GetString(item.Key) : null,
                    "途中離脱でスコアや実績を書き換えない: " + item.Key);
        }
        finally
        {
            SceneManager.sceneLoaded -= Observe;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            // 不具合が起きた場合も検証前の保存内容へ戻す。
            foreach (var item in stored)
                if (item.Value == null) PlayerPrefs.DeleteKey(item.Key); else PlayerPrefs.SetString(item.Key, item.Value);
            PlayerPrefs.Save();
        }
    }

    // 判定調整も来場者の画面なので、Esc は2秒以内に2回でタイトルへ(仕様面の点検 2)。下書きと選曲の引き継ぎは捨てる。
    // 選曲へ戻るのは画面の「もどる」(照準・クリック)。
    [UnityTest]
    public IEnumerator EscapeTwiceInCalibrationReturnsToTitle()
    {
        yield return Click(GameObject.Find("CalibrationButton").GetComponent<Button>());
        yield return ScreenTransitionPlayTests.WaitForTransition();
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (manager.Calibration == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(manager.Calibration);
        Assert.True(GameSession.IsCalibrationMode);
        yield return Press(Key.Escape);
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name, "1回目は知らせだけ");
        Assert.True(GameSession.IsCalibrationMode);
        yield return Press(Key.Escape);
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.False(GameSession.IsCalibrationMode);
        Assert.IsNull(GameSession.CalibrationSelectionSeconds);
        Assert.IsNull(GameSession.CalibrationSelectionSongId);
    }

    // 曲の途中で剣を見失ったら止め、1秒続けて戻ったら 3・2・1 のあと続きから(仕様面の点検 4)。
    // カメラの位置が届いた台では、曲の途中でマウスを剣の代わりにしない。
    [UnityTest]
    public IEnumerator LosingTheSaberMidSongPausesAndResumesAfterACount()
    {
        using (var sender = new UdpClient())
        {
            byte[] packet = System.Text.Encoding.ASCII.GetBytes("-0.2,-0.6,0.2,-0.6");
            void Send() => sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, input.port));
            double deadline = Time.realtimeSinceStartupAsDouble + 2;
            while (input.LastReceivedTime < 0 && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            Assert.GreaterOrEqual(input.LastReceivedTime, 0, "選曲の間に赤の位置が届く(カメラで遊ぶ台)");
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            deadline = Time.realtimeSinceStartupAsDouble + 20;
            while ((manager == null || !manager.songPlayer.IsPlaying || manager.noteSpawner.AliveCount == 0)
                && Time.realtimeSinceStartupAsDouble < deadline)
            {
                Send(); yield return null;
                if (manager == null) manager = Object.FindFirstObjectByType<GamePlayManager>();
            }
            Assert.True(manager.songPlayer.IsPlaying);
            foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None))
                Assert.False(bridge.fallbackToMouse, "カメラの台ではマウスを剣の代わりにしない");
            StringAssert.Contains("赤 ●", manager.TrackingView.StatusTextShown);
            // 届かなくなって3秒で止まる(ノーツは流れ続けている)。
            double lostAt = Time.realtimeSinceStartupAsDouble;
            deadline = lostAt + 8;
            while (!manager.songPlayer.IsPaused && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.True(manager.songPlayer.IsPaused, "見失ったら曲を止める");
            Assert.That(Time.realtimeSinceStartupAsDouble - lostAt, Is.InRange(2.8, 5.0));
            Assert.True(manager.TrackingView.StaffShown);
            StringAssert.Contains("一時停止中", manager.TrackingView.HeadlineShown);
            int cues = manager.TrackingView.CueCount;
            Assert.GreaterOrEqual(cues, 1, "止まった瞬間に低い刻みを1回");
            double pausedAt = manager.songPlayer.SongTime;
            int alive = manager.noteSpawner.AliveCount, judged = manager.scoreManager.JudgedNoteCount;
            yield return new WaitForSecondsRealtime(.6f);
            Assert.AreEqual(pausedAt, manager.songPlayer.SongTime, "止めている間は曲の時計も止まる");
            Assert.AreEqual(judged, manager.scoreManager.JudgedNoteCount, "止めている間に MISS を数えない");
            Assert.AreEqual(alive, manager.noteSpawner.AliveCount);
            Assert.Greater(manager.TrackingView.DimLevel, .2f, "止まっている間は画面を少し暗くする");
            // この送信位置は輪の外。5秒の構え待ちのあと、自動で3秒数えて続きから。
            double backAt = Time.realtimeSinceStartupAsDouble;
            bool counted = false, checking = false, ring = false, shotCount = false;
            deadline = backAt + 11;
            while (manager.songPlayer.IsPaused && Time.realtimeSinceStartupAsDouble < deadline)
            {
                Send(); yield return null;
                bool wasChecking = checking;
                counted |= manager.TrackingView.HeadlineShown.StartsWith("再開まで");
                checking |= manager.TrackingView.HeadlineShown == PlayTrackingStatus.ReadyText;
                if (manager.TrackingView.HeadlineShown == PlayTrackingStatus.ReadyText)
                    Assert.AreEqual(1, manager.TrackingView.Recovery.ShownPoseMask, "赤だけ届いた台では右の赤い輪だけ");
                if (manager.TrackingWatch.State == PlayTrackingWatch.Phase.Countdown)
                    Assert.Zero(manager.TrackingView.Recovery.ShownPoseMask, "3・2・1の間は構えの輪を消す");
                ring |= manager.TrackingView.RingShown;
                // 環境変数 SABER_UI_CATALOG_CAPTURE_DIR があれば、確認中と 3・2・1 の画面を撮る(撮る間も送り続ける)。
                if (checking && !wasChecking) UICatalogPlayTests.Capture("pause-checking");
                if (!shotCount && manager.TrackingView.HeadlineShown == "再開まで 2") { shotCount = true; UICatalogPlayTests.Capture("pause-countdown"); }
            }
            Assert.False(manager.songPlayer.IsPaused, "戻ったら再開する");
            Assert.True(counted, "再開の前に 3・2・1 を出す");
            Assert.True(checking, "剣が戻ったら構え直しを案内する");
            Assert.True(ring, "3・2・1 は輪で進み具合を見せる");
            Assert.That(Time.realtimeSinceStartupAsDouble - backAt, Is.InRange(7.9, 10.5));
            Assert.AreEqual(pausedAt, manager.songPlayer.SongTime, .25, "止めた位置の続きから");
            Assert.AreEqual(cues + 4, manager.TrackingView.CueCount, "3・2・1 の刻み3回と再開の音1回");
            Assert.AreEqual(PlayTrackingStatus.ResumedText, manager.TrackingView.HeadlineShown, "再開の直後は「再開！」");
            yield return new WaitForSecondsRealtime(PlayTrackingStatus.ResumedSeconds + .15f);
            Send(); yield return null;
            Assert.False(manager.TrackingView.PanelShown);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null; yield return null;
            yield return Press(Key.Escape);
        }
    }

    // 再開の 3・2・1 の途中で Esc を2回押してタイトルへ戻るとき、幕が閉じる間に刻みや再開の音を鳴らさない(2026-10-22 点検4回目)。
    [UnityTest]
    public IEnumerator AbortDuringTheResumeCountPlaysNoMoreCues()
    {
        using (var sender = new UdpClient())
        {
            byte[] packet = System.Text.Encoding.ASCII.GetBytes("-0.2,-0.6,0.2,-0.6");
            void Send() => sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, input.port));
            double deadline = Time.realtimeSinceStartupAsDouble + 2;
            while (input.LastReceivedTime < 0 && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            deadline = Time.realtimeSinceStartupAsDouble + 20;
            while ((manager == null || !manager.songPlayer.IsPlaying || manager.noteSpawner.AliveCount == 0)
                && Time.realtimeSinceStartupAsDouble < deadline)
            {
                Send(); yield return null;
                if (manager == null) manager = Object.FindFirstObjectByType<GamePlayManager>();
            }
            Assert.True(manager.songPlayer.IsPlaying);
            deadline = Time.realtimeSinceStartupAsDouble + 8;
            while (!manager.songPlayer.IsPaused && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.True(manager.songPlayer.IsPaused, "見失ったら曲を止める");
            var view = manager.TrackingView;
            deadline = Time.realtimeSinceStartupAsDouble + 6;
            while (view.HeadlineShown != "再開まで 3" && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            Assert.AreEqual("再開まで 3", view.HeadlineShown);
            // 「3」から約0.75秒で中断する。幕が閉じる間(約0.42秒)に「2」へ変わる時刻(1秒)が来る。
            double countFrom = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble < countFrom + .7) { Send(); yield return null; }
            foreach (var state in new[] { new KeyboardState(Key.Escape), new KeyboardState(), new KeyboardState(Key.Escape), new KeyboardState() })
            {
                InputSystem.QueueStateEvent(keyboard, state);
                Send(); yield return null; Send(); yield return null;
            }
            Assert.True(ScreenTransition.IsBusy, "2回目でタイトルへ戻り始める");
            int cues = view.CueCount;
            double gameUntil = Time.realtimeSinceStartupAsDouble;
            while (SceneManager.GetActiveScene().name == "Game" && Time.realtimeSinceStartupAsDouble < countFrom + 5)
            {
                gameUntil = Time.realtimeSinceStartupAsDouble;
                Send(); yield return null;
            }
            yield return ScreenTransitionPlayTests.WaitForTransition();
            Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
            Assert.Greater(gameUntil - countFrom, 1.02, "「2」へ変わる時刻まで本編が動いていた(確かめられる並び)");
            Assert.AreEqual(cues, view.CueCount, "中断と決めたあとは刻みも再開の音も鳴らさない");
        }
    }

    // 再開の 3・2・1 の途中で音の出力先が変わっても、数えるのは止めない(2026-10-24 ユーザー「曲止めないで上の表示だけにして」。
    // それまでは立て直しを待って数え直していた)。曲の音は止めている間に裏で読み直し、数え終えたら止めた位置から鳴らす。画面は本編のまま。
    [UnityTest]
    public IEnumerator AudioChangeDuringTheResumeCountKeepsCounting()
    {
        using (var sender = new UdpClient())
        {
            byte[] packet = System.Text.Encoding.ASCII.GetBytes("-0.2,-0.6,0.2,-0.6");
            void Send() => sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, input.port));
            double deadline = Time.realtimeSinceStartupAsDouble + 2;
            while (input.LastReceivedTime < 0 && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            deadline = Time.realtimeSinceStartupAsDouble + 20;
            while ((manager == null || !manager.songPlayer.IsPlaying || manager.noteSpawner.AliveCount == 0)
                && Time.realtimeSinceStartupAsDouble < deadline)
            {
                Send(); yield return null;
                if (manager == null) manager = Object.FindFirstObjectByType<GamePlayManager>();
            }
            Assert.True(manager.songPlayer.IsPlaying);
            deadline = Time.realtimeSinceStartupAsDouble + 8;
            while (!manager.songPlayer.IsPaused && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.True(manager.songPlayer.IsPaused, "見失ったら曲を止める");
            var view = manager.TrackingView;
            deadline = Time.realtimeSinceStartupAsDouble + 6;
            while (view.HeadlineShown != "再開まで 3" && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            Assert.AreEqual("再開まで 3", view.HeadlineShown);
            // 「3」から約0.7秒で出力先が変わる。0.5秒の立て直し待ちの間に「2」へ変わる時刻(1秒)が来る。
            double countFrom = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble < countFrom + .7) { Send(); yield return null; }
            int recoveries = AudioOutputRecovery.RecoveryCount;
            double pausedAt = manager.songPlayer.SongTime;
            AudioOutputRecovery.SimulateChangeForTest();
            deadline = countFrom + 2.5;
            while (view.HeadlineShown != "再開まで 2" && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            Assert.AreEqual("再開まで 2", view.HeadlineShown, "出力先が変わっても数えるのは止めない");
            deadline = Time.realtimeSinceStartupAsDouble + 6;
            while ((AudioOutputRecovery.RecoveryCount == recoveries || manager.AudioReloading) && Time.realtimeSinceStartupAsDouble < deadline)
            { Send(); yield return null; }
            Assert.AreEqual(AudioOutputRecovery.Action.ReloadSong, AudioOutputRecovery.LastAction);
            Assert.False(manager.AudioReloading, "止めている間に曲の音を読み直す");
            deadline = Time.realtimeSinceStartupAsDouble + 6;
            while (!manager.songPlayer.IsPlaying && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            Assert.True(manager.songPlayer.IsPlaying, "数え終えたら続きから再開する");
            Assert.That(manager.songPlayer.SongTime, Is.GreaterThanOrEqualTo(pausedAt - .000001), "止めた位置から続ける");
            Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null; yield return null;
            yield return Press(Key.Escape);
        }
    }

    // 運営のシャッター切り替え(Ctrl+Shift+S、b1b7160)を選曲で押しても、難易度は変わらない(2026-10-22、点検 4)。
    // 来場者の S は今までどおり「むずかしく」。
    [UnityTest]
    public IEnumerator OperatorChordDoesNotChangeTheSongOrDifficulty()
    {
        int song = controller.SelectedIndex, difficulty = controller.SelectedDifficultyIndex;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.LeftCtrl, Key.LeftShift));
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.LeftCtrl, Key.LeftShift, Key.S));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.LeftCtrl, Key.LeftShift, Key.D));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null; yield return null;
        Assert.AreEqual(song, controller.SelectedIndex, "Ctrl+Shift+D で曲が変わらない");
        Assert.AreEqual(difficulty, controller.SelectedDifficultyIndex, "Ctrl+Shift+S で難易度が変わらない");
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        yield return Press(Key.S);
        Assert.AreEqual(difficulty + 1, controller.SelectedDifficultyIndex, "来場者の S は今までどおり");
    }

    // 片方の色(青)だけ届かなくなったら、青ノーツが来ている間にスタッフ向けの1行を出す。曲は止めない(2026-10-22、点検 3)。
    [UnityTest]
    public IEnumerator LosingOnlyBlueWarnsTheStaffWithoutPausing()
    {
        using (var sender = new UdpClient())
        {
            byte[] red = System.Text.Encoding.ASCII.GetBytes("0.3,-0.6,0.5,-0.6");
            byte[] blue = System.Text.Encoding.ASCII.GetBytes("-0.5,-0.6,-0.3,-0.6");
            bool sendBlue = true;
            void Send()
            {
                sender.Send(red, red.Length, new IPEndPoint(IPAddress.Loopback, input.port));
                if (sendBlue) sender.Send(blue, blue.Length, new IPEndPoint(IPAddress.Loopback, input.port2));
            }
            double deadline = Time.realtimeSinceStartupAsDouble + 2;
            while ((input.LastReceivedTime < 0 || input.LastReceivedTime2 < 0) && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            Assert.GreaterOrEqual(input.LastReceivedTime2, 0, "選曲の間に青の位置も届く");
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            deadline = Time.realtimeSinceStartupAsDouble + 20;
            while ((manager == null || !manager.songPlayer.IsPlaying || manager.noteSpawner.AliveCount == 0)
                && Time.realtimeSinceStartupAsDouble < deadline)
            {
                Send(); yield return null;
                if (manager == null) manager = Object.FindFirstObjectByType<GamePlayManager>();
            }
            Assert.True(manager.songPlayer.IsPlaying);
            Assert.AreEqual("", manager.TrackingView.WarningShown, "両方届いている間は出さない");
            sendBlue = false;
            double lostAt = Time.realtimeSinceStartupAsDouble;
            deadline = lostAt + 12;
            while (manager.TrackingView.WarningShown.Length == 0 && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            StringAssert.Contains("青の剣の位置が届いていません", manager.TrackingView.WarningShown);
            UICatalogPlayTests.Capture("color-warning");
            Assert.GreaterOrEqual(Time.realtimeSinceStartupAsDouble - lostAt, PlayTrackingWatch.LostSeconds - .1, "3秒届かなくなってから");
            Assert.False(manager.songPlayer.IsPaused, "片方だけでは曲を止めない");
            Assert.False(manager.TrackingView.PanelShown, "上の中央の一時停止の枠は出さない");
            // 青が戻ったら消える。
            sendBlue = true;
            deadline = Time.realtimeSinceStartupAsDouble + 3;
            while (manager.TrackingView.WarningShown.Length > 0 && Time.realtimeSinceStartupAsDouble < deadline) { Send(); yield return null; }
            Assert.AreEqual("", manager.TrackingView.WarningShown);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null; yield return null;
            yield return Press(Key.Escape);
        }
    }

    // 音の出力先が変わっても(AudioSettings.Reset で起こす)、選曲は作り直さずそのまま(2026-10-22 ユーザー「戻るのやめて」)。
    // メニューの音・作った効果音は、鳴らす前に確かめて作り直すので中身がある。
    [UnityTest]
    public IEnumerator AudioOutputChangeKeepsSongSelectAsItIs()
    {
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        double deadline = Time.realtimeSinceStartupAsDouble + 5;
        while ((skin == null || !skin.IsReady) && Time.realtimeSinceStartupAsDouble < deadline) { yield return null; skin = Object.FindFirstObjectByType<SongSelectSkin>(); }
        var sameController = controller;
        string song = controller.SongIdAt(controller.SelectedIndex);
        int difficulty = controller.SelectedDifficultyIndex;
        double remaining = skin.RemainingSeconds;
        var before = MenuSfx.Clip("ToTitle", FlourishSfx.ToTitle);
        Assert.Greater(before.samples, 0);
        var made = ProceduralSfx.Clip("RevivePlayProbe", FlourishSfx.Cached("ToTitle", FlourishSfx.ToTitle));
        int recoveries = AudioOutputRecovery.RecoveryCount;
        Assert.True(AudioSettings.Reset(AudioSettings.GetConfiguration()));
        yield return null;
        Assert.AreEqual(0, made.samples, "作った音は中身が消える(確かめられる並び)");
        deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (AudioOutputRecovery.RecoveryCount == recoveries && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.AreEqual(AudioOutputRecovery.Action.InPlace, AudioOutputRecovery.LastAction);
        Assert.False(ScreenTransition.IsBusy, "画面を作り直さない");
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Assert.AreSame(sameController, Object.FindFirstObjectByType<SongSelectController>(), "同じ画面のまま");
        Assert.AreEqual(song, controller.SongIdAt(controller.SelectedIndex));
        Assert.AreEqual(difficulty, controller.SelectedDifficultyIndex);
        Assert.That(skin.RemainingSeconds, Is.InRange(remaining - 12, remaining + .01), "残り時間はそのまま進む");
        var after = MenuSfx.Clip("ToTitle", FlourishSfx.ToTitle);
        Assert.Greater(after.samples, 0, "メニューの音は作り直されて中身がある");
        var revived = made;
        ProceduralSfx.Revive(ref revived);
        Assert.AreNotSame(made, revived);
        Assert.Greater(revived.samples, 0, "作った効果音は鳴らす前に同じ波形で作り直す");
        Object.Destroy(revived);
        deadline = Time.realtimeSinceStartupAsDouble + 2;
        while (Object.FindFirstObjectByType<OperatorNotice>() == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(Object.FindFirstObjectByType<OperatorNotice>(), "スタッフ向けに立て直したことを知らせる");
    }

    // 曲の途中で音の出力先が変わっても曲は止めない(2026-10-24 ユーザー「曲止めないで上の表示だけにして」)。
    // 本物の音の作り直し(AudioSettings.Reset)で曲の音の中身が消えても、時計とノーツは進み続け、裏で読み直した音を今の位置から鳴らす。
    [UnityTest]
    public IEnumerator AudioOutputChangeMidSongKeepsPlaying()
    {
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        GamePlayManager manager = null;
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        while ((manager == null || !manager.songPlayer.IsPlaying) && Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
            if (manager == null) manager = Object.FindFirstObjectByType<GamePlayManager>();
        }
        Assert.True(manager.songPlayer.IsPlaying);
        string runId = GameSession.AchievementRunId;
        var oldClip = manager.songPlayer.Clip;
        var accents = Object.FindFirstObjectByType<GateAccents>();
        Assert.NotNull(accents);
        accents.Milestone(30);
        Assert.AreEqual(1, accents.ActiveRings);
        double ringFrom = Time.realtimeSinceStartupAsDouble;
        int recoveries = AudioOutputRecovery.RecoveryCount;
        Assert.True(AudioSettings.Reset(AudioSettings.GetConfiguration()));
        yield return null;
        Assert.False(manager.songPlayer.IsPaused, "知らせが来ても曲は止めない");
        double changedAt = manager.songPlayer.SongTime;
        bool pausedAny = false;
        deadline = Time.realtimeSinceStartupAsDouble + 10;
        while ((AudioOutputRecovery.RecoveryCount == recoveries || manager.AudioReloading) && Time.realtimeSinceStartupAsDouble < deadline)
        {
            pausedAny |= manager.songPlayer.IsPaused;
            yield return null;
        }
        Assert.AreEqual(AudioOutputRecovery.Action.ReloadSong, AudioOutputRecovery.LastAction);
        Assert.False(manager.AudioReloading, "裏で曲の音を読み直し終える");
        Assert.False(pausedAny, "読み直す間も曲は止めない");
        Assert.Greater(manager.songPlayer.SongTime, changedAt, "読み直す間も時計は進む");
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name, "画面は本編のまま");
        Assert.AreNotSame(oldClip, manager.songPlayer.Clip, "曲の音は読み直した");
        Assert.Greater(manager.songPlayer.Clip.samples, 0);
        double resumedAt = manager.songPlayer.SongTime;
        yield return new WaitForSecondsRealtime(.4f);
        Assert.Greater(manager.songPlayer.SongTime, resumedAt, "時計が進む");
        var source = manager.songPlayer.GetComponent<AudioSource>();
        Assert.True(source.isPlaying, "読み直した音が鳴っている");
        Assert.That(source.timeSamples / (double)source.clip.frequency, Is.EqualTo(manager.songPlayer.SongTime).Within(.2),
            "読み直した音は今の位置で鳴る");
        while (Time.realtimeSinceStartupAsDouble < ringFrom + GateAccents.MilestoneSeconds + .3) yield return null;
        Assert.AreEqual(0, accents.ActiveRings, "ゲートまわりの光はふつうに消えていく");
        Assert.AreEqual(runId, GameSession.AchievementRunId, "同じ回のまま続く");
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null; yield return null;
        yield return Press(Key.Escape);
    }

    // 出力先がサンプルレートの違う機器へ変わると、DSP の時計が跳ぶ(2026-10-28 ユーザー「演奏中に音の出力先が変わると強制的に
    // リザルト画面に行ってしまってる」)。曲の時計は跳ばずに実時間どおり続き、残りのノーツを飛ばして結果画面へ行かない。
    [UnityTest]
    public IEnumerator AudioOutputChangeToAnotherSampleRateMidSongKeepsTheSongClock()
    {
        var original = AudioSettings.GetConfiguration();
        try
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            GamePlayManager manager = null;
            double deadline = Time.realtimeSinceStartupAsDouble + 20;
            while ((manager == null || !manager.songPlayer.IsPlaying || manager.songPlayer.SongTime < 1)
                && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
                if (manager == null) manager = Object.FindFirstObjectByType<GamePlayManager>();
            }
            Assert.True(manager.songPlayer.IsPlaying);
            int recoveries = AudioOutputRecovery.RecoveryCount;
            double songBefore = manager.songPlayer.SongTime, realBefore = Time.realtimeSinceStartupAsDouble;
            var other = AudioSettings.GetConfiguration();
            other.sampleRate = other.sampleRate == 44100 ? 48000 : 44100;
            Assert.True(AudioSettings.Reset(other), "サンプルレートの違う出力先へ変わったことにする");
            yield return null;
            Assert.That(manager.songPlayer.SongTime - songBefore, Is.EqualTo(Time.realtimeSinceStartupAsDouble - realBefore).Within(.3),
                "曲の時計は跳ばない");
            deadline = Time.realtimeSinceStartupAsDouble + 10;
            while ((AudioOutputRecovery.RecoveryCount == recoveries || manager.AudioReloading) && Time.realtimeSinceStartupAsDouble < deadline)
            {
                Assert.AreEqual("Game", SceneManager.GetActiveScene().name, "結果画面へ行かない");
                yield return null;
            }
            Assert.AreEqual(AudioOutputRecovery.Action.ReloadSong, AudioOutputRecovery.LastAction);
            Assert.False(manager.AudioReloading, "裏で曲の音を読み直し終える");
            yield return new WaitForSecondsRealtime(.5f);
            Assert.AreEqual("Game", SceneManager.GetActiveScene().name, "画面は本編のまま");
            Assert.That(manager.songPlayer.SongTime - songBefore, Is.EqualTo(Time.realtimeSinceStartupAsDouble - realBefore).Within(.6),
                "読み直したあとも、曲の時計は実時間どおり");
            var source = manager.songPlayer.GetComponent<AudioSource>();
            Assert.True(source.isPlaying, "読み直した音が鳴っている");
            Assert.That(source.timeSamples / (double)source.clip.frequency, Is.EqualTo(manager.songPlayer.SongTime).Within(.2),
                "読み直した音は今の位置で鳴る");
        }
        finally
        {
            if (AudioSettings.GetConfiguration().sampleRate != original.sampleRate) AudioSettings.Reset(original);
        }
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null; yield return null;
        yield return Press(Key.Escape);
    }

    static int FreePort()
    {
        using (var receiver = new UdpClient(0)) return ((IPEndPoint)receiver.Client.LocalEndPoint).Port;
    }
}
