using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 画面の切り替えで編集内容・履歴を失わず、狭い窓でも固定操作と編集領域を保つ。
public class ChartEditorWorkspaceTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    readonly Dictionary<string, object> preferences = new Dictionary<string, object>();
    SaberChartEditorWindow window;
    bool savedEditing;
    int savedKeyboard;
    SaberChartDocument Document => Get<SaberChartDocument>("document");

    [SetUp]
    public void SetUp()
    {
        foreach (string key in new[] { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout", "ShowPlaybackPreview", "Workspace" })
            preferences[key] = !EditorPrefs.HasKey(Prefix + key) ? null
                : key == "SongId" || key == "RecordingLayout" || key == "Workspace" ? (object)EditorPrefs.GetString(Prefix + key)
                : key == "Zoom" ? (object)EditorPrefs.GetFloat(Prefix + key) : EditorPrefs.GetInt(Prefix + key);
        savedEditing = EditorGUIUtility.editingTextField;
        savedKeyboard = GUIUtility.keyboardControl;
        EditorGUIUtility.editingTextField = false;
        EditorPrefs.SetString(Prefix + "SongId", "__WorkspaceUITest");
        EditorPrefs.DeleteKey(Prefix + "Workspace");
        window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
        Set("document", new SaberChartDocument
        {
            bpm = 120f, coordScale = 1f,
            notes = new List<SaberChartNote>
            {
                new SaberChartNote { time = 2000, beat = 4, x = -1, y = 0, color = "blue" },
                new SaberChartNote { time = 4000, beat = 8, x = 1, y = 0, color = "red" },
            }
        });
        Set("loadedSongId", "__WorkspaceUITest"); Set("loadedDifficulty", "normal");
        Set("songId", "__WorkspaceUITest"); Set("difficultyIndex", 1);
        Set("recordMode", false); Set("recordStepMode", false);
        Set("selectedIndex", -1); Set("currentBeat", 4f); Set("beatZeroMs", 0f);
        Set("useGameTiming", false); Set("showPlaybackPreview", false);
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
    }

    [TearDown]
    public void TearDown()
    {
        if (window != null)
        {
            Call("StopPreview", false);
            window.DiscardChanges();
            Object.DestroyImmediate(window);
        }
        foreach (var pair in preferences)
        {
            if (pair.Value == null) EditorPrefs.DeleteKey(Prefix + pair.Key);
            else if (pair.Value is string text) EditorPrefs.SetString(Prefix + pair.Key, text);
            else if (pair.Value is float number) EditorPrefs.SetFloat(Prefix + pair.Key, number);
            else EditorPrefs.SetInt(Prefix + pair.Key, (int)pair.Value);
        }
        preferences.Clear();
        EditorGUIUtility.editingTextField = savedEditing;
        GUIUtility.keyboardControl = savedKeyboard;
    }

    [TestCase(1050, 650, true, true, true)]
    [TestCase(1050, 650, false, false, false)]
    [TestCase(1280, 720, true, false, true)]
    [TestCase(1920, 1080, false, true, false)]
    public void LayoutKeepsTransportAndTimelineInsideWindow(int width, int height, bool palette, bool inspector, bool picker)
    {
        Set("showPalette", palette); Set("showInspector", inspector); Set("showDocumentPicker", picker);
        Set("workspaceLeftWidth", 320f); Set("workspaceRightWidth", 460f);
        object layout = Call("CalculateWorkspace", new Vector2(width, height));
        Rect center = RectOf(layout, "center"), transport = RectOf(layout, "transport");
        Assert.GreaterOrEqual(center.width, 350);
        Assert.Greater(center.height, 250);
        Assert.LessOrEqual(center.xMax, width);
        Assert.GreaterOrEqual(center.yMin, transport.yMax);
        Assert.LessOrEqual(center.yMax, RectOf(layout, "footer").yMin);
        if (palette) Assert.LessOrEqual(RectOf(layout, "left").xMax, center.xMin);
        if (inspector) Assert.LessOrEqual(center.xMax, RectOf(layout, "right").xMin);
    }

    [Test]
    public void TabsRetainTheirScrollAndDoNotChangeDocumentOrUndo()
    {
        Set("selectedIndex", 0); Call("NudgeSelectedNoteTime", 10f);
        string before = SaberChartUtility.ToJson(Document, false);
        Set("rightScroll", new Vector2(0, 120));
        Section("Chart"); Set("rightScroll", new Vector2(0, 430));
        Section("Notes"); Assert.AreEqual(120, Get<Vector2>("rightScroll").y);
        Section("Chart"); Assert.AreEqual(430, Get<Vector2>("rightScroll").y);
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        Call("Undo"); Assert.AreEqual(2000, Document.notes[0].time);
        Call("Redo"); Assert.AreEqual(2010, Document.notes[0].time);
    }

    [Test]
    public void NewSelectionOpensNotesButSameSelectionDoesNotStealAnotherTab()
    {
        Set("showInspector", false); Set("selectedIndex", 0);
        Section("Song"); Call("RevealSelectedNote");
        Assert.AreEqual("Notes", Get<object>("inspectorSection").ToString());
        Assert.True(Get<bool>("showInspector"));
        Section("Chart"); Call("RevealSelectedNote");
        Assert.AreEqual("Chart", Get<object>("inspectorSection").ToString());
        Call("SetRange", 0f, 5000f);
        Assert.AreEqual("Notes", Get<object>("inspectorSection").ToString());
        Assert.AreEqual(-1, Get<int>("selectedIndex"));
    }

    [Test]
    public void RecordingAndStepModesKeepTheirSettingsAndOpenRecordingTab()
    {
        Set("recordInputOffsetMs", 72f); Set("recordLongCount", 5);
        Call("SetRecordingInputMode", 2);
        Assert.True(Get<bool>("recordStepMode"));
        Assert.AreEqual("Recording", Get<object>("inspectorSection").ToString());
        Call("SetRecordingInputMode", 0); Call("SetRecordingInputMode", 1);
        Assert.False(Get<bool>("recordStepMode"));
        Assert.AreEqual(72f, Get<float>("recordInputOffsetMs"));
        Assert.AreEqual(5, Get<int>("recordLongCount"));
    }

    [Test]
    public void ToolbarStopsCountInWithoutAudioAndWithoutChangingNotes()
    {
        string before = SaberChartUtility.ToJson(Document, false);
        Set("countingIn", true);
        Call("ToggleWorkspaceRecording");
        Assert.False(Get<bool>("countingIn"));
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
    }

    [Test]
    public void DiagnosticNavigationPreservesAllNoteDataAndResolvesOffsetPosition()
    {
        Document.offsetMs = 150;
        Document.notes[1].x = 5;
        string before = SaberChartUtility.ToJson(Document, false);
        var issues = (IList)Call("WorkspaceNoteIssues", 10000f);
        Assert.AreEqual(1, issues.Count);
        Assert.AreEqual(1, issues[0].GetType().GetField("index").GetValue(issues[0]));
        Section("Diagnostics"); Call("FocusDiagnosticNote", 1);
        Assert.AreEqual(1, Get<int>("selectedIndex"));
        Assert.AreEqual("Notes", Get<object>("inspectorSection").ToString());
        Assert.AreEqual(8f, Get<float>("currentBeat"));
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
    }

    [Test]
    public void ResetAndRestoreWorkspaceNeverChangeSaveDestinationOrNotes()
    {
        string before = SaberChartUtility.ToJson(Document, false);
        Set("songId", "PendingOtherSong");
        Set("showPalette", false); Set("workspaceRightWidth", 451f);
        Set("workspacePreferencesChanged", true); Call("SaveWorkspacePreferences");
        Call("ResetWorkspaceLayout");
        Assert.True(Get<bool>("showPalette"));
        Call("LoadWorkspacePreferences");
        Assert.False(Get<bool>("showPalette"));
        Assert.AreEqual(451f, Get<float>("workspaceRightWidth"));
        Assert.AreEqual("__WorkspaceUITest", Get<string>("loadedSongId"));
        Assert.AreEqual("PendingOtherSong", Get<string>("songId"));
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
    }

    [UnityTest]
    public IEnumerator AllPanelsRenderInEveryModeAtMinimumAndWideSizes()
    {
        // 実際のIMGUIを通す。タブやモードによるLayout/Repaint不一致も失敗として検出する。
        window.ShowUtility();
        foreach (var size in new[] { new Vector2(1050, 650), new Vector2(1440, 900) })
        {
            window.position = new Rect(50, 50, size.x, size.y);
            foreach (int mode in new[] { 0, 1, 2 })
            {
                Call("SetRecordingInputMode", mode);
                foreach (string section in new[] { "Notes", "Recording", "Navigation", "Chart", "Song", "Diagnostics" })
                {
                    Section(section);
                    window.Repaint();
                    yield return null; yield return null;
                    LogAssert.NoUnexpectedReceived();
                    CaptureIfRequested($"{size.x:0}-{mode}-{section}");
                }
            }
            Set("showDocumentPicker", true);
            window.Repaint(); yield return null; yield return null;
            CaptureIfRequested($"{size.x:0}-open");
            Set("showDocumentPicker", false);
        }
    }

    // Unityのウィンドウ自身の描画バッファだけを保存する。デスクトップの画面や他アプリは収録しない。
    void CaptureIfRequested(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int arg = Array.IndexOf(args, "-chartWorkspaceCapture");
        if (arg < 0 || arg + 1 >= args.Length) return;
        string output = args[arg + 1];
        Directory.CreateDirectory(output);
        object view = typeof(EditorWindow).GetField("m_Parent", Private).GetValue(window);
        Type viewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
        viewType.GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(view, null);
        float scale = EditorGUIUtility.pixelsPerPoint;
        int width = Mathf.RoundToInt(window.position.width * scale), height = Mathf.RoundToInt(window.position.height * scale);
        var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        RenderTexture old = RenderTexture.active;
        try
        {
            target.Create();
            viewType.GetMethod("GrabPixels", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { target, new Rect(0, 0, width, height) });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0); image.Apply();
            if (SystemInfo.graphicsUVStartsAtTop)
            {
                Color32[] pixels = image.GetPixels32(), flipped = new Color32[pixels.Length];
                for (int y = 0; y < height; y++) Array.Copy(pixels, y * width, flipped, (height - 1 - y) * width, width);
                image.SetPixels32(flipped); image.Apply();
            }
            File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = old;
            target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(image);
        }
    }

    [Test]
    public void HeightButtonsPreserveHorizontalPositionAndTimingAndSupportUndo()
    {
        Set("selectedIndex", 0);
        Document.notes[0].x = .123f;
        Call("NudgeNoteHeight", 1, true);
        Assert.AreEqual(.01f, Document.notes[0].y, .00001f);
        Assert.AreEqual(.123f, Document.notes[0].x);
        Assert.AreEqual(2000f, Document.notes[0].time);
        Assert.AreEqual(4f, Document.notes[0].beat);
        Call("Undo");
        Assert.AreEqual(0f, Document.notes[0].y);
        Call("Redo");
        Assert.AreEqual(.01f, Document.notes[0].y, .00001f);
    }

    [Test]
    public void HeightButtonsUpdatePlacementWithoutEditingChartAndClampRange()
    {
        string before = SaberChartUtility.ToJson(Document, false);
        Set("paletteYLane", 3);
        Call("NudgeNoteHeight", 1, false);
        Assert.AreEqual(4, Get<int>("paletteYLane"));
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        Call("SetRange", 0f, 5000f);
        Document.notes[0].y = 1.5f;
        Call("NudgeNoteHeight", 1, false);
        Assert.AreEqual(1.5f, Document.notes[0].y);
        Assert.Greater(Document.notes[1].y, 0f);
        Assert.AreEqual(-1f, Document.notes[0].x);
        Assert.AreEqual(1f, Document.notes[1].x);
    }

    void Section(string name) => Call("SetInspectorSection", Enum.Parse(typeof(SaberChartEditorWindow).GetNestedType("InspectorSection", BindingFlags.NonPublic), name));
    static Rect RectOf(object layout, string name) => (Rect)layout.GetType().GetField(name).GetValue(layout);
    T Get<T>(string name) => (T)typeof(SaberChartEditorWindow).GetField(name, Private).GetValue(window);
    void Set(string name, object value) => typeof(SaberChartEditorWindow).GetField(name, Private).SetValue(window, value);
    object Call(string name, params object[] args) => typeof(SaberChartEditorWindow).GetMethod(name, Private).Invoke(window, args);
}
