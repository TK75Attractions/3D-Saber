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

// 難易度の派生(上から下ろす候補)と、曲の設定の食い違いの扱いを確かめる。書き込みは架空の曲フォルダだけに行う。
public class ChartEditorDifficultyTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] PrefKeys = { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout", "ShowPlaybackPreview" };
    readonly Dictionary<string, object> savedPrefs = new Dictionary<string, object>();
    SaberChartEditorWindow window;

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
        EditorPrefs.SetString(Prefix + "SongId", "__ChartEditorDifficultyTest");
        EditorPrefs.SetInt(Prefix + "Difficulty", 1);
        EditorPrefs.SetInt(Prefix + "Snap", 3);
        window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
    }

    [TearDown]
    public void TearDown()
    {
        if (window != null)
        {
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
    }

    // ---- 速3: 下ろす候補の決まり ----

    static float BeatAt120(float timeMs) => timeMs / 500f;

    static SaberChartNote N(float beat, string color, float x, string direction = "none", string type = null,
        int count = 1, float lengthMs = 0) => new SaberChartNote
    {
        beat = beat, time = beat * 500f, x = x, color = color, direction = direction,
        type = type ?? (count > 1 ? "long" : direction == "none" ? "tap" : "direction"), count = count, lengthMs = lengthMs,
    };

    [Test]
    public void EasyCandidatesKeepBeatHeadsOneNotePerChordAndSimpleShapes()
    {
        var upper = new List<SaberChartNote>
        {
            N(0, "red", 1),
            N(.5f, "blue", -1),                 // 裏拍は残さない
            N(1, "gold", 0), N(1, "red", 2),    // 同時は金を優先して1つ
            N(2, "blue", -2, "up"),             // Easy は矢印を TAP に
            N(2.5f, "blue", -1),
            N(3, "blue", -1),                   // 同じ手(青)の間隔は1拍以上
            N(4, "red", 1, type: "long", count: 2, lengthMs: 300), // 短い LONG は TAP に
        };
        var candidates = SaberChartDerive.Candidates(upper, SaberChartDerive.Rules.For("easy"), BeatAt120, new SaberChartDocument());
        CollectionAssert.AreEqual(new[] { 0f, 500f, 1000f, 1500f, 2000f }, candidates.Select(n => n.time).ToArray());
        Assert.AreEqual("gold", candidates[1].color);
        Assert.AreEqual("none", candidates[2].direction);
        Assert.AreEqual("tap", candidates[2].type);
        Assert.AreEqual("tap", candidates[4].type);
        Assert.AreEqual(1, candidates[4].count);
        Assert.AreEqual(0, candidates[4].lengthMs);
        Assert.AreEqual("up", upper[4].direction, "上の難易度の譜面は変えない");
    }

    [Test]
    public void SameHandSpacingBlocksTooCloseNotesButOtherHandIsFree()
    {
        var upper = new List<SaberChartNote> { N(0, "red", 1), N(1, "red", 1), N(1, "blue", -1), N(1.5f, "red", 1), N(2, "red", 1) };
        var easy = SaberChartDerive.Candidates(upper, SaberChartDerive.Rules.For("easy"), BeatAt120, new SaberChartDocument());
        // 同時(1拍目の赤青)は1つ、同じ手の赤は1拍以上あける。
        Assert.AreEqual(3, easy.Count);
        var normal = SaberChartDerive.Candidates(upper, SaberChartDerive.Rules.For("normal"), BeatAt120, new SaberChartDocument());
        Assert.AreEqual(5, normal.Count, "Normal は8分と同時2つまで残す");
    }

    [Test]
    public void CandidatesNeverDuplicateNotesAlreadyInTheChart()
    {
        var upper = new List<SaberChartNote> { N(0, "red", 1), N(1, "blue", -1) };
        var existing = new SaberChartDocument { notes = new List<SaberChartNote> { N(0, "red", 1) } };
        var candidates = SaberChartDerive.Candidates(upper, SaberChartDerive.Rules.For("easy"), BeatAt120, existing);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("blue", candidates[0].color);
    }

    [Test]
    public void AdoptingCandidatesAddsThemAsOneUndoStepAndKeepsHumanNotes()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"),
                "{\"bpm\":120,\"notes\":[{\"beat\":0,\"time\":0,\"x\":1,\"color\":\"red\"},{\"beat\":0.5,\"time\":250,\"x\":-1,\"color\":\"blue\"}," +
                "{\"beat\":1,\"time\":500,\"x\":-1,\"color\":\"blue\"}]}");
            File.WriteAllText(Path.Combine(folder, "chart_easy.json"),
                "{\"bpm\":120,\"notes\":[{\"beat\":3,\"time\":1500,\"x\":0,\"color\":\"gold\"}]}");
            Load(song, 0);
            Call("MakeDeriveCandidates");
            Assert.AreEqual(2, Get<List<SaberChartNote>>("deriveCandidates").Count);
            Assert.AreEqual(1, Document.notes.Count, "候補を出しただけでは譜面を変えない");
            Call("AdoptDeriveCandidates");
            Assert.AreEqual(3, Document.notes.Count);
            Assert.True(Document.notes.Exists(n => n.color == "gold" && n.time == 1500f), "人が置いたノーツは残す");
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(1, Document.notes.Count);
        });
    }

    [Test]
    public void RangeLimitsTheCandidatesToTheSelectedSpan()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"),
                "{\"bpm\":120,\"notes\":[{\"beat\":0,\"time\":0,\"x\":1,\"color\":\"red\"},{\"beat\":2,\"time\":1000,\"x\":-1,\"color\":\"blue\"}]}");
            Load(song, 0);
            Call("SetRange", 900f, 1100f);
            Call("MakeDeriveCandidates");
            var candidates = Get<List<SaberChartNote>>("deriveCandidates");
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(1000f, candidates[0].time);
        });
    }

    [TestCase(100f, 0f)]
    [TestCase(0f, 100f)]
    [TestCase(100f, 100f)]
    public void UpperOffsetProjectionAlignsOverlayCandidatesRangeAndUndo(float upperOffset, float currentOffset)
    {
        WithSongFolder((song, folder) =>
        {
            var upper = new SaberChartDocument { bpm = 120f, offsetMs = upperOffset,
                beatZeroMs = currentOffset - upperOffset, notes = new List<SaberChartNote> { N(2, "red", 1), N(3, "blue", -1) } };
            foreach (SaberChartNote note in upper.notes) note.time += currentOffset - upperOffset;
            var current = new SaberChartDocument { bpm = 120f, offsetMs = currentOffset,
                notes = new List<SaberChartNote> { N(0, "gold", 0) } };
            string upperPath = Path.Combine(folder, "chart_hard.json");
            string upperJson = SaberChartUtility.ToJson(upper, true);
            File.WriteAllText(upperPath, upperJson);
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), SaberChartUtility.ToJson(current, true));
            Load(song, 1);
            var cachedUpper = (SaberChartDocument)Call("UpperDocument");
            float[] originalTimes = cachedUpper.notes.Select(n => n.time).ToArray();
            Call("MakeDeriveCandidates");
            var all = Get<List<SaberChartNote>>("deriveCandidates");
            CollectionAssert.AreEqual(new[] { 1000f, 1500f }, all.Select(n => n.time).ToArray());
            CollectionAssert.AreEqual(new[] { 2f, 3f }, all.Select(n => n.beat).ToArray());
            Assert.AreEqual(1, Document.notes.Count, "候補の表示だけでは編集中のノーツも変えない");

            var lane = new Rect(20f, 30f, 800f, 600f);
            var projectedRect = (Rect)Call("UpperNoteRect", cachedUpper.notes[0], cachedUpper, lane);
            var currentRect = (Rect)Call("NoteRect", N(2, "red", 1), lane);
            Assert.AreEqual(currentRect.center.y, projectedRect.center.y, .001f, "同じ音源時刻の重ね表示を揃える");

            Call("SetRange", 1000f, 1500f);
            Call("MakeDeriveCandidates");
            var inRange = Get<List<SaberChartNote>>("deriveCandidates");
            Assert.AreEqual(1, inRange.Count, "投影後の開始境界は含み、終了境界は含まない");
            Assert.AreEqual(1000f, inRange[0].time);
            Assert.AreEqual(cachedUpper.notes[0].time + upperOffset, inRange[0].time + currentOffset, .001f);
            Call("AdoptDeriveCandidates");
            Assert.AreEqual(2, Document.notes.Count);
            Assert.True(Document.notes.Any(n => n.color == "gold" && n.time == 0));
            Call("MakeDeriveCandidates");
            Assert.AreEqual(0, Get<List<SaberChartNote>>("deriveCandidates").Count, "投影済みの同じスロットは重複採用しない");
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(1, Document.notes.Count, "採用を一度のUndoで戻せる");
            CollectionAssert.AreEqual(originalTimes, cachedUpper.notes.Select(n => n.time).ToArray(), "上位のキャッシュは変更しない");
            Assert.AreEqual(upperJson, File.ReadAllText(upperPath), "上位ファイルは変更しない");
        });
    }

    [Test]
    public void RealZeroUpperOffsetProducesCandidatesAtTheirOriginalAudioTimes()
    {
        // Zero の保存フォルダ名は「製作中」。実譜面は読み取り、架空の曲へ複写して確かめる。
        string source = Path.Combine(Application.streamingAssetsPath, "Songs", "製作中");
        string hardJson = File.ReadAllText(Path.Combine(source, "chart_hard.json"));
        string normalJson = File.ReadAllText(Path.Combine(source, "chart_normal.json"));
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), hardJson);
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), normalJson);
            Load(song, 1);
            var upper = (SaberChartDocument)Call("UpperDocument");
            Assert.AreEqual(110f, upper.offsetMs - Document.offsetMs, "OFFSET差がある実データの対照");
            string upperBefore = SaberChartUtility.ToJson(upper, false);
            string currentBefore = SaberChartUtility.ToJson(Document, false);
            Call("MakeDeriveCandidates");
            var candidates = Get<List<SaberChartNote>>("deriveCandidates");
            Assert.Greater(candidates.Count, 0, "未変換では全ノーツが現在の8分格子から外れて候補が0になる");
            var upperGroups = SaberChartLint.GroupSimultaneous(upper.notes);
            foreach (SaberChartNote note in candidates)
            {
                Assert.True(upper.notes.Any(n => Mathf.Abs(n.time + upper.offsetMs - note.time - Document.offsetMs) < .02f),
                    "候補の音源上の時刻は上位の打点を保つ");
                var group = upperGroups.First(g => g.Any(n => Mathf.Abs(n.time + upper.offsetMs - note.time - Document.offsetMs) < .02f));
                float groupTime = group[0].time + upper.offsetMs - Document.offsetMs;
                Assert.AreEqual((float)Call("BeatAtTime", groupTime), note.beat, .001f,
                    "候補の拍は、既存仕様どおり25ms同時組の先頭を現在の譜面で数え直す");
            }
            var lane = new Rect(20f, 30f, 800f, 600f);
            var overlay = (Rect)Call("UpperNoteRect", upper.notes[0], upper, lane);
            var own = (Rect)Call("NoteRect", Document.notes[0], lane);
            Assert.AreEqual(own.center.y, overlay.center.y, .01f, "実データの同じ先頭打点を重ねる");
            float[] firstTimes = candidates.Select(n => n.time).ToArray();
            Call("MakeDeriveCandidates");
            CollectionAssert.AreEqual(firstTimes, Get<List<SaberChartNote>>("deriveCandidates").Select(n => n.time).ToArray(),
                "繰り返し表示してもOFFSETを累積加算しない");
            Assert.AreEqual(upperBefore, SaberChartUtility.ToJson(upper, false));
            Assert.AreEqual(currentBefore, SaberChartUtility.ToJson(Document, false));
            Assert.AreEqual(hardJson, File.ReadAllText(Path.Combine(folder, "chart_hard.json")));
            Assert.AreEqual(normalJson, File.ReadAllText(Path.Combine(folder, "chart_normal.json")));
        });
        Assert.AreEqual(hardJson, File.ReadAllText(Path.Combine(source, "chart_hard.json")));
        Assert.AreEqual(normalJson, File.ReadAllText(Path.Combine(source, "chart_normal.json")));
    }

    // ---- 取6: 曲の設定の食い違い ----

    const string NormalSettings = "{\"bpm\":196,\"offsetMs\":0,\"beatZeroMs\":20,\"displayLevel\":5," +
        "\"timeSignatures\":[{\"beat\":0,\"numerator\":4,\"denominator\":4},{\"beat\":16,\"numerator\":7,\"denominator\":8}]," +
        "\"notes\":[{\"beat\":1,\"time\":326.1224,\"x\":1,\"color\":\"red\"}]}";
    const string HardSettings = "{\"bpm\":120,\"offsetMs\":110,\"beatZeroMs\":0,\"displayLevel\":0," +
        "\"notes\":[{\"beat\":1,\"time\":500,\"x\":-1,\"color\":\"blue\"},{\"beat\":3,\"time\":1500,\"x\":1,\"color\":\"red\"}]}";

    [Test]
    public void MismatchedSongSettingsAreListedPerDifficulty()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalSettings);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), HardSettings);
            Load(song, 2);
            var mismatch = (List<(string difficulty, List<string> differences)>)Call("SettingsMismatch");
            Assert.AreEqual(1, mismatch.Count);
            Assert.AreEqual("normal", mismatch[0].difficulty);
            string text = string.Join(" ", mismatch[0].differences);
            StringAssert.Contains("BPM 120 / 196", text);
            StringAssert.Contains("OFFSET 110 / 0ms", text);
            StringAssert.Contains("原点 0 / 20ms", text);
            StringAssert.Contains("拍子 0件 / 2件", text);
            Assert.False(window.hasUnsavedChanges, "比べるだけでは変えない");
        });
    }

    [Test]
    public void LoadingSettingsFromAnotherDifficultyKeepsNoteTimesAndCanBeUndone()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalSettings);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), HardSettings);
            Load(song, 2);
            Call("LoadSongSettingsFrom", "normal");
            Assert.AreEqual(196f, Document.bpm);
            Assert.AreEqual(0f, Document.offsetMs);
            Assert.AreEqual(20f, Document.beatZeroMs);
            Assert.AreEqual(2, Document.timeSignatures.Count);
            CollectionAssert.AreEqual(new[] { 500f, 1500f }, Document.notes.Select(n => n.time).ToArray(), "ノーツの時刻は変えない");
            Assert.True(window.hasUnsavedChanges);
            Assert.AreEqual(120f, ChartLoader.LoadFromFile(Path.Combine(folder, "chart_hard.json")).bpm, "保存するまでファイルは変えない");
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(120f, Document.bpm);
            Assert.False(window.hasUnsavedChanges);
        });
    }

    [Test]
    public void CopyingSettingsWritesOtherDifficultiesWithoutMovingTheirNotes()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalSettings);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), "{\"_comment\":\"Hard のメモ\"," + HardSettings.Substring(1));
            Load(song, 1);
            int asked = 0;
            Set("confirmSettingsCopy", new Func<string, string, bool>((a, b) => { asked++; return true; }));
            Call("CopySongSettingsToOtherDifficulties");
            Assert.AreEqual(1, asked);
            string hardText = File.ReadAllText(Path.Combine(folder, "chart_hard.json"));
            var hard = ChartLoader.Parse(hardText);
            Assert.AreEqual(196f, hard.bpm);
            Assert.AreEqual(0f, hard.offsetMs);
            Assert.AreEqual(20f, hard.beatZeroMs);
            Assert.AreEqual(2, hard.timeSignatures.Count);
            CollectionAssert.AreEqual(new[] { 500f, 1500f }, hard.notes.Select(n => n.time).ToArray());
            Assert.AreEqual(0, hard.displayLevel, "表示レベルは難易度ごとの値なので写さない");
            StringAssert.Contains("Hard のメモ", hardText, "相手の制作メモも残す");
            Assert.False(window.hasUnsavedChanges, "編集中の譜面は変えない");
            Assert.IsEmpty((List<(string difficulty, List<string> differences)>)Call("SettingsMismatch"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SongSettingsKeepBeatsAlreadyFollowingTempoMap(bool copyToOtherDifficulty)
    {
        WithSongFolder((song, folder) =>
        {
            const string mapped = ",\"offsetMs\":0,\"beatZeroMs\":0,\"editorTempoMap\":[{\"beat\":0,\"timeMs\":0},{\"beat\":8,\"timeMs\":4000}]," +
                "\"notes\":[{\"beat\":2,\"time\":1000,\"x\":-1,\"color\":\"blue\"},{\"beat\":4,\"time\":2000,\"x\":1,\"color\":\"red\"}]}";
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":150" + mapped);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), "{\"bpm\":120" + mapped);
            Load(song, copyToOtherDifficulty ? 1 : 2);
            SaberChartDocument result;
            if (copyToOtherDifficulty)
            {
                Set("confirmSettingsCopy", new Func<string, string, bool>((a, b) => true));
                Call("CopySongSettingsToOtherDifficulties");
                result = SaberChartUtility.FromJson(File.ReadAllText(Path.Combine(folder, "chart_hard.json")));
            }
            else
            {
                Call("LoadSongSettingsFrom", "normal");
                result = Document;
            }
            Assert.AreEqual(150f, result.bpm);
            Assert.AreEqual(2, result.tempoMap.Count);
            Assert.AreEqual(4000f, result.tempoMap[1].timeMs);
            CollectionAssert.AreEqual(new[] { 1000f, 2000f }, result.notes.Select(n => n.time).ToArray());
            CollectionAssert.AreEqual(new[] { 2f, 4f }, result.notes.Select(n => n.beat).ToArray(),
                "両方の譜面で同じテンポ地図に合った拍を、別のBPMの格子へ書き換えない");
            if (!copyToOtherDifficulty)
            {
                Key(KeyCode.Z, EventModifiers.Control);
                Assert.AreEqual(120f, Document.bpm);
                CollectionAssert.AreEqual(new[] { 2f, 4f }, Document.notes.Select(n => n.beat).ToArray());
            }
        });
    }

    // 地図が加わる・消えるときは、前後で同じ地図のときと違い、1つの格子に乗っていた拍を新しい BPM で数え直す。
    // 加わる地図は 150 BPM の直線なので、新しい地図で数え直すようにしても期待値は変わらない。
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SongSettingsRecountBeatsWhenTheTempoMapIsAddedOrRemoved(bool copyToOtherDifficulty, bool sourceHasMap)
    {
        WithSongFolder((song, folder) =>
        {
            const string settings = ",\"offsetMs\":0,\"beatZeroMs\":0";
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":150" + settings +
                (sourceHasMap ? ",\"editorTempoMap\":[{\"beat\":0,\"timeMs\":0},{\"beat\":8,\"timeMs\":3200}]" : "") +
                ",\"notes\":[{\"beat\":1,\"time\":400,\"x\":0,\"color\":\"red\"}]}");
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), "{\"bpm\":120" + settings +
                (sourceHasMap ? "" : ",\"editorTempoMap\":[{\"beat\":0,\"timeMs\":0},{\"beat\":8,\"timeMs\":4000}]") +
                ",\"notes\":[{\"beat\":2,\"time\":1000,\"x\":-1,\"color\":\"blue\"},{\"beat\":4,\"time\":2000,\"x\":1,\"color\":\"red\"}]}");
            Load(song, copyToOtherDifficulty ? 1 : 2);
            SaberChartDocument result;
            if (copyToOtherDifficulty)
            {
                Set("confirmSettingsCopy", new Func<string, string, bool>((a, b) => true));
                Call("CopySongSettingsToOtherDifficulties");
                result = SaberChartUtility.FromJson(File.ReadAllText(Path.Combine(folder, "chart_hard.json")));
            }
            else
            {
                Call("LoadSongSettingsFrom", "normal");
                result = Document;
            }
            Assert.AreEqual(150f, result.bpm);
            Assert.AreEqual(sourceHasMap ? 2 : 0, result.tempoMap.Count);
            CollectionAssert.AreEqual(new[] { 1000f, 2000f }, result.notes.Select(n => n.time).ToArray());
            CollectionAssert.AreEqual(new[] { 2.5f, 5f }, result.notes.Select(n => n.beat).ToArray(),
                "地図が加わる・消えるときは、1つの格子に乗っていた拍を新しい BPM で数え直す");
        });
    }

    // 点が1つの地図は BPM で格子が動くので、同じ地図でも拍を保つとは決めていない。今までどおり新しい BPM で数え直す。
    [TestCase(false)]
    [TestCase(true)]
    public void SongSettingsStillRecountBeatsWithTheSameSinglePointTempoMap(bool copyToOtherDifficulty)
    {
        WithSongFolder((song, folder) =>
        {
            const string mapped = ",\"offsetMs\":0,\"beatZeroMs\":0,\"editorTempoMap\":[{\"beat\":0,\"timeMs\":0}]," +
                "\"notes\":[{\"beat\":2,\"time\":1000,\"x\":-1,\"color\":\"blue\"},{\"beat\":4,\"time\":2000,\"x\":1,\"color\":\"red\"}]}";
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":150" + mapped);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), "{\"bpm\":120" + mapped);
            Load(song, copyToOtherDifficulty ? 1 : 2);
            SaberChartDocument result;
            if (copyToOtherDifficulty)
            {
                Set("confirmSettingsCopy", new Func<string, string, bool>((a, b) => true));
                Call("CopySongSettingsToOtherDifficulties");
                result = SaberChartUtility.FromJson(File.ReadAllText(Path.Combine(folder, "chart_hard.json")));
            }
            else
            {
                Call("LoadSongSettingsFrom", "normal");
                result = Document;
            }
            Assert.AreEqual(150f, result.bpm);
            Assert.AreEqual(1, result.tempoMap.Count);
            CollectionAssert.AreEqual(new[] { 1000f, 2000f }, result.notes.Select(n => n.time).ToArray());
            CollectionAssert.AreEqual(new[] { 2.5f, 5f }, result.notes.Select(n => n.beat).ToArray(),
                "点が1つの地図は、同じ地図でも今までどおり新しい BPM で数え直す(保つかは未決)");
        });
    }

    // ---- helpers ----

    SaberChartDocument Document => Get<SaberChartDocument>("document");

    void Load(string song, int difficulty)
    {
        Set("songId", song);
        Set("difficultyIndex", difficulty);
        Set("confirmChangesDialog", new Func<string, string, string, string, string, int>((a, b, c, d, e) => 2));
        Set("reportLoadError", new Action<string>(message => Assert.Fail(message)));
        Call("LoadDocument");
    }

    void WithSongFolder(Action<string, string> check)
    {
        string song = "__ChartLoadTest_" + Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
        string folder = Path.GetFullPath(Path.Combine(root, song));
        Directory.CreateDirectory(folder);
        try { check(song, folder); }
        finally
        {
            Assert.True(folder.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.True(Path.GetFileName(folder).StartsWith("__ChartLoadTest_"));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
            string backups = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups"));
            if (Directory.Exists(backups))
                foreach (string file in Directory.GetFiles(backups, song + "_*.json")) File.Delete(file);
        }
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
        var methods = typeof(SaberChartEditorWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToArray();
        if (methods.Length == 0) throw new MissingMethodException(name);
        try { return methods[0].Invoke(window, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }
}
