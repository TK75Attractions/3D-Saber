using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// キーの決まり(矢印・Backspace・Ctrl+D)、方向キー、範囲の操作、目印など、操作の決まりを確かめる。
public class ChartEditorOperationTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] PrefKeys = { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout" };
    readonly Dictionary<string, object> savedPrefs = new Dictionary<string, object>();
    readonly Rect timeline = new Rect(0, 0, 400, 500);
    readonly Rect lanes = new Rect(62, 0, 326, 500);
    SaberChartEditorWindow window;
    bool savedTextEditing;
    float seconds;

    [SetUp]
    public void SetUp()
    {
        foreach (string key in PrefKeys)
        {
            string full = Prefix + key;
            savedPrefs[key] = !EditorPrefs.HasKey(full) ? null
                : key == "SongId" || key == "RecordingLayout" ? (object)EditorPrefs.GetString(full)
                : key == "Zoom" ? (object)EditorPrefs.GetFloat(full) : EditorPrefs.GetInt(full);
        }
        EditorPrefs.SetString(Prefix + "SongId", "__ChartEditorOperationTest");
        EditorPrefs.SetInt(Prefix + "Difficulty", 1);
        EditorPrefs.SetInt(Prefix + "Snap", 3);
        EditorPrefs.SetFloat(Prefix + "Zoom", 82f);
        savedTextEditing = EditorGUIUtility.editingTextField;
        EditorGUIUtility.editingTextField = false;
        window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
        Set("document", new SaberChartDocument
        {
            notes = new List<SaberChartNote>
            {
                new SaberChartNote { beat = 4, time = 2000, x = -.3571429f, y = 0, color = "blue" },
                new SaberChartNote { beat = 4, time = 2000, x = 1.0714285f, y = 0, color = "red" },
                new SaberChartNote { beat = 8, time = 4000, x = 0, y = .4285714f, color = "gold",
                    type = "long", count = 3 },
            }
        });
        Set("beatZeroMs", 0f);
        Set("currentBeat", 4f);
        Set("pixelsPerBeat", 82f);
        Set("snapIndex", 3);
        Set("recordMode", false);
        Set("recordStepMode", false);
        Set("recordPositions", null);
        Set("recordActivePad", 1);
        Set("recordPositionGrid", 2);
        Set("recordMirrorPositions", false);
        Set("directionAdvance", true);
        Set("recordingClockOverride", (Func<float?>)(() => seconds));
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
        seconds = 1f;
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
        foreach (var pair in savedPrefs)
        {
            string full = Prefix + pair.Key;
            if (pair.Value == null) EditorPrefs.DeleteKey(full);
            else if (pair.Value is string text) EditorPrefs.SetString(full, text);
            else if (pair.Value is float number) EditorPrefs.SetFloat(full, number);
            else EditorPrefs.SetInt(full, (int)pair.Value);
        }
        savedPrefs.Clear();
        EditorGUIUtility.editingTextField = savedTextEditing;
    }

    // ---- 使1: 矢印は常にシーク、位置は Alt+矢印 ----

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void PlainArrowsSeekInEveryMode(int mode)
    {
        Call("SetRecordingInputMode", mode);
        Key(KeyCode.RightArrow);
        Assert.AreEqual(4.25f, Get<float>("currentBeat"), .0001f);
        Key(KeyCode.UpArrow);
        Assert.AreEqual(4.5f, Get<float>("currentBeat"), .0001f, "縦のタイムラインで ↑ は先へ");
        Key(KeyCode.DownArrow);
        Key(KeyCode.LeftArrow);
        Assert.AreEqual(4f, Get<float>("currentBeat"), .0001f);
        Assert.AreEqual(3, Document.notes.Count, "シークで譜面は変えない");
    }

    [Test]
    public void AltArrowsMoveTheKeyPositionInRecordingModesWithoutSeeking()
    {
        Call("SetRecordingInputMode", 2);
        Call("SetRecordingPosition", 1, new Vector2(-1f, 0f), false);
        Key(KeyCode.RightArrow, EventModifiers.Alt | EventModifiers.Shift);
        Assert.AreEqual(-.99f, Get<Vector2[]>("recordPositions")[1].x, .0001f);
        Assert.AreEqual(4f, Get<float>("currentBeat"));
    }

    [Test]
    public void AltArrowsNudgeTheSelectedNoteInEditMode()
    {
        Set("selectedIndex", 0);
        Key(KeyCode.RightArrow, EventModifiers.Alt);
        Assert.AreEqual(-.3571429f + 5f / 7f, Document.notes.Find(n => n.color == "blue").x, .0001f);
        Key(KeyCode.UpArrow, EventModifiers.Alt | EventModifiers.Shift);
        Assert.AreEqual(.01f, Document.notes.Find(n => n.color == "blue").y, .0001f);
        Assert.AreEqual(4f, Get<float>("currentBeat"), "位置の移動ではシークしない");
        Key(KeyCode.Z, EventModifiers.Control);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.False(window.hasUnsavedChanges);
    }

    // ---- 使1: Backspace は「直前の1打を取り消す」(ステップ)/「選択を消す」(編集) ----

    [Test]
    public void BackspaceInStepModeUndoesTheLastHitAndReturnsToItsBeat()
    {
        Call("SetRecordingInputMode", 2);
        Set("snapIndex", 0);
        Set("currentBeat", 2f);
        Key(KeyCode.D);
        Key(KeyCode.D, EventType.KeyUp);
        Assert.AreEqual(4, Document.notes.Count);
        Assert.AreEqual(3f, Get<float>("currentBeat"));
        Key(KeyCode.Backspace);
        Assert.AreEqual(3, Document.notes.Count);
        Assert.AreEqual(2f, Get<float>("currentBeat"), "取り消した拍へ戻る");
        Key(KeyCode.Backspace);
        Assert.AreEqual(3, Document.notes.Count, "押し続けでは1回だけ");
        Key(KeyCode.Backspace, EventType.KeyUp);
        Key(KeyCode.J);
        Key(KeyCode.J, EventType.KeyUp);
        Assert.AreEqual(1000f, Document.notes.Single(n => n.color == "red" && n.time < 1500).time);
    }

    [Test]
    public void BackspaceInEditModeDeletesTheSelection()
    {
        Set("selectedIndex", 2);
        Key(KeyCode.Backspace);
        Assert.AreEqual(2, Document.notes.Count);
        Assert.False(Document.notes.Exists(n => n.color == "gold"));
    }

    // ---- 使1: Ctrl+D は選択があれば選択、無ければ今の拍 ----

    [Test]
    public void CtrlDWithoutSelectionCopiesTheCurrentBeatAndRepeatIsIgnored()
    {
        Set("selectedIndex", -1);
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(5, Document.notes.Count);
        Assert.AreEqual(2, Document.notes.Count(n => Mathf.Abs(n.time - 2125f) < .01f));
        Assert.AreEqual(4.25f, Get<float>("currentBeat"), .0001f);
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(5, Document.notes.Count, "押し続けでは繰り返さない");
        Key(KeyCode.D, EventType.KeyUp);
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(7, Document.notes.Count, "離して押し直すと続けてコピー");
    }

    [Test]
    public void PageKeysJumpBetweenNotesInEditMode()
    {
        Set("currentBeat", 0f);
        Key(KeyCode.PageDown);
        Assert.AreEqual(4f, Get<float>("currentBeat"), .0001f);
        Key(KeyCode.PageDown, EventType.KeyUp);
        Key(KeyCode.PageDown);
        Assert.AreEqual(8f, Get<float>("currentBeat"), .0001f);
        Key(KeyCode.PageDown, EventType.KeyUp);
        Key(KeyCode.PageUp);
        Assert.AreEqual(4f, Get<float>("currentBeat"), .0001f);
    }

    // ---- 速5: 方向キー ----

    [Test]
    public void DirectionKeysSetTheSelectedNoteAndMoveToTheNext()
    {
        Set("selectedIndex", 0);
        Key(KeyCode.E);
        var blue = Document.notes.Find(n => n.color == "blue");
        Assert.AreEqual("upright", blue.direction);
        Assert.AreEqual("direction", blue.type);
        Assert.AreEqual(1, Get<int>("selectedIndex"), "付けたら次のノーツへ");
        Key(KeyCode.E);
        Assert.AreEqual("none", Document.notes[1].direction, "押し続けのリピートでは次へ進まない");
        Key(KeyCode.E, EventType.KeyUp);
        Key(KeyCode.Keypad2);
        Assert.AreEqual("down", Document.notes[1].direction);
        Key(KeyCode.Keypad2, EventType.KeyUp);
        Key(KeyCode.S);
        var gold = Document.notes.Find(n => n.color == "gold");
        Assert.AreEqual("none", gold.direction);
        Assert.AreEqual("long", gold.type, "LONG は LONG のまま");
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual("none", Document.notes[1].direction);
        Assert.AreEqual("upright", Document.notes[0].direction);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.False(window.hasUnsavedChanges, "1回の方向付けを1回の Undo で戻す");
    }

    [Test]
    public void DirectionKeyWithoutSelectionSetsThePaletteForTheNextNote()
    {
        Set("selectedIndex", -1);
        Key(KeyCode.Z);
        Assert.AreEqual("downleft", Get<string>("paletteDirection"));
        Assert.AreEqual("direction", Get<string>("paletteType"));
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void StepModeUsesTheNumpadForTheCurrentBeatAndKeepsLettersForInput()
    {
        Call("SetRecordingInputMode", 2);
        Set("directionAdvance", false);
        Key(KeyCode.Keypad7);
        Assert.True(Document.notes.Where(n => n.time == 2000).All(n => n.direction == "upleft"));
        Key(KeyCode.Keypad7, EventType.KeyUp);
        Key(KeyCode.Q);
        Assert.True(Document.notes.Where(n => n.time == 2000).All(n => n.direction == "upleft"), "ステップでは文字の方向キーを使わない");
        Key(KeyCode.D);
        Assert.AreEqual(4, Document.notes.Count, "D は配置のキーのまま");
    }

    [Test]
    public void NumpadWhileRecordingChangesTheDirectionOfTheNextHit()
    {
        Set("recordMode", true);
        Set("recorder", new SaberChartRecorder(Document, 10, 0, 0, true, 3));
        Set("isPlaying", true);
        Key(KeyCode.Keypad6);
        Key(KeyCode.F);
        var hit = Document.notes.Find(n => Mathf.Abs(n.time - 1000f) < .01f);
        Assert.NotNull(hit);
        Assert.AreEqual("right", hit.direction);
        Assert.AreEqual("direction", hit.type);
    }

    // ---- 速6: 聴きながら方向を付ける ----

    [Test]
    public void DirectionWhilePlayingTargetsTheNearestNoteLeftToRightAndUndoesAsOnePass()
    {
        Set("isPlaying", true);
        seconds = 2.05f;
        Key(KeyCode.Keypad4);
        Key(KeyCode.Keypad4, EventType.KeyUp);
        Key(KeyCode.Keypad6);
        Key(KeyCode.Keypad6, EventType.KeyUp);
        Assert.AreEqual("left", Document.notes.Find(n => n.color == "blue").direction, "同時打ちは左から");
        Assert.AreEqual("right", Document.notes.Find(n => n.color == "red").direction);
        Assert.AreEqual("none", Document.notes.Find(n => n.color == "gold").direction);
        seconds = 3.0f;
        Key(KeyCode.Keypad8);
        Assert.AreEqual("none", Document.notes.Find(n => n.color == "gold").direction, "200ms より遠いノーツには付けない");
        Call("StopPreview", false);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.True(Document.notes.All(n => n.direction == "none"), "止めるまでの方向付けを1回の Undo で戻す");
        Assert.False(window.hasUnsavedChanges);
    }

    // ---- 使4・使5・使6 ----

    [Test]
    public void ClickingWhilePlayingSeeksInsteadOfPlacing()
    {
        Set("editTool", Enum.Parse(Field("editTool").FieldType, "Draw"));
        float y = (float)Call("YForBeat", 6f, timeline);
        Assert.False((bool)Call("HandleMouseDownWhilePlaying", 0, new Vector2(200, y), timeline), "止まっているときは通常の配置");
        Set("isPlaying", true);
        Assert.True((bool)Call("HandleMouseDownWhilePlaying", 0, new Vector2(200, y), timeline));
        Assert.AreEqual(3, Document.notes.Count, "再生中のクリックでノーツを増やさない");
        Assert.AreEqual(6f, Get<float>("currentBeat"), .0001f);
        Set("isPlaying", true);
        Assert.True((bool)Call("HandleMouseDownWhilePlaying", 1, new Vector2(200, y), timeline), "右クリックも再生中は消さない");
        Assert.AreEqual(3, Document.notes.Count);
    }

    [Test]
    public void NotesAreDrawnAtTheirActualHorizontalPosition()
    {
        float laneWidth = lanes.width / 8;
        Rect g = (Rect)Call("NoteRect", new SaberChartNote { x = 0f }, lanes);
        Rect j = (Rect)Call("NoteRect", new SaberChartNote { x = .7142857f }, lanes);
        Assert.AreEqual(lanes.x + 4 * laneWidth, g.center.x, .01f);
        Assert.AreEqual(laneWidth, j.center.x - g.center.x, .01f, "録音キーの G と J を別の位置に描く");
    }

    [Test]
    public void ClickingTheSameSpotCyclesThroughOverlappingNotes()
    {
        Document.notes.Add(new SaberChartNote { beat = 4, time = 2000, x = -.3571429f, y = .4285714f, color = "gold" });
        Vector2 spot = ((Rect)Call("NoteRect", Document.notes[0], lanes)).center;
        int first = (int)Call("FindNoteForClick", spot, lanes);
        Set("selectedIndex", first);
        int second = (int)Call("FindNoteForClick", spot, lanes);
        Assert.AreNotEqual(first, second, "同じ場所をもう一度クリックすると下のノーツを選ぶ");
        Set("selectedIndex", second);
        Assert.AreEqual(first, (int)Call("FindNoteForClick", spot, lanes), "さらにクリックすると一巡する");
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(5)]
    public void OneWheelNotchMovesOneBeatWhateverTheSnap(int snap)
    {
        Set("snapIndex", snap);
        Assert.AreEqual(1f, (float)Call("WheelSeekBeats", 3f, false), .0001f);
        Assert.AreEqual(SaberChartUtility.SnapStep(new[] { 4, 8, 12, 16, 24, 32 }[snap]), (float)Call("WheelSeekBeats", 3f, true), .0001f);
    }

    // ---- 速8: 小節・時刻へ移動、目印 ----

    [TestCase("1:23.5", 83.5f)]
    [TestCase("83.5", 83.5f)]
    [TestCase("0:02", 2f)]
    public void ClockTextIsParsed(string text, float expected)
    {
        Assert.True(SaberChartEditorWindow.TryParseClock(text, out float value));
        Assert.AreEqual(expected, value, .0001f);
    }

    [TestCase("1:75")]
    [TestCase("abc")]
    [TestCase("-3")]
    public void InvalidClockTextIsRejected(string text)
    {
        Assert.False(SaberChartEditorWindow.TryParseClock(text, out _));
    }

    [Test]
    public void JumpToMeasureFollowsMeterChangesAndJumpToTimeUsesAudioTime()
    {
        Document.timeSignatures = new List<ChartTimeSignature> { new ChartTimeSignature { beat = 0, numerator = 7, denominator = 8 } };
        Document.offsetMs = 500;
        Set("jumpMeasureText", "3");
        Call("JumpToMeasureText");
        Assert.AreEqual(7f, Get<float>("currentBeat"), .0001f, "7/8 の3小節目は7拍目から");
        Set("jumpTimeText", "0:02.5");
        Call("JumpToTimeText");
        Assert.AreEqual(4f, Get<float>("currentBeat"), .0001f, "音源の2.5秒は OFFSET を引いた譜面の2000ms");
    }

    [Test]
    public void MarkersAreSavedOutsideTheGameFieldsAndInheritedByNewCharts()
    {
        Set("newMarkerName", "サビ1");
        Call("AddMarkerAtCursor");
        Assert.AreEqual(1, Document.markers.Count);
        Assert.AreEqual(2000f, Document.markers[0].timeMs, .01f);
        string file = SaberChartUtility.ToFileJson(Document);
        StringAssert.Contains("\"editorMarkers\": [{\"timeMs\": 2000, \"name\": \"サビ1\"}]", file);
        Assert.AreEqual(3, ChartLoader.Parse(file).notes.Count, "本編はそのまま読める");
        var again = SaberChartUtility.FromJson(file);
        Assert.AreEqual("サビ1", again.markers.Single().name);
        Assert.IsEmpty(again.extraFields, "目印を未知の項目として二重に持たない");
        var fresh = new SaberChartDocument();
        SaberChartUtility.CopySongSettings(again, fresh);
        Assert.AreEqual("サビ1", fresh.markers.Single().name);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.IsEmpty(Document.markers);
    }

    // ---- 速1: 範囲の操作 ----

    [Test]
    public void RangeIncludesTheStartAndExcludesTheEnd()
    {
        var notes = SaberChartRangeOps.NotesIn(Document, 2000f, 4000f);
        Assert.AreEqual(2, notes.Count);
        Assert.IsEmpty(SaberChartRangeOps.NotesIn(Document, 4000f, 4000f));
    }

    [Test]
    public void MirrorSwapsSidesHandsAndArrowsWhileColorSwapOnlyChangesHands()
    {
        var note = new SaberChartNote { x = -1.2f, color = "blue", direction = "upleft", type = "direction" };
        SaberChartRangeOps.Mirror(new[] { note });
        Assert.AreEqual(1.2f, note.x);
        Assert.AreEqual("red", note.color);
        Assert.AreEqual("upright", note.direction);
        SaberChartRangeOps.SwapColors(new[] { note });
        Assert.AreEqual("blue", note.color);
        Assert.AreEqual(1.2f, note.x);
        Assert.AreEqual("upright", note.direction);
    }

    [Test]
    public void BracketKeysSelectARangeAndShiftMovesItWithTheNotes()
    {
        Set("currentBeat", 3f);
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, character = '[' });
        Set("currentBeat", 6f);
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, character = ']' });
        Assert.AreEqual(1500f, Get<float>("rangeStartMs"), .01f);
        Assert.AreEqual(3000f, Get<float>("rangeEndMs"), .01f);
        Call("ShiftSelectionBySnap", 1);
        Assert.AreEqual(2, Document.notes.Count(n => Mathf.Abs(n.time - 2125f) < .01f));
        Assert.AreEqual(4.25f, Document.notes[0].beat, .0001f, "動かしたノーツの拍の値も合わせる");
        Assert.AreEqual(4000f, Document.notes[2].time, "範囲の外は動かさない");
        Assert.AreEqual(1625f, Get<float>("rangeStartMs"), .01f, "範囲もノーツと一緒に動く");
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.False(window.hasUnsavedChanges, "1回の Undo で戻す");
        Key(KeyCode.Escape);
        Assert.False((bool)GetProperty("HasRangeSelection"));
    }

    [Test]
    public void FromCursorToEndShiftsEverythingAfterThePosition()
    {
        Set("currentBeat", 5f);
        Call("SelectFromCursorToEnd");
        Set("rangeShiftMsText", "30");
        Call("ShiftSelectionByMsText", 1f);
        Assert.AreEqual(4030f, Document.notes.Find(n => n.color == "gold").time, .01f);
        Assert.AreEqual(2, Document.notes.Count(n => n.time == 2000f), "今の位置より前は動かさない");
    }

    [Test]
    public void DuplicateAfterRepeatsThePhraseAndKeepsExistingNotes()
    {
        Call("SetRange", 2000f, 2500f);
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(5, Document.notes.Count);
        Assert.AreEqual(2, Document.notes.Count(n => Mathf.Abs(n.time - 2500f) < .01f));
        Assert.AreEqual(2500f, Get<float>("rangeStartMs"), .01f, "範囲は複製先へ移る");
        Key(KeyCode.D, EventType.KeyUp);
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(7, Document.notes.Count);
        Call("SetRange", 2000f, 2500f);
        Call("CopySelection");
        Set("currentBeat", 5f);
        Call("PasteAtCursor");
        Assert.AreEqual(7, Document.notes.Count, "貼り付け先に同じノーツがあれば残して足さない");
    }

    [Test]
    public void RangeColorTypeDirectionAndDeleteApplyToEveryNoteInside()
    {
        Call("SetRange", 0f, 5000f);
        Call("SetSelectionColor", "gold");
        Assert.True(Document.notes.All(n => n.color == "gold"));
        Call("SetSelectionType", "tap");
        Assert.True(Document.notes.All(n => n.type == "tap" && n.count == 1));
        Key(KeyCode.X);
        Assert.True(Document.notes.All(n => n.direction == "down" && n.type == "direction"));
        Key(KeyCode.X, EventType.KeyUp);
        Key(KeyCode.Delete);
        Assert.IsEmpty(Document.notes);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(3, Document.notes.Count);
    }

    // ---- helpers ----

    SaberChartDocument Document => Get<SaberChartDocument>("document");

    void Key(KeyCode key, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
    void Key(KeyCode key, EventType type) =>
        Call("HandleKeyboardShortcuts", new Event { type = type, keyCode = key });
    void Set(string name, object value) => Field(name).SetValue(window, value);
    T Get<T>(string name) => (T)Field(name).GetValue(window);
    object GetProperty(string name) =>
        typeof(SaberChartEditorWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
    static FieldInfo Field(string name) =>
        typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new MissingFieldException(name);
    object Call(string name, params object[] args)
    {
        var methods = typeof(SaberChartEditorWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToArray();
        if (methods.Length == 0) throw new MissingMethodException(name);
        try { return methods[0].Invoke(window, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }
}
