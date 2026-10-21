using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実際の結果画面で、演出中の誤操作とセーバー・キーボードでの帰還を確認する。
public class ResultNavigationPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    string songId;
    InputPoint input;
    ResultReveal reveal;
    Button back;
    Keyboard keyboard;
    Mouse mouse;
    InputSettings originalInputSettings;
    InputSettings testInputSettings;
    readonly string[] settingKeys = { "judgmentOffsetMs", CalibrationDraft.ActiveKey, CalibrationDraft.SpeakerKey, CalibrationDraft.HeadphoneKey };
    readonly Dictionary<string, int?> savedSettings = new Dictionary<string, int?>();
    float? savedSpeed;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (string key in settingKeys) savedSettings[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) : (int?)null;
        savedSpeed = PlayerPrefs.HasKey("noteApproachTime") ? PlayerPrefs.GetFloat("noteApproachTime") : (float?)null;
        // バッチ実行でも仮想キーボード・マウスをゲームへ送る。製品の設定アセットは変更しない。
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        testInputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testInputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        songId = "__ResultNavigation_" + Guid.NewGuid().ToString("N");
        GameSession.SelectedSongId = songId;
        GameSession.SelectedSongTitle = "Navigation test";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.ResetResult();
        GameSession.FinalScore = 12345;
        GameSession.FinalPerfect = 10;
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None))
            Object.DestroyImmediate(old.gameObject);
        // OnEnableで受信が始まるので、テスト専用ポートを先に設定する。
        var receiver = new GameObject("ResultNavigationInput");
        receiver.SetActive(false);
        Object.DontDestroyOnLoad(receiver);
        input = receiver.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        receiver.SetActive(true);
        InputPoint.EnsureInstance();
        keyboard = InputSystem.AddDevice<Keyboard>();
        mouse = InputSystem.AddDevice<Mouse>();
        yield return SceneManager.LoadSceneAsync("Result");
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (Object.FindFirstObjectByType<ResultReveal>() == null && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        reveal = Object.FindFirstObjectByType<ResultReveal>();
        Assert.NotNull(reveal);
        reveal.enabled = false;
        reveal.Tick(0);
        back = Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponentInChildren<Button>();
        Assert.NotNull(back);
        Canvas.ForceUpdateCanvases();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        yield return WaitForTransition();
        if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
        if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("ResultNavigationCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        if (input != null) Object.DestroyImmediate(input.gameObject);
        HighScoreStore.Clear(songId, "Normal");
        foreach (var entry in sessionValues) entry.Key.SetValue(null, entry.Value);
        sessionValues.Clear();
        foreach (var setting in savedSettings)
            if (setting.Value.HasValue) PlayerPrefs.SetInt(setting.Key, setting.Value.Value); else PlayerPrefs.DeleteKey(setting.Key);
        savedSettings.Clear();
        if (savedSpeed.HasValue) PlayerPrefs.SetFloat("noteApproachTime", savedSpeed.Value); else PlayerPrefs.DeleteKey("noteApproachTime");
        PlayerPrefs.Save();
    }

    [UnityTest]
    public IEnumerator HiddenBackCannotExitAndScoreIsSavedBeforeNavigation()
    {
        Assert.False(back.IsInteractable(), "登場前の透明なBACKで結果画面を閉じない");
        ExecuteEvents.Execute(back.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        ExecuteEvents.Execute(back.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        var table = HighScoreStore.Load(songId, "Normal");
        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual(12345, table.entries[0].score);
        reveal.Tick(999);
        Assert.True(back.IsInteractable());
        ExecuteEvents.Execute(back.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        Assert.True(ScreenTransition.IsBusy);
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.AreEqual(1, HighScoreStore.Load(songId, "Normal").entries.Count, "戻る操作で二重に記録しない");
    }

    [UnityTest]
    public IEnumerator SaberReachesBottomBackAndDwellReturnsToTitle()
    {
        var pointer = Object.FindFirstObjectByType<SaberUIPointer>();
        Assert.NotNull(pointer, "ゲーム終了後もセーバーで操作できること");
        Assert.IsNull(Object.FindFirstObjectByType<SaberInputBridge>(), "結果画面はゲームのセーバー本体を持たない");
        reveal.Tick(999);
        Canvas.ForceUpdateCanvases();
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, back.transform.position);
        float x = (screen.x / Screen.width * 2 - 1) / input.sensitivity;
        float y = (screen.y / Screen.height * 2 - 1) / input.sensitivity;
        byte[] packet = Encoding.ASCII.GetBytes(x.ToString("R", CultureInfo.InvariantCulture) + "," + y.ToString("R", CultureInfo.InvariantCulture));
        double deadline = Time.realtimeSinceStartupAsDouble + 3;
        bool hovered = false;
        using (var sender = new UdpClient())
        {
            // 入力停止後はカーソルを隠し、古い位置で操作を続けない。
            byte[] center = Encoding.ASCII.GetBytes("0,0");
            sender.Send(center, center.Length, new IPEndPoint(IPAddress.Loopback, input.port));
            double receivedDeadline = Time.realtimeSinceStartupAsDouble + 2;
            while (!input.IsRecentlyActive() && Time.realtimeSinceStartupAsDouble < receivedDeadline) yield return null;
            Assert.True(input.IsRecentlyActive());
            yield return new WaitForSecondsRealtime(SaberUIPointer.StaleSeconds + .2f);
            Assert.IsNull(pointer.HoveredForTest);
            foreach (var image in pointer.GetComponentsInChildren<Image>(true)) Assert.False(image.gameObject.activeSelf);
            deadline = Time.realtimeSinceStartupAsDouble + 3;
            while (SceneManager.GetActiveScene().name == "Result" && Time.realtimeSinceStartupAsDouble < deadline)
            {
                sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, input.port));
                if (pointer != null && pointer.HoveredForTest == back) hovered = true;
                yield return null;
            }
        }
        Assert.True(hovered, "画面下部まで届き、BACK上で滞留を始めること");
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
    }

    [UnityTest]
    public IEnumerator MouseSkipPreservesKeyboardBackSelection()
    {
        typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reveal, 0f);
        reveal.enabled = true;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(15, 15) }.WithButton(MouseButton.Left));
        yield return null;
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        Assert.True(back.IsInteractable());
        Assert.AreEqual(back.gameObject, EventSystem.current.currentSelectedGameObject);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(15, 15) });
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
        yield return null;
        yield return null;
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
    }

    [UnityTest]
    public IEnumerator FirstEnterSkipsRevealAndSecondEnterReturnsToTitle()
    {
        typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reveal, 0f);
        reveal.enabled = true;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
        yield return null;
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name, "演出スキップと画面終了を同じEnterで実行しない");
        Assert.True(back.IsInteractable());
        Assert.AreEqual(back.gameObject, EventSystem.current.currentSelectedGameObject);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
        yield return null;
        yield return null;
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
    }

    // 来場者の画面に共通の Esc(仕様面の点検 2)。1回目は演出を飛ばして知らせを出すだけ、2秒以内の2回目でタイトルへ。
    [UnityTest]
    public IEnumerator EscapeTwiceWithinTwoSecondsReturnsToTitle()
    {
        typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reveal, 0f);
        reveal.enabled = true;
        yield return Tap(Key.Escape);
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name, "1回目では戻らない");
        Assert.NotNull(Object.FindFirstObjectByType<OperatorNotice>());
        Assert.True(back.IsInteractable(), "1回目の Esc は今までどおり演出も飛ばす");
        yield return Tap(Key.Escape);
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.AreEqual(1, HighScoreStore.Load(songId, "Normal").entries.Count, "戻る操作で二重に記録しない");
    }

    // 1回も切っていない回(無人のまま流れた回など)は、HI-SCORE・本日の順位に残さず、順位の欄に理由を出す(仕様面の点検 1)。
    // 同じ回の結果画面を作り直しても、二重に残さない。
    [UnityTest]
    public IEnumerator RunWithoutAnyCutIsShownButNotRecorded()
    {
        GameSession.ResetResult();
        GameSession.FinalScore = 0;
        GameSession.FinalMiss = 120;
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null; yield return null;
        Assert.AreEqual(1, HighScoreStore.Load(songId, "Normal").entries.Count, "前の回の記録だけが残る");
        Assert.IsNull(GameSession.FinalDailyRanking);
        var status = GameObject.Find("DailyRanking").transform.Find("Details/Status").GetComponent<TMPro.TMP_Text>();
        Assert.AreEqual(DailyRankingPresentation.UnrecordedHeadline, status.text);
        GameSession.ResetResult();
        GameSession.FinalScore = 23456;
        GameSession.FinalPerfect = 20;
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null;
        yield return SceneManager.LoadSceneAsync("Result");
        yield return null;
        var table = HighScoreStore.Load(songId, "Normal");
        Assert.AreEqual(2, table.entries.Count, "同じ回の結果画面を作り直しても一度だけ残す");
        Assert.AreEqual(23456, table.entries[0].score);
    }

    IEnumerator Tap(Key key)
    {
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        yield return null;
    }

    [UnityTest]
    public IEnumerator ResultOffersOneCenteredTitleButtonAndResetsTheNextPlayersSettings()
    {
        GameSession.SelectedSongId = "Epilogue";
        GameSession.SelectedSongTitle = "校歌";
        GameSession.SelectedDifficulty = "Hard";
        GameSession.JudgmentOffsetMs = 180;
        GameSession.NoteApproachTime = .5f;
        reveal.Tick(999);
        Assert.AreEqual("タイトルへ", back.GetComponentInChildren<TMPro.TextMeshProUGUI>().text);
        Assert.AreEqual(new Vector2(0f, -432f), back.GetComponent<RectTransform>().anchoredPosition);
        Assert.IsNull(GameObject.Find("RetrySong"));
        Assert.IsNull(GameObject.Find("ReturnToSongSelect"));
        Assert.IsNull(GameObject.Find("SkipHint"));
        Assert.AreEqual(1, Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponentsInChildren<Button>(true).Length,
            "中央のタイトルへボタンだけを表示する");
        ExecuteEvents.Execute(back.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        Assert.True(ScreenTransition.IsBusy);

        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.AreEqual(GameSession.JudgmentOffsetDefaultMs, GameSession.JudgmentOffsetMs);
        Assert.AreEqual(GameSession.NoteApproachTimeDefault, GameSession.NoteApproachTime);
    }

    static IEnumerator WaitForTransition()
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.False(ScreenTransition.IsBusy);
    }

    static int FreePort()
    {
        using (var receiver = new UdpClient(0)) return ((IPEndPoint)receiver.Client.LocalEndPoint).Port;
    }
}
