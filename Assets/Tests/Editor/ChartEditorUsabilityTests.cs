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

// 譜面エディターの使いやすさの改善(難易度の切り替え・問題の印と F8・範囲の絞り込みと拍そろえ・選択の後始末・
// 録音中の向き・知らせの履歴・曲情報の確認と重ねる保存・設定コピーの失敗の知らせ)を確かめる。架空の曲フォルダだけを使う。
public class ChartEditorUsabilityTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] PrefKeys = { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout", "ShowPlaybackPreview" };
    readonly Dictionary<string, object> savedPrefs = new Dictionary<string, object>();
    SaberChartEditorWindow window;
    bool savedTextEditing;
    int savedKeyboardControl;

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
        savedTextEditing = EditorGUIUtility.editingTextField;
        savedKeyboardControl = GUIUtility.keyboardControl;
        EditorPrefs.SetString(Prefix + "SongId", "__ChartEditorUsabilityTest");
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
        EditorGUIUtility.editingTextField = savedTextEditing;
        GUIUtility.keyboardControl = savedKeyboardControl;
    }

    const string NormalChart = "{\"bpm\":120,\"notes\":[{\"beat\":0,\"time\":0,\"x\":1,\"color\":\"red\"}," +
        "{\"beat\":2,\"time\":1000,\"x\":-1,\"color\":\"blue\"},{\"beat\":4,\"time\":2000,\"x\":1,\"color\":\"red\"}]}";
    const string HardChart = "{\"bpm\":120,\"notes\":[{\"beat\":1,\"time\":500,\"x\":0,\"color\":\"gold\"}]}";

    // ---- 難易度の切り替え ----

    [Test]
    public void HeaderSwitchOpensAnotherDifficultyAndKeepsTheChoiceWhenCancelled()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), HardChart);
            Load(song, 1);
            Call("SwitchEditingDifficulty", 2);
            Assert.AreEqual("hard", Get<string>("loadedDifficulty"));
            Assert.AreEqual(1, Document.notes.Count, "Hard の中身を開く");

            Document.notes[0].x = 2f;
            Set("confirmChangesDialog", new Func<string, string, string, string, string, int>((a, b, c, d, e) => 1));
            Call("SwitchEditingDifficulty", 0);
            Assert.AreEqual("hard", Get<string>("loadedDifficulty"), "保存の確認でやめたら切り替えない");
            Assert.AreEqual(2, Get<int>("difficultyIndex"), "「開く・作る」の選択も元のまま");
            Assert.AreEqual(2f, Document.notes[0].x, "編集を残す");
        });
    }

    // ---- 問題の印と F8 ----

    [Test]
    public void F8VisitsNotesWithProblemsInOrderAndWraps()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"notes\":[" +
                "{\"beat\":2,\"time\":1000,\"x\":-0.5,\"color\":\"blue\",\"type\":\"direction\",\"direction\":\"right\"}," +
                "{\"beat\":2,\"time\":1000,\"x\":0.5,\"color\":\"red\",\"type\":\"direction\",\"direction\":\"left\"}," +
                "{\"beat\":4,\"time\":2000,\"x\":1,\"color\":\"red\"}]}");
            Load(song, 1);
            int[] marks = (int[])Call("NoteIssueMarks");
            CollectionAssert.AreEqual(new[] { 2, 2, 0 }, marks, "内向きの同時押しはエラーの印、問題のないノーツは印なし");

            Key(KeyCode.F8);
            Assert.AreEqual(0, Get<int>("selectedIndex"));
            StringAssert.Contains("1/2", Get<string>("statusMessage"));
            StringAssert.Contains("内向き", Get<string>("statusMessage"), "理由も出す");
            Key(KeyCode.F8);
            Assert.AreEqual(1, Get<int>("selectedIndex"));
            Key(KeyCode.F8);
            Assert.AreEqual(0, Get<int>("selectedIndex"), "最後まで行ったら最初へ");
            StringAssert.Contains("最初に戻りました", Get<string>("statusMessage"));
            Key(KeyCode.F8, EventModifiers.Shift);
            Assert.AreEqual(1, Get<int>("selectedIndex"), "Shift+F8 は前へ(最初から最後へ戻る)");
        });
    }

    [Test]
    public void HoverInfoShowsHeightArrowAndTheProblem()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"notes\":[" +
                "{\"beat\":2,\"time\":1000,\"x\":-0.5,\"y\":1.5,\"color\":\"blue\",\"type\":\"direction\",\"direction\":\"right\"}," +
                "{\"beat\":2,\"time\":1000,\"x\":0.5,\"color\":\"red\",\"type\":\"direction\",\"direction\":\"left\"}]}");
            Load(song, 1);
            string text = (string)Call("NoteInfoText", Document.notes[0], 0);
            StringAssert.Contains("青 / 左手", text);
            StringAssert.Contains("方向 →", text);
            StringAssert.Contains("高さ 8/8", text, "TAP 以外でも高さが分かる");
            StringAssert.Contains("1.000秒", text);
            StringAssert.Contains("エラー: 内向き", text, "問題のあるノーツは理由も出す");
        });
    }

    // ---- 範囲の絞り込みと拍そろえ ----

    [Test]
    public void RangeColorFilterLimitsOperationsAndResetsWithTheRange()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"notes\":[" +
                "{\"beat\":0,\"time\":0,\"x\":1,\"color\":\"red\"},{\"beat\":0,\"time\":0,\"x\":-1,\"color\":\"blue\"}," +
                "{\"beat\":1,\"time\":500,\"x\":1,\"color\":\"red\"},{\"beat\":1,\"time\":500,\"x\":-1,\"color\":\"blue\"}]}");
            Load(song, 1);
            Call("SetRange", 0f, 1000f);
            Set("rangeColorFilter", 1);
            Assert.AreEqual(2, ((List<SaberChartNote>)Call("RangeNotes")).Count, "赤だけ");
            Call("DeleteSelection");
            CollectionAssert.AreEqual(new[] { "blue", "blue" }, Document.notes.Select(n => n.color).ToArray(), "青は残す");
            Call("ClearRange");
            Assert.AreEqual(0, Get<int>("rangeColorFilter"), "範囲を外したら全部に戻す");
            Key(KeyCode.Z, EventModifiers.Control);
            Assert.AreEqual(4, Document.notes.Count, "1回の Undo で戻せる");
        });
    }

    [Test]
    public void RangeSnapMovesOnlyOffGridNotesAndCanBeUndone()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"notes\":[" +
                "{\"beat\":0,\"time\":0,\"x\":1,\"color\":\"red\"},{\"beat\":0.28,\"time\":140,\"x\":-1,\"color\":\"blue\"}," +
                "{\"beat\":0.52,\"time\":260,\"x\":1,\"color\":\"red\"},{\"beat\":4,\"time\":2010,\"x\":1,\"color\":\"red\"}]}");
            Load(song, 1);
            Set("snapIndex", 3); // 16分
            Call("SetRange", 0f, 1000f);
            Call("SnapRangeToGrid");
            CollectionAssert.AreEqual(new[] { 0f, 125f, 250f, 2010f }, Document.notes.Select(n => n.time).ToArray(), "範囲の外は動かさない");
            StringAssert.Contains("2ノーツ", Get<string>("statusMessage"));
            StringAssert.Contains("15ms", Get<string>("statusMessage"), "いちばん大きく動いた量を出す");
            Key(KeyCode.Z, EventModifiers.Control);
            CollectionAssert.AreEqual(new[] { 0f, 140f, 260f, 2010f }, Document.notes.Select(n => n.time).ToArray());
        });
    }

    // ---- 選択の後始末 ----

    [Test]
    public void PageDownAndEscapeDoNotLeaveAStaleSelection()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
            Load(song, 1);
            Set("selectedIndex", 0);
            Key(KeyCode.PageDown);
            KeyUp(KeyCode.PageDown);
            Assert.AreEqual(2f, Get<float>("currentBeat"), .001f);
            Assert.AreEqual(-1, Get<int>("selectedIndex"), "画面の外に残る選択は外す");

            Set("selectedIndex", 2);
            Key(KeyCode.PageDown);
            KeyUp(KeyCode.PageDown);
            Assert.AreEqual(4f, Get<float>("currentBeat"), .001f);
            Assert.AreEqual(2, Get<int>("selectedIndex"), "移った先のノーツの選択は残す");

            Key(KeyCode.Escape);
            Assert.AreEqual(-1, Get<int>("selectedIndex"), "範囲がなければ Esc で選択を外す");
        });
    }

    [Test]
    public void ChoosingADirectionForATapMakesItAnArrowNote()
    {
        Func<string, string, string> next = (type, direction) => (string)typeof(SaberChartEditorWindow)
            .GetMethod("TypeAfterDirectionEdit", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { type, direction });
        Assert.AreEqual("direction", next("tap", "down"), "向きを選んだ TAP は方向ノーツ(本編は向きで判定する)");
        Assert.AreEqual("tap", next("direction", "none"), "向きを外した方向ノーツは TAP");
        Assert.AreEqual("long", next("long", "down"), "LONG は LONG のまま");
    }

    // ---- 録音・ステップ中の向き ----

    [Test]
    public void AltLettersSetDirectionsWhileLettersAreRecordingKeys()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
            Load(song, 1);
            // ステップ入力では D F G J K が配置のキー。向きは今の拍のノーツに付く。
            Set("recordMode", true);
            Set("recordStepMode", true);
            Set("directionAdvance", false);
            Set("currentBeat", 0f);
            Key(KeyCode.W);
            KeyUp(KeyCode.W);
            Assert.AreEqual("none", Document.notes[0].direction, "ステップの画面では文字だけでは向きにしない");
            Key(KeyCode.W, EventModifiers.Alt);
            KeyUp(KeyCode.W);
            Assert.AreEqual("up", Document.notes[0].direction, "Alt+W で上(テンキーの無いノートPC向け)");
            Assert.AreEqual("direction", Document.notes[0].type);
            Key(KeyCode.D, EventModifiers.Alt);
            Assert.AreEqual("right", Document.notes[0].direction, "Alt+D は配置のキー D ではなく右向き");
            Assert.AreEqual(3, Document.notes.Count, "Alt を押したままでは配置しない");
        });
    }

    // ---- 知らせ ----

    [Test]
    public void StatusHistoryKeepsRecentMessagesAndMergesNumberOnlyChanges()
    {
        Get<List<(string time, string message)>>("statusHistory").Clear();
        Call("SetStatus", "ズーム 120 px/拍");
        Call("SetStatus", "ズーム 137 px/拍");
        Call("SetStatus", "保存: chart_normal.json");
        var history = Get<List<(string time, string message)>>("statusHistory");
        CollectionAssert.AreEqual(new[] { "保存: chart_normal.json", "ズーム 137 px/拍" }, history.Select(item => item.message).ToArray());
        for (int i = 0; i < 30; i++) Call("SetStatus", "知らせ" + new string('あ', i % 7) + "です");
        Assert.LessOrEqual(Get<List<(string time, string message)>>("statusHistory").Count, 15);
        double shortSeconds = (double)Static("StatusSeconds", "保存しました");
        double longSeconds = (double)Static("StatusSeconds", new string('あ', 90));
        Assert.AreEqual(3.0, shortSeconds, .5);
        Assert.Greater(longSeconds, 8.0, "長い知らせは読み切れるまで出す");
    }

    [Test]
    public void SaveFailuresExplainTheNextStep()
    {
        string busy = (string)Static("SaveFailureMessage", new IOException("使用中"));
        StringAssert.Contains("使用中", busy);
        StringAssert.Contains("ほかのアプリ", busy);
        StringAssert.Contains("この窓に残っています", busy);
        StringAssert.Contains("読み取り専用", (string)Static("SaveFailureMessage", new UnauthorizedAccessException("拒否")));
    }

    // ---- 曲情報 ----

    const string StageText = "{\n  \"schemaVersion\": 1,\n  \"displayName\": \"A\",\n  \"composer\": \"X\",\n" +
        "  \"timeOrigin\": \"audio-file-start\",\n  \"previewStartSeconds\": 0,\n  \"sections\": [\n" +
        "    { \"label\": \"サビ1\", \"startSeconds\": 10, \"endSeconds\": 20, \"intensity\": 0.8 },\n" +
        "    { \"label\": \"サビ2\", \"startSeconds\": 30, \"endSeconds\": 40, \"intensity\": 0.8 }\n  ]\n}\n";

    [Test]
    public void RemovingASectionAndReloadingAskFirst()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
            File.WriteAllText(Path.Combine(folder, "stage.json"), StageText);
            Load(song, 1);
            Call("LoadSongInfo", song);
            bool answer = false;
            int asked = 0;
            Set("confirmSongInfoAction", new Func<string, string, string, bool>((title, message, ok) => { asked++; return answer; }));
            Assert.False((bool)Call("RemoveSongSection", 0));
            Assert.AreEqual(2, SongInfo.Sections.Count, "キャンセルなら消さない");
            answer = true;
            Assert.True((bool)Call("RemoveSongSection", 0));
            Assert.AreEqual("サビ2", SongInfo.Sections.Single().Label);
            answer = false;
            Assert.False((bool)Call("ReloadSongInfoFromFile"));
            Assert.AreEqual(1, SongInfo.Sections.Count, "未保存の変更を黙って捨てない");
            answer = true;
            Assert.True((bool)Call("ReloadSongInfoFromFile"));
            Assert.AreEqual(2, SongInfo.Sections.Count);
            Assert.AreEqual(4, asked);
        });
    }

    [Test]
    public void OutsideStageChangesReloadWhenCleanAndMergeOnSaveWhenEdited()
    {
        WithSongFolder((song, folder) =>
        {
            string stage = Path.Combine(folder, "stage.json");
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
            File.WriteAllText(stage, StageText);
            Load(song, 1);
            Call("LoadSongInfo", song);

            File.WriteAllText(stage, StageText.Replace("\"X\"", "\"Y\""));
            Call("CheckStageForExternalChanges");
            Assert.AreEqual("Y", SongInfo.Composer, "未編集なら最新を読み直す");

            SongInfo.DisplayName = "B";
            File.WriteAllText(stage, StageText.Replace("\"X\"", "\"Z\""));
            Call("CheckStageForExternalChanges");
            Assert.True(Get<bool>("stageChangedOutside"), "編集中は読み直さずに知らせる");
            Assert.AreEqual("B", SongInfo.DisplayName);

            Assert.True((bool)Call("SaveSongInfo"));
            SaberStageInfo saved = SaberStageInfo.Parse(File.ReadAllText(stage));
            Assert.AreEqual("B", saved.DisplayName, "ここで変えた項目は書く");
            Assert.AreEqual("Z", saved.Composer, "ほかで直した項目は残す");
            Assert.AreEqual(2, saved.Sections.Count);
            StringAssert.Contains("表示名", Get<string>("statusMessage"));
        });
    }

    [Test]
    public void MergingKeepsTheOtherToolsFormatAndPlacesNewKeysInOrder()
    {
        var mine = SaberStageInfo.Parse("{\n  \"displayName\": \"A\",\n  \"composer\": \"X\",\n  \"timeOrigin\": \"audio-file-start\",\n  \"sections\": []\n}\n");
        mine.DisplayName = "B";
        mine.Reading = "よみ";
        string latest = "{\n    \"displayName\": \"A\",\n    \"composer\": \"Y\",\n    \"timeOrigin\": \"audio-file-start\",\n" +
                        "    \"sections\": [ { \"label\": \"サビ\", \"startSeconds\": 10, \"endSeconds\": 20 } ]\n}\n";
        SaberStageInfo merged = mine.MergeOnto(latest, out List<string> keys);
        Assert.AreEqual("B", merged.DisplayName);
        Assert.AreEqual("よみ", merged.Reading);
        Assert.AreEqual("Y", merged.Composer, "相手が直した項目は残す");
        Assert.AreEqual(1, merged.Sections.Count, "相手が足した区間も残す");
        CollectionAssert.AreEquivalent(new[] { "displayName", "displayReading" }, keys);
        string text = merged.Write();
        StringAssert.Contains("    \"composer\"", text, "相手の字下げを保つ");
        Assert.Less(text.IndexOf("displayReading", StringComparison.Ordinal), text.IndexOf("timeOrigin", StringComparison.Ordinal));
    }

    // ---- 設定のコピー ----

    [Test]
    public void CopyingSettingsContinuesPastAFailureAndListsIt()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":196,\"notes\":[{\"beat\":1,\"time\":306.1224,\"x\":1,\"color\":\"red\"}]}");
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), HardChart);
            File.WriteAllText(Path.Combine(folder, "chart_easy.json"), "{ broken JSON");
            Load(song, 1);
            string reported = null;
            Set("confirmSettingsCopy", new Func<string, string, bool>((a, b) => true));
            Set("reportSettingsCopyProblem", new Action<string, string>((title, message) => reported = message));
            Call("CopySongSettingsToOtherDifficulties");
            Assert.AreEqual(196f, ChartLoader.LoadFromFile(Path.Combine(folder, "chart_hard.json")).bpm, "読めない Easy のあとも Hard へ書く");
            Assert.AreEqual("{ broken JSON", File.ReadAllText(Path.Combine(folder, "chart_easy.json")), "読めないファイルは変えない");
            StringAssert.Contains("書けた難易度: Hard", reported);
            StringAssert.Contains("Easy: ファイルを読めませんでした", reported);
            StringAssert.Contains("一部コピーできませんでした", Get<string>("statusMessage"));
        });
    }

    // ---- 録音のずれ ----

    [Test]
    public void DeviationNearHalfTheGridIsNotOfferedAsACorrection()
    {
        var chart = new SaberChartDocument { bpm = 190 };
        var take = new SaberChartRecorder(chart, 60, 16, 0, false, 2);
        float step = 60000f / 190f / 4f;
        for (int i = 0; i < 12; i++) take.Press(100 + i, ((26 + 2 * i) * step + 50f) / 1000f, i % 2 == 0 ? -1f : 1f, 0f, "gold");
        SaberChartLatency.Summary summary = SaberChartLatency.Summarize(take.Deviations);
        Assert.AreEqual(step, take.MinGridStepMs, .5f);
        Assert.Less(summary.Median, 0f, "BPM190 の16分では、遅れ +50ms が早さに見える");
        Assert.True(SaberChartLatency.AmbiguousAgainstGrid(summary.Median, take.MinGridStepMs), "この中央値は補正に使わない");
        Assert.False(SaberChartLatency.AmbiguousAgainstGrid(10f, take.MinGridStepMs), "格子の1/4より小さいずれは使える");
        Assert.False(SaberChartLatency.AmbiguousAgainstGrid(25f, float.PositiveInfinity), "格子が分からないときは今までどおり");
    }

    // ---- helpers ----

    SaberChartDocument Document => Get<SaberChartDocument>("document");
    SaberStageInfo SongInfo => Get<SaberStageInfo>("songInfo");

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
                foreach (string file in Directory.GetFiles(backups, song + "_*")) File.Delete(file);
        }
    }

    void Key(KeyCode key, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
    void KeyUp(KeyCode key) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyUp, keyCode = key });
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
    static object Static(string name, params object[] args)
    {
        MethodInfo method = typeof(SaberChartEditorWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ??
                            throw new MissingMethodException(name);
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }
}
