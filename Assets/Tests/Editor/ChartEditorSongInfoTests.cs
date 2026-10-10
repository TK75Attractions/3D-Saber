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

// 曲情報パネル(stage.json・表示レベル・ジャケット)と、プレイ画面を本編に近づける表示を確かめる。
public class ChartEditorSongInfoTests
{
    const string Prefix = "3DSaber.ChartEditor.";
    static readonly string[] PrefKeys = { "SongId", "Difficulty", "Snap", "Measure", "Zoom", "RecordingLayout" };
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
        EditorPrefs.SetString(Prefix + "SongId", "__ChartEditorSongInfoTest");
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
        SongDisplayInfo.ClearCache();
    }

    // ---- stage.json の読み書き ----

    [Test]
    public void EveryExistingStageFileRoundTripsUnchanged()
    {
        string songs = Path.Combine(Application.streamingAssetsPath, "Songs");
        int checkedFiles = 0;
        foreach (string folder in Directory.GetDirectories(songs))
        {
            string path = Path.Combine(folder, "stage.json");
            if (!File.Exists(path)) continue;
            string text = File.ReadAllText(path);
            Assert.AreEqual(text, SaberStageInfo.Parse(text).Write(), "開いて保存しただけで書き方を変えない: " + path);
            Assert.False(SaberStageInfo.Parse(text).IsDirty);
            checkedFiles++;
        }
        Assert.GreaterOrEqual(checkedFiles, 9);
    }

    [Test]
    public void EditingKeepsUnknownFieldsAndSectionExtras()
    {
        string text = "{\n  \"schemaVersion\": 1,\n  \"displayName\": \"曲\",\n  \"timeOrigin\": \"audio-file-start\",\n" +
                      "  \"previewStartSeconds\": 10.5,\n  \"audioSha256\": \"abc\",\n  \"_comment\": \"メモ\",\n  \"sections\": [\n" +
                      "    { \"label\": \"サビ\", \"startSeconds\": 20, \"endSeconds\": 40, \"fadeInSeconds\": 1, \"fadeOutSeconds\": 2, \"intensity\": 0.80, \"impactSeconds\": 0.6 }\n" +
                      "  ]\n}\n";
        var info = SaberStageInfo.Parse(text);
        info.DisplayName = "新しい名前";
        info.Reading = "あたらしいなまえ";
        info.Listed = false;
        info.PreviewStartSeconds = 12.25;
        info.Sections[0].EndSeconds = 44;
        info.AddSection("二回目", 60, 80);
        string written = info.Write();
        StringAssert.Contains("\"displayName\": \"新しい名前\",\n  \"displayReading\": \"あたらしいなまえ\",\n  \"timeOrigin\"", written);
        StringAssert.Contains("\"listed\": false,\n  \"audioSha256\": \"abc\"", written);
        StringAssert.Contains("\"_comment\": \"メモ\"", written);
        StringAssert.Contains("\"intensity\": 0.80, \"impactSeconds\": 0.6 }", written, "触っていない数値の綴りと項目を保つ");
        StringAssert.Contains("\"endSeconds\": 44,", written);
        var timeline = JsonUtility.FromJson<StagePerformanceTimeline>(written);
        Assert.AreEqual(2, timeline.sections.Length);
        Assert.AreEqual(12.25, timeline.previewStartSeconds, 1e-6);
        info.Listed = true;
        StringAssert.DoesNotContain("listed", info.Write(), "選曲に出すときは項目を書かない");
    }

    [Test]
    public void NewStageInfoIsReadableByTheGame()
    {
        var info = SaberStageInfo.CreateNew("NewTune");
        Assert.False(info.Exists);
        Assert.True(info.IsDirty);
        info.AddSection("サビ", 30, 50);
        var timeline = JsonUtility.FromJson<StagePerformanceTimeline>(info.Write());
        Assert.AreEqual(1, timeline.sections.Length);
        Assert.AreEqual(0, timeline.previewStartSeconds);
        StringAssert.Contains("\"displayName\": \"NewTune\"", info.Write());
    }

    // ---- 曲情報パネル ----

    [Test]
    public void SavingSongInfoWritesStageJsonAndOtherDifficultyLevels()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"displayLevel\":5,\"notes\":[{\"beat\":2,\"time\":1000,\"x\":0}]}");
            File.WriteAllText(Path.Combine(folder, "chart_hard.json"), "{\"_comment\":\"Hard\",\"bpm\":120,\"displayLevel\":0,\"notes\":[{\"beat\":2,\"time\":1000,\"x\":1}]}");
            Load(song, 1);
            Call("EnsureSongInfo");
            var info = Get<SaberStageInfo>("songInfo");
            Assert.False(info.Exists);
            info.DisplayName = "テストの曲";
            info.Reading = "てすとのきょく";
            info.AddSection("サビ", 30, 50);
            Get<int[]>("songInfoLevels")[2] = 8;
            Assert.True((bool)GetProperty("SongInfoDirty"));
            Assert.True((bool)Call("SaveSongInfo"));
            string stage = File.ReadAllText(Path.Combine(folder, "stage.json"));
            StringAssert.Contains("\"displayName\": \"テストの曲\"", stage);
            Assert.AreEqual("テストの曲", SongSelectController.DisplaySongTitle(song), "コードを直さずに本編の曲名が変わる");
            Assert.AreEqual("てすとのきょく", SongDisplayInfo.Get(song).Reading);
            var hard = File.ReadAllText(Path.Combine(folder, "chart_hard.json"));
            Assert.AreEqual(8, ChartLoader.Parse(hard).displayLevel);
            StringAssert.Contains("\"_comment\": \"Hard\"", hard);
            Assert.AreEqual(5, ChartLoader.LoadFromFile(Path.Combine(folder, "chart_normal.json")).displayLevel, "編集中の難易度は譜面と一緒に保存する");
            Assert.False((bool)GetProperty("SongInfoDirty"));
        });
    }

    [Test]
    public void EditingDifficultyLevelIsAnUndoableChartChange()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"displayLevel\":5,\"notes\":[]}");
            Load(song, 1);
            Call("ApplyDisplayLevel", 6);
            Assert.AreEqual(6, Document.displayLevel);
            Assert.True(window.hasUnsavedChanges);
            Call("HandleKeyboardShortcuts", new Event { type = EventType.KeyDown, keyCode = KeyCode.Z, modifiers = EventModifiers.Control });
            Assert.AreEqual(5, Document.displayLevel);
            Assert.False(window.hasUnsavedChanges);
        });
    }

    [Test]
    public void SwitchingSongsAsksToSaveUnsavedSongInfo()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"notes\":[]}");
            Load(song, 1);
            Call("EnsureSongInfo");
            Get<SaberStageInfo>("songInfo").DisplayName = "変えた名前";
            int asked = 0;
            Set("confirmSongInfoDialog", new Func<string, string, bool>((a, b) => { asked++; return true; }));
            Set("loadedSongId", "__AnotherSong");
            Call("EnsureSongInfo");
            Assert.AreEqual(1, asked);
            StringAssert.Contains("変えた名前", File.ReadAllText(Path.Combine(folder, "stage.json")));
        });
    }

    [Test]
    public void CoverImagesAreConvertedToPngAndBackedUp()
    {
        WithSongFolder((song, folder) =>
        {
            File.WriteAllText(Path.Combine(folder, "chart_normal.json"), "{\"bpm\":120,\"notes\":[]}");
            Load(song, 1);
            string source = Path.Combine(Path.GetTempPath(), "__cover_" + Guid.NewGuid().ToString("N") + ".jpg");
            var texture = new Texture2D(6, 6);
            try
            {
                File.WriteAllBytes(source, texture.EncodeToJPG());
                Call("ImportCoverImage", source);
                string cover = Path.Combine(folder, "cover.png");
                Assert.True(File.Exists(cover));
                StringAssert.Contains("6×6（正方形）", (string)CallStatic("DescribeImage", cover));
                Call("ImportCoverImage", source);
                string backups = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups"));
                Assert.IsNotEmpty(Directory.GetFiles(backups, song + "_cover_*.png"), "前のジャケットを残す");
            }
            finally
            {
                Object.DestroyImmediate(texture);
                if (File.Exists(source)) File.Delete(source);
            }
        });
    }

    // ---- 質8: プレイ画面を本編に近づける ----

    [Test]
    public void PreviewUsesTheGameCameraAndShowsBarLinesAndHitRanges()
    {
        var document = new SaberChartDocument
        {
            bpm = 120,
            notes = new List<SaberChartNote> { new SaberChartNote { time = 2000, x = 1, y = 0, color = "red" } },
        };
        using (var preview = new SaberChartPlaybackPreview())
        {
            preview.Tick(document, 1.8);
            Assert.AreEqual(SaberChartPlaybackPreview.GameCameraPosition, preview.CameraPosition, "本編と同じ視点");
            Assert.Greater(preview.VisibleBarLineCount, 0, "本編の小節線(2秒の小節頭)が流れてくる");
            Assert.AreEqual(0, preview.VisibleHitRingCount, "当たりの円は既定では出さない");
            preview.ShowHitRadius = true;
            preview.Tick(document, 1.9);
            Assert.AreEqual(1, preview.VisibleHitRingCount, "判定の前後0.25秒のノーツに円");
            preview.Tick(document, 1.0);
            Assert.AreEqual(0, preview.VisibleHitRingCount);
            preview.UseGameCamera = false;
            preview.ShowBarLines = false;
            preview.Tick(document, 1.8);
            Assert.AreEqual(SaberChartPlaybackPreview.FlatCameraPosition, preview.CameraPosition);
            Assert.AreEqual(0, preview.VisibleBarLineCount);
            Assert.AreEqual(1, preview.VisibleNoteCount, "目印はノーツの数に入れない");
        }
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
        SongDisplayInfo.ClearCache();
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
    static object CallStatic(string name, params object[] args) =>
        typeof(SaberChartEditorWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
}
