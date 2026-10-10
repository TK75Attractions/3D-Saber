using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

// 曲名・ふりがな・公開を stage.json から読む(曲ごとのコードの決め打ちをしない)ことを確かめる。
public class SongDisplayInfoTests
{
    string folder;

    [SetUp]
    public void SetUp() => SongDisplayInfo.ClearCache();

    [TearDown]
    public void TearDown()
    {
        if (folder != null)
        {
            string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
            Assert.True(Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.True(Path.GetFileName(folder).StartsWith("__SongDisplayTest_"));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
            folder = null;
        }
        SongDisplayInfo.ClearCache();
    }

    [TestCase("Epilogue", "校歌", "校歌")]
    [TestCase("Andalusia", "アンダルシア", "アンダルシア")]
    [TestCase("揺籠", "揺籠", "揺籠")]
    [TestCase("ElDorado", "ElDorado", "EL DORADO")]
    [TestCase("NeonParade", "Neon Parade", "NEON PARADE")]
    [TestCase("PrismCircuit", "Prism Circuit", "PRISM CIRCUIT")]
    [TestCase("2_23_AM", "2:23 AM", "2:23 AM")]
    public void TitlesComeFromStageJson(string songId, string listTitle, string styledTitle)
    {
        Assert.AreEqual(listTitle, SongSelectController.DisplaySongTitle(songId));
        Assert.AreEqual(styledTitle, ResultSkin.SongIdToDisplayTitle(songId));
    }

    [TestCase("Epilogue", "こうか")]
    [TestCase("揺籠", "ゆりかご")]
    [TestCase("ElDorado", "")]
    [TestCase("Andalusia", "")]
    public void ReadingsComeFromStageJson(string songId, string reading)
    {
        Assert.AreEqual(reading, SongDisplayInfo.Get(songId).Reading);
    }

    [Test]
    public void MissingOrMismatchedFoldersFallBackToTheId()
    {
        Assert.AreEqual("ELDORADO", ResultSkin.SongIdToDisplayTitle("ELDORADO"), "表示名がフォルダ名と同じだけなら、渡された ID の綴りのまま");
        Assert.AreEqual("校歌", SongSelectController.DisplaySongTitle("epilogue"), "保存記録の ID と大文字小文字が違っても同じ曲");
        Assert.AreEqual("NoSuchSong", SongSelectController.DisplaySongTitle("NoSuchSong"));
        Assert.True(SongDisplayInfo.Get("NoSuchSong").Listed);
        Assert.AreEqual("", ResultSkin.SongIdToDisplayTitle(null));
    }

    [Test]
    public void NewSongNamesAndTheListedFlagNeedNoCode()
    {
        string songId = "__SongDisplayTest_" + Guid.NewGuid().ToString("N");
        folder = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "chart_normal.json"),
            "{\"bpm\":120,\"displayLevel\":3,\"notes\":[{\"beat\":2,\"time\":1000,\"x\":0,\"y\":0,\"type\":\"tap\",\"color\":\"red\",\"direction\":\"none\",\"count\":1}]}");
        File.WriteAllText(Path.Combine(folder, "stage.json"),
            "{ \"schemaVersion\": 1, \"displayName\": \"新しい曲\", \"displayReading\": \"あたらしいきょく\", \"artist\": \"だれか\", \"listed\": false, \"sections\": [] }");
        SongDisplayInfo.ClearCache();
        Assert.AreEqual("新しい曲", SongSelectController.DisplaySongTitle(songId));
        Assert.AreEqual("あたらしいきょく", SongDisplayInfo.Get(songId).Reading);
        Assert.AreEqual("だれか", SongDisplayInfo.Get(songId).Artist);
        CollectionAssert.DoesNotContain(SongSelectController.EnumerateSongIds(), songId, "選曲に出さない曲は並べない");
        File.WriteAllText(Path.Combine(folder, "stage.json"), "{ \"schemaVersion\": 1, \"displayName\": \"新しい曲\", \"sections\": [] }");
        SongDisplayInfo.ClearCache();
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), songId, "項目が無ければこれまでどおり出す");
    }
}
