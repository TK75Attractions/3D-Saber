using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// 譜面エディターが「触っていない値を変えない」「開いた場所に保存する」ことを確かめる。
// 譜面の中身ではなく操作の決まりを確かめるので、架空の曲フォルダだけを使う。
public class ChartEditorSafetyTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] PrefKeys = { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout", "ShowPlaybackPreview" };
    static readonly Type Drafts = typeof(SaberChartEditorWindow).Assembly.GetType("Saber.ChartEditor.SaberChartDrafts");
    readonly Dictionary<string, object> savedPrefs = new Dictionary<string, object>();
    readonly Rect timeline = new Rect(0, 0, 400, 500);
    readonly Rect lanes = new Rect(62, 0, 326, 500);
    SaberChartEditorWindow window;
    string draftRoot;
    bool savedTextEditing;

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
        EditorPrefs.SetString(Prefix + "SongId", "__ChartEditorSafetyTest");
        EditorPrefs.SetInt(Prefix + "Difficulty", 1);
        EditorPrefs.SetInt(Prefix + "Snap", 3);
        EditorPrefs.SetFloat(Prefix + "Zoom", 82f);
        draftRoot = Path.Combine(Path.GetTempPath(), "__ChartDraftTest_" + Guid.NewGuid().ToString("N"));
        Drafts.GetField("rootOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, draftRoot);
        savedTextEditing = EditorGUIUtility.editingTextField;
        EditorGUIUtility.editingTextField = false;
        window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
        Set("document", new SaberChartDocument
        {
            notes = new List<SaberChartNote>
            {
                new SaberChartNote { beat = 4.00740f, time = 2003.7f, x = .123f, y = .2142857f },
                new SaberChartNote { beat = 8, time = 4000, x = 1.0714285f, y = .2142857f },
            }
        });
        Set("beatZeroMs", 0f);
        Set("currentBeat", 4f);
        Set("pixelsPerBeat", 82f);
        Set("snapIndex", 3);
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
    }

    [TearDown]
    public void TearDown()
    {
        if (window != null)
        {
            window.DiscardChanges();
            Object.DestroyImmediate(window);
        }
        Drafts.GetField("rootOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        if (Directory.Exists(draftRoot)) Directory.Delete(draftRoot, true);
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

    // ---- 守1: クリックと手ぶれでは動かさない。動かすときは端数を保った相対移動 ----

    [Test]
    public void ClickAndSmallJitterDoNotRoundOrMoveTheNote()
    {
        string before = SaberChartUtility.ToJson(Document, false);
        var start = new Vector2(200, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(2, -3), false, timeline, lanes);
        Call("DragNoteTo", start + new Vector2(3, -2), false, timeline, lanes);
        Call("EndNoteDrag");
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false), "選んだだけで格子・8列へ丸めない");
        Assert.False(window.hasUnsavedChanges);
        Assert.False(History<bool>("CanUndo"), "動かしていないドラッグは履歴に残さない");
    }

    [Test]
    public void DragMovesInWholeSnapStepsAndLanesWhileKeepingRecordedOffsets()
    {
        Set("placementSnap", true);
        var start = new Vector2(200, 360);
        float laneWidth = lanes.width / 8;
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(laneWidth, -82 * .25f), false, timeline, lanes);
        Call("EndNoteDrag");
        var moved = Document.notes.Single(n => n.x > .5f && n.x < 1f);
        Assert.AreEqual(2003.7f + 125f, moved.time, .01f, "16分1つ分だけ動かし、元の端数を保つ");
        Assert.AreEqual(.123f + 5f / 7f, moved.x, .0001f, "列の間隔1つ分だけ動かし、元の端数を保つ");
        Assert.True(window.hasUnsavedChanges);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(2003.7f, Document.notes[0].time, .001f);
        Assert.AreEqual(.123f, Document.notes[0].x, .0001f);
    }

    [Test]
    public void ShiftDragSnapsToTheGridAndLaneCenter()
    {
        var start = new Vector2(200, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(lanes.width / 8, -82 * .25f), true, timeline, lanes);
        Call("EndNoteDrag");
        var moved = Document.notes.Single(n => Mathf.Abs(n.time - 2125f) < .01f);
        Assert.AreEqual(4.25f, moved.beat, .0001f);
        Assert.AreEqual(SaberChartUtility.CoordinateForLane(4, 8, -2.5f, 2.5f), moved.x, .0001f);
    }

    [Test]
    public void DefaultPlacementIgnoresTheSavedSnapAndCanRecreateAnOffGridNote()
    {
        Assert.False(Get<bool>("placementSnap"));
        var original = Document.notes[0].Clone();
        Document.notes.Clear();
        float x = (float)typeof(SaberChartEditorWindow).GetMethod("NoteCenterX",
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { original.x, lanes });
        float y = (float)Call("YForBeat", original.beat, timeline);
        Call("AddNoteAtMouse", new Vector2(x, y), timeline, lanes);
        Assert.AreEqual(original.time, Document.notes[0].time, .001f);
        Assert.AreEqual(original.x, Document.notes[0].x, .0001f);
        Call("AddNoteAtMouse", new Vector2(x, y), timeline, lanes);
        Assert.AreEqual(1, Document.notes.Count, "同じ自由位置に重複配置しない");
    }

    [Test]
    public void FreeDragMovesLessThanOneSnapAndUndoesInOneOperation()
    {
        string before = SaberChartUtility.ToJson(Document, false);
        var note = Document.notes[0];
        var start = new Vector2(200, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(7, -5), false, timeline, lanes);
        Call("DragNoteTo", start + new Vector2(9, -7), false, timeline, lanes);
        Call("EndNoteDrag");
        Assert.AreEqual(2003.7f + 500f * 7f / 82f, note.time, .001f);
        Assert.AreEqual(.123f + 9f / (lanes.width / 8f) * (5f / 7f), note.x, .0001f);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        Assert.False(History<bool>("CanUndo"));
    }

    [Test]
    public void FreeDragBackToTheStartRestoresExactTimePositionAndStoredBeat()
    {
        Document.notes[0].beat = 99.123f; // 時刻から再計算できない既存の拍も保つ。
        string before = SaberChartUtility.ToJson(Document, false);
        Set("savedJson", before);
        var start = new Vector2(200, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(9, -7), false, timeline, lanes);
        Call("DragNoteTo", start, false, timeline, lanes);
        Call("EndNoteDrag");
        Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void FreeHorizontalDragPreservesTheOriginalTimingAndBeat()
    {
        var note = Document.notes[0];
        float beat = note.beat;
        var start = new Vector2(200, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(7, 0), false, timeline, lanes);
        Call("EndNoteDrag");
        Assert.AreEqual(2003.7f, note.time);
        Assert.AreEqual(beat, note.beat);
        Assert.AreNotEqual(.123f, note.x);
    }

    [Test]
    public void FreeDragAcrossTempoChangeFollowsThePointerWithoutRounding()
    {
        Document.tempoMap = new List<SaberChartTempoPoint>
        {
            new SaberChartTempoPoint { beat = 0, timeMs = 0 },
            new SaberChartTempoPoint { beat = 4, timeMs = 2000 },
            new SaberChartTempoPoint { beat = 8, timeMs = 3000 },
        };
        var note = Document.notes[0];
        note.time = 1990f;
        var start = new Vector2(200, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(0, -8.2f), false, timeline, lanes);
        Call("EndNoteDrag");
        Assert.AreEqual(2020f, note.time, .001f);
        Assert.AreEqual(4.08f, note.beat, .0001f);
        Assert.AreEqual(.123f, note.x);
    }

    [Test]
    public void FreeDragClampsToChartStartAndCanStillReturnToTheOriginalPosition()
    {
        var note = Document.notes[0];
        var start = new Vector2(200, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(-1000, 1000), false, timeline, lanes);
        Assert.AreEqual(0f, note.time);
        Assert.AreEqual(-2.5f, note.x);
        Call("DragNoteTo", start, false, timeline, lanes);
        Call("EndNoteDrag");
        Assert.AreEqual(2003.7f, note.time);
        Assert.AreEqual(.123f, note.x);
    }

    [Test]
    public void PlacementSnapIsOptionalAndShiftCanTemporarilyEnableIt()
    {
        Assert.AreEqual(4.137f, (float)Call("PlacementBeat", 4.137f, false));
        Assert.AreEqual(4.25f, (float)Call("PlacementBeat", 4.137f, true));
        Set("placementSnap", true);
        Assert.AreEqual(4.25f, (float)Call("PlacementBeat", 4.137f, false));
        Document.notes.Clear();
        Call("AddNoteAtMouse", new Vector2(200, 360 - .137f * 82), timeline, lanes);
        Assert.AreEqual(2125f, Document.notes[0].time, .001f);
        Assert.AreEqual(SaberChartUtility.CoordinateForLane(3, 8, -2.5f, 2.5f), Document.notes[0].x);
    }

    // ---- 守2・守8: 再コンパイルのあとも未保存の印と Undo を失わない ----

    [Test]
    public void ReEnablingTheWindowRestoresUnsavedMarkAndHistory()
    {
        Set("selectedIndex", 0);
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(3, Document.notes.Count);
        // Unity は m_HasUnsavedChanges を保存しないため、再コンパイル後は false になる。
        SetUnsavedFlag(false);
        Call("OnDisable");
        ClearHistory(); // 再コンパイルでは履歴のオブジェクトも作り直される
        Call("OnEnable");
        Assert.True(window.hasUnsavedChanges, "開き直したら内容から未保存を判定し直す");
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(2, Document.notes.Count, "再コンパイルをまたいで Undo できる");
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void HistoryFromAnotherChartIsNotRestored()
    {
        Set("selectedIndex", 0);
        Key(KeyCode.D, EventModifiers.Control);
        Call("OnDisable");
        ClearHistory();
        Set("document", new SaberChartDocument());
        Call("OnEnable");
        Assert.False(History<bool>("CanUndo"), "別の譜面の履歴を混ぜない");
    }

    // ---- 守3: 保存は開いた場所へ ----

    [Test]
    public void SavingFromTheLoadConfirmationWritesTheOpenedDifficulty()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), OneNoteChart);
            Load(target, 1);
            Set("selectedIndex", 0);
            Key(KeyCode.D, EventModifiers.Control);
            Set("difficultyIndex", 2);
            Set("confirmChangesDialog", new Func<string, string, string, string, string, int>((a, b, c, d, e) => 0));
            Call("LoadDocument");
            Assert.False(File.Exists(Path.Combine(folder, "chart_hard.json")), "切り替え先の難易度に書かない");
            Assert.AreEqual(2, ChartLoader.LoadFromFile(Path.Combine(folder, "chart_normal.json")).notes.Count);
            Assert.AreEqual("hard", Get<string>("loadedDifficulty"));
        });
    }

    [Test]
    public void CtrlSAfterChangingTheSelectionStillSavesTheOpenedFile()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), OneNoteChart);
            Load(target, 1);
            Set("selectedIndex", 0);
            Key(KeyCode.D, EventModifiers.Control);
            Set("difficultyIndex", 0);
            Key(KeyCode.S, EventModifiers.Control);
            Assert.False(File.Exists(Path.Combine(folder, "chart_easy.json")));
            Assert.AreEqual(2, ChartLoader.LoadFromFile(Path.Combine(folder, "chart_normal.json")).notes.Count);
            Assert.False(window.hasUnsavedChanges);
            Assert.True((bool)Call("SelectionDiffersFromEditing"), "選択と編集中が違うことを画面で示す");
        });
    }

    [Test]
    public void DuplicateToSelectionCreatesTheOtherDifficultyOnlyWhenSaved()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), OneNoteChart);
            string normalText = File.ReadAllText(Path.Combine(folder, "chart_normal.json"));
            Load(target, 1);
            Set("difficultyIndex", 2);
            Call("DuplicateToSelection");
            Assert.AreEqual("hard", Get<string>("loadedDifficulty"));
            Assert.True(window.hasUnsavedChanges);
            Assert.False(File.Exists(Path.Combine(folder, "chart_hard.json")), "保存するまで書かない");
            Assert.AreEqual(150f, Document.bpm, "曲の設定を引き継ぐ");
            Assert.True((bool)Call("SaveDocument"));
            Assert.True(File.Exists(Path.Combine(folder, "chart_hard.json")));
            Assert.AreEqual(normalText, File.ReadAllText(Path.Combine(folder, "chart_normal.json")));
            Assert.False(File.Exists(Path.Combine(folder, "chart.json")), "Hard から chart.json を作らない");
        });
    }

    // ---- 守4・守5: 保存で触っていない値を変えない。変更がなければ書かない ----

    [Test]
    public void OpeningAndSavingWithoutChangesDoesNotRewriteTheFile()
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(path, ElDoradoLikeChart);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            Load(target, 1);
            Assert.AreEqual(599f, Get<float>("beatZeroMs"), .01f, "表示には推定した原点を使う");
            Assert.True((bool)Call("SaveDocument"));
            Assert.True((bool)Call("SaveForTestIfNeeded"), "本編テストも変更がなければ保存しない");
            Assert.AreEqual(ElDoradoLikeChart, File.ReadAllText(path));
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(path));
            Assert.False(File.Exists(Path.Combine(folder, "chart.json")));
        });
    }

    [Test]
    public void SavingAnEditKeepsTheCommentTheFileOriginAndUntouchedBeats()
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(path, ElDoradoLikeChart);
            Load(target, 1);
            Set("selectedIndex", 0);
            Key(KeyCode.D, EventModifiers.Control);
            Assert.True((bool)Call("SaveForTestIfNeeded"));

            string saved = File.ReadAllText(path);
            var root = (SaberJsonObjectProxy)saved;
            Assert.AreEqual("_comment", root.FirstKey, "制作メモを先頭に残す");
            StringAssert.Contains("\"_comment\": \"手で置いた譜面のメモ\"", saved);
            var chart = ChartLoader.Parse(saved);
            Assert.AreEqual(0f, chart.beatZeroMs, "推定した原点をファイルに書かない（本編の小節線を変えない）");
            CollectionAssert.AreEqual(new[] { 1199f, 1349f, 1799f, 2099f }, chart.notes.Select(n => n.time).ToArray());
            CollectionAssert.AreEqual(new[] { 1f, 1.25f, 2f, 2.5f }, chart.notes.Select(n => n.beat).ToArray(),
                "触っていないノーツの拍の値は保ち、新しいノーツは表示中の格子に合わせる");
            Assert.AreEqual(saved, File.ReadAllText(Path.Combine(folder, "chart.json")), "Normal は chart.json にも同じ内容");
        });
    }

    [Test]
    public void VariableTempoBeatsSurviveSavingAndBpmEdits()
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(path, VariableTempoChart);
            Load(target, 1);
            Assert.False(SaberChartUtility.BeatsFollowSingleGrid(Document));
            Call("ApplyChartSettings", 120f, 0f, Get<float>("beatZeroMs"), 1f);
            Set("selectedIndex", 1);
            Call("SetSpatialPosition", 6, 1);
            Assert.True((bool)Call("SaveDocument"));
            var chart = ChartLoader.LoadFromFile(path);
            CollectionAssert.AreEqual(new[] { 2f, 6f, 9f }, chart.notes.Select(n => n.beat).ToArray(),
                "テンポの変化を表す拍の値を、1つの BPM で計算し直さない");
            Assert.AreEqual(120f, chart.bpm);
        });
    }

    [Test]
    public void ExplicitOriginEditIsTheOnlyWayTheFileOriginChanges()
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(path, ElDoradoLikeChart);
            Load(target, 1);
            Call("ApplyChartSettings", 100f, 0f, 599f, 1f);
            Assert.False(window.hasUnsavedChanges, "表示中の値と同じなら変更にしない");
            Call("WriteDisplayedOriginToFile");
            Assert.True((bool)Call("SaveDocument"));
            Assert.AreEqual(599f, ChartLoader.LoadFromFile(path).beatZeroMs, .01f);
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(0f, Document.beatZeroMs);
            Assert.AreEqual(599f, Get<float>("beatZeroMs"), .01f, "Undo しても表示の格子は動かない");
        });
    }

    [Test]
    public void EditingSettingsAndReloadingKeepsNegativeOriginAndAuthoredNoteTimes()
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_hard.json");
            File.WriteAllText(path,
                "{\"bpm\":98,\"offsetMs\":110,\"beatZeroMs\":-90," +
                "\"timeSignatures\":[{\"beat\":0,\"numerator\":5,\"denominator\":8}]," +
                "\"notes\":[{\"beat\":5.047,\"time\":3000,\"x\":-0.75,\"y\":0.2,\"color\":\"blue\"}]} ");
            Load(target, 2);
            Call("ApplyChartSettings", 99f, 110f, Get<float>("beatZeroMs"), 1f);
            Assert.True((bool)Call("SaveDocument"));
            Load(target, 2);
            Assert.AreEqual(-90f, Document.beatZeroMs, "他の設定変更で原点を0へ丸めない");
            Assert.AreEqual(-90f, Get<float>("beatZeroMs"));
            Assert.AreEqual(20f, Document.beatZeroMs + Document.offsetMs, "音源上の拍の位置を維持");
            Assert.AreEqual(3000f, Document.notes[0].time, "作者のノーツ時刻を維持");
            Assert.AreEqual(110f, Document.offsetMs, "作者のタイミング補正を維持");
            Assert.AreEqual(-.75f, Document.notes[0].x);
            Assert.AreEqual(.2f, Document.notes[0].y);
        });
    }

    // ---- 新規: 既存の譜面を黙って置き換えない。曲の設定は他の難易度から引き継ぐ ----

    [Test]
    public void NewChartInheritsSongSettingsAndAsksBeforeReplacingAFile()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"),
                "{\"bpm\":150,\"offsetMs\":30,\"beatZeroMs\":40,\"displayLevel\":6," +
                "\"timeSignatures\":[{\"beat\":0,\"numerator\":7,\"denominator\":8}],\"notes\":[{\"beat\":1,\"time\":440}]}");
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), OneNoteChart);
            string hardText = File.ReadAllText(Path.Combine(folder, "chart_hard.json"));
            Set("songId", target);
            Set("difficultyIndex", 2);
            Call("NewDocument");
            Assert.AreEqual(150f, Document.bpm);
            Assert.AreEqual(30f, Document.offsetMs);
            Assert.AreEqual(40f, Document.beatZeroMs);
            Assert.AreEqual(1, Document.timeSignatures.Count);
            Assert.IsEmpty(Document.notes);
            Assert.AreEqual(0, Document.displayLevel, "表示レベルは難易度ごとの値なので引き継がない");

            int asked = 0;
            Set("confirmDuplicateOverwrite", new Func<string, string, bool>((a, b) => { asked++; return false; }));
            Assert.False((bool)Call("SaveDocument"));
            Assert.AreEqual(1, asked);
            Assert.AreEqual(hardText, File.ReadAllText(Path.Combine(folder, "chart_hard.json")));
            Set("confirmDuplicateOverwrite", new Func<string, string, bool>((a, b) => { asked++; return true; }));
            Assert.True((bool)Call("SaveDocument"));
            Assert.AreEqual(150f, ChartLoader.LoadFromFile(Path.Combine(folder, "chart_hard.json")).bpm);
        });
    }

    // ---- 守7: 下書き ----

    [Test]
    public void DraftIsOfferedAfterReopeningAndCanBeRestoredOrDiscarded()
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(path, OneNoteChart);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            Load(target, 1);
            Set("selectedIndex", 0);
            Key(KeyCode.D, EventModifiers.Control);
            Assert.True((bool)Call("WriteDraftNow"));
            // Unity が落ちたつもりで、未保存の編集を捨てて開き直す。
            Set("confirmChangesDialog", new Func<string, string, string, string, string, int>((a, b, c, d, e) => 2));
            Call("LoadDocument");
            Assert.AreEqual(1, Document.notes.Count);
            Assert.NotNull(Get<object>("pendingDraft"), "ファイルより新しい下書きを案内する");
            Call("RestoreDraft");
            Assert.AreEqual(2, Document.notes.Count);
            Assert.True(window.hasUnsavedChanges, "下書きを開いても保存するまでファイルは変えない");
            Assert.AreEqual(1, ChartLoader.LoadFromFile(path).notes.Count);
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(1, Document.notes.Count, "下書きを開く前へ Undo で戻れる");
            Call("CheckForDraft");
            Assert.NotNull(Get<object>("pendingDraft"));
            Call("DiscardDraft");
            Assert.IsNull(Get<object>("pendingDraft"));
            Assert.AreEqual(0, (int)Drafts.GetMethod("Count").Invoke(null, null));
        });
    }

    [Test]
    public void BrokenNewestDraftKeepsOlderRecoveryAndTheCurrentDocument()
    {
        WithChartFolder((target, folder) =>
        {
            string chartPath = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(chartPath, OneNoteChart); File.SetLastWriteTimeUtc(chartPath, DateTime.UtcNow.AddHours(-1));
            Load(target, 1);
            var recovery = SaberChartUtility.Clone(Document); recovery.notes[0].x = 1.25f;
            string oldPath = (string)Drafts.GetMethod("Write").Invoke(null, new object[] { target, "normal", recovery, null });
            recovery.notes[0].x = 1.5f;
            string brokenPath = (string)Drafts.GetMethod("Write").Invoke(null, new object[] { target, "normal", recovery, null });
            var args = new object[] { target, "normal", null };
            object broken = Drafts.GetMethod("LatestFor").Invoke(null, args);
            broken.GetType().GetField("document").SetValue(broken, "{bad json");
            File.WriteAllText(brokenPath, JsonUtility.ToJson(broken));
            Call("CheckForDraft");
            string before = SaberChartUtility.ToJson(Document, false), disk = File.ReadAllText(chartPath);
            Call("RestoreDraft");
            Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
            Assert.AreEqual(disk, File.ReadAllText(chartPath));
            Assert.IsTrue(File.Exists(oldPath)); Assert.IsFalse(File.Exists(brokenPath));
            Assert.AreEqual(1, Directory.GetFiles(draftRoot, "*.unreadable-*").Length);
            Assert.NotNull(Get<object>("pendingDraft"), "前の下書きを次の復元候補にする");
            Call("RestoreDraft"); Assert.AreEqual(1.25f, Document.notes[0].x);
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        });
    }

    [Test]
    public void DraftDeduplicationPreservesItsSourceVersion()
    {
        var write = Drafts.GetMethod("Write");
        string first = (string)write.Invoke(null, new object[] { "__DraftSource", "normal", Document, "version-one" });
        string second = (string)write.Invoke(null, new object[] { "__DraftSource", "normal", Document, "version-two" });
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(second, write.Invoke(null, new object[] { "__DraftSource", "normal", Document, "version-two" }));
        Assert.AreEqual(2, (int)Drafts.GetMethod("Count").Invoke(null, null));
    }

    [Test]
    public void SavingDeletesOnlyThisWindowsDraftsForTheSavedChart()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), OneNoteChart);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), OneNoteChart);
            Load(target, 1);
            Document.notes[0].x = .25f; Call("UpdateDirtyState"); Call("WriteDraftNow");
            var owned = Get<List<string>>("sessionDraftPaths");
            string normal = owned.Single();

            Load(target, 2);
            Document.notes[0].x = .5f; Call("UpdateDirtyState"); Call("WriteDraftNow");
            string hard = owned.Single(path => path != normal);
            string other = (string)Drafts.GetMethod("Write").Invoke(null,
                new object[] { target + "_other", "hard", Document, null });
            owned.Add(other);
            // 上部の候補を変えても、保存先は開いた曲・難易度のまま。
            Set("songId", target + "_other"); Set("difficultyIndex", 1);
            Assert.True((bool)Call("SaveDocument"));
            Assert.False(File.Exists(hard));
            Assert.True(File.Exists(normal));
            Assert.True(File.Exists(other));
            CollectionAssert.AreEquivalent(new[] { normal, other }, owned);

            Load(target, 1);
            Document.notes[0].x = .75f; Call("UpdateDirtyState"); Call("WriteDraftNow");
            Assert.True((bool)Call("SaveDocument"));
            Assert.False(File.Exists(normal));
            Assert.True(File.Exists(other));
            CollectionAssert.AreEqual(new[] { other }, owned);
        });
    }

    [Test]
    public void SavingUnchangedChartAlsoClearsOnlyItsOwnedDrafts()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), OneNoteChart);
            Load(target, 1);
            float original = Document.notes[0].x;
            Document.notes[0].x = .25f; Call("UpdateDirtyState"); Call("WriteDraftNow");
            string draft = Get<List<string>>("sessionDraftPaths").Single();
            Document.notes[0].x = original; Call("UpdateDirtyState");
            Assert.True((bool)Call("SaveDocument"));
            Assert.False(File.Exists(draft));
            Assert.IsEmpty(Get<List<string>>("sessionDraftPaths"));
        });
    }

    [Test]
    public void ReusingAnEarlierSessionDraftDoesNotClaimItForDeletionOnSave()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), OneNoteChart);
            Load(target, 1); Document.notes[0].x = 1.25f; Call("UpdateDirtyState");
            string prior = (string)Drafts.GetMethod("Write").Invoke(null,
                new object[] { target, "normal", Document, Call("DraftSourceHash") });
            Assert.IsTrue((bool)Call("WriteDraftNow"));
            Assert.IsFalse(Get<List<string>>("sessionDraftPaths").Contains(prior));
            Assert.IsTrue((bool)Call("SaveDocument")); Assert.IsTrue(File.Exists(prior));
        });
    }

    [Test]
    public void ManualDraftListIncludesDraftsOlderThanAnExternallyUpdatedChart()
    {
        WithChartFolder((target, folder) =>
        {
            string chartPath = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(chartPath, OneNoteChart); Load(target, 1);
            var recovery = SaberChartUtility.Clone(Document); recovery.notes[0].x = 1.25f;
            Drafts.GetMethod("Write").Invoke(null, new object[] { target, "normal", recovery, Call("DraftSourceHash") });
            File.AppendAllText(chartPath, "\n ");
            Call("CheckForDraft"); Assert.IsNull(Get<object>("pendingDraft"));
            var drafts = (System.Collections.IEnumerable)Drafts.GetMethod("ListFor").Invoke(null, new object[] { target, "normal" });
            object draft = drafts.Cast<object>().Single(); Set("pendingDraft", draft);
            bool warned = false; Set("confirmStaleDraft", new Func<string, bool>(message => { warned = true; return false; }));
            string before = SaberChartUtility.ToJson(Document, false); Call("RestoreDraft");
            Assert.IsTrue(warned); Assert.AreEqual(before, SaberChartUtility.ToJson(Document, false));
        });
    }

    [Test]
    public void DraftsKeepTheNewestFivePerChartAndSkipUnchangedContent()
    {
        var write = Drafts.GetMethod("Write");
        write.Invoke(null, new object[] { "__DraftOther", "hard", Document, null });
        write.Invoke(null, new object[] { "__DraftPrune", "normal", Document, null });
        write.Invoke(null, new object[] { "__DraftPrune", "normal", Document, null });
        Assert.AreEqual(2, (int)Drafts.GetMethod("Count").Invoke(null, null), "中身が同じなら書かない");
        for (int i = 0; i < 8; i++)
        {
            Document.offsetMs = i + 1; // 中身を変えて書く
            write.Invoke(null, new object[] { "__DraftPrune", "normal", Document, null });
        }
        Assert.AreEqual(6, (int)Drafts.GetMethod("Count").Invoke(null, null), "1つの譜面は新しい5件。ほかの譜面の下書きは押し出さない");
    }

    [Test]
    public void SavingClearsTheDraftsOfThatChart()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), OneNoteChart);
            Load(target, 1);
            Set("selectedIndex", 0);
            Key(KeyCode.D, EventModifiers.Control);
            Assert.True((bool)Call("WriteDraftNow"));
            Assert.True((bool)Call("SaveDocument"));
            Assert.AreEqual(0, (int)Drafts.GetMethod("Count").Invoke(null, null));
        });
    }

    // ---- 未知の項目の往復 ----

    [Test]
    public void UnknownFieldsRoundTripThroughFilesHistoryAndClones()
    {
        string source = "{\"_comment\":\"メモ \\\"引用\\\"\",\"author\":{\"name\":\"A\",\"n\":[1,2]},\"bpm\":90,\"notes\":[]}";
        var document = SaberChartUtility.FromJson(source);
        Assert.AreEqual(2, document.extraFields.Count);
        string file = SaberChartUtility.ToFileJson(document);
        StringAssert.StartsWith("{\n    \"_comment\": \"メモ \\\"引用\\\"\",", file.Replace("\r\n", "\n"));
        Assert.AreEqual(90f, ChartLoader.Parse(file).bpm);
        var again = SaberChartUtility.FromJson(file);
        Assert.AreEqual(SaberChartUtility.ToJson(document, false), SaberChartUtility.ToJson(again, false));
        var cloned = SaberChartUtility.Clone(document);
        Assert.AreEqual("author", cloned.extraFields[1].key);
        Assert.False(file.Contains("extraFields"), "エディターの作業用の項目はファイルに書かない");
    }

    // ---- 取4: chart.json は Normal から ----

    [TestCase(0)]
    [TestCase(2)]
    public void SavingEasyOrHardFirstDoesNotCreateTheFallbackChart(int difficulty)
    {
        WithChartFolder((target, folder) =>
        {
            Set("songId", target);
            Set("difficultyIndex", difficulty);
            Call("NewDocument");
            Assert.True((bool)Call("SaveDocument"));
            Assert.False(File.Exists(Path.Combine(folder, "chart.json")));
            StringAssert.Contains("Normal がまだ無い", Get<string>("statusMessage"));
        });
    }

    [TestCase("chart_normal.json", false)]
    [TestCase("chart.json", false)]
    [TestCase("chart_normal.json", true)]
    public void ExternalChangesRequireConfirmationAndCancellationPreservesBothVersions(string changedFile, bool delete)
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(path, OneNoteChart);
            File.WriteAllText(Path.Combine(folder, "chart.json"), OneNoteChart);
            Load(target, 1);
            Set("selectedIndex", 0); Key(KeyCode.D, EventModifiers.Control);
            string draft = SaberChartUtility.ToJson(Document, false);
            string changed = Path.Combine(folder, changedFile);
            if (delete) File.Delete(changed); else File.WriteAllText(changed, VariableTempoChart);
            // 再コンパイル相当の再有効化でも、読込時点の比較を維持する。
            Call("OnDisable"); Call("OnEnable");
            int asked = 0;
            Set("confirmExternalOverwrite", new Func<string, bool>(message => { asked++; return false; }));
            Assert.False((bool)Call("SaveDocument"));
            Assert.AreEqual(1, asked);
            Assert.AreEqual(draft, SaberChartUtility.ToJson(Document, false));
            if (delete) Assert.False(File.Exists(changed)); else Assert.AreEqual(VariableTempoChart, File.ReadAllText(changed));
            Assert.True(window.hasUnsavedChanges);
            Set("confirmExternalOverwrite", new Func<string, bool>(message => { asked++; return true; }));
            Assert.True((bool)Call("SaveDocument"));
            Assert.AreEqual(2, asked);
            Assert.AreEqual(2, ChartLoader.LoadFromFile(path).notes.Count);
            Set("selectedIndex", 0); Key(KeyCode.D, EventModifiers.Control);
            Assert.True((bool)Call("SaveDocument")); Assert.AreEqual(2, asked, "保存成功後は比較元を更新する");
        });
    }

    [Test]
    public void FileCreatedAfterOpeningAnEmptyDifficultyIsAlsoProtected()
    {
        WithChartFolder((target, folder) =>
        {
            Load(target, 2);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), OneNoteChart);
            Document.notes.Add(new SaberChartNote { time = 600 }); Call("UpdateDirtyState");
            Set("confirmExternalOverwrite", new Func<string, bool>(message => false));
            Assert.False((bool)Call("SaveDocument"));
            Assert.AreEqual(OneNoteChart, File.ReadAllText(Path.Combine(folder, "chart_hard.json")));
        });
    }

    [Test]
    public void MissingDifficultyOpensNormalAsAnUnsavedNewChart()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart.json"), OneNoteChart);
            Load(target, 2);
            Assert.AreEqual(1, Document.notes.Count, "Normal の内容を元にする");
            Call("UpdateDirtyState");
            Assert.True(window.hasUnsavedChanges, "保存や本編テストで黙って Hard を作らないよう、未保存の新しい譜面として開く");
            StringAssert.Contains("まだありません", Get<string>("persistentWarning"));
            Assert.False(File.Exists(Path.Combine(folder, "chart_hard.json")));
        });
    }

    [Test]
    public void OutsideSavesReloadACleanChartAndWarnAnEditedOne()
    {
        WithChartFolder((target, folder) =>
        {
            string path = Path.Combine(folder, "chart_normal.json");
            File.WriteAllText(path, OneNoteChart);
            Load(target, 1);
            Set("currentBeat", 3f);
            File.WriteAllText(path, VariableTempoChart);
            Set("nextDiskCheckAt", 0d); Call("CheckDiskForExternalChanges");
            Assert.AreEqual(ChartLoader.LoadFromFile(path).notes.Count, Document.notes.Count, "未編集なら、ほかのツールが保存した最新を読み直す");
            Assert.False(window.hasUnsavedChanges);
            Assert.AreEqual(3f, Get<float>("currentBeat"), "表示位置は保つ");
            Set("selectedIndex", 0); Key(KeyCode.D, EventModifiers.Control);
            string edited = SaberChartUtility.ToJson(Document, false);
            File.WriteAllText(path, OneNoteChart);
            Set("nextDiskCheckAt", 0d); Call("CheckDiskForExternalChanges");
            Assert.AreEqual(edited, SaberChartUtility.ToJson(Document, false), "編集中は読み直さない");
            StringAssert.Contains("書き換えられました", Get<string>("persistentWarning"));
        });
    }

    // 編集中の難易度のファイルが外で消えても、Normal(chart.json)を「Hard・保存済み」として読み直さない。
    // 以前はそのまま保存すると、Normal の中身で chart_hard.json を黙って作っていた。
    [Test]
    public void OutsideDeletionKeepsTheOpenChartUnsavedInsteadOfLoadingNormal()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart.json"), OneNoteChart);
            string hard = Path.Combine(folder, "chart_hard.json");
            File.WriteAllText(hard, VariableTempoChart);
            Load(target, 2);
            string opened = SaberChartUtility.ToJson(Document, false);
            Call("UpdateDirtyState"); Assert.False(window.hasUnsavedChanges);
            File.Delete(hard);
            Set("nextDiskCheckAt", 0d); Call("CheckDiskForExternalChanges");
            Assert.AreEqual(opened, SaberChartUtility.ToJson(Document, false), "Normal の中身に入れ替えない");
            Call("UpdateDirtyState");
            Assert.True(window.hasUnsavedChanges, "ディスクに無いので未保存として残す");
            StringAssert.Contains("無くなりました", Get<string>("persistentWarning"));
            Assert.False(File.Exists(hard), "黙って作り直さない");
        });
    }

    [Test]
    public void FailedAutomaticRestoreKeepsAPersistentWarningAndNeverClaimsSaved()
    {
        WithChartFolder((target, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{ broken JSON");
            Set("songId", target); Set("difficultyIndex", 1); Set("loadedSongId", null);
            Set("document", new SaberChartDocument()); Set("documentInitialized", false);
            Call("RestoreLastDocument"); Call("UpdateDirtyState");
            StringAssert.Contains("前回の譜面を読み込めません", Get<string>("persistentWarning"));
            Assert.IsNull(Get<string>("savedJson")); Assert.True(window.hasUnsavedChanges);
            Assert.AreEqual("{ broken JSON", File.ReadAllText(Path.Combine(folder, "chart_normal.json")));
        });
    }

    // ---- helpers ----

    const string OneNoteChart = "{\"bpm\":150,\"notes\":[{\"beat\":2,\"time\":800,\"x\":1}]}";
    // ElDorado のように原点の欄が無く、拍の値は 599ms を原点とする格子に乗っている譜面。
    const string ElDoradoLikeChart = "{\"_comment\":\"手で置いた譜面のメモ\",\"bpm\":100,\"coordScale\":1,\"offsetMs\":0,\"notes\":[" +
        "{\"beat\":1,\"time\":1199,\"x\":-1,\"y\":0,\"type\":\"tap\",\"color\":\"red\",\"direction\":\"none\",\"count\":1}," +
        "{\"beat\":2,\"time\":1799,\"x\":1,\"y\":0,\"type\":\"tap\",\"color\":\"blue\",\"direction\":\"none\",\"count\":1}," +
        "{\"beat\":2.5,\"time\":2099,\"x\":0,\"y\":0,\"type\":\"tap\",\"color\":\"gold\",\"direction\":\"none\",\"count\":1}]}";
    // 校歌のように、拍の値が途中でテンポの変わる格子に乗っている譜面。
    const string VariableTempoChart = "{\"bpm\":92,\"offsetMs\":0,\"notes\":[" +
        "{\"beat\":2,\"time\":1000,\"x\":-1},{\"beat\":6,\"time\":3000,\"x\":1},{\"beat\":9,\"time\":5200,\"x\":0}]}";

    SaberChartDocument Document => Get<SaberChartDocument>("document");

    [TestCase("NewDocument")]
    [TestCase("DuplicateToSelection")]
    public void ChangingTargetKeepsOldDraftAndAudioOut(string operation)
    {
        Call("WriteDraftNow");
        Document.notes.Clear();
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
        Call("CheckForDraft");
        Assert.NotNull(Get<object>("pendingDraft"));
        var clip = AudioClip.Create("旧曲", 800, 1, 8000, false);
        try
        {
            Set("audioClip", clip);
            Set("songId", "__Other_" + Guid.NewGuid().ToString("N"));
            Call(operation);
            Call("RestoreDraft");
            Assert.IsEmpty(Document.notes);
            Assert.IsNull(Get<AudioClip>("audioClip"));
        }
        finally { Object.DestroyImmediate(clip); }
    }

    [Test]
    public void ClipboardKeepsBeatsAfterTempoChange()
    {
        Document.notes[0].time = 1000f;
        Document.notes[1].time = 1500f;
        Call("SetRange", 1000f, 2000f);
        Call("CopySelection");
        Call("ApplyChartSettings", 60f, 0f, 0f, 1f);
        Call("PasteAtCursor");
        Call("PasteAtCursor");
        CollectionAssert.AreEqual(new[] { 1000f, 1500f, 4000f, 5000f },
            Document.notes.Select(n => n.time));
    }

    void Load(string target, int difficulty)
    {
        Set("songId", target);
        Set("difficultyIndex", difficulty);
        Set("confirmChangesDialog", new Func<string, string, string, string, string, int>((a, b, c, d, e) => 2));
        Set("reportLoadError", new Action<string>(message => Assert.Fail(message)));
        Call("LoadDocument");
        Assert.AreEqual(target, Get<string>("loadedSongId"));
    }

    void WithChartFolder(Action<string, string> check)
    {
        string target = "__ChartLoadTest_" + Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
        string folder = Path.GetFullPath(Path.Combine(root, target));
        Directory.CreateDirectory(folder);
        try { check(target, folder); }
        finally
        {
            Assert.True(folder.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.True(Path.GetFileName(folder).StartsWith("__ChartLoadTest_"));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
            string backups = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups"));
            if (Directory.Exists(backups))
                foreach (string file in Directory.GetFiles(backups, target + "_*.json")) File.Delete(file);
        }
    }

    // ファイルの先頭の項目名だけを読む小さな補助。
    sealed class SaberJsonObjectProxy
    {
        public string FirstKey;
        public static explicit operator SaberJsonObjectProxy(string json)
        {
            int start = json.IndexOf('"');
            int end = json.IndexOf('"', start + 1);
            return new SaberJsonObjectProxy { FirstKey = json.Substring(start + 1, end - start - 1) };
        }
    }

    void SetUnsavedFlag(bool value) =>
        typeof(EditorWindow).GetProperty("hasUnsavedChanges").GetSetMethod(true).Invoke(window, new object[] { value });

    void ClearHistory()
    {
        object history = Get<object>("history");
        history.GetType().GetMethod("Clear").Invoke(history, null);
    }

    T History<T>(string property)
    {
        object history = Get<object>("history");
        return (T)history.GetType().GetProperty(property).GetValue(history);
    }

    void Key(KeyCode key, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
    void Set(string name, object value) => Field(name).SetValue(window, value);
    T Get<T>(string name) => (T)Field(name).GetValue(window);
    static FieldInfo Field(string name) =>
        typeof(SaberChartEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new MissingFieldException(name);
    object Call(string name, params object[] args)
    {
        var method = typeof(SaberChartEditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
                     throw new MissingMethodException(name);
        try { return method.Invoke(window, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }
}
