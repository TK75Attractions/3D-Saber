using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

// 実譜面を保存せず、エディターの入力フォーカス・編集履歴・新規作成の操作を確認する。
public class ChartEditorWorkflowTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] IntPrefs = { "Difficulty", "Snap", "Measure" };
    readonly Dictionary<string, int> savedInts = new Dictionary<string, int>();
    readonly HashSet<string> existingPrefs = new HashSet<string>();
    SaberChartEditorWindow window;
    string savedSong;
    float savedZoom;
    bool savedTextEditing;
    int savedKeyboardControl;
    readonly Rect timeline = new Rect(0, 0, 400, 500);
    readonly Rect lanes = new Rect(62, 0, 326, 500);

    [SetUp]
    public void SetUp()
    {
        foreach (string key in new[] { "SongId", "Difficulty", "Snap", "Measure", "Zoom" })
            if (EditorPrefs.HasKey(Prefix + key)) existingPrefs.Add(key);
        savedSong = EditorPrefs.GetString(Prefix + "SongId");
        savedZoom = EditorPrefs.GetFloat(Prefix + "Zoom");
        foreach (string key in IntPrefs) savedInts[key] = EditorPrefs.GetInt(Prefix + key);
        savedTextEditing = EditorGUIUtility.editingTextField;
        savedKeyboardControl = GUIUtility.keyboardControl;
        // OnEnableで利用者の曲を開かず、既存の編集設定も試験後に戻す。
        EditorPrefs.SetString(Prefix + "SongId", "__ChartEditorWorkflowTest");
        EditorPrefs.SetInt(Prefix + "Difficulty", 1);
        EditorPrefs.SetInt(Prefix + "Snap", 3);
        EditorPrefs.SetFloat(Prefix + "Zoom", 82f);
        window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
        Set("document", new SaberChartDocument
        {
            notes = new List<SaberChartNote>
            {
                new SaberChartNote { beat = 4, time = 2000, x = -.3571429f, y = .2142857f },
                new SaberChartNote { beat = 8, time = 4000, x = 1.0714285f, y = .2142857f }
            }
        });
        Set("beatZeroMs", 0f);
        Set("currentBeat", 4f);
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
        EditorGUIUtility.editingTextField = false;
    }

    [TearDown]
    public void TearDown()
    {
        if (window != null)
        {
            window.DiscardChanges();
            Object.DestroyImmediate(window);
        }
        EditorPrefs.SetString(Prefix + "SongId", savedSong);
        EditorPrefs.SetFloat(Prefix + "Zoom", savedZoom);
        foreach (string key in IntPrefs) EditorPrefs.SetInt(Prefix + key, savedInts[key]);
        foreach (string key in new[] { "SongId", "Difficulty", "Snap", "Measure", "Zoom" })
            if (!existingPrefs.Contains(key)) EditorPrefs.DeleteKey(Prefix + key);
        existingPrefs.Clear();
        savedInts.Clear();
        EditorGUIUtility.editingTextField = savedTextEditing;
        GUIUtility.keyboardControl = savedKeyboardControl;
    }

    [Test]
    public void MeterEditingSupportsHistoryAndNavigationWithoutMovingNotes()
    {
        var times = Document.notes.ConvertAll(note => note.time);
        Call("SetTimeSignature", 0f, 7, 8);
        Assert.True(window.hasUnsavedChanges);
        CollectionAssert.AreEqual(times, Document.notes.ConvertAll(note => note.time));
        Set("currentBeat", 0f);
        Key(KeyCode.RightArrow, EventModifiers.Shift);
        Assert.AreEqual(3.5f, Get<float>("currentBeat"));
        Key(KeyCode.RightArrow, EventModifiers.Shift);
        Assert.AreEqual(7f, Get<float>("currentBeat"));
        Call("SetTimeSignature", 7f, 3, 4);
        Key(KeyCode.RightArrow, EventModifiers.Shift);
        Assert.AreEqual(10f, Get<float>("currentBeat"));
        Call("Undo");
        Assert.AreEqual(1, Document.timeSignatures.Count);
        Call("Undo");
        Assert.AreEqual(0, Document.timeSignatures.Count);
        Call("Redo");
        Assert.AreEqual(7, Document.timeSignatures[0].numerator);
        CollectionAssert.AreEqual(times, Document.notes.ConvertAll(note => note.time));
    }

    [Test]
    public void MeterSaveReloadAndDeletionKeepNoteTimesAndTheGridOrigin()
    {
        WithChartFolder(target =>
        {
            var times = Document.notes.ConvertAll(note => note.time);
            Call("SetTimeSignature", 0f, 4, 4);
            Call("SetTimeSignature", 8f, 7, 8);
            Call("SetTimeSignature", 15f, 3, 4);
            Call("EditTimeSignature", 1, 8f, 7, 8, true);
            Assert.AreEqual(2, Document.timeSignatures.Count);
            Call("Undo");
            Assert.AreEqual(3, Document.timeSignatures.Count);
            Set("beatZeroMs", 123f);
            Set("songId", target);
            Assert.True((bool)Call("SaveDocument"));
            Set("document", new SaberChartDocument());
            LoadTarget(target, false);
            Assert.AreEqual(123f, Get<float>("beatZeroMs"));
            Assert.AreEqual(7, Document.timeSignatures[1].numerator);
            Assert.AreEqual(8, Document.timeSignatures[1].denominator);
            CollectionAssert.AreEqual(times, Document.notes.ConvertAll(note => note.time));
            Assert.False(window.hasUnsavedChanges);
        });
    }

    [Test]
    public void SpatialPositionEditAfterTypingReturnsShortcutsToTheChart()
    {
        Set("selectedIndex", 0);
        EditorGUIUtility.editingTextField = true;
        Call("SetSpatialPosition", 6, 1);
        Assert.False(EditorGUIUtility.editingTextField);
        Assert.AreEqual(SaberChartUtility.CoordinateForLane(6, 8, -2.5f, 2.5f), Document.notes[0].x);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(-.3571429f, Document.notes[0].x, .0001f);
    }

    [TestCase(KeyCode.Delete, EventModifiers.None)]
    [TestCase(KeyCode.D, EventModifiers.Control)]
    public void TypingStillDoesNotDeleteOrDuplicateNotes(KeyCode key, EventModifiers modifiers)
    {
        Set("selectedIndex", 0);
        EditorGUIUtility.editingTextField = true;
        Key(key, modifiers);
        Assert.AreEqual(2, Document.notes.Count);
    }

    [Test]
    public void NewEasyChartStartsOnWholeBeats()
    {
        Set("difficultyIndex", 0);
        Call("NewDocument");
        Assert.AreEqual(4, GetProperty<int>("CurrentSnap"));
        // 半拍の近くをクリックしても、新規Easyの既定では整数拍へ置く。
        Call("AddNoteAtMouse", new Vector2(200, 500 * .72f - 1.4f * 82), timeline, lanes);
        Assert.AreEqual(1, Document.notes.Count);
        Assert.AreEqual(1f, Document.notes[0].beat);
        Assert.AreEqual(500f, Document.notes[0].time);
    }

    [TestCase(1)]
    [TestCase(2)]
    public void OtherNewDifficultiesKeepTheChosenSnap(int difficulty)
    {
        Set("difficultyIndex", difficulty);
        Call("NewDocument");
        Assert.AreEqual(16, GetProperty<int>("CurrentSnap"));
    }

    [Test]
    public void DuplicateShortcutUsesSnapAndCanBeUndone()
    {
        Set("selectedIndex", 0);
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(3, Document.notes.Count);
        Assert.AreEqual(2125f, Document.notes[1].time);
        Assert.AreEqual(Document.notes[0].x, Document.notes[1].x);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void UndoDuringDragDoesNotContinueDraggingTheRestoredChart()
    {
        // マウスイベントは実ウィンドウで別途確認し、ここでは進行中ドラッグの履歴境界を検証する。
        var history = typeof(SaberChartEditorWindow).GetField("history", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        history.GetType().GetMethod("Record").Invoke(history, new object[] { SaberChartUtility.ToJson(Document, false) });
        Document.notes[0].beat = 5;
        Document.notes[0].time = 2500;
        Set("draggingNote", true);
        Set("dragRecorded", true);
        Set("dragNoteIndex", 0);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(2000f, Document.notes[0].time);
        Assert.False(Get<bool>("draggingNote"), "Undo後に古いドラッグ操作を続けない");
        Assert.False(window.hasUnsavedChanges);
        Key(KeyCode.Y, EventModifiers.Control);
        Assert.AreEqual(2500f, Document.notes[0].time, "終了したドラッグをRedoできる");
    }

    static readonly System.Type AudioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
    static object AudioCall(string name) => AudioUtil.GetMethod(name,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, null);

    IEnumerator AwaitPreview(AudioClip clip, float afterSeconds)
    {
        double deadline = EditorApplication.timeSinceStartup + 4;
        while (EditorApplication.timeSinceStartup < deadline)
        {
            if ((bool)AudioCall("IsPreviewClipPlaying") &&
                (int)AudioCall("GetPreviewClipSamplePosition") / (float)clip.frequency > afterSeconds) yield break;
            yield return null;
        }
        Assert.Fail("音声プレビューが指定位置まで再生されませんでした");
    }

    [UnityTest]
    public IEnumerator PreviewFollowsRealSamplesAndSharesGameOffsetWithoutChangingChart()
    {
        bool hadOffset = PlayerPrefs.HasKey("judgmentOffsetMs");
        int savedOffset = GameSession.JudgmentOffsetMs;
        var clip = AudioClip.Create("EditorTimingSilence", 48000 * 5, 1, 48000, false);
        string original = SaberChartUtility.ToJson(Document, false);
        try
        {
            PlayerPrefs.SetInt("judgmentOffsetMs", 125);
            Call("SetAudioClip", clip);
            Set("currentBeat", 2f);
            Set("useGameTiming", true);
            Call("TogglePreview");
            yield return AwaitPreview(clip, 1.3f);
            Call("UpdatePlaybackPosition");
            float seconds = (int)AudioCall("GetPreviewClipSamplePosition") / (float)clip.frequency;
            Assert.That(Get<float>("currentBeat"), Is.EqualTo((seconds - .125f) * 2).Within(.055f));

            Set("useGameTiming", false);
            Call("UpdatePlaybackPosition");
            seconds = (int)AudioCall("GetPreviewClipSamplePosition") / (float)clip.frequency;
            Assert.That(Get<float>("currentBeat"), Is.EqualTo(seconds * 2).Within(.055f));
            Assert.AreEqual(original, SaberChartUtility.ToJson(Document, false));
            Assert.False(window.hasUnsavedChanges, "試聴の補正で譜面を書き換えない");
            Assert.AreEqual(125, GameSession.JudgmentOffsetMs);
        }
        finally
        {
            Call("StopPreview", false);
            Object.DestroyImmediate(clip);
            if (hadOffset) PlayerPrefs.SetInt("judgmentOffsetMs", savedOffset); else PlayerPrefs.DeleteKey("judgmentOffsetMs");
        }
    }

    [UnityTest]
    public IEnumerator SeekingAndStoppingUseAudioStateInsteadOfContinuingTheOldWallClock()
    {
        var clip = AudioClip.Create("EditorSeekSilence", 48000 * 4, 1, 48000, false);
        try
        {
            Call("SetAudioClip", clip);
            Set("useGameTiming", false);
            Set("currentBeat", 0f);
            Call("TogglePreview");
            yield return AwaitPreview(clip, .1f);
            Call("SeekToBeat", 4f);
            yield return AwaitPreview(clip, 2.1f);
            Call("UpdatePlaybackPosition");
            float seconds = (int)AudioCall("GetPreviewClipSamplePosition") / (float)clip.frequency;
            Assert.That(Get<float>("currentBeat"), Is.EqualTo(seconds * 2).Within(.055f));
            Call("TogglePreview");
            float paused = Get<float>("currentBeat");
            for (int i = 0; i < 5; i++) { yield return null; Call("UpdatePlaybackPosition"); }
            Assert.AreEqual(paused, Get<float>("currentBeat"));
            Call("TogglePreview");
            yield return AwaitPreview(clip, paused * .5f + .1f);
            Call("UpdatePlaybackPosition");
            AudioCall("StopAllPreviewClips");
            Call("UpdatePlaybackPosition");
            Assert.False(Get<bool>("isPlaying"), "音声が止まったら表示も止まる");
        }
        finally { Call("StopPreview", false); Object.DestroyImmediate(clip); }
    }

    SaberChartDocument Document => Get<SaberChartDocument>("document");

    [TestCase(false, "malformed")]
    [TestCase(true, "malformed")]
    [TestCase(false, "locked")]
    [TestCase(true, "locked")]
    [TestCase(false, "invalidId")]
    [TestCase(true, "invalidId")]
    public void FailedLoadAfterDiscardKeepsUnsavedDocumentAndUndo(bool fromMenu, string failure)
    {
        WithChartFolder(target =>
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "Songs", target, "chart_normal.json");
            System.IO.File.WriteAllText(path, failure == "malformed" ? "{ invalid json" : "{\"bpm\":150,\"notes\":[]}");
            if (failure == "invalidId") target = "../invalid";
            Set("loadedSongId", "__OriginalChart");
            Set("loadedDifficulty", "normal");
            Set("selectedIndex", 0);
            Key(KeyCode.D, EventModifiers.Control);
            var original = Document;
            string json = SaberChartUtility.ToJson(original, false);
            string saved = Get<string>("savedJson");
            int selected = Get<int>("selectedIndex");
            int confirmations = 0, errors = 0;
            Set("confirmChangesDialog", new System.Func<string, string, string, string, string, int>((a, b, c, d, e) => { confirmations++; return 2; }));
            Set("reportLoadError", new System.Action<string>(message => { Assert.IsNotEmpty(message); errors++; }));
            string originalDestination = Get<string>("songId");
            System.IO.FileStream locked = failure == "locked"
                ? new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None) : null;
            try { LoadTarget(target, fromMenu); }
            finally { locked?.Dispose(); }
            Assert.AreEqual(1, errors, "読込失敗を明示する");
            Assert.AreEqual(1, confirmations);
            Assert.AreSame(original, Document);
            Assert.AreEqual(json, SaberChartUtility.ToJson(Document, false));
            Assert.AreEqual(saved, Get<string>("savedJson"));
            Assert.True(window.hasUnsavedChanges, "読込失敗で保存警告を失わない");
            Assert.AreEqual(selected, Get<int>("selectedIndex"));
            Assert.AreEqual(4f, Get<float>("currentBeat"));
            Assert.AreEqual("__OriginalChart", Get<string>("loadedSongId"));
            Assert.AreEqual(fromMenu ? originalDestination : target, Get<string>("songId"));
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(2, Document.notes.Count, "失敗後も前の譜面をUndoできる");
            Assert.False(window.hasUnsavedChanges);
            Key(KeyCode.Y, EventModifiers.Control);
            Assert.AreEqual(3, Document.notes.Count);
            Assert.True(window.hasUnsavedChanges);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CancelLoadKeepsEditingSession(bool fromMenu)
    {
        Set("selectedIndex", 0);
        Key(KeyCode.D, EventModifiers.Control);
        var original = Document;
        string originalDestination = Get<string>("songId");
        Set("confirmChangesDialog", new System.Func<string, string, string, string, string, int>((a, b, c, d, e) => 1));
        Set("reportLoadError", new System.Action<string>(message => Assert.Fail(message)));
        LoadTarget("../invalid", fromMenu);
        Assert.AreSame(original, Document);
        Assert.True(window.hasUnsavedChanges);
        Assert.AreEqual(fromMenu ? originalDestination : "../invalid", Get<string>("songId"));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void SuccessfulLoadDiscardsOnlyOnceAndClearsOldHistory(bool fromMenu, bool legacy)
    {
        WithChartFolder(target =>
        {
            string file = legacy ? "chart.json" : "chart_normal.json";
            System.IO.File.WriteAllText(System.IO.Path.Combine(Application.streamingAssetsPath, "Songs", target, file),
                "{\"bpm\":150,\"notes\":[{\"beat\":2,\"time\":800,\"x\":1}]}");
            Set("selectedIndex", 0);
            Key(KeyCode.D, EventModifiers.Control);
            int confirmations = 0;
            Set("confirmChangesDialog", new System.Func<string, string, string, string, string, int>((a, b, c, d, e) => { confirmations++; return 2; }));
            Set("reportLoadError", new System.Action<string>(message => Assert.Fail(message)));
            LoadTarget(target, fromMenu);
            Assert.AreEqual(1, confirmations);
            Assert.AreEqual(150f, Document.bpm);
            Assert.AreEqual(1, Document.notes.Count);
            Assert.False(window.hasUnsavedChanges);
            Assert.AreEqual(target, Get<string>("songId"));
            Assert.AreEqual(target, Get<string>("loadedSongId"));
            Assert.AreEqual(-1, Get<int>("selectedIndex"));
            Assert.AreEqual(0f, Get<float>("currentBeat"));
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(1, Document.notes.Count, "古い譜面の履歴を混ぜない");
        });
    }

    void LoadTarget(string target, bool fromMenu)
    {
        if (fromMenu) Call("LoadSongFromMenu", target);
        else { Set("songId", target); Call("LoadDocument"); }
    }

    void WithChartFolder(System.Action<string> check)
    {
        string target = "__ChartLoadTest_" + System.Guid.NewGuid().ToString("N");
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.streamingAssetsPath, "Songs")) + System.IO.Path.DirectorySeparatorChar;
        string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, target));
        System.IO.Directory.CreateDirectory(folder);
        try { check(target); }
        finally
        {
            Assert.True(folder.StartsWith(root, System.StringComparison.OrdinalIgnoreCase));
            Assert.True(System.IO.Path.GetFileName(folder).StartsWith("__ChartLoadTest_"));
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
            if (System.IO.File.Exists(folder + ".meta")) System.IO.File.Delete(folder + ".meta");
        }
    }
    void Key(KeyCode key, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
    void Set(string name, object value) => typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, value);
    T Get<T>(string name) => (T)typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
    T GetProperty<T>(string name) => (T)typeof(SaberChartEditorWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
    object Call(string name, params object[] args) => typeof(SaberChartEditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args);
}
