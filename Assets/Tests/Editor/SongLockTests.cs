using NUnit.Framework;
using UnityEngine;

// 譜面未制作の曲を曲選択でロック(選べない)する仕組みの検証。
public class SongLockTests
{
    [Test]
    public void HasPlayableChart_ElDorado_IsPlayable()
    {
        Assert.IsTrue(SongSelectController.HasPlayableChart(
            "ElDorado", new[] { "Easy", "Normal", "Hard" }));
    }

    [Test]
    public void HasPlayableChart_EmptyChartSong_IsLocked()
    {
        // 揺籠は空chart.jsonのみ → ロック
        Assert.IsFalse(SongSelectController.HasPlayableChart(
            "揺籠", new[] { "Easy", "Normal", "Hard" }));
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
        // 譜面未制作の曲(揺籠)は一覧に「完全に出ない」。実譜面のある曲は出る。
        var listed = SongSelectController.EnumerateSongIds();
        CollectionAssert.Contains(listed, "ElDorado");
        CollectionAssert.DoesNotContain(listed, "揺籠");
    }

    [Test]
    public void EnumerateSongIds_AllMode_StillSeesChartlessSongs()
    {
        // playableOnly=false ならフォルダ自体は見える(ツール用途)
        var all = SongSelectController.EnumerateSongIds(playableOnly: false);
        CollectionAssert.Contains(all, "揺籠");
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
