using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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

// 2026-10-27 実際の結果画面で、印の一言を打つ欄(ChartMarkCommentPanel)。打っている間は Enter・Esc で結果画面を出ない。
// F5 で閉じると「戻る」を選び直し、いつもの Enter で進める。ResultNavigationPlayTests と同じ仮想キーボードの用意。
// 2026-10-28 結果画面の F5 で開き直す・変換中の F5 では閉じない・開く前の選択が無くても「戻る」へ戻す。
// たまに落ちた最初の Assert は、残っていた常駐の印(0.6秒後に欄を自動で開いて選択を外す)を先に消し、選択が決まるまで待つ。
public class ChartMarkPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    string songId, folder;
    InputPoint input;
    ResultReveal reveal;
    Button back;
    Keyboard keyboard;
    InputSettings originalInputSettings;
    InputSettings testInputSettings;
    ChartMarkRecorder recorder;

    static GameObject Selected => EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // 常駐の印が残っていると、結果画面に入って0.6秒後に欄を自動で開き、選択を外す(たまに落ちた原因の候補)。
        foreach (var old in Object.FindObjectsByType<ChartMarkRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(old.gameObject);
        if (ChartMarkCommentPanel.Current != null) Object.DestroyImmediate(ChartMarkCommentPanel.Current.gameObject);
        PhoneSaberOperatorKeys.TextEntryActive = false;
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        testInputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testInputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        // タイトルへ進むと次の人の設定に戻す(ResetPlayerSettings)。本物の判定のずらし・速度は消さない。
        GameSession.SetPlayerSettingsForTest(true);
        songId = "__ChartMark_" + Guid.NewGuid().ToString("N");
        GameSession.SelectedSongId = songId;
        GameSession.SelectedSongTitle = "Chart mark test";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.ResetResult();
        GameSession.FinalScore = 12345;
        GameSession.FinalPerfect = 10;
        folder = Path.Combine(Path.GetTempPath(), "ChartMarkPlayTests_" + Guid.NewGuid().ToString("N"));
        ChartMarks.FolderOverride = folder;
        ChartMarks.BeginPlay();
        var chart = new ChartData { bpm = 120 };
        for (int i = 0; i < 8; i++) chart.notes.Add(new NoteData { time = 500 * i, beat = i, color = "red" });
        ChartMarks.Add(chart, 1, 0, null, songId, "Chart mark test", "Normal", "", DateTime.Now);
        ChartMarks.Add(chart, 2, 0, null, songId, "Chart mark test", "Normal", "", DateTime.Now.AddSeconds(1));

        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None))
            Object.DestroyImmediate(old.gameObject);
        // 結果画面の照準が受信を始めるので、テスト専用のポートを先に決める。
        var receiver = new GameObject("ChartMarkInput");
        receiver.SetActive(false);
        Object.DontDestroyOnLoad(receiver);
        input = receiver.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        receiver.SetActive(true);
        InputPoint.EnsureInstance();
        keyboard = InputSystem.AddDevice<Keyboard>();
        yield return SceneManager.LoadSceneAsync("Result");
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (Object.FindFirstObjectByType<ResultReveal>() == null && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        reveal = Object.FindFirstObjectByType<ResultReveal>();
        Assert.NotNull(reveal);
        reveal.enabled = false;
        reveal.Tick(999);
        // 1曲モードの結果は来場者の出口を隠す(2026-10-31)。隠した BACK も取っておき、出ていないことを確かめる。
        back = Object.FindFirstObjectByType<ResultController>().GetComponent<Canvas>().GetComponentInChildren<Button>(true);
        Assert.NotNull(back);
        Assert.False(back.gameObject.activeInHierarchy);
        Canvas.ForceUpdateCanvases();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (ChartMarkCommentPanel.Current != null) ChartMarkCommentPanel.Current.Close();
        if (ChartMarkCommentPanel.Current != null) ChartMarkCommentPanel.Current.Close();
        if (recorder != null) Object.DestroyImmediate(recorder.gameObject);
        recorder = null;
        yield return WaitForTransition();
        if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("ChartMarkCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        if (input != null) Object.DestroyImmediate(input.gameObject);
        HighScoreStore.Clear(songId, "Normal");
        foreach (var entry in sessionValues) entry.Key.SetValue(null, entry.Value);
        sessionValues.Clear();
        GameSession.SetPlayerSettingsForTest(false);
        ChartMarks.BeginPlay();
        ChartMarks.FolderOverride = null;
        InputManager.EscapeSuppressed = false;
        PhoneSaberOperatorKeys.TextEntryActive = false;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    // 1曲モード(本番の既定)。来場者の出口は無く、スタッフの Enter でタイトルへ戻る。打っている間の Enter では戻らない。
    [UnityTest]
    public IEnumerator TypingKeepsTheResultScreenAndF5HandsTheExitBack()
    {
        yield return null;
        Assert.IsNull(Selected, "1曲モードの結果は、来場者の出口を選ばない(出していない)");
        keyboard.MakeCurrent();
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        Assert.NotNull(panel);
        Assert.IsNull(EventSystem.current.currentSelectedGameObject, "打っている間は Enter で「戻る」を押さない");

        foreach (char c in "abc") InputSystem.QueueTextEvent(keyboard, c);
        yield return null;
        yield return null;
        Assert.AreEqual("abc", panel.TextAt(0), "文字入力(日本語入力の確定も同じ口)で打てる");
        yield return Tap(Key.Enter);
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
        Assert.IsFalse(ScreenTransition.IsBusy);
        Assert.AreEqual(1, panel.Row, "Enter で次の印へ");

        yield return Tap(Key.Escape);
        yield return Tap(Key.Escape);
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name, "Esc 2回でもタイトルへ戻らない(日本語入力の取り消しの Esc)");
        Assert.IsFalse(ScreenTransition.IsBusy);

        yield return Tap(Key.F5);
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen, "F5 で閉じる");
        Assert.IsFalse(InputManager.EscapeSuppressed);
        Assert.IsFalse(back.gameObject.activeInHierarchy, "閉じても来場者の出口は隠したまま");
        var saved = JsonUtility.FromJson<ChartMarkFile>(File.ReadAllText(ChartMarks.CurrentPath));
        Assert.AreEqual("abc", saved.marks[0].comment);
        Assert.AreEqual("", saved.marks[1].comment);

        yield return Tap(Key.Enter);
        yield return WaitForTransition();
        Assert.AreEqual("Title", SceneManager.GetActiveScene().name, "閉じたあとはスタッフの Enter でタイトルへ");
    }

    [UnityTest]
    public IEnumerator F5OnTheResultScreenReopensThePanelAndTheExitComesBack()
    {
        recorder = new GameObject("ChartMarkRecorderForTest").AddComponent<ChartMarkRecorder>();
        Assert.AreSame(recorder, ChartMarkRecorder.Instance);
        Assert.AreEqual(ChartMarkRecorder.StateResult, ChartMarkRecorder.PlayState);
        Assert.IsFalse(recorder.TryMark(ChartMarks.SourcePhone, out string message), "結果画面では印を付けない");
        Assert.AreEqual(ChartMarkRecorder.NotPlayingMessage, message);
        Assert.AreEqual(2, ChartMarks.Count);

        keyboard.MakeCurrent();
        yield return null;
        // 開く前の選択が無くても、閉じたら結果画面の「戻る」を選ぶ。
        EventSystem.current.SetSelectedGameObject(null);
        yield return Tap(Key.F5);
        var panel = ChartMarkCommentPanel.Current;
        Assert.NotNull(panel, "結果画面の F5 で一言の欄を開く");
        Assert.IsTrue(PhoneSaberOperatorKeys.TextEntryActive, "打っている間は運営の F キーを止める");
        foreach (char c in "ab") InputSystem.QueueTextEvent(keyboard, c);
        yield return null;
        yield return null;
        Assert.AreEqual("ab", panel.TextAt(0));

        yield return Tap(Key.F5);
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen, "F5 で閉じる(閉じた F5 で開き直さない)");
        Assert.IsFalse(PhoneSaberOperatorKeys.TextEntryActive);
        Assert.IsNull(Selected, "1曲モードは来場者の出口が無いので、閉じても隠した BACK を選ばない");
        Assert.AreEqual("ab", JsonUtility.FromJson<ChartMarkFile>(File.ReadAllText(ChartMarks.CurrentPath)).marks[0].comment);

        yield return Tap(Key.F5);
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen, "閉じたあとでも F5 でもう一度開ける");
        Assert.AreEqual("ab", ChartMarkCommentPanel.Current.TextAt(0), "保存した一言が出る");
        ChartMarkCommentPanel.Current.Close();
        yield return null;
        yield return null;
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);
    }

    [UnityTest]
    public IEnumerator F5WaitsForTheConversionBeforeClosing()
    {
        keyboard.MakeCurrent();
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        Assert.NotNull(panel);
        yield return null;
        panel.Compose("へんかん");
        yield return Tap(Key.F5);
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen, "変換中の F5 では閉じない");
        StringAssert.Contains(ChartMarkCommentPanel.ConfirmFirstNotice, panel.FooterText, "変換を確定してから F5 と案内する");
        Assert.AreEqual("Result", SceneManager.GetActiveScene().name);

        // 変換が終わってから(終わったコマは数えない)の F5 で閉じる。
        panel.Compose("");
        yield return null;
        yield return null;
        yield return Tap(Key.F5);
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen, "変換を確定したあとの F5 で閉じる");
    }

    [UnityTest]
    public IEnumerator TypingDoesNotSkipTheResultReveal()
    {
        // 結果の演出の途中で欄が開いても、打った文字で演出を飛ばさない(ResultReveal はキーボードを直接読む)。
        typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reveal, 0f);
        reveal.Tick(0);
        reveal.enabled = true;
        keyboard.MakeCurrent();
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        Assert.NotNull(panel);
        yield return Tap(Key.A);
        float elapsed = (float)typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reveal);
        Assert.Less(elapsed, 5f, "打っている間は演出を飛ばさない");
        panel.Close();
        yield return null;
        yield return null;
        yield return Tap(Key.A);
        elapsed = (float)typeof(ResultReveal).GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reveal);
        Assert.GreaterOrEqual(elapsed, 999f, "閉じたあとは今までどおりキーで飛ばせる");
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
