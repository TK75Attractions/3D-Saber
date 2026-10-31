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

// 実際の結果画面で、演出中の誤操作とキーボードでの帰還を確認する。
// 2026-10-31 ユーザー「来場者が自分でタイトルへ戻れる的を消す」: 次の曲が無い回(この試験の1曲モード)は、シーンの BACK を隠し、
// 照準も出さない。タイトルへ戻すのはスタッフ(Enter・Esc 2回・右クリック3回)。2曲モードの「次の曲へ」は VisitorPlaysPlayTests。
public class ResultNavigationPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    string songId;
    InputPoint input;
    ResultReveal reveal;
    ResultSkin skin;
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
        skin = Object.FindFirstObjectByType<ResultSkin>();
        Assert.NotNull(skin);
        back = Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponentInChildren<Button>(true);
        Assert.NotNull(back);
        Canvas.ForceUpdateCanvases();
    }

    float RevealElapsed => (float)typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reveal);

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
    public IEnumerator VisitorExitIsHiddenAndScoreIsSavedBeforeNavigation()
    {
        Assert.False(skin.VisitorExitShown, "次の曲が無い回は来場者の出口を出さない");
        Assert.False(back.gameObject.activeInHierarchy, "シーンの BACK(タイトルへ)は隠す");
        ExecuteEvents.Execute(back.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        ExecuteEvents.Execute(back.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        var table = HighScoreStore.Load(songId, "Normal");
        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual(12345, table.entries[0].score);
        reveal.Tick(999);
        Assert.False(back.gameObject.activeInHierarchy, "演出のあとも出さない");
        ExecuteEvents.Execute(back.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        yield return null;
        Assert.False(ScreenTransition.IsBusy, "隠した BACK では戻らない");
        yield return Tap(Key.Enter);
        Assert.True(ScreenTransition.IsBusy, "スタッフの Enter でタイトルへ");
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.AreEqual(1, HighScoreStore.Load(songId, "Normal").entries.Count, "戻る操作で二重に記録しない");
    }

    // 2026-10-31 まではセーバーで下の「タイトルへ」にかざして戻れた(来場者の誤爆)。今は照準も出さず、かざしても戻らない。
    [UnityTest]
    public IEnumerator SaberCannotLeaveTheResultWhenThereIsNoNextSong()
    {
        Assert.IsNull(Object.FindFirstObjectByType<SaberUIPointer>(), "指す的が無い回は照準も出さない");
        Assert.IsNull(Object.FindFirstObjectByType<SaberInputBridge>(), "結果画面はゲームのセーバー本体を持たない");
        reveal.Tick(999);
        Canvas.ForceUpdateCanvases();
        // 前の「タイトルへ」の位置(下の中央)にかざし続ける。
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, back.transform.position);
        float x = (screen.x / Screen.width * 2 - 1) / input.sensitivity;
        float y = (screen.y / Screen.height * 2 - 1) / input.sensitivity;
        byte[] packet = Encoding.ASCII.GetBytes(x.ToString("R", CultureInfo.InvariantCulture) + "," + y.ToString("R", CultureInfo.InvariantCulture));
        using (var sender = new UdpClient())
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 3;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, input.port));
                yield return null;
            }
        }
        Assert.True(input.IsRecentlyActive(), "剣の座標は届いている");
        Assert.False(ScreenTransition.IsBusy);
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name, "かざしてもタイトルへ戻らない");
    }

    [UnityTest]
    public IEnumerator MouseSkipThenStaffEnterReturnsToTitle()
    {
        typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reveal, 0f);
        reveal.enabled = true;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(15, 15) }.WithButton(MouseButton.Left));
        yield return null;
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        Assert.GreaterOrEqual(RevealElapsed, 999f, "クリックで演出を飛ばす");
        Assert.False(back.gameObject.activeInHierarchy);
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
        Assert.GreaterOrEqual(RevealElapsed, 999f, "1回目の Enter は演出を飛ばす");
        Assert.False(back.gameObject.activeInHierarchy);
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
        Assert.GreaterOrEqual(RevealElapsed, 999f, "1回目の Esc は今までどおり演出も飛ばす");
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
    public IEnumerator ResultOffersNoVisitorExitAndStaffEnterResetsTheNextPlayersSettings()
    {
        GameSession.SelectedSongId = "Epilogue";
        GameSession.SelectedSongTitle = "校歌";
        GameSession.SelectedDifficulty = "Hard";
        GameSession.JudgmentOffsetMs = 180;
        GameSession.NoteApproachTime = .5f;
        reveal.Tick(999);
        Assert.False(back.gameObject.activeInHierarchy, "来場者がタイトルへ戻る的は出さない(2026-10-31)");
        Assert.AreEqual(new Vector2(0f, -432f), back.GetComponent<RectTransform>().anchoredPosition, "2曲モードの「次の曲へ」と同じ位置のまま");
        Assert.IsNull(GameObject.Find("RetrySong"));
        Assert.IsNull(GameObject.Find("ReturnToSongSelect"));
        Assert.IsNull(GameObject.Find("SkipHint"));
        Assert.AreEqual(1, Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponentsInChildren<Button>(true).Length,
            "ボタンはシーンの BACK だけ(この回は隠したまま)");
        yield return Tap(Key.Enter);
        Assert.True(ScreenTransition.IsBusy);

        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
        Assert.AreEqual(GameSession.JudgmentOffsetDefaultMs, GameSession.JudgmentOffsetMs);
        Assert.AreEqual(GameSession.NoteApproachTimeDefault, GameSession.NoteApproachTime);
    }

    // 結果の画面は Overlay なので、16:9 以外の画面でも 1920×1080 の全体が収まる拡大(Expand)にし、出口はその中に置く
    // (縦横の平均で拡大していたため、21:9 などの横長で出口が画面の下へ切れていた。2026-10-30 全体の点検)。
    [UnityTest]
    public IEnumerator TheExitStaysOnScreenWhateverTheScreenShape()
    {
        reveal.Tick(999);
        yield return null;
        var scaler = Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponent<CanvasScaler>();
        Assert.AreEqual(CanvasScaler.ScaleMode.ScaleWithScreenSize, scaler.uiScaleMode);
        Assert.AreEqual(new Vector2(1920, 1080), scaler.referenceResolution);
        Assert.AreEqual(CanvasScaler.ScreenMatchMode.Expand, scaler.screenMatchMode, "1920×1080 の全体がどの画面比でも収まる");
        var rect = back.GetComponent<RectTransform>();
        Assert.AreEqual(new Vector2(.5f, .5f), rect.anchorMin);
        Assert.AreEqual(rect.anchorMin, rect.anchorMax);
        Vector2 min = rect.anchoredPosition - Vector2.Scale(rect.sizeDelta, rect.pivot);
        Vector2 max = min + rect.sizeDelta;
        Assert.True(min.x >= -960 && max.x <= 960 && min.y >= -540 && max.y <= 540, "出口は 1920×1080 の中: " + min + "〜" + max);
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
