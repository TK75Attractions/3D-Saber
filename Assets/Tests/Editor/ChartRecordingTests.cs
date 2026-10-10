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

    [Test]
    public void UndoInputRemovesWholeChordAndNeverRevivesHeldNotes()
    {
        var existing = new SaberChartNote { time = 100, color = "gold" };
        var doc = new SaberChartDocument { notes = new List<SaberChartNote> { existing } };
        var take = new SaberChartRecorder(doc, 10, 0, 0, true, 3);
        var first = take.Press(0, 1, -2, 0, "blue");
        take.Release(0, 1.1f);
        int group = take.BeginInputGroup();
        take.Press(20, 2, -1, 1, "blue", inputGroup: group);
        take.Press(21, 2, 1, 1, "red", inputGroup: group);
        Assert.AreEqual(2, take.UndoLastInput());
        Assert.AreEqual(1, take.AddedCount);
        CollectionAssert.AreEqual(new[] { existing, first }, doc.notes);
        Assert.IsNull(take.Press(20, 2.5f, -1, 1, "blue"), "取り消し後の押し続けリピートを追加しない");
        take.Release(20, 3);
        take.Release(21, 3);
        Assert.AreEqual(2, doc.notes.Count);
        Assert.AreEqual(1, take.UndoLastInput());
        Assert.AreEqual(0, take.AddedCount);
        Assert.False(take.CanUndoInput);
        Assert.AreEqual(0, take.UndoLastInput());
        take.Finish(4);
        Assert.AreSame(existing, doc.notes[0]);
        Assert.AreEqual("tap", existing.type);
    }

    [Test]
    public void UndoPartiallyDuplicateChordOnlyRemovesNewNotes()
    {
        var doc = new SaberChartDocument();
        var take = new SaberChartRecorder(doc, 10, 4, 0, true, 2);
        var existing = take.Press(0, 1, -1, 0, "blue");
        take.Release(0, 1.1f);
        int group = take.BeginInputGroup();
        Assert.IsNull(take.Press(20, 1.01f, -1, 0, "blue", inputGroup: group));
        take.Press(21, 1.01f, 1, 0, "red", inputGroup: group);
        Assert.AreEqual(1, take.UndoLastInput());
        take.Finish(2);
        Assert.AreEqual(1, doc.notes.Count);
        Assert.AreSame(existing, doc.notes[0]);
        Assert.AreEqual("tap", existing.type);
        Assert.AreEqual(1, take.AddedCount);
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
        Set("recordMirrorPositions", false);
        Set("recordAdvancedSettings", false);
        Set("recordHelpExpanded", false);
        Set("recordStepMode", false);
        Set("recordStepAdvance", true);
        Set("recordPositionPresets", null);
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
    public void StepGroupCopyPreservesChordPropertiesAndOneUndoRestoresAll()
    {
        Call("SetRecordingInputMode", 2);
        Set("currentBeat", 2f);
        Set("snapIndex", 0);
        Set("beatZeroMs", 80f);
        Document.beatZeroMs = 80;
        Document.notes.Add(new SaberChartNote { time = 1080, beat = 2, x = -1.23f, y = .57f,
            color = "blue", type = "long", count = 4, lengthMs = 875, direction = "upright" });
        Document.notes.Add(new SaberChartNote { time = 1080, beat = 2, x = 1.67f, y = -.43f,
            color = "red", type = "direction", direction = "downleft" });
        string before = SaberChartUtility.ToJson(Document, false);
        Key(KeyCode.D, modifiers: EventModifiers.Control);
        Assert.AreEqual(4, Document.notes.Count);
        Assert.AreEqual(3, Get<float>("currentBeat"));
        var copy = Document.notes.Find(n => n.time == 1580 && n.color == "blue");
        Assert.NotNull(copy);
        Assert.AreEqual(3, copy.beat);
        Assert.AreEqual(-1.23f, copy.x);
        Assert.AreEqual(.57f, copy.y);
        Assert.AreEqual("long", copy.type);
        Assert.AreEqual("upright", copy.direction);
        Assert.AreEqual(4, copy.count);
        Assert.AreEqual(875, copy.lengthMs);
        Key(KeyCode.D, modifiers: EventModifiers.Control);
        Assert.AreEqual(4, Document.notes.Count, "押し続けで大量に複製しない");
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(4, Document.notes.Count);
        Key(KeyCode.D, EventType.KeyUp);
        Key(KeyCode.D, modifiers: EventModifiers.Control);
        Assert.AreEqual(6, Document.notes.Count);
        Assert.AreEqual(4, Get<float>("currentBeat"));
    }

    [Test]
    public void StepGroupCopySkipsExistingNotesAndRespectsMeterBoundary()
    {
        Call("SetRecordingInputMode", 2);
        Set("currentBeat", 3f);
        Set("snapIndex", 0);
        Document.timeSignatures.Add(new ChartTimeSignature { beat = 0, numerator = 7, denominator = 8 });
        Document.notes.Add(new SaberChartNote { time = 1500, beat = 3, x = -1, color = "blue" });
        Document.notes.Add(new SaberChartNote { time = 1500, beat = 3, x = 1, color = "red" });
        var existing = new SaberChartNote { time = 1750, beat = 3.5f, x = -1, color = "blue", type = "long", count = 5, lengthMs = 987 };
        Document.notes.Add(existing);
        Call("EditRecordingStep", 0);
        Assert.AreEqual(4, Document.notes.Count);
        Assert.AreEqual(3.5f, Get<float>("currentBeat"));
        Assert.AreSame(existing, Document.notes.Find(n => n.time == 1750 && n.color == "blue"));
        Assert.AreEqual(987, existing.lengthMs);
        Assert.NotNull(Document.notes.Find(n => n.time == 1750 && n.color == "red"));
    }

    [TestCase("left", "right")]
    [TestCase("right", "left")]
    [TestCase("upleft", "upright")]
    [TestCase("upright", "upleft")]
    [TestCase("downleft", "downright")]
    [TestCase("downright", "downleft")]
    [TestCase("up", "up")]
    [TestCase("none", "none")]
    public void StepGroupMirrorExchangesHandsAndDirectionsAndKeepsLongTiming(string direction, string expected)
    {
        Call("SetRecordingInputMode", 2);
        Document.notes.Add(new SaberChartNote { x = -.61f, y = .78f, color = "blue", type = "long",
            count = 3, lengthMs = 980, direction = direction });
        Document.notes.Add(new SaberChartNote { x = 1.32f, color = "red" });
        Document.notes.Add(new SaberChartNote { x = .3f, color = "gold" });
        Document.notes.Add(new SaberChartNote { beat = 1, time = 500, x = -.4f, color = "blue" });
        string before = SaberChartUtility.ToJson(Document, false);
        Key(KeyCode.M, modifiers: EventModifiers.Control);
        var mirror = Document.notes.Find(n => n.type == "long");
        Assert.AreEqual(.61f, mirror.x);
        Assert.AreEqual(.78f, mirror.y);
        Assert.AreEqual("red", mirror.color);
        Assert.AreEqual(expected, mirror.direction);
        Assert.AreEqual(980, mirror.lengthMs);
        Assert.AreEqual(3, mirror.count);
        Assert.AreEqual(0, mirror.time);
        Assert.AreEqual(-.3f, Document.notes.Find(n => n.color == "gold").x);
        Assert.AreEqual(-.4f, Document.notes.Find(n => n.time == 500).x);
        Key(KeyCode.M, modifiers: EventModifiers.Control);
        Assert.AreEqual(.61f, mirror.x, "キーリピートで元へ反転しない");
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
    }

    [Test]
    public void StepGroupDeleteAndJumpUseActualNoteTimeAndUndoTheWholeChord()
    {
        Call("SetRecordingInputMode", 2);
        Document.notes.Add(new SaberChartNote { time = 733.5f, beat = 9, x = -1, color = "blue" });
        Document.notes.Add(new SaberChartNote { time = 733.5f, beat = 9, x = 1, color = "red" });
        Document.notes.Add(new SaberChartNote { time = 800, beat = 1.6f, color = "gold" });
        string before = SaberChartUtility.ToJson(Document, false);
        Key(KeyCode.PageDown);
        Assert.AreEqual(1.467f, Get<float>("currentBeat"), .00001);
        Assert.AreEqual(2, ((List<SaberChartNote>)Call("RecordingStepNotes")).Count);
        Key(KeyCode.Delete, modifiers: EventModifiers.Shift);
        Assert.AreEqual(1, Document.notes.Count);
        Assert.AreEqual(800, Document.notes[0].time);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        Key(KeyCode.PageDown, EventType.KeyUp);
        Key(KeyCode.PageDown);
        Assert.AreEqual(1.6f, Get<float>("currentBeat"), .00001);
        Key(KeyCode.PageUp);
        Assert.AreEqual(1.467f, Get<float>("currentBeat"), .00001);
    }

    [Test]
    public void StepGroupActionsProtectTypingPlaybackAndHeldInput()
    {
        Call("SetRecordingInputMode", 2);
        Set("recordStepAdvance", false);
        Key(KeyCode.H);
        string chord = SaberChartUtility.ToJson(Document, false);
        Call("EditRecordingStep", 2);
        Call("JumpRecordingStep", 1);
        Assert.AreEqual(chord, SaberChartUtility.ToJson(Document, false));
        Assert.AreEqual(0, Get<float>("currentBeat"));
        Key(KeyCode.H, EventType.KeyUp);
        EditorGUIUtility.editingTextField = true;
        Key(KeyCode.Delete, modifiers: EventModifiers.Shift);
        Assert.AreEqual(chord, SaberChartUtility.ToJson(Document, false));
        EditorGUIUtility.editingTextField = false;
        Set("isPlaying", true);
        Key(KeyCode.D, modifiers: EventModifiers.Control);
        Key(KeyCode.M, modifiers: EventModifiers.Control);
        Key(KeyCode.Delete, modifiers: EventModifiers.Shift);
        Assert.AreEqual(chord, SaberChartUtility.ToJson(Document, false));
        Set("isPlaying", false);
        Call("OnLostFocus");
        Key(KeyCode.Delete, modifiers: EventModifiers.Shift);
        Assert.IsEmpty(Document.notes);
    }

    [Test]
    public void StepChordWorksWithoutAudioAndUsesGridTimeRatherThanRecordingOffsets()
    {
        Call("SetRecordingInputMode", 2);
        Set("audioClip", null);
        Set("currentBeat", 2.13f);
        Set("snapIndex", 1);
        Set("beatZeroMs", 123f);
        Document.beatZeroMs = 123;
        Document.offsetMs = 456;
        Set("recordInputOffsetMs", 789f);
        Set("recordingClockOverride", (Func<float?>)(() => throw new InvalidOperationException("ステップ入力で音声時計を読まない")));
        Key(KeyCode.H);
        Key(KeyCode.H);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.True(Document.notes.TrueForAll(n => n.time == 1123 && n.beat == 2 && n.type == "tap"));
        Assert.AreEqual(2.13f, Get<float>("currentBeat"));
        Assert.True((bool)Call("RecordingPadHeld", 1));
        Key(KeyCode.H, EventType.KeyUp);
        Assert.AreEqual(2.5f, Get<float>("currentBeat"));
        Assert.False(Get<bool>("isPlaying"));
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(2, Document.notes.Count, "同時打ちは1回のUndo/Redoで扱う");
    }

    [Test]
    public void StepPhysicalChordWaitsForAllReleasesAndDuplicateDoesNotAdvance()
    {
        Call("SetRecordingInputMode", 2);
        Set("snapIndex", 0);
        Set("currentBeat", 4f);
        Key(KeyCode.F);
        Key(KeyCode.J);
        seconds = 8;
        Key(KeyCode.F, EventType.KeyUp);
        Assert.AreEqual(4, Get<float>("currentBeat"));
        Key(KeyCode.J, EventType.KeyUp);
        Assert.AreEqual(5, Get<float>("currentBeat"));
        Assert.AreEqual(2, Document.notes.Count);
        Assert.True(Document.notes.TrueForAll(n => n.time == 2000 && n.type == "tap"));
        Call("SeekToBeat", 4f);
        Key(KeyCode.H);
        Key(KeyCode.H, EventType.KeyUp);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.AreEqual(4, Get<float>("currentBeat"), "重複だけの入力は時刻を進めない");
    }

    [TestCase("direction")]
    [TestCase("long")]
    public void StepUsesPaletteTypeAndCanStayAtSameBeat(string type)
    {
        Call("SetRecordingInputMode", 2);
        Set("recordStepAdvance", false);
        Set("paletteType", type);
        Set("paletteDirection", "right");
        Set("paletteCount", 4);
        Key(KeyCode.B);
        Key(KeyCode.B, EventType.KeyUp);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.True(Document.notes.TrueForAll(n => n.type == type && n.count == (type == "long" ? 4 : 1) &&
            n.direction == (type == "direction" ? "right" : "none") && n.lengthMs == 0));
        Assert.AreEqual(0, Get<float>("currentBeat"));
        Key(KeyCode.G);
        Key(KeyCode.G, EventType.KeyUp);
        Assert.AreEqual(3, Document.notes.Count);
        Assert.True(Document.notes.TrueForAll(n => n.time == 0));
    }

    [Test]
    public void StepAdvanceAndRestNavigationRespectFractionalBarsAndMeterChanges()
    {
        Call("SetRecordingInputMode", 2);
        Set("snapIndex", 0);
        Document.timeSignatures = new List<ChartTimeSignature>
        {
            new ChartTimeSignature { beat = 0, numerator = 7, denominator = 8 },
            new ChartTimeSignature { beat = 5, numerator = 3, denominator = 4 },
        };
        Set("currentBeat", 3f);
        Key(KeyCode.G);
        Key(KeyCode.G, EventType.KeyUp);
        Assert.AreEqual(3.5f, Get<float>("currentBeat"));
        Key(KeyCode.Tab);
        Assert.AreEqual(4.5f, Get<float>("currentBeat"));
        Key(KeyCode.Tab);
        Assert.AreEqual(5, Get<float>("currentBeat"));
        Key(KeyCode.Tab, modifiers: EventModifiers.Shift);
        Assert.AreEqual(4.5f, Get<float>("currentBeat"));
        Key(KeyCode.Tab, modifiers: EventModifiers.Shift);
        Assert.AreEqual(3.5f, Get<float>("currentBeat"));
        Key(KeyCode.Tab, modifiers: EventModifiers.Shift);
        Assert.AreEqual(3, Get<float>("currentBeat"));
        Set("currentBeat", 0f);
        Key(KeyCode.Tab, modifiers: EventModifiers.Shift);
        Assert.AreEqual(0, Get<float>("currentBeat"));
        Assert.AreEqual(1, Document.notes.Count, "休符移動ではノーツを追加しない");
    }

    [Test]
    public void StepProtectsTextAndPlaybackAndDoesNotAdvanceAfterFocusLossOrSeek()
    {
        Call("SetRecordingInputMode", 2);
        EditorGUIUtility.editingTextField = true;
        Key(KeyCode.H);
        Key(KeyCode.Tab);
        EditorGUIUtility.editingTextField = false;
        Key(KeyCode.J, modifiers: EventModifiers.Control);
        Set("isPlaying", true);
        Key(KeyCode.B);
        Assert.IsEmpty(Document.notes);
        Set("isPlaying", false);
        Key(KeyCode.H);
        Call("OnLostFocus");
        Key(KeyCode.H, EventType.KeyUp);
        Assert.AreEqual(0, Get<float>("currentBeat"));
        Key(KeyCode.B);
        Call("SeekToBeat", 8f);
        Key(KeyCode.B, EventType.KeyUp);
        Assert.AreEqual(8, Get<float>("currentBeat"));
        Assert.AreEqual(4, Document.notes.Count);
        Call("SetRecordingInputMode", 1);
        Key(KeyCode.D);
        Assert.AreEqual(4, Document.notes.Count, "録音モードの停止中はステップ配置しない");
    }

    [Test]
    public void StepUndoDuringHoldDoesNotRecreateNotesFromKeyRepeat()
    {
        Call("SetRecordingInputMode", 2);
        Key(KeyCode.H);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Key(KeyCode.H);
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.H, EventType.KeyUp);
        Assert.AreEqual(0, Get<float>("currentBeat"));
        Key(KeyCode.H);
        Assert.AreEqual(2, Document.notes.Count);
    }

    [Test]
    public void StepSpacePlaysAudioWithoutRecordingAndCancelsPendingAdvance()
    {
        Call("SetRecordingInputMode", 2);
        Set("currentBeat", 4f);
        Key(KeyCode.D);
        Key(KeyCode.Space);
        Assert.True(Get<bool>("isPlaying"));
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Key(KeyCode.H);
        Assert.AreEqual(1, Document.notes.Count);
        Key(KeyCode.Space);
        Assert.False(Get<bool>("isPlaying"));
        float stoppedAt = Get<float>("currentBeat");
        Key(KeyCode.D, EventType.KeyUp);
        Assert.AreEqual(stoppedAt, Get<float>("currentBeat"));
        Assert.AreEqual(1, Document.notes.Count);
    }

    [Test]
    public void ThreePositionPresetsAreIndependentAndSurviveReload()
    {
        var expected = new[] { new Vector2(-1.234f, .678f), new Vector2(-.25f, 1.25f), new Vector2(2, -1.4f) };
        for (int i = 0; i < 3; i++)
        {
            Call("SetRecordingPosition", 1, expected[i], false);
            Set("recordPositionGrid", i);
            Key((KeyCode)((int)KeyCode.F1 + i), modifiers: EventModifiers.Shift);
            Call("SetRecordingPosition", 1, Vector2.zero, false);
            Key((KeyCode)((int)KeyCode.F1 + i), modifiers: EventModifiers.Shift);
            Key((KeyCode)((int)KeyCode.F1 + i), EventType.KeyUp);
        }
        Set("recordPositions", null);
        Set("recordPositionPresets", null);
        Call("LoadRecordingLayout");
        for (int i = 0; i < 3; i++)
        {
            Key((KeyCode)((int)KeyCode.F1 + i));
            Key((KeyCode)((int)KeyCode.F1 + i), EventType.KeyUp);
            Assert.AreEqual(expected[i], Get<Vector2[]>("recordPositions")[1]);
            Assert.AreEqual(i, Get<int>("recordPositionGrid"));
        }
        Assert.IsEmpty(Document.notes);
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void PresetChangeDuringHoldOnlyAffectsFollowingNotes()
    {
        Call("SetRecordingMirror", true);
        Call("SetRecordingPosition", 1, new Vector2(-1.2f, .8f), false);
        Call("SaveRecordingPreset", 0);
        Call("SetRecordingPosition", 1, new Vector2(-.4f, -.7f), false);
        Call("SaveRecordingPreset", 1);
        Call("RecallRecordingPreset", 0);
        Arm();
        Key(KeyCode.H);
        Key(KeyCode.F2);
        Assert.True(Get<bool>("recordMirrorPositions"));
        seconds = 1.6f;
        Key(KeyCode.H, EventType.KeyUp);
        seconds = 2;
        Key(KeyCode.H);
        Assert.AreEqual(4, Document.notes.Count);
        Assert.AreEqual(-1.2f, Document.notes[0].x);
        Assert.AreEqual(.8f, Document.notes[0].y);
        Assert.AreEqual(600, Document.notes[0].lengthMs, .01);
        Assert.AreEqual(-.4f, Document.notes[2].x);
        Assert.AreEqual(-.7f, Document.notes[2].y);
    }

    [Test]
    public void MissingOrMalformedPresetDoesNotChangeCurrentLayout()
    {
        var xy = new Vector2(-1.234f, .567f);
        Call("SetRecordingPosition", 1, xy, false);
        EditorPrefs.SetString(Prefix + "RecordingLayout",
            "{\"positions\":[{}, {\"x\":-1.234,\"y\":0.567}, {}, {}, {}],\"presets\":[{\"positions\":[{}]},null,null]}");
        Set("recordPositions", null);
        Set("recordPositionPresets", null);
        Call("LoadRecordingLayout");
        Call("RecallRecordingPreset", 0);
        Call("RecallRecordingPreset", 1);
        Assert.AreEqual(xy, Get<Vector2[]>("recordPositions")[1]);
        Assert.IsEmpty(Document.notes);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RetakeFromLiveOrStoppedRewindsOnlyThisTakeAndCanBeUndone(bool stopFirst)
    {
        Document.notes.Add(new SaberChartNote { time = 100, x = -2, color = "gold" });
        string baseline = SaberChartUtility.ToJson(Document, false);
        Set("recordCountIn", false);
        Set("currentBeat", 2f);
        Call("StartRecording");
        Key(KeyCode.H);
        seconds = 1.4f;
        Key(KeyCode.H, EventType.KeyUp);
        string take = SaberChartUtility.ToJson(Document, false);
        if (stopFirst) Key(KeyCode.Space);
        Key(KeyCode.R, modifiers: EventModifiers.Shift);
        Assert.True(Get<bool>("isPlaying"));
        Assert.NotNull(Get<SaberChartRecorder>("recorder"));
        Assert.AreEqual(1, Get<float>("playbackAudioStartSeconds"), .001);
        Assert.AreEqual(baseline, SaberChartUtility.ToJson(Document, false));
        var retry = Get<SaberChartRecorder>("recorder");
        Key(KeyCode.R, modifiers: EventModifiers.Shift);
        Assert.AreSame(retry, Get<SaberChartRecorder>("recorder"), "長押しリピートで再録音を繰り返さない");
        Key(KeyCode.R, EventType.KeyUp);
        Key(KeyCode.Space);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(take, SaberChartUtility.ToJson(Document, false));
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(baseline, SaberChartUtility.ToJson(Document, false));
        Key(KeyCode.R, modifiers: EventModifiers.Shift);
        Assert.AreEqual(1, Get<float>("playbackAudioStartSeconds"), .001, "Undo/Redo後の再試行も元の音声位置を保つ");
    }

    [Test]
    public void RetakeRefusesToRemoveEditsMadeAfterRecording()
    {
        Set("recordCountIn", false);
        Call("StartRecording");
        Key(KeyCode.H);
        Key(KeyCode.H, EventType.KeyUp);
        Key(KeyCode.Space);
        Document.notes[0].y = 1.234f;
        string edited = SaberChartUtility.ToJson(Document, false);
        Key(KeyCode.R, modifiers: EventModifiers.Shift);
        Assert.AreEqual(edited, SaberChartUtility.ToJson(Document, false));
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Assert.False(Get<bool>("isPlaying"));
        Assert.That(Get<string>("statusMessage"), Does.Contain("録音後に譜面を編集"));
    }

    [Test]
    public void CancelledRetakeCountInCanRestoreOriginalTake()
    {
        Set("recordCountIn", false);
        Call("StartRecording");
        Key(KeyCode.B);
        Key(KeyCode.B, EventType.KeyUp);
        Key(KeyCode.Space);
        string take = SaberChartUtility.ToJson(Document, false);
        Set("recordCountIn", true);
        Key(KeyCode.R, modifiers: EventModifiers.Shift);
        Assert.True(Get<bool>("countingIn"));
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.Escape);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(take, SaberChartUtility.ToJson(Document, false));
        Assert.IsNull(Get<AudioClip>("countInClip"));
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

    [TestCase(KeyCode.H, 1, 3, 0)]
    [TestCase(KeyCode.B, 0, 4, 0)]
    [TestCase(KeyCode.H, 1, 3, 4)]
    [TestCase(KeyCode.B, 0, 4, 4)]
    public void OneKeyChordReadsClockOncePerEdgeAndPreservesBothPositions(KeyCode key, int bluePad, int redPad, int snap)
    {
        Document.offsetMs = 100;
        Document.beatZeroMs = 50;
        var blueXY = new Vector2(-1.234f, .567f);
        var redXY = new Vector2(2.123f, -.891f);
        Call("SetRecordingPosition", bluePad, blueXY, false);
        Call("SetRecordingPosition", redPad, redXY, false);
        Set("paletteType", SaberChartUtility.TypeDirection);
        Set("paletteDirection", "up");
        Set("recorder", new SaberChartRecorder(Document, 10, snap, 30, true, 3));
        Set("isPlaying", true);
        int reads = 0;
        Set("recordingClockOverride", (Func<float?>)(() =>
        {
            reads++;
            float value = seconds;
            seconds += .004f; // 二度読むと量子化の境界をまたぐ音声時計。
            return value;
        }));
        seconds = 1.429f;
        Key(key);
        Assert.AreEqual(1, reads, "左右を別々の時刻で記録しない");
        Assert.AreEqual(2, Document.notes.Count);
        var blue = Document.notes.Find(n => n.color == "blue");
        var red = Document.notes.Find(n => n.color == "red");
        Assert.AreEqual(snap == 0 ? 1299 : 1050, blue.time, .01);
        Assert.AreEqual(blue.time, red.time);
        Assert.AreEqual(blue.beat, red.beat);
        Assert.AreEqual("direction", blue.type);
        Assert.AreEqual("direction", red.type);
        Assert.True((bool)Call("RecordingPadHeld", bluePad));
        Assert.True((bool)Call("RecordingPadHeld", redPad));
        Call("SetRecordingPosition", bluePad, Vector2.zero, false);
        Call("SetRecordingPosition", redPad, Vector2.zero, false);
        seconds = 1.929f;
        Key(key, EventType.KeyUp);
        Assert.AreEqual(2, reads, "終端も一度の音声時計でそろえる");
        Assert.AreEqual(500, blue.lengthMs, .01);
        Assert.AreEqual(blue.lengthMs, red.lengthMs);
        Assert.AreEqual("long", blue.type);
        Assert.AreEqual("long", red.type);
        Assert.AreEqual(3, blue.count);
        Assert.AreEqual(3, red.count);
        Assert.AreEqual("up", blue.direction);
        Assert.AreEqual("up", red.direction);
        Assert.AreEqual(blueXY, new Vector2(blue.x, blue.y));
        Assert.AreEqual(redXY, new Vector2(red.x, red.y));
        Assert.False((bool)Call("RecordingPadHeld", bluePad));
        Assert.False((bool)Call("RecordingPadHeld", redPad));
    }

    [Test]
    public void ChordRepeatAndIndividualKeyReleaseDoNotSplitOrRepeatPair()
    {
        Arm();
        Key(KeyCode.H);
        seconds = 1.2f;
        Key(KeyCode.H);
        Key(KeyCode.F);
        seconds = 1.6f;
        Key(KeyCode.F, EventType.KeyUp);
        Assert.True((bool)Call("RecordingPadHeld", 1), "単打を離しても同時打ちは保持する");
        Assert.True((bool)Call("RecordingPadHeld", 3));
        Key(KeyCode.H);
        seconds = 2;
        Key(KeyCode.H, EventType.KeyUp);
        Assert.AreEqual(3, Document.notes.Count);
        Assert.AreEqual(1000, Document.notes[0].lengthMs, .01);
        Assert.AreEqual(1000, Document.notes[1].lengthMs, .01);
        Assert.AreEqual(400, Document.notes[2].lengthMs, .01);
        seconds = 2.2f;
        Key(KeyCode.H);
        seconds = 2.3f;
        Key(KeyCode.H, EventType.KeyUp);
        Assert.AreEqual(5, Document.notes.Count);
        Assert.AreEqual("tap", Document.notes[3].type);
        Assert.AreEqual("tap", Document.notes[4].type);
    }

    [Test]
    public void ChordIgnoresTextFieldsAndModifiersButAlwaysReleasesBothNotes()
    {
        Arm();
        Key(KeyCode.H, modifiers: EventModifiers.Control);
        Key(KeyCode.B, modifiers: EventModifiers.Command);
        Key(KeyCode.H, modifiers: EventModifiers.Alt);
        EditorGUIUtility.editingTextField = true;
        Key(KeyCode.H);
        Key(KeyCode.B);
        Assert.IsEmpty(Document.notes);
        EditorGUIUtility.editingTextField = false;
        Key(KeyCode.H);
        Key(KeyCode.B);
        EditorGUIUtility.editingTextField = true;
        seconds = 1.5f;
        Key(KeyCode.H, EventType.KeyUp, EventModifiers.Control);
        Key(KeyCode.B, EventType.KeyUp, EventModifiers.Alt);
        Assert.AreEqual(4, Document.notes.Count);
        Assert.True(Document.notes.TrueForAll(n => n.type == "long" && n.lengthMs == 500));
        foreach (int pad in new[] { 0, 1, 3, 4 }) Assert.False((bool)Call("RecordingPadHeld", pad));
    }

    [Test]
    public void ChordsHeldThroughCountInRequireReleaseBeforeFirstNote()
    {
        Set("countingIn", true);
        Key(KeyCode.H);
        Key(KeyCode.B);
        Assert.IsEmpty(Document.notes);
        Set("countingIn", false);
        Arm();
        Key(KeyCode.H);
        Key(KeyCode.B);
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.H, EventType.KeyUp);
        Key(KeyCode.B, EventType.KeyUp);
        Key(KeyCode.H);
        Key(KeyCode.B);
        Assert.AreEqual(4, Document.notes.Count);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StopOrFocusLossFinalizesChordsAndUndoRestoresWholeTake(bool loseFocus)
    {
        Arm();
        Key(KeyCode.H);
        seconds = 1.2f;
        Key(KeyCode.B);
        seconds = 1.9f;
        if (loseFocus) Call("OnLostFocus");
        else Key(KeyCode.Escape);
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Assert.False(Get<bool>("isPlaying"));
        Assert.AreEqual(4, Document.notes.Count);
        Assert.AreEqual(900, Document.notes[0].lengthMs, .01);
        Assert.AreEqual(Document.notes[0].lengthMs, Document.notes[1].lengthMs);
        Assert.AreEqual(700, Document.notes[2].lengthMs, .01);
        Assert.AreEqual(Document.notes[2].lengthMs, Document.notes[3].lengthMs);
        string recorded = SaberChartUtility.ToJson(Document, false);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(recorded, SaberChartUtility.ToJson(Document, false));
        Arm();
        seconds = 2.2f;
        Key(KeyCode.H);
        Key(KeyCode.B);
        Assert.AreEqual(8, Document.notes.Count, "停止後の新しい録音に押し続け状態を残さない");
    }

    [Test]
    public void MissingAudioClockStopsChordWithoutCreatingHalfAPair()
    {
        Arm();
        Set("recordingClockOverride", (Func<float?>)(() => null));
        Key(KeyCode.H);
        Assert.IsEmpty(Document.notes);
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void BackspaceRemovesOneInputWithoutStoppingAndRepeatCannotEraseMore()
    {
        Arm();
        Key(KeyCode.K);
        seconds = 1.1f;
        Key(KeyCode.K, EventType.KeyUp);
        seconds = 2;
        Key(KeyCode.H);
        seconds = 2.1f;
        Key(KeyCode.Backspace);
        Assert.AreEqual(1, Document.notes.Count);
        Assert.True(Get<bool>("isPlaying"));
        Assert.AreEqual(1, Get<SaberChartRecorder>("recorder").AddedCount);
        Key(KeyCode.Backspace);
        Key(KeyCode.H);
        Assert.AreEqual(1, Document.notes.Count);
        seconds = 2.6f;
        Key(KeyCode.H, EventType.KeyUp);
        Key(KeyCode.Backspace, EventType.KeyUp);
        Key(KeyCode.Space);
        Assert.AreEqual(1, Document.notes.Count);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Assert.AreEqual(1, Document.notes.Count, "取り消した同時打ちは録音全体のRedoでも復活しない");
    }

    [Test]
    public void UndoInputProtectsTextFieldsAndEmptyTakeDoesNotCreateHistory()
    {
        Arm();
        Key(KeyCode.H);
        EditorGUIUtility.editingTextField = true;
        Key(KeyCode.Backspace);
        EditorGUIUtility.editingTextField = false;
        Key(KeyCode.Backspace, modifiers: EventModifiers.Control);
        Assert.AreEqual(2, Document.notes.Count);
        Key(KeyCode.Backspace);
        Assert.IsEmpty(Document.notes);
        Assert.False(window.hasUnsavedChanges);
        Key(KeyCode.Space);
        var history = Get<object>("history");
        Assert.False((bool)history.GetType().GetProperty("CanUndo").GetValue(history));
        Arm();
        seconds = 3;
        Key(KeyCode.B);
        Key(KeyCode.Backspace);
        Assert.IsEmpty(Document.notes, "前の録音のBackspace保持状態を持ち越さない");
    }

    [Test]
    public void LinkedPositionsFollowHeightAndFineMovementWithoutMovingRecordedNotes()
    {
        Call("SetRecordingPosition", 1, new Vector2(-1.234f, .567f), false);
        Call("SetRecordingMirror", true);
        Assert.AreEqual(new Vector2(1.234f, .567f), Get<Vector2[]>("recordPositions")[3]);
        Arm();
        Key(KeyCode.H);
        Call("SetRecordingHeight", .8571429f);
        Key(KeyCode.RightArrow, modifiers: EventModifiers.Shift);
        var positions = Get<Vector2[]>("recordPositions");
        Assert.AreEqual(-1.224f, positions[1].x, .00001);
        Assert.AreEqual(1.224f, positions[3].x, .00001);
        Assert.AreEqual(.8571429f, positions[1].y);
        Assert.AreEqual(positions[1].y, positions[3].y);
        Assert.True(Document.notes.TrueForAll(n => n.y == .567f));
        Call("SetRecordingMirror", false);
        Call("SetRecordingHeight", 0f);
        Assert.AreEqual(0, positions[1].y);
        Assert.AreEqual(.8571429f, positions[3].y);
        Key(KeyCode.Alpha3);
        Call("SetRecordingMirror", true);
        Call("SetRecordingHeight", -.8571429f);
        Assert.AreEqual(-.8571429f, positions[2].y);
        Assert.AreEqual(0, positions[1].y, "金の調整では青赤を移動しない");
    }

    [Test]
    public void RecordingModeSwitchFinishesTextEditingAndCannotInterruptTake()
    {
        EditorGUIUtility.editingTextField = true;
        Set("rightScroll", new Vector2(0, 600));
        Call("SetRecordingMode", false);
        Assert.False(Get<bool>("recordMode"));
        Assert.False(EditorGUIUtility.editingTextField);
        Assert.AreEqual(Vector2.zero, Get<Vector2>("rightScroll"));
        Call("SetRecordingMode", true);
        Arm();
        Key(KeyCode.F);
        Call("SetRecordingMode", false);
        Assert.True(Get<bool>("recordMode"));
        Assert.True(Get<bool>("isPlaying"));
        Assert.AreEqual(1, Document.notes.Count);
    }

    [UnityTest]
    public IEnumerator ReviewStartsAtRecordedPositionWithoutRecordingOrChangingChart()
    {
        Set("recordCountIn", false);
        Set("recordingClockOverride", null);
        Set("currentBeat", 4f);
        Call("StartRecording");
        Assert.NotNull(Get<SaberChartRecorder>("recorder"));
        Assert.AreEqual(2f, Get<float>("playbackAudioStartSeconds"), .001);
        double deadline = EditorApplication.timeSinceStartup + 6;
        while (Get<float>("lastRecordingSeconds") < 2.1f && EditorApplication.timeSinceStartup < deadline)
            yield return null;
        Key(KeyCode.H);
        Assert.AreEqual(2, Document.notes.Count);
        Key(KeyCode.Space);
        string recorded = SaberChartUtility.ToJson(Document, false);
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Key(KeyCode.Y, modifiers: EventModifiers.Control);
        Set("currentBeat", 8f);
        Key(KeyCode.Return);
        Assert.True(Get<bool>("isPlaying"));
        Assert.IsNull(Get<SaberChartRecorder>("recorder"));
        Assert.AreEqual(2f, Get<float>("playbackAudioStartSeconds"), .001);
        Assert.AreEqual(recorded, SaberChartUtility.ToJson(Document, false));
        Set("currentBeat", 6f);
        Key(KeyCode.Return);
        Assert.AreEqual(6, Get<float>("currentBeat"), "Enter長押しのリピートで再生位置を繰り返し戻さない");
        Key(KeyCode.Return, EventType.KeyUp);
        Key(KeyCode.Return);
        Assert.AreEqual(4, Get<float>("currentBeat"));
        Key(KeyCode.Return, EventType.KeyUp);
        Key(KeyCode.Space);
        Document.bpm = 150;
        Set("beatZeroMs", 100f);
        Key(KeyCode.Return);
        Assert.AreEqual(2, Get<float>("playbackAudioStartSeconds"), .001, "BPMや原点を調整しても聴き直しは元の音声位置から");
        Key(KeyCode.Return, EventType.KeyUp);
        Key(KeyCode.Space);
        Set("document", new SaberChartDocument());
        Key(KeyCode.Return);
        Assert.False(Get<bool>("isPlaying"), "別の譜面では古い録音位置を再生しない");
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
        Call("SetRecordingMirror", true);
        Call("SaveRecordingLayout");
        Set("recordPositions", null);
        Set("recordActivePad", 0);
        Set("recordPositionGrid", 0);
        Set("recordPositionOnly", false);
        Set("recordMirrorPositions", false);
        Call("LoadRecordingLayout");
        Assert.AreEqual(new Vector2(-1.234f, .567f), Get<Vector2[]>("recordPositions")[4]);
        Assert.AreEqual(4, Get<int>("recordActivePad"));
        Assert.AreEqual(3, Get<int>("recordPositionGrid"));
        Assert.True(Get<bool>("recordPositionOnly"));
        Assert.True(Get<bool>("recordMirrorPositions"));
        Assert.AreEqual(new Vector2(1.234f, .567f), Get<Vector2[]>("recordPositions")[0]);
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
    public IEnumerator TimelineClippingPreservesMousePlacementAndDragging()
    {
        Call("SetRecordingInputMode", 0);
        Set("showPlaybackPreview", false);
        Set("currentBeat", 4f);
        Set("pixelsPerBeat", 80f);
        Set("snapIndex", 0);
        window.position = new Rect(40, 40, 1050, 650);
        window.Show(); shown = true; window.Focus();
        yield return null;
        window.Repaint();
        yield return null;
        Vector2 origin = window.rootVisualElement.worldBound.position;
        var timeline = new Rect(252, 95, 498, 520);
        float y = (float)Call("YForBeat", 4f, timeline);
        Vector2 start = origin + new Vector2(314 + 53 * 2.5f, y);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = start });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = start });
        Assert.AreEqual(1, Document.notes.Count);
        Assert.AreEqual(4, Document.notes[0].beat);
        Assert.AreEqual(SaberChartUtility.CoordinateForLane(2, 8, -2.5f, 2.5f), Document.notes[0].x);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = start });
        Vector2 end = start + new Vector2(53, -80);
        window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = end });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = end });
        Assert.AreEqual(1, Document.notes.Count);
        Assert.AreEqual(5, Document.notes[0].beat);
        Assert.AreEqual(SaberChartUtility.CoordinateForLane(3, 8, -2.5f, 2.5f), Document.notes[0].x);
        LogAssert.NoUnexpectedReceived();
    }

    [UnityTest]
    public IEnumerator RecordingPadsAndChordsWorkAtMinimumSizeAndReleaseOutsideEndsLong()
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
        Rect panel = new Rect(252, 95, 498, (650 - 95 - 28 - 7) * .68f);
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

        // 同時打ちボタンとキーを併用し、枠外で離してもその入力の左右だけを確定する。
        Rect innerChord = (Rect)Call("RecordingChordRect", panel, 0);
        Rect outerChord = (Rect)Call("RecordingChordRect", panel, 1);
        Assert.Less(innerChord.yMax, plane.yMin - 16, "同時打ちボタンがXY見出しへ重ならない");
        Assert.Less(innerChord.xMax, outerChord.xMin);
        Assert.Greater(plane.height, 150);
        Assert.LessOrEqual(plane.yMax, panel.yMax - 22);
        seconds = 5;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + innerChord.center });
        Assert.AreEqual(6, Document.notes.Count);
        Assert.AreEqual(Document.notes[4].time, Document.notes[5].time);
        seconds = 5.2f;
        Key(KeyCode.B);
        seconds = 5.8f;
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + new Vector2(30, 615) });
        Assert.AreEqual(-1, Get<int>("recordMouseChord"));
        Assert.AreEqual(800, Document.notes[4].lengthMs, .01);
        Assert.AreEqual(Document.notes[4].lengthMs, Document.notes[5].lengthMs);
        Assert.True((bool)Call("RecordingPadHeld", 0));
        Assert.True((bool)Call("RecordingPadHeld", 4));
        seconds = 5.95f;
        Key(KeyCode.B, EventType.KeyUp);
        Assert.AreEqual(750, Document.notes[6].lengthMs, .01);
        Assert.AreEqual(Document.notes[6].lengthMs, Document.notes[7].lengthMs);
        seconds = 6;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + outerChord.center });
        seconds = 6.1f;
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + outerChord.center });
        Assert.AreEqual(10, Document.notes.Count);
        Assert.AreEqual("tap", Document.notes[8].type);
        Assert.AreEqual("tap", Document.notes[9].type);

        Rect undo = (Rect)Call("RecordingUndoRect", panel);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + undo.center });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + undo.center });
        Assert.AreEqual(8, Document.notes.Count, "取り消しボタンも同時打ちをまとめて戻す");
        Assert.NotNull(Get<SaberChartRecorder>("recorder"));

        // 録音中も修飾キーなしでマーカーをつかめる。つかんだ瞬間に位置が飛ばない。
        Call("SelectRecordingPad", 1);
        Call("SetRecordingPosition", 1, new Vector2(-1, .4f), false);
        Call("SetRecordingMirror", true);
        Vector2 marker = (Vector2)Call("RecordingPositionToPoint", plane, new Vector2(-1, .4f));
        Vector2 grab = new Vector2(3, -2);
        Vector2 target = (Vector2)Call("RecordingPositionToPoint", plane, new Vector2(-1.5f, .8f));
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + marker + grab });
        Assert.AreEqual(new Vector2(-1, .4f), Get<Vector2[]>("recordPositions")[1]);
        window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = origin + target + grab });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + target + grab });
        Assert.AreEqual(8, Document.notes.Count);
        Assert.AreEqual(-1.5f, Get<Vector2[]>("recordPositions")[1].x, .001);
        Assert.AreEqual(1.5f, Get<Vector2[]>("recordPositions")[3].x, .001);
        Assert.AreEqual(.8f, Get<Vector2[]>("recordPositions")[1].y, .001);
        Assert.AreEqual(.8f, Get<Vector2[]>("recordPositions")[3].y, .001);
        Vector2 adjustMode = origin + new Vector2(panel.x + 128, panel.y + 145);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = adjustMode });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = adjustMode });
        Assert.True(Get<bool>("recordPositionOnly"));
        Vector2 blank = origin + plane.position + new Vector2(12, 12);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = blank });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = blank });
        Assert.AreEqual(8, Document.notes.Count, "位置調整モードは空白でも入力せず配置だけを変える");
        Vector2 inputMode = origin + new Vector2(panel.x + 48, panel.y + 145);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = inputMode });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = inputMode });
        Assert.False(Get<bool>("recordPositionOnly"));
        Vector2 mirrorToggle = origin + new Vector2(panel.x + 225, panel.y + 145);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = mirrorToggle });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = mirrorToggle });
        Assert.False(Get<bool>("recordMirrorPositions"));
        Key(KeyCode.Space);
        Call("SetRecordingInputMode", 2);
        Set("audioClip", null);
        Set("currentBeat", 16f);
        Set("snapIndex", 3);
        window.Repaint();
        yield return null;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + innerChord.center });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + innerChord.center });
        Assert.AreEqual(10, Document.notes.Count);
        Assert.AreEqual(8000, Document.notes[8].time);
        Assert.AreEqual(8000, Document.notes[9].time);
        Assert.AreEqual(16.25f, Get<float>("currentBeat"));
        Key(KeyCode.Z, modifiers: EventModifiers.Control);
        Assert.AreEqual(8, Document.notes.Count);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = origin + plane.center });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = origin + plane.center });
        Assert.AreEqual(9, Document.notes.Count);
        Assert.AreEqual(8125, Document.notes[8].time);
        Assert.AreEqual(16.5f, Get<float>("currentBeat"));
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
