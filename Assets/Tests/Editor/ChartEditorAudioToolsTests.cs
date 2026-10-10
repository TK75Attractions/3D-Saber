using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// 音に合わせる道具(テンポ地図の格子、打ち込みの遅れの測定と補正、ヒット音とメトロノーム)の決まりを確かめる。
public class ChartEditorAudioToolsTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] PrefKeys =
    {
        "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout", "OutputProfile",
        "InputOffset.0", "InputOffset.1", "InputOffset.2", "InputOffsetNote.0", "InputOffsetNote.1", "InputOffsetNote.2",
    };
    readonly Dictionary<string, object> savedPrefs = new Dictionary<string, object>();
    SaberChartEditorWindow window;
    AudioClip clip;
    float seconds;

    [SetUp]
    public void SetUp()
    {
        foreach (string key in PrefKeys)
        {
            string full = Prefix + key;
            if (!EditorPrefs.HasKey(full)) { savedPrefs[key] = null; continue; }
            savedPrefs[key] = key.StartsWith("InputOffset.") || key == "Zoom" ? (object)EditorPrefs.GetFloat(full)
                : key == "Difficulty" || key == "Snap" || key == "Measure" || key == "OutputProfile" ? (object)EditorPrefs.GetInt(full)
                : EditorPrefs.GetString(full);
        }
        EditorPrefs.SetString(Prefix + "SongId", "__ChartEditorAudioToolsTest");
        EditorPrefs.SetInt(Prefix + "Snap", 3);
        window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
        Set("document", new SaberChartDocument
        {
            notes = new List<SaberChartNote>
            {
                new SaberChartNote { beat = 2, time = 1000, x = -1, color = "blue" },
                new SaberChartNote { beat = 4, time = 2000, x = 1, color = "red" },
            }
        });
        Set("beatZeroMs", 0f);
        Set("currentBeat", 0f);
        Set("snapIndex", 3);
        Set("recordInputOffsetMs", 0f);
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
        seconds = 0f;
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
        if (clip != null) Object.DestroyImmediate(clip);
        foreach (var pair in savedPrefs)
        {
            string full = Prefix + pair.Key;
            if (pair.Value == null) EditorPrefs.DeleteKey(full);
            else if (pair.Value is string text) EditorPrefs.SetString(full, text);
            else if (pair.Value is float number) EditorPrefs.SetFloat(full, number);
            else EditorPrefs.SetInt(full, (int)pair.Value);
        }
        savedPrefs.Clear();
    }

    // ---- 合10: テンポ地図の格子 ----

    [Test]
    public void GridWithoutTempoMapMatchesTheSingleBpmFormula()
    {
        var grid = new SaberChartGrid(100f, 599f, null);
        Assert.AreEqual(SaberChartUtility.BeatToTimeMs(2.5f, 100f, 599f), grid.TimeAt(2.5f), .001f);
        Assert.AreEqual(SaberChartUtility.TimeMsToBeat(2099f, 100f, 599f), grid.BeatAt(2099f), .0001f);
        Assert.AreEqual(100f, grid.TempoAt(10f));
    }

    [Test]
    public void TempoMapConnectsPointsAndExtendsTheEdgeTempos()
    {
        // 120 BPM で8拍(4秒)、そこから 92 BPM。
        var map = new[]
        {
            new SaberChartTempoPoint { beat = 0, timeMs = 1000 },
            new SaberChartTempoPoint { beat = 8, timeMs = 5000 },
            new SaberChartTempoPoint { beat = 16, timeMs = 5000 + 8 * 60000f / 92f },
        };
        var grid = new SaberChartGrid(120f, 0f, map);
        Assert.AreEqual(3000f, grid.TimeAt(4f), .01f);
        Assert.AreEqual(5000f + 4 * 60000f / 92f, grid.TimeAt(12f), .01f);
        Assert.AreEqual(12f, grid.BeatAt(5000f + 4 * 60000f / 92f), .0001f);
        Assert.AreEqual(120f, grid.TempoAt(2f), .01f);
        Assert.AreEqual(92f, grid.TempoAt(20f), .01f, "最後の点より後ろは最後の区間のテンポ");
        Assert.AreEqual(500f, grid.TimeAt(-1f), .01f, "最初の点より前は最初の区間のテンポ");
        var single = new SaberChartGrid(120f, 0f, new[] { new SaberChartTempoPoint { beat = 4, timeMs = 2600 } });
        Assert.AreEqual(2100f, single.TimeAt(3f), .01f, "点が1つなら BPM のまま点へ合わせる");
    }

    [Test]
    public void InvalidOrBackwardPointsAreDropped()
    {
        var cleaned = SaberChartGrid.Clean(new[]
        {
            new SaberChartTempoPoint { beat = 4, timeMs = 2000 },
            new SaberChartTempoPoint { beat = 0, timeMs = 0 },
            new SaberChartTempoPoint { beat = 6, timeMs = 1900 },   // 時刻が戻る
            new SaberChartTempoPoint { beat = float.NaN, timeMs = 10 },
            new SaberChartTempoPoint { beat = 8, timeMs = 4000 },
        });
        CollectionAssert.AreEqual(new[] { 0f, 4f, 8f }, cleaned.Select(p => p.beat).ToArray());
    }

    [Test]
    public void TempoMapFromVariableTempoBeatsKeepsOnlyTheBends()
    {
        var notes = new List<SaberChartNote>();
        for (int beat = 0; beat <= 8; beat++) notes.Add(new SaberChartNote { beat = beat, time = 1000 + beat * 500f });
        for (int beat = 9; beat <= 16; beat++) notes.Add(new SaberChartNote { beat = beat, time = 5000 + (beat - 8) * 60000f / 92f });
        var map = SaberChartGrid.FromNotes(notes);
        CollectionAssert.AreEqual(new[] { 0f, 8f, 16f }, map.Select(p => p.beat).ToArray());
        Assert.AreEqual(5000f, map[1].timeMs, .01f);
    }

    [Test]
    public void TempoMapIsSavedOutsideTheGameFieldsAndShared()
    {
        Document.tempoMap = new List<SaberChartTempoPoint>
        {
            new SaberChartTempoPoint { beat = 0, timeMs = 0 }, new SaberChartTempoPoint { beat = 8, timeMs = 4000 },
        };
        string file = SaberChartUtility.ToFileJson(Document);
        StringAssert.Contains("\"editorTempoMap\": [{\"beat\": 0, \"timeMs\": 0}, {\"beat\": 8, \"timeMs\": 4000}]", file);
        var chart = ChartLoader.Parse(file);
        Assert.AreEqual(2, chart.notes.Count);
        Assert.AreEqual(120f, chart.bpm, "本編の BPM はそのまま");
        var again = SaberChartUtility.FromJson(file);
        Assert.AreEqual(2, again.tempoMap.Count);
        Assert.IsEmpty(again.extraFields);
        var other = new SaberChartDocument();
        StringAssert.Contains("テンポ地図 2点 / 0点", string.Join(" ", SaberChartUtility.SongSettingDifferences(again, other)));
        SaberChartUtility.CopySongSettings(again, other);
        Assert.AreEqual(2, other.tempoMap.Count);
    }

    [Test]
    public void EditorPlacementAndDraggingFollowTheTempoMap()
    {
        // 8拍目から 60 BPM(1拍=1秒)に落ちる地図。
        Document.tempoMap = new List<SaberChartTempoPoint>
        {
            new SaberChartTempoPoint { beat = 0, timeMs = 0 }, new SaberChartTempoPoint { beat = 8, timeMs = 4000 },
            new SaberChartTempoPoint { beat = 16, timeMs = 12000 },
        };
        Assert.AreEqual(5000f, (float)Call("TimeAtBeat", 9f), .01f);
        Assert.AreEqual(9f, (float)Call("BeatAtTime", 5000f), .0001f);
        Set("currentBeat", 9f);
        Set("snapIndex", 0);
        Call("EditBeatNotes", 0); // 空の拍なので何もしない
        Document.notes.Add(new SaberChartNote { beat = 9, time = 5000, x = 0, color = "gold" });
        Call("EditBeatNotes", 0);
        Assert.NotNull(Document.notes.Find(n => Mathf.Abs(n.time - 6000f) < .01f), "遅い区間の1拍は1秒");
    }

    [Test]
    public void TempoMapEditsAreUndoableAndAddingAPointKeepsTheGrid()
    {
        Set("currentBeat", 3.4f);
        float before = (float)Call("TimeAtBeat", 6f);
        Call("AddTempoPointAtCursor");
        Assert.AreEqual(1, Document.tempoMap.Count);
        Assert.AreEqual(3f, Document.tempoMap[0].beat);
        Assert.AreEqual(before, (float)Call("TimeAtBeat", 6f), .01f, "点を置いただけでは格子は動かない");
        Call("EditTempoPoint", 0, 3f, 1700f, false);
        Assert.AreEqual(1700f, (float)Call("TimeAtBeat", 3f), .01f);
        Assert.AreEqual(1000f, Document.notes[0].time, "地図を変えてもノーツの時刻は動かさない");
        Assert.AreEqual(2f, Document.notes[0].beat, "拍の値も勝手に変えない");
        Call("RecalculateBeatsWithGrid");
        Assert.AreEqual((float)Call("BeatAtTime", 1000f), Document.notes[0].beat, .0001f);
        Key(KeyCode.Z, EventModifiers.Control);
        Key(KeyCode.Z, EventModifiers.Control);
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.IsEmpty(Document.tempoMap);
        Assert.False(window.hasUnsavedChanges);
    }

    [Test]
    public void RecorderSnapsOnTheTempoMappedGrid()
    {
        var doc = new SaberChartDocument { bpm = 120 };
        var grid = new SaberChartGrid(120f, 0f, new[]
        {
            new SaberChartTempoPoint { beat = 0, timeMs = 0 }, new SaberChartTempoPoint { beat = 4, timeMs = 2000 },
            new SaberChartTempoPoint { beat = 8, timeMs = 6000 },
        });
        var take = new SaberChartRecorder(doc, 10, 4, 0, false, 2, null, grid);
        var note = take.Press(0, 3.45f, 0, 0, "gold");
        Assert.AreEqual(3000f, note.time, .01f, "60 BPM の区間では1拍=1秒の格子へ寄せる");
        Assert.AreEqual(5f, note.beat, .0001f);
    }

    // ---- 合1: 打ち込みの遅れ ----

    [Test]
    public void SummaryUsesMedianAndRobustSpread()
    {
        var summary = SaberChartLatency.Summarize(new[] { 20f, 22f, 25f, 18f, 24f, 21f, 23f, 19f, 140f });
        Assert.AreEqual(9, summary.Count);
        Assert.AreEqual(22f, summary.Median, .01f);
        Assert.AreEqual(1, summary.Outliers, "外れた1打に引きずられない");
        Assert.True(summary.Reliable);
        Assert.False(SaberChartLatency.Summarize(new[] { 20f, 22f, 25f }).Reliable, "打数が少ないと使わない");
        Assert.False(SaberChartLatency.Summarize(new[] { -60f, 50f, -40f, 70f, -55f, 65f, -45f, 52f }).Reliable, "ばらつきが大きいと使わない");
    }

    [Test]
    public void CalibrationIgnoresLeadInAndFarTaps()
    {
        var taps = new List<float> { SaberChartLatency.ClickTimeMs(1) + 30f };          // 前置きは数えない
        for (int beat = 4; beat < 20; beat++) taps.Add(SaberChartLatency.ClickTimeMs(beat) + 30f + (beat % 2 == 0 ? 3f : -3f));
        taps.Add(SaberChartLatency.ClickTimeMs(10) + 290f);                               // 窓の外
        var summary = SaberChartLatency.Measure(taps);
        Assert.AreEqual(16, summary.Count);
        Assert.AreEqual(30f, summary.Median, 3.01f);
        Assert.True(summary.Reliable);
    }

    [Test]
    public void RecorderKeepsThePreSnapErrorOfEachHit()
    {
        var doc = new SaberChartDocument();
        var take = new SaberChartRecorder(doc, 10, 4, 10, true, 2);
        take.Press(0, 1.040f, -1, 0, "blue");
        take.Release(0, 1.05f);
        take.Press(1, 1.530f, 1, 0, "red");
        CollectionAssert.AreEqual(new[] { 30f, 20f }, take.Deviations.Select(d => Mathf.Round(d)).ToArray(),
            "入力補正を引いたあとの、いちばん近い格子との差");
        Assert.AreEqual(1000f, doc.notes[0].time, "Snap した時刻は従来どおり");
    }

    [Test]
    public void CalibratingWithTheClickTrackSetsAndSavesTheInputOffset()
    {
        Set("calibrationClockOverride", (Func<float?>)(() => seconds));
        Set("outputProfile", 1);
        Call("StartLatencyCalibration");
        Assert.True(Get<bool>("calibrating"));
        for (int beat = 4; beat < 20; beat++)
        {
            seconds = (SaberChartLatency.ClickTimeMs(beat) + 40f) / 1000f;
            Key(beat % 2 == 0 ? KeyCode.F : KeyCode.Space);
            Key(beat % 2 == 0 ? KeyCode.F : KeyCode.Space, EventType.KeyUp);
        }
        Key(KeyCode.J);
        Key(KeyCode.J); // 押し続けは1打
        Assert.AreEqual(17, Get<List<float>>("calibrationTaps").Count);
        Assert.IsEmpty(Document.notes.Where(n => n.time > 2000), "測定中のキーで譜面を変えない");
        Call("FinishLatencyCalibration", false);
        Assert.False(Get<bool>("calibrating"));
        Assert.AreEqual(40f, Get<float>("recordInputOffsetMs"), .01f);
        Assert.AreEqual(40f, EditorPrefs.GetFloat(Prefix + "InputOffset.1"), .01f, "出力機器ごとに保存する");
        Assert.AreEqual(1, EditorPrefs.GetInt(Prefix + "OutputProfile"));
        StringAssert.StartsWith("測定", EditorPrefs.GetString(Prefix + "InputOffsetNote.1"));
        EditorPrefs.SetFloat(Prefix + "InputOffset.0", 12f);
        EditorPrefs.SetInt(Prefix + "OutputProfile", 0);
        Call("LoadInputOffsetProfile");
        Assert.AreEqual(12f, Get<float>("recordInputOffsetMs"), "出力を切り替えると、その機器の補正を使う");
    }

    [Test]
    public void CancelledOrPoorCalibrationKeepsTheOffset()
    {
        Set("recordInputOffsetMs", 7f);
        Set("calibrationClockOverride", (Func<float?>)(() => seconds));
        Call("StartLatencyCalibration");
        Key(KeyCode.Escape);
        Assert.False(Get<bool>("calibrating"));
        Assert.AreEqual(7f, Get<float>("recordInputOffsetMs"));
        Call("StartLatencyCalibration");
        seconds = SaberChartLatency.ClickTimeMs(5) / 1000f;
        Key(KeyCode.D);
        Call("FinishLatencyCalibration", false);
        Assert.AreEqual(7f, Get<float>("recordInputOffsetMs"), "打数が足りなければ変えない");
        StringAssert.Contains("1打", Get<string>("calibrationResult"));
    }

    [Test]
    public void TakeStatisticsCanBeAddedToTheInputOffset()
    {
        var take = new SaberChartRecorder(Document, 10, 4, 0, false, 2);
        for (int i = 0; i < 10; i++) take.Press(100 + i, 3 + i * .5f + .025f, i % 2 == 0 ? -2f : 2f, .5f, "gold");
        Call("RememberTakeLatency", take);
        Assert.True(Get<bool>("hasLastTakeSummary"));
        Set("recordInputOffsetMs", 5f);
        Call("ApplyLastTakeOffset");
        Assert.AreEqual(30f, Get<float>("recordInputOffsetMs"), .01f, "今の補正に、録ったずれの中央値を足す");
        Assert.False(Get<bool>("hasLastTakeSummary"), "足したら同じ値を二度足さない");
    }

    // ---- 合6: ヒット音とメトロノーム ----

    [Test]
    public void ClicksAreMixedAtNoteAndBeatTimesThroughTheSamePath()
    {
        clip = AudioClip.Create("ClickMixTest", 44100 * 4, 2, 44100, false);
        Set("audioClip", clip);
        Assert.AreSame(clip, Call("PlaybackClipFor", clip), "オフのときは曲だけ");
        Set("playHitSounds", true);
        var mix = (AudioClip)Call("PlaybackClipFor", clip);
        Assert.AreNotSame(clip, mix);
        Assert.AreEqual(clip.samples, mix.samples);
        var data = new float[mix.samples * mix.channels];
        mix.GetData(data, 0);
        Assert.Greater(Energy(data, mix, 1.0), .01f, "1000ms のノーツにクリック");
        Assert.Less(Energy(data, mix, .5), .0001f, "ノーツのない所は無音のまま");
        Assert.AreSame(mix, Call("PlaybackClipFor", clip), "同じ内容なら作り直さない");
        Document.notes.Add(new SaberChartNote { time = 500, x = 0, color = "gold" });
        Assert.AreNotSame(mix, Call("PlaybackClipFor", clip), "ノーツが変わったら作り直す");
    }

    [Test]
    public void MetronomeAccentsBarStartsAndFollowsTheMeter()
    {
        clip = AudioClip.Create("MetronomeTest", 44100 * 6, 1, 44100, false);
        Document.timeSignatures = new List<ChartTimeSignature> { new ChartTimeSignature { beat = 0, numerator = 3, denominator = 4 } };
        var beats = ((IEnumerable<(double seconds, bool accent)>)Call("MetronomeSeconds", clip.length)).ToList();
        Assert.AreEqual(0, beats[0].seconds, .0001);
        Assert.True(beats[0].accent);
        Assert.False(beats[1].accent);
        Assert.True(beats[3].accent, "3/4 なので4つ目が次の小節の頭");
        Assert.AreEqual(1.5, beats[3].seconds, .0001);
        Assert.True(beats.All(b => b.seconds < clip.length));
    }

    [Test]
    public void RecordingNeverPlaysTheClickMix()
    {
        clip = AudioClip.Create("NoClicksWhileRecording", 44100 * 4, 1, 44100, false);
        Set("playHitSounds", true);
        Set("suppressClicksForNextPlay", true);
        Assert.AreSame(clip, Call("PlaybackClipFor", clip));
    }

    static float Energy(float[] data, AudioClip mix, double atSeconds)
    {
        int start = (int)(atSeconds * mix.frequency) * mix.channels;
        float sum = 0;
        for (int i = start; i < start + 400 * mix.channels && i < data.Length; i++) sum += Mathf.Abs(data[i]);
        return sum / (400 * mix.channels);
    }

    // ---- helpers ----

    SaberChartDocument Document => Get<SaberChartDocument>("document");

    void Key(KeyCode key, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
    void Key(KeyCode key, EventType type) =>
        Call("HandleKeyboardShortcuts", new Event { type = type, keyCode = key });
    void Set(string name, object value) => Field(name).SetValue(window, value);
    T Get<T>(string name) => (T)Field(name).GetValue(window);
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
