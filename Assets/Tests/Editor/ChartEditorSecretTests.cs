using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// 譜面エディターで裏譜面(本編の RushESecretState.Charts)を開いて配置を直すとき、ノーツごとの仕掛け(エディターの知らない項目)を
// 壊さないことと、裏譜面で使えない操作を止めることを確かめる。実際の裏譜面は読むだけ。書き込みは架空の曲フォルダだけに行う。
public class ChartEditorSecretTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] PrefKeys = { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout", "ShowPlaybackPreview", "SecretDifficulty" };
    static readonly string[] StringPrefKeys = { "SongId", "RecordingLayout", "SecretDifficulty" };
    static readonly Type Store = typeof(SaberChartEditorWindow).Assembly.GetType("Saber.ChartEditor.SaberChartFileStore");
    static readonly Type Drafts = typeof(SaberChartEditorWindow).Assembly.GetType("Saber.ChartEditor.SaberChartDrafts");
    static readonly FieldInfo TableOverride = typeof(SaberChartSecrets).GetField("tableOverride", BindingFlags.NonPublic | BindingFlags.Static);
    // エディターが知っているノーツの項目。これ以外が「知らない項目」(仕掛け)。
    static readonly string[] KnownNoteKeys = { "beat", "time", "x", "y", "type", "color", "direction", "count", "lengthMs" };
    // エディターが自分の形で書き直す上の段の項目(ほかの項目は元の綴りのまま残る)。
    static readonly string[] RewrittenRootKeys =
        { "bpm", "coordScale", "offsetMs", "displayLevel", "beatZeroMs", "timeSignatures", "notes", "editorMarkers", "editorTempoMap" };
    const string NormalChart = "{\"bpm\":180,\"notes\":[{\"beat\":1,\"time\":333.3333,\"x\":0.6,\"y\":0.3,\"color\":\"red\"}]}";

    readonly Dictionary<string, object> savedPrefs = new Dictionary<string, object>();
    readonly List<string> tempSongs = new List<string>();
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
                : StringPrefKeys.Contains(key) ? (object)EditorPrefs.GetString(full)
                : key == "Zoom" ? (object)EditorPrefs.GetFloat(full) : EditorPrefs.GetInt(full);
        }
        EditorPrefs.SetString(Prefix + "SongId", "__ChartSecretTest_none");
        EditorPrefs.SetInt(Prefix + "Difficulty", 1);
        EditorPrefs.SetInt(Prefix + "Snap", 3);
        draftRoot = Path.Combine(Path.GetTempPath(), "__ChartSecretDraft_" + Guid.NewGuid().ToString("N"));
        Drafts.GetField("rootOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, draftRoot);
        savedTextEditing = EditorGUIUtility.editingTextField;
        EditorGUIUtility.editingTextField = false;
    }

    [TearDown]
    public void TearDown()
    {
        CloseWindow();
        EditorGUIUtility.editingTextField = savedTextEditing;
        TableOverride.SetValue(null, null);
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
        string root = SongsRoot + Path.DirectorySeparatorChar;
        string backups = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups"));
        foreach (string song in tempSongs)
        {
            string folder = Path.GetFullPath(Path.Combine(root, song));
            Assert.True(folder.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.True(song.StartsWith("__ChartSecretTest_"));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
            if (Directory.Exists(backups))
                foreach (string file in Directory.GetFiles(backups, song + "_*.json")) File.Delete(file);
        }
        // 保存(SaberChartFileStore.Save)が取り込んだ架空の曲フォルダを、アセットの一覧からも消す。
        if (tempSongs.Count > 0) AssetDatabase.Refresh();
        tempSongs.Clear();
    }

    // ---- 実際の裏譜面を読んで書き出す ----

    // 何も変えずに書き出すと、各ノーツの知らない項目が値の綴りまで同じで、知っている項目(回数 1000 など)も同じ。
    [TestCase("ManicSonic", "r")]
    [TestCase("Aobasou", "w")]
    [TestCase("escapade", "l")]
    [TestCase("揺籠", "p")]
    [TestCase("BenkyouRemaster", "q")]
    [TestCase("Morning", "m")]
    [TestCase("製作中", "v")]
    [TestCase("Epilogue", "f")]
    [TestCase("ElDorado", "g")]
    public void RealSecretChartRoundTripKeepsEveryNoteField(string song, string difficulty)
    {
        string original = File.ReadAllText(RealChart(song, difficulty));
        string saved = SaberChartUtility.ToFileJson(SaberChartUtility.FromJson(original));
        List<string> before = NoteRecords(original), after = NoteRecords(saved);
        Assert.Greater(before.Count, 0);
        CollectionAssert.AreEqual(before, after, "ノーツの項目(知らない項目は値の綴りまで)が同じ");
        CollectionAssert.AreEqual(GameNotes(original), GameNotes(saved), "本編が読むノーツの中身も同じ");
        if (difficulty != "g")
            Assert.True(before.Any(record => record.Split('§')[1].Length > 0), "この裏譜面にはノーツの仕掛けがある");
    }

    // ノーツに仕掛けの無い裏譜面(仕掛けは上の段だけ)も、書き出しでノーツが変わらない。D はノーツが無い(14曲全部をどちらかで見る)。
    [TestCase("RushE", "e")]
    [TestCase("2_23_AM", "y")]
    [TestCase("Andalusia", "t")]
    [TestCase("logic", "s")]
    [TestCase("Phobia PARTⅠ", "d")]
    public void SecretChartsWithoutNoteMechanicsRoundTripUnchanged(string song, string difficulty)
    {
        string original = File.ReadAllText(RealChart(song, difficulty));
        string saved = SaberChartUtility.ToFileJson(SaberChartUtility.FromJson(original));
        CollectionAssert.AreEqual(NoteRecords(original), NoteRecords(saved), "ノーツの項目が同じ");
        CollectionAssert.AreEqual(GameNotes(original), GameNotes(saved), "本編が読むノーツの中身も同じ");
    }

    // 上の段の quiz・reverse などは今までどおり残り、ToFileJson だけでは _editorEditedAt を付けない(保存のときだけ)。
    [TestCase("BenkyouRemaster", "q", "quiz")]
    [TestCase("ManicSonic", "r", "reverse")]
    [TestCase("escapade", "l", "_lBuild")]
    [TestCase("揺籠", "p", "_pBuild")]
    [TestCase("2_23_AM", "y", "dark")]
    [TestCase("Andalusia", "t", "bull")]
    [TestCase("logic", "s", "scroll")]
    [TestCase("Phobia PARTⅠ", "d", "aim")]
    [TestCase("Epilogue", "f", "fall")]
    [TestCase("Morning", "m", "memory")]
    public void RootFieldsOfRealSecretChartsStayTheSame(string song, string difficulty, string mechanic)
    {
        string original = File.ReadAllText(RealChart(song, difficulty));
        JsonNode before = JsonNode.Parse(original), after = JsonNode.Parse(SaberChartUtility.ToFileJson(SaberChartUtility.FromJson(original)));
        Assert.NotNull(before[mechanic]);
        foreach (var member in before.Members.Where(member => !RewrittenRootKeys.Contains(member.Key)))
            Assert.AreEqual(member.Value.Canonical(), after[member.Key]?.Canonical(), member.Key);
        Assert.IsNull(after[SaberChartUtility.EditedAtKey]);
    }

    // あるノーツの x・time を変えて書き出すと、そのノーツの知らない項目は残り、ほかのノーツは同じ。
    [Test]
    public void MovingAMirrorNoteKeepsItsFieldsAndLeavesOtherNotesAlone()
    {
        string original = File.ReadAllText(RealChart("ManicSonic", "r"));
        SaberChartDocument document = SaberChartUtility.FromJson(original);
        SaberChartNote moved = document.notes.First(note => SaberChartUtility.NoteExtraFlag(note, "mirror"));
        string fields = moved.extraJson;
        moved.time += 10f;
        moved.x = -moved.x;
        List<string> before = NoteRecords(original), after = NoteRecords(SaberChartUtility.ToFileJson(document));
        List<string> gone = before.Except(after).ToList(), added = after.Except(before).ToList();
        Assert.AreEqual(1, gone.Count, "変わったのは動かした1個だけ");
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(gone[0].Split('§')[1], added[0].Split('§')[1], "動かしたノーツの知らない項目はそのまま");
        StringAssert.Contains("mirror=true", added[0]);
        SaberChartNote reloaded = SaberChartUtility.FromJson(SaberChartUtility.ToFileJson(document)).notes
            .Single(note => Mathf.Abs(note.time - moved.time) < .001f && Mathf.Abs(note.x - moved.x) < .0001f && note.color == moved.color);
        Assert.AreEqual(fields, reloaded.extraJson);
    }

    [TestCase("escapade", "l", 1000)]
    [TestCase("ElDorado", "g", 100)]
    public void LongCountsAboveNinetyNineSurviveLoadingAndSaving(string song, string difficulty, int count)
    {
        string original = File.ReadAllText(RealChart(song, difficulty));
        SaberChartDocument document = SaberChartUtility.FromJson(original);
        Assert.True(document.notes.Any(note => note.count == count), "読み込みで 99 に丸めない");
        ChartData game = ChartLoader.Parse(SaberChartUtility.ToFileJson(document));
        Assert.True(game.notes.Any(note => note.count == count), "書き出しでも 99 に丸めない");
        Assert.AreEqual(ChartLoader.Parse(original).notes.Max(note => note.count), game.notes.Max(note => note.count));
    }

    // ---- ノーツの知らない項目の持ち方 ----

    [Test]
    public void NewNotesHaveNoUnknownFieldsAndClonesCopyThem()
    {
        Assert.AreEqual(string.Empty, new SaberChartNote().extraJson);
        var document = new SaberChartDocument { notes = { new SaberChartNote { time = 500, x = 1, color = "blue" } } };
        JsonNode note = JsonNode.Parse(SaberChartUtility.ToFileJson(document))["notes"].Items.Single();
        CollectionAssert.AreEquivalent(KnownNoteKeys, note.Members.Select(member => member.Key).ToArray(), "新しいノーツには知らない項目を書かない");

        var withFields = new SaberChartNote { time = 1000, x = .6f, y = .3f, color = "red", extraJson = "{\"mirror\": true, \"windX\": -3.0}" };
        Assert.AreEqual(withFields.extraJson, withFields.Clone().extraJson);
        document.notes.Add(withFields);
        SaberChartDocument cloned = SaberChartUtility.Clone(document);
        Assert.AreEqual(withFields.extraJson, cloned.notes.Single(n => n.time == 1000).extraJson, "譜面の複製(履歴・下書きと同じ JSON)でも残る");
        Assert.AreEqual(string.Empty, cloned.notes.Single(n => n.time == 500).extraJson);
        // 知らない項目のない譜面は、比較・履歴の JSON に空の項目を書かない(今までと同じ文字)。
        StringAssert.DoesNotContain("extraJson", SaberChartUtility.ToJson(new SaberChartDocument { notes = { new SaberChartNote() } }, false));
        StringAssert.DoesNotContain("extraJson", SaberChartUtility.ToJson(new SaberChartDocument { notes = { new SaberChartNote() } }, true));
        StringAssert.Contains("mirror", SaberChartUtility.ToJson(document, false));
    }

    // null のノーツが混ざった譜面でも、知らない項目が正しいノーツに付く(null を外す前に、同じ位置の対応で移す)。
    [Test]
    public void NullNotesDoNotShiftUnknownFieldsToOtherNotes()
    {
        const string json = "{\"bpm\":120,\"notes\":[null," +
            "{\"time\":1000,\"x\":0.6,\"y\":0.3,\"color\":\"red\",\"mirror\":true},null," +
            "{\"time\":500,\"x\":-1,\"y\":0,\"color\":\"blue\",\"windX\":-3.0,\"windY\":0.25},null]}";
        SaberChartDocument document = SaberChartUtility.FromJson(json);
        Assert.AreEqual(2, document.notes.Count);
        Assert.AreEqual(500f, document.notes[0].time);
        Assert.AreEqual("{\"windX\": -3.0, \"windY\": 0.25}", document.notes[0].extraJson);
        Assert.AreEqual("{\"mirror\": true}", document.notes[1].extraJson);
        List<JsonNode> notes = JsonNode.Parse(SaberChartUtility.ToFileJson(document))["notes"].Items;
        Assert.AreEqual(2, notes.Count, "null を本編のノーツにしない");
        Assert.AreEqual("-3.0", notes[0]["windX"].Raw, "数の書き方もそのまま");
        Assert.IsNull(notes[0]["mirror"]);
        Assert.AreEqual("true", notes[1]["mirror"].Raw);
        Assert.IsNull(notes[1]["windX"]);
        ChartData game = ChartLoader.Parse(SaberChartUtility.ToFileJson(document));
        Assert.AreEqual(-3f, game.notes[0].windX);
        Assert.AreEqual(.25f, game.notes[0].windY);
        Assert.AreEqual(0f, game.notes[1].windX);
    }

    // ---- 保存先と _editorEditedAt ----

    [Test]
    public void SavingASecretChartWritesOnlyItsOwnFileWithTheEditedTime()
    {
        string song = NewSong(), folder = Folder(song);
        string secretPath = Path.Combine(folder, "chart_r.json");
        File.Copy(RealChart("ManicSonic", "r"), secretPath);
        File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
        File.WriteAllText(Path.Combine(folder, "chart.json"), NormalChart);
        string original = File.ReadAllText(secretPath);
        SaberChartDocument document = SaberChartUtility.FromJson(original);
        SaberChartNote moved = document.notes.First(note => SaberChartUtility.NoteExtraFlag(note, "mirror"));
        moved.x = -moved.x;

        string destination = Save(document, song, "r");
        Assert.AreEqual(Path.GetFullPath(secretPath), Path.GetFullPath(destination));
        Assert.AreEqual(NormalChart, File.ReadAllText(Path.Combine(folder, "chart.json")), "裏譜面は chart.json に書かない");
        Assert.AreEqual(NormalChart, File.ReadAllText(Path.Combine(folder, "chart_normal.json")));
        string saved = File.ReadAllText(secretPath);
        JsonNode root = JsonNode.Parse(saved);
        Assert.True(DateTimeOffset.TryParseExact(Unquote(root[SaberChartUtility.EditedAtKey].Raw), "yyyy-MM-dd'T'HH:mm:sszzz",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "保存した日時を ISO 8601 で書く");
        Assert.IsNull(document.extraFields.Find(field => field.key == SaberChartUtility.EditedAtKey), "編集中の譜面(比較用の JSON)には入れない");
        // 触っていない上の段は元のファイルの文字のまま(改行だけはそろえる)。
        foreach (string key in new[] { "reverse", "_comment", "bpm", "editorTempoMap", "timeSignatures" })
            Assert.AreEqual(RawText(original, key), RawText(saved, key), key);
        Assert.AreEqual(NoteRecords(original).Count, NoteRecords(saved).Count);
        Assert.AreEqual(1, NoteRecords(original).Except(NoteRecords(saved)).Count(), "動かした1個だけが変わる");

        Save(document, song, "r");
        Assert.AreEqual(1, JsonNode.Parse(File.ReadAllText(secretPath)).Members.Count(member => member.Key == SaberChartUtility.EditedAtKey),
            "2回目以降は上書き");
        Save(document, song, "hard");
        Assert.IsNull(JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "chart_hard.json")))[SaberChartUtility.EditedAtKey], "表の譜面には書かない");
    }

    // 上の段の bpm などは、触っていなければ元の文字のまま(float に丸めない)。
    [Test]
    public void UntouchedBpmKeepsItsOriginalDigits()
    {
        string song = NewSong(), folder = Folder(song);
        string path = Path.Combine(folder, "chart_p.json");
        File.Copy(RealChart("揺籠", "p"), path);
        SaberChartDocument document = SaberChartUtility.FromJson(File.ReadAllText(path));
        document.notes[0].x += .01f;
        Save(document, song, "p");
        Assert.AreEqual("57.333333333333336", RawText(File.ReadAllText(path), "bpm"));
        Assert.AreEqual(document.notes.Count, ChartLoader.Parse(File.ReadAllText(path)).notes.Count);
    }

    // ---- 触っていないノーツの元の文字(数を float に丸めずに比べる) ----

    // 裏譜面のファイルとして読む(元の文字を持たせる)と、書き出しで全ノーツが元の項目の並びと綴りのまま。
    [TestCase("RushE", "e")]
    [TestCase("ElDorado", "g")]
    [TestCase("Phobia PARTⅠ", "d")]
    [TestCase("Morning", "m")]
    [TestCase("Aobasou", "w")]
    [TestCase("logic", "s")]
    [TestCase("製作中", "v")]
    [TestCase("Andalusia", "t")]
    [TestCase("2_23_AM", "y")]
    [TestCase("Epilogue", "f")]
    [TestCase("ManicSonic", "r")]
    [TestCase("escapade", "l")]
    [TestCase("揺籠", "p")]
    [TestCase("BenkyouRemaster", "q")]
    public void UntouchedNotesKeepTheirOriginalText(string song, string difficulty)
    {
        string original = File.ReadAllText(RealChart(song, difficulty));
        string saved = SaberChartUtility.ToFileJson(SaberChartUtility.FromJson(original, true));
        CollectionAssert.AreEqual(NoteTexts(original), NoteTexts(saved), "ノーツの文字(項目の並び・数の綴り)が同じ");
    }

    // あるノーツを動かしても、ほかのノーツは元の文字のまま。動かしたノーツも、変えていない項目は元の綴り。
    [Test]
    public void EditingOneNoteKeepsTheOthersAndItsUntouchedFieldsAsWritten()
    {
        string original = File.ReadAllText(RealChart("Aobasou", "w"));
        JsonNode precise = JsonNode.Parse(original)["notes"].Items.First(note => note["beat"]?.Raw == "31.999999884234256");
        SaberChartDocument document = SaberChartUtility.FromJson(original, true);
        Assert.True(document.notes.All(note => note.sourceJson.Length > 0), "裏譜面のノーツはどれも元の文字を持つ");
        SaberChartNote moved = document.notes.First(note => Mathf.Abs(note.time - float.Parse(precise["time"].Raw, CultureInfo.InvariantCulture)) > 1f);
        float movedTime = moved.time, movedY = moved.y;
        string movedColor = moved.color;
        moved.x += .25f;
        string saved = SaberChartUtility.ToFileJson(document);
        List<string> before = NoteTexts(original), after = NoteTexts(saved);
        Assert.AreEqual(1, before.Except(after).Count(), "変わったのは動かした1個だけ");
        Assert.Contains(precise.Canonical(), after, "触っていない 31.999999884234256 のノーツは元のまま");
        JsonNode written = JsonNode.Parse(saved)["notes"].Items.Single(note =>
            Mathf.Abs(float.Parse(note["time"].Raw, CultureInfo.InvariantCulture) - movedTime) < .001f &&
            Mathf.Abs(float.Parse(note["y"].Raw, CultureInfo.InvariantCulture) - movedY) < .0001f &&
            Unquote(note["color"].Raw) == movedColor && Mathf.Abs(float.Parse(note["x"].Raw, CultureInfo.InvariantCulture) - moved.x) < .0001f);
        JsonNode source = JsonNode.Parse(original)["notes"].Items.Single(note => before.Except(after).Single() == note.Canonical());
        foreach (var member in source.Members.Where(member => member.Key != "x"))
            Assert.AreEqual(member.Value.Canonical(), written[member.Key]?.Canonical(), "変えていない " + member.Key + " は元の綴り");
        Assert.AreNotEqual(source["x"].Raw, written["x"].Raw);
        Assert.AreEqual(1, SaberChartUtility.ToJson(SaberChartUtility.Clone(document), false).Split(new[] { "31.999999884234256" }, StringSplitOptions.None).Length - 1,
            "元の文字は譜面の複製(Undo・下書きと同じ JSON)でも残る");
    }

    // 裏譜面の保存(読み込み → 1個動かす → 保存)でも、触っていないノーツの綴りが残る。表の譜面は元の文字を持たない(今までどおり)。
    [Test]
    public void SavingASecretChartKeepsUntouchedNoteDigitsButNormalChartsAreUnchanged()
    {
        string song = NewSong(), folder = Folder(song);
        string path = Path.Combine(folder, "chart_w.json");
        File.Copy(RealChart("Aobasou", "w"), path);
        string original = File.ReadAllText(path);
        var document = (SaberChartDocument)StoreLoad(song, "w");
        Assert.True(document.notes.All(note => note.sourceJson.Length > 0));
        document.notes[0].x += .25f;
        Save(document, song, "w");
        string saved = File.ReadAllText(path);
        StringAssert.Contains("\"beat\": 31.999999884234256", saved);
        Assert.AreEqual(1, NoteTexts(original).Except(NoteTexts(saved)).Count());

        var hard = (SaberChartDocument)StoreLoad("Aobasou", "hard");
        Assert.True(hard.notes.All(note => note.sourceJson.Length == 0), "表の譜面は元の文字を持たない");
        StringAssert.DoesNotContain("sourceJson", SaberChartUtility.ToJson(hard, false));
        Assert.True(SaberChartUtility.FromJson(original).notes.All(note => note.sourceJson.Length == 0), "ふつうの読み込みでは持たない");
    }

    // 元のファイルにあって、エディターが書かない上の段の項目(空の editorMarkers など)は、触っていなければ元のまま残る。
    [Test]
    public void EmptyRootArraysThatTheEditorOmitsStayInTheFile()
    {
        string song = NewSong(), folder = Folder(song);
        string path = Path.Combine(folder, "chart_r.json");
        const string original = "{\n  \"_comment\": \"x\",\n  \"editorMarkers\": [],\n  \"editorTempoMap\": [],\n  \"bpm\": 180,\n  \"coordScale\": 1,\n" +
            "  \"offsetMs\": 0,\n  \"displayLevel\": 10,\n  \"beatZeroMs\": 0,\n  \"timeSignatures\": [],\n  \"notes\": [\n" +
            "    {\"beat\": 1, \"time\": 333.33333333333337, \"x\": 0.6, \"y\": 0.3, \"type\": \"tap\", \"color\": \"red\", \"direction\": \"none\", \"count\": 1, \"mirror\": true}\n" +
            "  ],\n  \"reverse\": {\"enabled\": true}\n}";
        File.WriteAllText(path, original);
        Save((SaberChartDocument)StoreLoad(song, "r"), song, "r");
        string saved = File.ReadAllText(path);
        foreach (string key in new[] { "editorMarkers", "editorTempoMap", "bpm", "timeSignatures", "reverse", "_comment" })
            Assert.AreEqual(RawText(original, key), RawText(saved, key), key);
        CollectionAssert.AreEqual(NoteTexts(original), NoteTexts(saved));
        Assert.NotNull(JsonNode.Parse(saved)[SaberChartUtility.EditedAtKey]);
    }

    // ---- 裏譜面へは、その裏譜面を読み込んだ譜面だけを保存する ----

    // 読み込んでいない選択(「開く・作る」で裏譜面を選んだだけ)や、読み込みに失敗したあとの内容では、裏譜面のファイルを書かない。
    [Test]
    public void SavingASecretIsRefusedUnlessThatSecretWasLoaded()
    {
        string song = SecretSong(out string folder);
        string secretPath = Path.Combine(folder, "chart_r.json"), normalPath = Path.Combine(folder, "chart_normal.json");
        string secretBefore = File.ReadAllText(secretPath);
        OpenWindow();
        Set("songId", song);
        Set("difficultyIndex", 3);
        Set("confirmDuplicateOverwrite", new Func<string, string, bool>((title, message) => true));
        Set("document", new SaberChartDocument { notes = { new SaberChartNote { beat = 1, time = 333.3333f, x = .6f, color = "red" } } });
        Call("UpdateDirtyState");
        Assert.AreEqual("r", Property<string>("EditingDifficulty"), "保存先は選択の裏譜面になる");
        Assert.False((bool)Call("SaveDocument"), "読み込んでいない裏譜面へは保存しない");
        StringAssert.Contains("読み込んで直したときだけ", Get<string>("statusMessage"));
        Assert.AreEqual(secretBefore, File.ReadAllText(secretPath));

        // 表の譜面を開いて、選択だけ裏譜面にしても、保存先は開いた表の譜面のまま。
        Load(song, 1);
        Document.notes.Add(new SaberChartNote { beat = 2, time = 666.6667f, x = -.6f, color = "blue" });
        Call("UpdateDirtyState");
        Set("difficultyIndex", 3);
        Assert.True((bool)Call("SaveDocument"));
        Assert.AreEqual(2, ChartLoader.LoadFromFile(normalPath).notes.Count);
        Assert.AreEqual(secretBefore, File.ReadAllText(secretPath));

        // 読み込みに失敗したあと(前回の裏譜面が壊れていて開けなかった窓)も書かない。
        CloseWindow();
        File.WriteAllText(secretPath, "{ broken");
        EditorPrefs.SetString(Prefix + "SongId", song);
        EditorPrefs.SetInt(Prefix + "Difficulty", 3);
        EditorPrefs.SetString(Prefix + "SecretDifficulty", "r");
        OpenWindow();
        StringAssert.Contains("前回の譜面を読み込めません", Get<string>("persistentWarning"));
        Set("confirmDuplicateOverwrite", new Func<string, string, bool>((title, message) => true));
        Document.notes.Add(new SaberChartNote { beat = 1, time = 333.3333f, x = .6f, color = "red" });
        Call("UpdateDirtyState");
        Assert.False((bool)Call("SaveDocument"));
        Assert.AreEqual("{ broken", File.ReadAllText(secretPath));
    }

    // ノーツの無い裏譜面(D は的とトゲだけ)も、読み込めば保存できる(ノーツの数では決めない)。
    [Test]
    public void SecretWithoutNotesCanBeSavedOnceLoaded()
    {
        string song = NewSong(), folder = Folder(song);
        string path = Path.Combine(folder, "chart_d.json");
        File.Copy(RealChart("Phobia PARTⅠ", "d"), path);
        string original = File.ReadAllText(path);
        UseSecret(song, RushESecretState.PhobiaAvoid);
        OpenWindow();
        Load(song, 3);
        Assert.AreEqual("d", Get<string>("loadedDifficulty"));
        Assert.AreEqual(0, Document.notes.Count);
        Call("ApplyDisplayLevel", 9);
        Assert.True((bool)Call("SaveDocument"));
        string saved = File.ReadAllText(path);
        Assert.AreEqual("9", RawText(saved, "displayLevel"));
        Assert.AreEqual(RawText(original, "aim"), RawText(saved, "aim"), "的とトゲは元のまま");
        Assert.AreEqual(0, JsonNode.Parse(saved)["notes"].Items.Count);
    }

    // ---- 曲ごとの裏譜面の一覧 ----

    [Test]
    public void EverySecretChartIsListedForItsOwnSongOnly()
    {
        foreach (SecretChart chart in RushESecretState.Charts)
            Assert.AreSame(chart, SaberChartSecrets.For(chart.SongId), chart.SongId);
        Assert.AreEqual("R", SaberChartSecrets.For("ManicSonic").Difficulty);
        Assert.AreEqual("D", SaberChartSecrets.For("Phobia PARTⅠ").Difficulty);
        Assert.AreEqual("r", SaberChartSecrets.Value(SaberChartSecrets.For("ManicSonic")));
        string song = NewSong(), folder = Folder(song);
        foreach (string difficulty in new[] { "easy", "normal", "hard" })
            File.WriteAllText(Path.Combine(folder, "chart_" + difficulty + ".json"), NormalChart);
        Assert.IsNull(SaberChartSecrets.For(song), "Easy/Normal/Hard だけの曲には無い");
        Assert.IsNull(SaberChartSecrets.For(null));
        Assert.IsNull(SaberChartSecrets.For("../ManicSonic"));
        Assert.True(SaberChartSecrets.IsSecret("r"));
        Assert.False(SaberChartSecrets.IsSecret("hard"));
        Assert.AreEqual("裏 R", SaberChartSecrets.Label("r"));
        Assert.AreEqual("R（鏡門）", SaberChartSecrets.Title("r"));
    }

    // 裏譜面のファイルが無い曲では、chart.json(Normal)や空の譜面で代わりに開かない。
    [Test]
    public void MissingSecretFileNeverFallsBackToNormal()
    {
        string song = NewSong(), folder = Folder(song);
        File.WriteAllText(Path.Combine(folder, "chart.json"), NormalChart);
        File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
        UseSecret(song, RushESecretState.ManicSonicReverse);
        Assert.IsNull(SaberChartSecrets.For(song));
        Assert.IsNull(Store.GetMethod("FindChartToLoad").Invoke(null, new object[] { song, "r" }));
        OpenWindow();
        Load(song, 1);
        Call("SwitchEditingDifficulty", 3);
        Assert.AreEqual("normal", Get<string>("loadedDifficulty"));
        Set("difficultyIndex", 3);
        Call("LoadDocument");
        Assert.AreEqual("normal", Get<string>("loadedDifficulty"), "裏譜面の無い曲は Normal を開く");
        Assert.AreEqual(1, Get<int>("difficultyIndex"));
        Assert.False(File.Exists(Path.Combine(folder, "chart_r.json")));
    }

    // ---- エディターの窓 ----

    [Test]
    public void SecretChartOpensFromTheHeaderAndKeepsItsMechanicsThroughEditsUndoAndSave()
    {
        string song = SecretSong(out string folder);
        string secretPath = Path.Combine(folder, "chart_r.json");
        string originalText = File.ReadAllText(secretPath);
        int mirrors = MirrorCount(originalText);
        OpenWindow();
        Load(song, 1);
        Call("RefreshDifficultyFiles");
        Assert.AreSame(RushESecretState.ManicSonicReverse, Get<SecretChart>("difficultySecret"), "ヘッダーに R の札を出す");
        Call("SwitchEditingDifficulty", 3);
        Assert.AreEqual("r", Get<string>("loadedDifficulty"));
        Assert.AreEqual(3, Get<int>("difficultyIndex"));
        Assert.AreEqual("裏 R", Property<string>("EditingDifficultyLabel"));
        Assert.AreEqual("R", Property<string>("EditingGameDifficulty"), "本編テストと譜面チェックには表の文字");
        StringAssert.Contains("裏譜面 R を開きました", Get<string>("statusMessage"));
        Assert.False(window.hasUnsavedChanges);
        Assert.AreEqual(mirrors, Document.notes.Count(note => SaberChartUtility.NoteExtraFlag(note, "mirror")));

        // 複製は仕掛けごと写り、Undo・Redo でも消えない。
        Set("selectedIndex", Document.notes.FindIndex(note => SaberChartUtility.NoteExtraFlag(note, "mirror")));
        Key(KeyCode.D, EventModifiers.Control);
        Assert.AreEqual(mirrors + 1, Document.notes.Count(note => SaberChartUtility.NoteExtraFlag(note, "mirror")));
        Key(KeyCode.Z, EventModifiers.Control);
        Assert.AreEqual(mirrors, Document.notes.Count(note => SaberChartUtility.NoteExtraFlag(note, "mirror")));
        Key(KeyCode.Y, EventModifiers.Control);
        Assert.AreEqual(mirrors + 1, Document.notes.Count(note => SaberChartUtility.NoteExtraFlag(note, "mirror")));
        // 高さを動かしても仕掛けは残る(Undo・Redo で選択は外れるので選び直す)。
        Set("selectedIndex", Document.notes.FindIndex(note => SaberChartUtility.NoteExtraFlag(note, "mirror")));
        SaberChartNote nudged = SelectedNote;
        string fields = nudged.extraJson;
        float height = nudged.y;
        Call("NudgeSelection", KeyCode.DownArrow, false);
        Assert.AreNotEqual(height, nudged.y);
        Assert.AreEqual(fields, nudged.extraJson);
        // 新しく置いたノーツには付かない。
        int count = Document.notes.Count;
        Call("AddNoteAtMouse", new Vector2(80, 40), timeline, lanes);
        Assert.AreEqual(count + 1, Document.notes.Count);
        Assert.AreEqual(string.Empty, SelectedNote.extraJson);

        Assert.True((bool)Call("SaveDocument"));
        Assert.False(window.hasUnsavedChanges);
        string saved = File.ReadAllText(secretPath);
        Assert.AreEqual(mirrors + 1, MirrorCount(saved));
        Assert.AreEqual(1, NoteTexts(originalText).Except(NoteTexts(saved)).Count(), "触っていないノーツは元の文字のまま(Undo・Redo のあとも)。変わったのは高さを動かした1個");
        Assert.NotNull(JsonNode.Parse(saved)[SaberChartUtility.EditedAtKey]);
        Assert.AreEqual(NormalChart, File.ReadAllText(Path.Combine(folder, "chart.json")));
        Assert.AreEqual(NormalChart, File.ReadAllText(Path.Combine(folder, "chart_normal.json")));
    }

    [Test]
    public void SecretChartBlocksNewDuplicateDeriveAndSongSettings()
    {
        string song = SecretSong(out string folder);
        string secretPath = Path.Combine(folder, "chart_r.json"), normalPath = Path.Combine(folder, "chart_normal.json");
        string secretBefore = File.ReadAllText(secretPath);
        OpenWindow();
        // 表の譜面から裏譜面を作らない(ここへ複製・新規)。
        Load(song, 1);
        Set("difficultyIndex", 3);
        Assert.False((bool)Call("DuplicateToSelection"));
        Assert.AreEqual("normal", Get<string>("loadedDifficulty"));
        Call("NewDocument");
        Assert.AreEqual("normal", Get<string>("loadedDifficulty"));
        StringAssert.Contains("生成の道具", Get<string>("statusMessage"));

        Call("SwitchEditingDifficulty", 3);
        Assert.AreEqual("r", Get<string>("loadedDifficulty"));
        string json = SaberChartUtility.ToJson(Document, false);
        // 裏譜面から表の譜面へも複製しない。
        Set("difficultyIndex", 2);
        Assert.False((bool)Call("DuplicateToSelection"));
        Assert.AreEqual("r", Get<string>("loadedDifficulty"));
        // 曲の設定は道具で決めているので変えない(写さない・読み込まない)。
        Call("ApplyChartSettings", Document.bpm + 10f, Document.offsetMs + 5f, 30f, 2f);
        Call("SetTimeSignature", 8f, 7, 8);
        Call("SetTempoMap", new List<SaberChartTempoPoint>(), "消しました");
        Call("RecalculateBeatsWithGrid");
        Call("WriteDisplayedOriginToFile");
        Call("LoadSongSettingsFrom", "normal");
        Set("confirmSettingsCopy", new Func<string, string, bool>((title, message) => true));
        Call("CopySongSettingsToOtherDifficulties");
        Assert.IsEmpty((System.Collections.IList)Call("SettingsMismatch"), "表の難易度とは比べない");
        StringAssert.Contains("道具で決めています", Get<string>("statusMessage"));
        Assert.AreEqual(json, SaberChartUtility.ToJson(Document, false));
        Assert.False(window.hasUnsavedChanges);
        Assert.AreEqual(NormalChart, File.ReadAllText(normalPath));
        Assert.AreEqual(secretBefore, File.ReadAllText(secretPath));
        Assert.False(File.Exists(Path.Combine(folder, "chart_hard.json")));
    }

    // 「開く・作る」の選択: 裏譜面の番号は、その曲に裏譜面があるときだけ裏譜面。無い曲では Normal。
    [Test]
    public void PickerOffersTheSecretOfTheChosenSongOnly()
    {
        string song = SecretSong(out _);
        string plain = NewSong();
        File.WriteAllText(Path.Combine(Folder(plain), "chart_normal.json"), NormalChart);
        OpenWindow();
        Set("songId", song);
        Set("difficultyIndex", 3);
        Assert.AreEqual("r", Property<string>("CurrentDifficulty"));
        Assert.AreSame(RushESecretState.ManicSonicReverse, Call("PickerSecret"));
        Set("songId", plain);
        Assert.AreEqual("normal", Property<string>("CurrentDifficulty"));
        Assert.IsNull(Call("PickerSecret"));
    }

    // 前回の難易度は、裏譜面なら文字でも覚え、その曲の裏譜面と違えば Normal に戻す。
    [Test]
    public void ReopeningRemembersTheSecretByItsLetter()
    {
        string song = SecretSong(out _);
        EditorPrefs.SetString(Prefix + "SongId", song);
        EditorPrefs.SetInt(Prefix + "Difficulty", 3);
        EditorPrefs.SetString(Prefix + "SecretDifficulty", "r");
        OpenWindow();
        Assert.AreEqual(3, Get<int>("difficultyIndex"));
        Assert.AreEqual("r", Get<string>("loadedDifficulty"), "前回の裏譜面を開き直す");
        CloseWindow();
        EditorPrefs.SetInt(Prefix + "Difficulty", 3);
        EditorPrefs.SetString(Prefix + "SecretDifficulty", "m");
        OpenWindow();
        Assert.AreEqual(1, Get<int>("difficultyIndex"), "別の文字なら Normal");
    }

    // 未保存の判定・下書き・再コンパイル(Unity のシリアライズ)にも、ノーツの知らない項目を含める。
    [Test]
    public void DirtyCheckDraftsAndSerializationIncludeUnknownFields()
    {
        OpenWindow();
        const string fields = "{\"mirror\": true}";
        Set("document", new SaberChartDocument
        {
            notes = { new SaberChartNote { beat = 4, time = 2000, x = .6f, y = .3f, color = "red", extraJson = fields } },
        });
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
        Assert.False(window.hasUnsavedChanges);
        Document.notes[0].extraJson = "{\"mirror\": false}";
        Call("UpdateDirtyState");
        Assert.True(window.hasUnsavedChanges, "知らない項目だけが違っても未保存");
        Document.notes[0].extraJson = fields;

        string draftPath = (string)Drafts.GetMethod("Write").Invoke(null, new object[] { "__ChartSecretTest_draft", "r", Document, null });
        Assert.NotNull(draftPath);
        object[] args = { "__ChartSecretTest_draft", "r", null };
        object draft = Drafts.GetMethod("LatestFor").Invoke(null, args);
        string draftJson = (string)draft.GetType().GetField("document").GetValue(draft);
        Assert.AreEqual(fields, SaberChartUtility.FromJson(draftJson).notes[0].extraJson, "下書きから戻しても残る");

        string serialized = EditorJsonUtility.ToJson(window);
        var other = ScriptableObject.CreateInstance<SaberChartEditorWindow>();
        try
        {
            EditorJsonUtility.FromJsonOverwrite(serialized, other);
            var restored = (SaberChartDocument)Field("document").GetValue(other);
            Assert.AreEqual(fields, restored.notes[0].extraJson, "再コンパイルで窓を作り直しても残る");
        }
        finally
        {
            other.DiscardChanges();
            Object.DestroyImmediate(other);
        }
    }

    // 動かした軸だけを変える。元から範囲の外にある値(L の x 2.7)は、触らない限りそのまま。
    [Test]
    public void MovingOneAxisKeepsTheOtherAndOutOfRangeValues()
    {
        OpenWindow();
        Set("document", new SaberChartDocument
        {
            notes = { new SaberChartNote { beat = 4, time = 2000, x = 2.7f, y = 0f, color = "blue", extraJson = "{\"slide\": true}" } },
        });
        Set("currentBeat", 4f);
        Set("pixelsPerBeat", 82f);
        Set("savedJson", SaberChartUtility.ToJson(Document, false));
        Call("UpdateDirtyState");
        Set("selectedIndex", 0);
        Call("NudgeSelection", KeyCode.UpArrow, false);
        Assert.AreEqual(2.7f, Document.notes[0].x, "高さだけ動かしたら横は変えない");
        Assert.AreEqual(3f / 7f, Document.notes[0].y, .0001f);
        Call("NudgeSelection", KeyCode.RightArrow, false);
        Assert.AreEqual(2.7f, Document.notes[0].x, "範囲の外へはそれ以上広げない");
        Call("NudgeSelection", KeyCode.LeftArrow, false);
        Assert.AreEqual(2.7f - 5f / 7f, Document.notes[0].x, .0001f);
        Document.notes[0].x = 2.7f;
        Document.notes[0].y = 1.8f;
        Call("NudgeNoteHeight", -1, false);
        Assert.AreEqual(1.8f - 3f / 7f, Document.notes[0].y, .0001f, "範囲の外の高さを端へ飛ばさない");
        var start = new Vector2(380, 360);
        Call("BeginNoteDrag", 0, start);
        Call("DragNoteTo", start + new Vector2(1, -20), false, timeline, lanes);
        Call("EndNoteDrag");
        Assert.Greater(Document.notes[0].x, 2.5f, "縦に動かしたときの手ぶれで横を端へ寄せない");
        Assert.AreEqual("{\"slide\": true}", Document.notes[0].extraJson);
    }

    // ---- 譜面チェック ----

    // R の鏡は門の内側(本編の ReverseRules.InsideGate)だけに効くので、門の外の鏡のノーツは注意。
    [Test]
    public void MirrorNoteOutsideTheGateIsAWarning()
    {
        static SaberChartNote Note(float time, float x, string extra) =>
            new SaberChartNote { time = time, x = x, y = .3f, color = x < 0 ? "blue" : "red", type = "tap", extraJson = extra };
        var chart = new SaberChartDocument
        {
            bpm = 180,
            notes = { Note(1000, .6f, "{\"mirror\": true}"), Note(2000, 1.9f, "{\"mirror\": true}"), Note(3000, -1.9f, ""), Note(4000, -1.9f, "{\"mirror\": false}") },
        };
        var issues = SaberChartLint.Check(chart, "R").Where(issue => issue.rule == "mirror-outside-gate").ToList();
        Assert.AreEqual(1, issues.Count);
        Assert.AreEqual(SaberChartIssueLevel.Warning, issues[0].level);
        CollectionAssert.AreEqual(new[] { 1 }, issues[0].notes);
        Assert.True(ReverseRules.InsideGate(chart.notes[0].x, chart.notes[0].y));
        Assert.False(ReverseRules.InsideGate(chart.notes[1].x, chart.notes[1].y));

        SaberChartDocument real = SaberChartUtility.FromJson(File.ReadAllText(RealChart("ManicSonic", "r")));
        Assert.IsEmpty(SaberChartLint.Check(real, "R").Where(issue => issue.rule == "mirror-outside-gate"), "今の R は門の内側だけ");
        int fingerprint = SaberChartLint.Fingerprint(real);
        real.notes.First(note => SaberChartUtility.NoteExtraFlag(note, "mirror")).x = 1.9f;
        Assert.AreEqual(1, SaberChartLint.Check(real, "R").Count(issue => issue.rule == "mirror-outside-gate"));
        Assert.AreNotEqual(fingerprint, SaberChartLint.Fingerprint(real));
    }

    // ---- G の長い版の音源と演出 ----

    [Test]
    public void SecretChartWithItsOwnAudioUsesItLikeTheGame()
    {
        Assert.AreEqual("audio_g.mp3", Path.GetFileName((string)StoreCall("AudioPathFor", "ElDorado", "g")));
        Assert.AreEqual("audio.mp3", Path.GetFileName((string)StoreCall("AudioPathFor", "ElDorado", "hard")));
        Assert.AreEqual("audio.mp3", Path.GetFileName((string)StoreCall("AudioPath", "ElDorado")));
        Assert.AreEqual("stage_g.json", Path.GetFileName((string)StoreCall("StageVariantPath", "ElDorado", "g")));
        Assert.IsNull(StoreCall("StageVariantPath", "ElDorado", "hard"));
    }

    // ---- プレイレビュー ----

    // 返信でノーツを置き換えても、元のノーツの知らない項目を引き継ぐ。足すノーツには付けない。
    [Test]
    public void ReviewReplacementKeepsTheOriginalNoteFields()
    {
        const string fields = "{\"mirror\": true}";
        var chart = new SaberChartDocument();
        chart.notes.Add(new SaberChartNote { time = 1000, color = "red", x = .6f, y = .3f, extraJson = fields });
        chart.notes.Add(new SaberChartNote { time = 2000, color = "blue", x = -.6f, y = .3f });
        SaberReviewRequest request = SaberPlaybackReviewData.Request(chart, "ManicSonic", "r", "chart_r.json", new[]
        {
            new SaberReviewComment { fromSeconds = .5f, toSeconds = 2.5f, instruction = "鏡のノーツを少し遅く", noteIndices = new[] { 0 } },
        });
        SaberChartNote replacement = chart.notes[0].Clone();
        replacement.time = 1100;
        replacement.extraJson = string.Empty;
        var patch = new SaberReviewPatch
        {
            songId = "ManicSonic", difficulty = "r", baseHash = request.baseHash,
            operations = new List<SaberReviewOperation>
            {
                new SaberReviewOperation { action = "replace", index = 0, note = replacement },
                new SaberReviewOperation { action = "add", note = new SaberChartNote { time = 1500, x = 0, y = .3f, color = "gold", extraJson = fields } },
            },
        };
        SaberChartDocument result = SaberPlaybackReviewData.Apply(request, chart, patch, 10);
        Assert.AreEqual(fields, result.notes.Single(note => note.time == 1100).extraJson);
        Assert.AreEqual(string.Empty, result.notes.Single(note => note.time == 1500).extraJson);
        Assert.AreEqual(fields, chart.notes[0].extraJson, "元の譜面は変えない");
    }

    // ---- helpers ----

    static string SongsRoot => Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs"));
    static string RealChart(string song, string difficulty) =>
        Path.Combine(Application.streamingAssetsPath, "Songs", song, "chart_" + difficulty + ".json");
    static string Folder(string song) => Path.Combine(Application.streamingAssetsPath, "Songs", song);

    string NewSong()
    {
        string song = "__ChartSecretTest_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Folder(song));
        tempSongs.Add(song);
        return song;
    }

    // 架空の曲を R の曲に見立てる(実際の chart_r.json の写しと、Normal・chart.json)。
    string SecretSong(out string folder)
    {
        string song = NewSong();
        folder = Folder(song);
        File.Copy(RealChart("ManicSonic", "r"), Path.Combine(folder, "chart_r.json"));
        File.WriteAllText(Path.Combine(folder, "chart_normal.json"), NormalChart);
        File.WriteAllText(Path.Combine(folder, "chart.json"), NormalChart);
        UseSecret(song, RushESecretState.ManicSonicReverse);
        return song;
    }

    static void UseSecret(string song, SecretChart chart) =>
        TableOverride.SetValue(null, new Func<string, SecretChart>(id => id == song ? chart : RushESecretState.For(id)));

    static string Save(SaberChartDocument document, string song, string difficulty)
    {
        try { return (string)Store.GetMethod("Save").Invoke(null, new object[] { document, song, difficulty }); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    static object StoreCall(string name, params object[] args)
    {
        try { return Store.GetMethod(name, BindingFlags.Static | BindingFlags.Public).Invoke(null, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    // ノーツ1個を1行にした一覧(並びの違いは見ない)。知っている項目は数として、知らない項目は値の綴りのまま比べる。
    static List<string> NoteRecords(string json)
    {
        var records = new List<string>();
        foreach (JsonNode note in JsonNode.Parse(json)["notes"].Items)
        {
            if (note.Kind != 'o') continue;
            string known = string.Join("|", Number(note, "time"), Number(note, "x"), Number(note, "y"), Number(note, "beat"),
                Text(note, "color", "default"), Text(note, "type", "tap"), Text(note, "direction", "none"),
                Number(note, "count", 1), Number(note, "lengthMs"));
            string unknown = string.Join(",", note.Members.Where(member => !KnownNoteKeys.Contains(member.Key))
                .Select(member => member.Key + "=" + member.Value.Canonical()));
            records.Add(known + "§" + unknown);
        }
        records.Sort(StringComparer.Ordinal);
        return records;
    }

    static string Number(JsonNode note, string key, float fallback = 0f)
    {
        JsonNode value = note[key];
        float number = value == null ? fallback : float.Parse(value.Raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        return number.ToString("R", CultureInfo.InvariantCulture);
    }

    static string Text(JsonNode note, string key, string fallback) =>
        note[key] == null ? fallback : Unquote(note[key].Raw).Trim().ToLowerInvariant();

    static string Unquote(string raw) => raw.Length >= 2 && raw[0] == '"' ? raw.Substring(1, raw.Length - 2) : raw;

    // ノーツごとの文字(空白を除いた形。数は元の綴りのまま比べる)。並びの違いは見ない。
    static List<string> NoteTexts(string json) => JsonNode.Parse(json)["notes"].Items.Where(note => note.Kind == 'o')
        .Select(note => note.Canonical()).OrderBy(text => text, StringComparer.Ordinal).ToList();

    // SaberChartFileStore.Load(曲, 難易度, out 読んだファイル)。
    static object StoreLoad(string song, string difficulty)
    {
        try { return Store.GetMethod("Load").Invoke(null, new object[] { song, difficulty, null }); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    static int MirrorCount(string json) =>
        JsonNode.Parse(json)["notes"].Items.Count(note => note.Kind == 'o' && note["mirror"]?.Raw == "true");

    // 上の段の項目の値の元の文字(改行はそろえる)。
    static string RawText(string json, string key)
    {
        JsonNode value = JsonNode.Parse(json)[key];
        Assert.NotNull(value, key);
        return json.Substring(value.Start, value.End - value.Start).Replace("\r\n", "\n");
    }

    // 本編(ChartLoader)が読んだノーツ。公開の項目を全部並べる(並びの違いは見ない)。
    static List<string> GameNotes(string json) =>
        ChartLoader.Parse(json).notes.Select(DescribeNote).OrderBy(text => text, StringComparer.Ordinal).ToList();

    static string DescribeNote(object note) => string.Join(";", note.GetType()
        .GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(field => field.Name, StringComparer.Ordinal)
        .Select(field => field.Name + "=" + DescribeValue(field.GetValue(note))));

    static string DescribeValue(object value)
    {
        switch (value)
        {
            case null: return "null";
            case float number: return number.ToString("R", CultureInfo.InvariantCulture);
            case string text: return text;
            case System.Collections.IList list: return "[" + string.Join(",", list.Cast<object>().Select(DescribeNote)) + "]";
            default: return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    SaberChartDocument Document => Get<SaberChartDocument>("document");
    SaberChartNote SelectedNote => Property<SaberChartNote>("SelectedNote");

    void OpenWindow() => window = ScriptableObject.CreateInstance<SaberChartEditorWindow>();

    void CloseWindow()
    {
        if (window == null) return;
        window.DiscardChanges();
        Object.DestroyImmediate(window);
        window = null;
    }

    void Load(string song, int difficulty)
    {
        Set("songId", song);
        Set("difficultyIndex", difficulty);
        Set("confirmChangesDialog", new Func<string, string, string, string, string, int>((a, b, c, d, e) => 2));
        Set("reportLoadError", new Action<string>(message => Assert.Fail(message)));
        Call("LoadDocument");
        Assert.AreEqual(song, Get<string>("loadedSongId"));
    }

    void Key(KeyCode key, EventModifiers modifiers = EventModifiers.None) =>
        Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
    void Set(string name, object value) => Field(name).SetValue(window, value);
    T Get<T>(string name) => (T)Field(name).GetValue(window);
    T Property<T>(string name) =>
        (T)(typeof(SaberChartEditorWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMemberException(name)).GetValue(window);
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

    // 試験用の小さな JSON の読み取り(エディターの読み書きとは別の実装)。値は元の綴り、位置も覚える。
    sealed class JsonNode
    {
        public char Kind; // 'o' = オブジェクト、'a' = 配列、'v' = 値
        public string Raw;
        public int Start, End;
        public readonly List<KeyValuePair<string, JsonNode>> Members = new List<KeyValuePair<string, JsonNode>>();
        public readonly List<JsonNode> Items = new List<JsonNode>();

        public JsonNode this[string key]
        {
            get
            {
                JsonNode found = null;
                foreach (var member in Members) if (member.Key == key) found = member.Value;
                return found;
            }
        }

        // 空白を除いた形(値の綴りは変えない)。
        public string Canonical() => Kind == 'v' ? Raw
            : Kind == 'a' ? "[" + string.Join(",", Items.Select(item => item.Canonical())) + "]"
            : "{" + string.Join(",", Members.Select(member => "\"" + member.Key + "\":" + member.Value.Canonical())) + "}";

        public static JsonNode Parse(string text)
        {
            int index = 0;
            return Read(text, ref index);
        }

        static JsonNode Read(string text, ref int index)
        {
            Skip(text, ref index);
            var node = new JsonNode { Start = index };
            char c = text[index];
            if (c == '{')
            {
                node.Kind = 'o';
                index++;
                Skip(text, ref index);
                if (text[index] == '}') index++;
                else
                    while (true)
                    {
                        Skip(text, ref index);
                        string key = Unquote(ReadString(text, ref index));
                        Skip(text, ref index);
                        index++; // :
                        node.Members.Add(new KeyValuePair<string, JsonNode>(key, Read(text, ref index)));
                        Skip(text, ref index);
                        if (text[index++] == '}') break;
                    }
            }
            else if (c == '[')
            {
                node.Kind = 'a';
                index++;
                Skip(text, ref index);
                if (text[index] == ']') index++;
                else
                    while (true)
                    {
                        node.Items.Add(Read(text, ref index));
                        Skip(text, ref index);
                        if (text[index++] == ']') break;
                    }
            }
            else
            {
                node.Kind = 'v';
                if (c == '"') node.Raw = ReadString(text, ref index);
                else
                {
                    int start = index;
                    while (index < text.Length && ",]} \t\r\n".IndexOf(text[index]) < 0) index++;
                    node.Raw = text.Substring(start, index - start);
                }
            }
            node.End = index;
            return node;
        }

        static string ReadString(string text, ref int index)
        {
            int start = index++;
            while (text[index] != '"') index += text[index] == '\\' ? 2 : 1;
            index++;
            return text.Substring(start, index - start);
        }

        static void Skip(string text, ref int index)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == '﻿')) index++;
        }
    }
}
