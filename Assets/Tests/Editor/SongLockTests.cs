using System.IO;
using NUnit.Framework;
using UnityEngine;

// 譜面未制作の曲を曲選択でロック(選べない)する仕組みの検証。
// 実在曲はユーザーが譜面を保存した瞬間に「譜面あり」へ変わるため、
// 空譜面の検証はテスト専用の曲フォルダをその場で作って行う(実データを前提にしない)。
public class SongLockTests
{
    private const string ChartlessSongId = "__TestChartlessSong__";

    private static string SongsRoot => Path.Combine(Application.streamingAssetsPath, "Songs");
    private static string ChartlessDir => Path.Combine(SongsRoot, ChartlessSongId);

    [OneTimeSetUp]
    public void CreateChartlessSong()
    {
        Directory.CreateDirectory(ChartlessDir);
        File.WriteAllText(Path.Combine(ChartlessDir, "chart.json"),
            "{ \"bpm\": 120.0, \"coordScale\": 1.0, \"offsetMs\": 0, \"notes\": [] }");
    }

    [OneTimeTearDown]
    public void RemoveChartlessSong()
    {
        if (Directory.Exists(ChartlessDir)) Directory.Delete(ChartlessDir, true);
        string meta = ChartlessDir + ".meta";
        if (File.Exists(meta)) File.Delete(meta);
    }

    [Test]
    public void HasPlayableChart_ElDorado_IsPlayable()
    {
        Assert.IsTrue(SongSelectController.HasPlayableChart(
            "ElDorado", new[] { "Easy", "Normal", "Hard" }));
    }

    [Test]
    public void HasPlayableChart_EmptyChartSong_IsLocked()
    {
        // 空chart.jsonのみ → ロック
        Assert.IsFalse(SongSelectController.HasPlayableChart(
            ChartlessSongId, new[] { "Easy", "Normal", "Hard" }));
    }

    [Test]
    public void HasPlayableChart_UnknownSong_IsLocked()
    {
        Assert.IsFalse(SongSelectController.HasPlayableChart(
            "__no_such_song__", new[] { "Normal" }));
    }

    [Test]
    public void EnumerateSongIds_ExcludesChartlessSongs()
    {
        // 譜面未制作の曲は一覧に「完全に出ない」。実譜面のある曲は出る。
        var listed = SongSelectController.EnumerateSongIds();
        CollectionAssert.Contains(listed, "ElDorado");
        CollectionAssert.DoesNotContain(listed, ChartlessSongId);
    }

    [Test]
    public void EnumerateSongIds_AllMode_StillSeesChartlessSongs()
    {
        // playableOnly=false ならフォルダ自体は見える(ツール用途)
        var all = SongSelectController.EnumerateSongIds(playableOnly: false);
        CollectionAssert.Contains(all, ChartlessSongId);
    }

    [Test]
    public void Controller_ListContainsNoLockedSongs()
    {
        var go = new GameObject("songSelect");
        try
        {
            var ctl = go.AddComponent<SongSelectController>();
            ctl.Populate();
            // 一覧から除外済みなので、リスト上にロック曲は存在しない
            for (int i = 0; i < 100; i++)
            {
                Assert.IsFalse(ctl.IsLocked(i), "一覧にロック曲が混ざらない: index " + i);
            }
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
