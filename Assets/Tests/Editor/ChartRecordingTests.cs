using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public class ChartRecordingTests
{
    [Test]
    public void AudioTimeSubtractsChartAndInputOffsetsOnce()
    {
        var doc = new SaberChartDocument { bpm = 120, offsetMs = 120, beatZeroMs = 80 };
        var take = new SaberChartRecorder(doc, 10, 0, 30, true, 3);
        var note = take.Press(0, 1.25f, -1, 0, "blue");
        Assert.AreEqual(1100, note.time, .001);
        Assert.AreEqual(2.04f, note.beat, .001);
        Assert.AreEqual("tap", note.type);
        take.Release(0, 1.35f);
        Assert.AreEqual(0, note.lengthMs);
    }

    [Test]
    public void ChordsAndSeparatePositionsSurviveWhileRepeatAndDuplicatesDoNot()
    {
        var doc = new SaberChartDocument();
        var take = new SaberChartRecorder(doc, 10, 4, 0, true, 2);
        take.Press(0, 1.01f, -1, 0, "blue");
        Assert.IsNull(take.Press(0, 1.51f, -1, 0, "blue"), "キーリピートを無視する");
        take.Press(1, 1.02f, 1, 0, "red");
        take.Press(2, 1.03f, -2, 0, "blue");
        take.Release(0, 1.1f);
        Assert.IsNull(take.Press(0, 1.1f, -1, 0, "blue"), "同一Snapへの二重登録を防ぐ");
        take.Finish(1.15f);
        Assert.AreEqual(3, doc.notes.Count);
        Assert.AreEqual(3, take.AddedCount);
        Assert.That(doc.notes.TrueForAll(n => n.time == 1000));
    }

    [Test]
    public void LongReleaseQuantizesBothEndsAndRoundTrips()
    {
        var doc = new SaberChartDocument { offsetMs = 100, beatZeroMs = 50 };
        var take = new SaberChartRecorder(doc, 10, 8, 0, true, 4);
        var note = take.Press(0, 1.17f, 1, .5f, "red", "up");
        take.Release(0, 1.94f);
        Assert.AreEqual(1050, note.time, .001);
        Assert.AreEqual(750, note.lengthMs, .001);
        Assert.AreEqual("long", note.type);
        Assert.AreEqual(4, note.count);
        var restored = SaberChartUtility.FromJson(SaberChartUtility.ToJson(doc)).notes[0];
        Assert.AreEqual("up", restored.direction);
        Assert.AreEqual(note.time, restored.time);
        Assert.AreEqual(note.lengthMs, restored.lengthMs);
    }

    [Test]
    public void SnappingFollowsMeterChanges()
    {
        var doc = new SaberChartDocument { timeSignatures = new List<ChartTimeSignature>
            { new ChartTimeSignature { beat = 0, numerator = 7, denominator = 8 } } };
        var take = new SaberChartRecorder(doc, 10, 4, 0, false, 2);
        var note = take.Press(0, 1.8f, 0, 0, "gold");
        Assert.AreEqual(3.5f, note.beat);
        Assert.AreEqual(1750, note.time);
    }

    [Test]
    public void StopFinalizesAllHeldNotesAndPreservesExistingNotes()
    {
        var existing = new SaberChartNote { time = 123, x = 2, color = "gold" };
        var doc = new SaberChartDocument { notes = new List<SaberChartNote> { existing } };
        var take = new SaberChartRecorder(doc, 2, 0, 0, true, 3);
        take.Press(0, 1, -1, 0, "blue");
        take.Press(1, 1, 1, 0, "red");
        take.Finish(3);
        Assert.AreEqual(3, doc.notes.Count);
        Assert.AreSame(existing, doc.notes[0]);
        Assert.AreEqual(123, existing.time);
        Assert.AreEqual(1000, doc.notes[1].lengthMs);
        Assert.AreEqual(1000, doc.notes[2].lengthMs);
        Assert.False(take.IsHeld(0));
        Assert.False(take.IsHeld(1));
    }

    [Test]
    public void RecordingOverExistingNoteNeverTurnsItIntoALong()
    {
        var existing = new SaberChartNote { time = 1000, beat = 2, x = -1, color = "blue" };
        var doc = new SaberChartDocument { notes = new List<SaberChartNote> { existing } };
        var take = new SaberChartRecorder(doc, 10, 4, 0, true, 2);
        Assert.IsNull(take.Press(0, 1.01f, -1, 0, "blue"));
        take.Finish(2);
        Assert.AreEqual("tap", existing.type);
        Assert.AreEqual(0, take.AddedCount);
    }

    [Test]
    public void FreeTimingWorksBeforeGridOriginAndRejectsOutOfAudioNotes()
    {
        var doc = new SaberChartDocument { beatZeroMs = 500, offsetMs = 100 };
        var take = new SaberChartRecorder(doc, 1, 0, 0, false, 2);
        Assert.IsNull(take.Press(0, .05f, 0, 0, "gold"));
        take.Release(0, .06f);
        var note = take.Press(0, .2f, 0, 0, "gold");
        Assert.AreEqual(100, note.time, .001);
        take.Release(0, .9f);
        Assert.AreEqual("tap", note.type, "長押しOFFならTAPのまま");
        Assert.IsNull(take.Press(1, 1, 0, 0, "gold"));
        Assert.IsNull(take.Press(2, float.NaN, 0, 0, "gold"));
        var snapped = new SaberChartRecorder(doc, 1, 4, 0, true, 2);
        Assert.IsNull(snapped.Press(3, .99f, 1, 0, "red"));
    }

    [Test]
    public void ShortHoldOnCoarseGridDoesNotCreateAnImplicitLongPastAudioEnd()
    {
        var doc = new SaberChartDocument();
        var take = new SaberChartRecorder(doc, 1.2f, 4, 0, true, 2);
        var note = take.Press(0, .76f, 0, 0, "gold");
        take.Release(0, 1.03f);
        Assert.AreEqual("tap", note.type, "量子化した始点と終点が同じならTAPにする");
        Assert.AreEqual(0, note.lengthMs);
    }
}

public class ChartRecordingWorkflowTests
{
    private SaberChartEditorWindow window;
    private AudioClip clip;
    private float seconds;
    private bool textEditing;
    private bool shown;
    private readonly Dictionary<string, object> preferences = new Dictionary<string, object>();
    private const string Prefix = "3DSaber.ChartEditor.";
    private SaberChartDocument Document => Get<SaberChartDocument>("document");

    [SetUp]
    public void SetUp()
    {
        shown = false;
        foreach (string key in new[] { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout" })
        {
            preferences[key] = !EditorPrefs.HasKey(Prefix + key) ? null : key == "SongId" || key == "RecordingLayout" ? (object)EditorPrefs.GetString(Prefix + key)
                : key == "Zoom" ? (object)EditorPrefs.GetFloat(Prefix + key) : EditorPrefs.GetInt(Prefix + key);
        }
        EditorPrefs.SetString(Prefix + "SongId", "__RecordingTest");
        window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
        clip = AudioClip.Create("RecordingTest", 44100 * 10, 1, 44100, false);
        Set("audioClip", clip);
        Set("document", new SaberChartDocument());
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Set("recordMode", true);
        Set("recordPositions", null);
        Set("recordHeight", 1);
        Set("recordActivePad", 1);
        Set("recordPositionGrid", 2);
        Set("recordPositionOnly", false);
        Set("recordingClockOverride", (Func<float?>)(() => seconds));
        textEditing = EditorGUIUtility.editingTextField;
        EditorGUIUtility.editingTextField = false;
        seconds = 1;
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Call("StopPreview", false);
            window.DiscardChanges();
            if (shown) window.Close();
            if (window != null) Object.DestroyImmediate(window);
        }
        finally
        {
            Object.DestroyImmediate(clip);
            foreach (var pair in preferences)
            {
                if (pair.Value == null) EditorPrefs.DeleteKey(Prefix + pair.Key);
                else if (pair.Value is string value) EditorPrefs.SetString(Prefix + pair.Key, value);
                else if (pair.Value is float number) EditorPrefs.SetFloat(Prefix + pair.Key, number);
                else EditorPrefs.SetInt(Prefix + pair.Key, (int)pair.Value);
            }
            preferences.Clear();
            EditorGUIUtility.editingTextField = textEditing;
        }
    }

    private void Arm()
    {
        Set("recorder", new SaberChartRecorder(Document, 10, 0, 0, true, 3));
        Set("isPlaying", true);
    }

    [Test]
    public void KeyChordsRepeatsAndLongReleaseUseAudioClockRatherThanDisplayCursor()
    {
        Arm();
        Set("currentBeat", 90f);
        Key(KeyCode.F);
        Key(KeyCode.J);
        seconds = 1.1f;
        Key(KeyCode.F);
        Key(KeyCode.J, EventType.KeyUp);
        seconds = 1.7f;
        Key(KeyCode.F, EventType.KeyUp);
        Key(KeyCode.Space);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.AreEqual(1000, Document.notes[0].time);
        Assert.AreEqual("blue", Document.notes[0].color);
        Assert.AreEqual("red", Document.notes[1].color);
        Assert.AreEqual(700, Document.notes[0].lengthMs, .01);
        Assert.AreEqual("tap", Document.notes[1].type);
        Assert.True(window.hasUnsavedChanges);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(0, Document.notes.Count);
        Assert.False(window.hasUnsavedChanges);
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.AreEqual(700, Document.notes[0].lengthMs, .01);
    }

    [Test]
    public void UndoWhileRecordingFinalizesThenRemovesWholeTake()
    {
        Arm();
        Key(KeyCode.D);
        seconds = 2;
        Key(KeyCode.K);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(0, Document.notes.Count);
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(2, Document.notes.Count);
    }

    [Test]
    public void TextFieldsAndModifiedKeysNeverRecordAndKeyUpStillReleases()
    {
        Arm();
        Key(KeyCode.D, modifiers: EventModifiers.Control);
        Key(KeyCode.J, modifiers: EventModifiers.Alt);
        EditorGUIUtility.editingTextField = true;
        Key(KeyCode.G);
        Assert.IsEmpty(Document.notes);
        EditorGUIUtility.editingTextField = false;
        Key(KeyCode.G);
        EditorGUIUtility.editingTextField = true;
        seconds = 1.5f;
        Key(KeyCode.G, EventType.KeyUp);
        Assert.False(Get<SaberChartRecorder>("recorder").IsHeld(2));
        Assert.AreEqual("long", Document.notes[0].type);
    }

    [Test]
    public void FocusLossStopsAndFinalizesHeldInput()
    {
        Arm();
        Key(KeyCode.F);
        seconds = 1.6f;
        Call("OnLostFocus");
        Assert.False(Get<bool>("isPlaying"));
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Assert.AreEqual(600, Document.notes[0].lengthMs, .01);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.IsEmpty(Document.notes);
    }

    [Test]
    public void SeekFinishesRecordingBeforeMovingAndStopsPlayback()
    {
        Arm();
        Key(KeyCode.J);
        seconds = 1.4f;
        Call("SeekToBeat", 8f);
        Assert.False(Get<bool>("isPlaying"));
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Assert.AreEqual(8, Get<float>("currentBeat"));
        Assert.AreEqual(400, Document.notes[0].lengthMs, .01);
    }

    [Test]
    public void CountInAndEmptyRecordingLeaveChartAndHistoryUnchanged()
    {
        string before = SaberChartUtility.ToJson(Document, false);
        Set("countingIn", true);
        Key(KeyCode.D);
        Key(KeyCode.Escape);
        Assert.False(Get<bool>("countingIn"));
        Arm();
        Key(KeyCode.Space);
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        Assert.False(window.hasUnsavedChanges);
        var history = Get<object>("history");
        Assert.False((bool)history.GetType().GetProperty("CanUndo").GetValue(history));
    }

    [UnityTest]
    public IEnumerator CountInStartsRealAudioAndRecordingUsesItsSamples()
    {
        Document.bpm = 600;
        Document.offsetMs = 100;
        Set("recordingClockOverride", null);
        Set("recordCountIn", true);
        Set("recordSnap", false);
        Set("currentBeat", 0f);
        Set("useGameTiming", true);
        Call("StartRecording");
        Assert.True(Get<bool>("countingIn"));
        Assert.NotNull(Get<AudioClip>("countInClip"));
        Key(KeyCode.F);
        Assert.IsEmpty(Document.notes, "カウント中は記録しない");
        double deadline = EditorApplication.timeSinceStartup + 6;
        while ((Get<SaberChartRecorder>("recorder") == null || Get<float>("lastRecordingSeconds") < .3f) &&
               EditorApplication.timeSinceStartup < deadline) yield return null;
        Assert.NotNull(Get<SaberChartRecorder>("recorder"));
        Assert.IsNull(Get<AudioClip>("countInClip"));
        Key(KeyCode.F);
        Assert.IsEmpty(Document.notes, "カウント中から押し続けたキーのリピートは記録しない");
        Key(KeyCode.F, EventType.KeyUp);
        Key(KeyCode.F);
        Assert.AreEqual(1, Document.notes.Count);
        Assert.AreEqual(Get<float>("lastRecordingSeconds") * 1000 - 100, Document.notes[0].time, .01);
        float pressed = Get<float>("lastRecordingSeconds");
        while (Get<float>("lastRecordingSeconds") < pressed + .35f && EditorApplication.timeSinceStartup < deadline)
            yield return null;
        Key(KeyCode.F, EventType.KeyUp);
        Key(KeyCode.Space);
        Assert.AreEqual("long", Document.notes[0].type);
        Assert.GreaterOrEqual(Document.notes[0].lengthMs, 300);
        Assert.False(Get<bool>("isPlaying"));
    }

    [Test]
    public void EachRecordingKeyHasIndependentPreciseXYAndHistoryPreservesThem()
    {
        var points = new[] { new Vector2(-2.321f, 1.234f), new Vector2(.127f, -.987f),
            new Vector2(-.543f, 1.111f), new Vector2(2.345f, .456f), new Vector2(-1.234f, -1.432f) };
        var keys = new[] { KeyCode.D, KeyCode.F, KeyCode.G, KeyCode.J, KeyCode.K };
        var colors = new[] { "blue", "blue", "gold", "red", "red" };
        for (int i = 0; i < points.Length; i++) Call("SetRecordingPosition", i, points[i], false);
        Arm();
        foreach (var key in keys) Key(key);
        seconds = 1.1f;
        Key(KeyCode.Space);
        Assert.AreEqual(5, Document.notes.Count);
        for (int i = 0; i < points.Length; i++)
            Assert.True(Document.notes.Exists(n => Mathf.Abs(n.x - points[i].x) < .0001f &&
                Mathf.Abs(n.y - points[i].y) < .0001f && n.color == colors[i]));
        string recorded = SaberChartUtility.ToJson(Document, false);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(recorded, SaberChartUtility.ToJson(Document, false));
    }

    [Test]
    public void LivePositionChangesAffectNextNoteButNeverMoveHeldOrExistingNotes()
    {
        Call("SetRecordingPosition", 1, new Vector2(-2, 1.33f), false);
        Arm();
        Key(KeyCode.F);
        Call("SetRecordingPosition", 1, new Vector2(2, -.78f), false);
        seconds = 1.6f;
        Key(KeyCode.F, EventType.KeyUp);
        seconds = 2;
        Key(KeyCode.F);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.AreEqual(-2, Document.notes[0].x);
        Assert.AreEqual(1.33f, Document.notes[0].y);
        Assert.AreEqual(600, Document.notes[0].lengthMs, .01);
        Assert.AreEqual(2, Document.notes[1].x);
        Assert.AreEqual(-.78f, Document.notes[1].y);
    }

    [Test]
    public void GridFreePlacementAndFineArrowMovementAreSeparateFromTimeSnap()
    {
        Set("recordPositionGrid", 4); // XY 32分割
        Call("SetRecordingPosition", 0, new Vector2(.2f, .2f), true);
        Assert.AreEqual(new Vector2(.15625f, .1875f), Get<Vector2[]>("recordPositions")[0]);
        Set("recordPositionGrid", 0);
        Call("SetRecordingPosition", 0, new Vector2(.123f, .234f), true);
        Key(KeyCode.Alpha1);
        Key(KeyCode.RightArrow, modifiers: EventModifiers.Shift);
        Assert.AreEqual(.133f, Get<Vector2[]>("recordPositions")[0].x, .00001);
        Key(KeyCode.UpArrow);
        Assert.AreEqual(.284f, Get<Vector2[]>("recordPositions")[0].y, .00001);
        Set("recordPositionGrid", 3); // XY 16分割
        Key(KeyCode.Alpha5);
        Call("SetRecordingPosition", 4, Vector2.zero, false);
        Arm();
        Key(KeyCode.UpArrow);
        Key(KeyCode.LeftArrow);
        Assert.AreEqual(new Vector2(-.3125f, .1875f), Get<Vector2[]>("recordPositions")[4]);
        Assert.AreEqual(0, Get<float>("currentBeat"), "録音中の矢印はシークしない");
        Assert.True(Get<bool>("isPlaying"));
        Assert.IsEmpty(Document.notes);
    }

    [Test]
    public void MirrorBoundsInvalidNumbersAndTextFieldsPreserveOtherPositions()
    {
        Call("SetRecordingPosition", 1, new Vector2(-1.23f, 1.11f), false);
        Call("MirrorRecordingPosition");
        Assert.AreEqual(new Vector2(1.23f, 1.11f), Get<Vector2[]>("recordPositions")[3]);
        Call("SetRecordingPosition", 2, new Vector2(100, -100), false);
        Assert.AreEqual(new Vector2(2.5f, -1.5f), Get<Vector2[]>("recordPositions")[2]);
        Call("SetRecordingPosition", 2, new Vector2(float.NaN, 0), false);
        Assert.AreEqual(new Vector2(2.5f, -1.5f), Get<Vector2[]>("recordPositions")[2]);
        EditorGUIUtility.editingTextField = true;
        Key(KeyCode.Alpha5);
        Key(KeyCode.UpArrow);
        Assert.AreEqual(1, Get<int>("recordActivePad"));
        Assert.AreEqual(new Vector2(-1.23f, 1.11f), Get<Vector2[]>("recordPositions")[1]);
        Assert.False(window.hasUnsavedChanges, "入力位置の設定だけでは譜面を変更しない");
    }

    [Test]
    public void PlaneCoordinatesIncludeAllEdgesAndPreserveUpwardY()
    {
        var plane = new Rect(100, 200, 500, 300);
        Assert.AreEqual(new Vector2(-2.5f, 1.5f), (Vector2)Call("RecordingPointToPosition", plane, new Vector2(100, 200)));
        Assert.AreEqual(new Vector2(2.5f, -1.5f), (Vector2)Call("RecordingPointToPosition", plane, new Vector2(600, 500)));
        Assert.AreEqual(Vector2.zero, (Vector2)Call("RecordingPointToPosition", plane, plane.center));
        var expected = new Vector2(1.234f, -.987f);
        var point = (Vector2)Call("RecordingPositionToPoint", plane, expected);
        Assert.Less(Vector2.Distance(expected, (Vector2)Call("RecordingPointToPosition", plane, point)), .00001f);
        Call("SetRecordingPosition", 3, expected, false);
        Assert.AreEqual(3, (int)Call("RecordingMarkerAt", plane, point + new Vector2(3, -3)));
        Assert.AreEqual(-1, (int)Call("RecordingMarkerAt", plane, plane.position));
        Call("SetRecordingPosition", 1, expected, false);
        Assert.AreEqual(1, (int)Call("RecordingMarkerAt", plane, point), "重なったときは選択マーカーを優先する");
    }

    [Test]
    public void PreviousHeightIsMigratedOnceToIndependentPositions()
    {
        Set("recordHeight", 2);
        Set("recordPositions", null);
        Call("EnsureRecordingPositions");
        foreach (var p in Get<Vector2[]>("recordPositions")) Assert.AreEqual(.8571429f, p.y);
        Call("SetRecordingPosition", 1, new Vector2(-.25f, -.35f), false);
        Set("recordHeight", 0);
        Call("EnsureRecordingPositions");
        Assert.AreEqual(new Vector2(-.25f, -.35f), Get<Vector2[]>("recordPositions")[1]);
    }

    [Test]
    public void PreciseLayoutSettingsSurviveClosingAndReopening()
    {
        Call("SetRecordingPosition", 4, new Vector2(-1.234f, .567f), false);
        Set("recordActivePad", 4);
        Set("recordPositionGrid", 3);
        Set("recordPositionOnly", true);
        Call("SaveRecordingLayout");
        Set("recordPositions", null);
        Set("recordActivePad", 0);
        Set("recordPositionGrid", 0);
        Set("recordPositionOnly", false);
        Call("LoadRecordingLayout");
        Assert.AreEqual(new Vector2(-1.234f, .567f), Get<Vector2[]>("recordPositions")[4]);
        Assert.AreEqual(4, Get<int>("recordActivePad"));
        Assert.AreEqual(3, Get<int>("recordPositionGrid"));
        Assert.True(Get<bool>("recordPositionOnly"));
    }

    [Test]
    public void InvalidSavedLayoutFallsBackWithoutChangingChart()
    {
        EditorPrefs.SetString(Prefix + "RecordingLayout", "{broken");
        Call("LoadRecordingLayout");
        Call("EnsureRecordingPositions");
        Assert.AreEqual(5, Get<Vector2[]>("recordPositions").Length);
        Assert.IsEmpty(Document.notes);
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void LosingFocusDuringPositionAdjustmentReleasesPointerWithoutStoppingAudition()
    {
        Set("isPlaying", true);
        Set("recordXYButton", 0);
        Set("recordXYPad", 1);
        Set("recordXYAdjusting", true);
        Call("OnLostFocus");
        Assert.AreEqual(-1, Get<int>("recordXYButton"));
        Assert.AreEqual(-1, Get<int>("recordXYPad"));
        Assert.True(Get<bool>("isPlaying"));
        Assert.IsEmpty(Document.notes);
    }

    [UnityTest]
    public IEnumerator RecordingPadsDrawAtMinimumWindowSizeAndMouseReleaseOutsideEndsLong()
    {
        window.position = new Rect(40, 40, 1050, 650);
        window.Show();
        shown = true;
        window.Focus();
        double deadline = EditorApplication.timeSinceStartup + 8;
        while (Get<SaberChartPlaybackPreview>("playbackPreview") == null && EditorApplication.timeSinceStartup < deadline)
        {
            window.Repaint();
            yield return null;
        }
        Assert.NotNull(Get<SaberChartPlaybackPreview>("playbackPreview"), Get<string>("playbackPreviewError"));
        Arm();
        Set("isPlaying", false); // 手動時計のGUI試験では音声側の自動停止を走らせない。
        // SendEventの宛先はタブを含む親View。コンテンツ座標へその余白を加える。
        Vector2 origin = window.rootVisualElement.worldBound.position;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + new Vector2(275, 153) });
        Assert.AreEqual(1, Document.notes.Count);
        Assert.AreEqual("blue", Document.notes[0].color);
        seconds = 1.6f;
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + new Vector2(30, 615) });
        Assert.False(Get<SaberChartRecorder>("recorder").IsHeld(5));
        Assert.AreEqual(600, Document.notes[0].lengthMs, .01);

        // XY入力は停止せずに任意位置へ打ち込める。調整だけのドラッグはノーツを増やさない。
        Set("recordPositionGrid", 0);
        Rect panel = new Rect(252, 95, 498, (650 - 95 - 28 - 7) * .62f);
        Rect plane = (Rect)Call("RecordingPlaneRect", panel);
        Vector2 blue = (Vector2)Call("RecordingPositionToPoint", plane, new Vector2(1.2f, .9f));
        seconds = 2;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + blue });
        seconds = 2.1f;
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + blue });
        Assert.AreEqual(2, Document.notes.Count);
        Assert.AreEqual("blue", Document.notes[1].color);
        Assert.AreEqual(1.2f, Document.notes[1].x, .001);
        Assert.AreEqual(.9f, Document.notes[1].y, .001);
        Vector2 red = (Vector2)Call("RecordingPositionToPoint", plane, new Vector2(-1.4f, -1.1f));
        seconds = 3;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 1, mousePosition = origin + red });
        seconds = 3.5f;
        window.SendEvent(new Event { type = EventType.MouseUp, button = 1, mousePosition = origin + new Vector2(30, 615) });
        Assert.AreEqual("red", Document.notes[2].color);
        Assert.AreEqual(-1.4f, Document.notes[2].x, .001);
        Assert.AreEqual(-1.1f, Document.notes[2].y, .001);
        Assert.AreEqual(500, Document.notes[2].lengthMs, .01);
        seconds = 4;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, modifiers = EventModifiers.Shift, mousePosition = origin + plane.center });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + plane.center });
        Assert.AreEqual("gold", Document.notes[3].color);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, modifiers = EventModifiers.Alt, mousePosition = origin + plane.center });
        window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, modifiers = EventModifiers.Alt, mousePosition = origin + new Vector2(plane.xMax, plane.yMin) });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + new Vector2(plane.xMax, plane.yMin) });
        Assert.AreEqual(4, Document.notes.Count);
        Assert.AreEqual(new Vector2(2.5f, 1.5f), Get<Vector2[]>("recordPositions")[2]);
        Assert.AreEqual(0, Document.notes[3].x, .001);
        Assert.AreEqual(0, Document.notes[3].y, .001);
        Key(KeyCode.Space);
        Set("expandPlaybackPreview", true);
        window.Repaint();
        yield return null;
        Assert.IsNull(Get<string>("playbackPreviewError"));
        LogAssert.NoUnexpectedReceived();
    }

    private void Key(KeyCode key, EventType type = EventType.KeyDown, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = type, keyCode = key, modifiers = modifiers });
    private T Get<T>(string name) => (T)typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
    private void Set(string name, object value) => typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, value);
    private object Call(string name, params object[] args) => typeof(SaberChartEditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args);
}
