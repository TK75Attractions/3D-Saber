using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;

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

    SaberChartDocument Document => Get<SaberChartDocument>("document");
    void Key(KeyCode key, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
    void Set(string name, object value) => typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, value);
    T Get<T>(string name) => (T)typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
    T GetProperty<T>(string name) => (T)typeof(SaberChartEditorWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
    object Call(string name, params object[] args) => typeof(SaberChartEditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args);
}
